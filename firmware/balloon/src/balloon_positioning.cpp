#include "balloon_positioning.h"

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include <string.h>

#include "esp_event.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#include "balloon_clock.h"
#include "balloon_led_controller.h"
#include "balloon_mesh.h"
#include "balloon_neighbor_link.h"
#include "balloon_protocol.h"
#include "balloon_time_link.h"
#include "bm_anchor_protocol.h"
#include "bm_config.h"

static const char *TAG = "positioning";

static const uint8_t s_anchor_token[8] = BM_ANCHOR_TOKEN_BYTES;
static SemaphoreHandle_t s_ftm_done;
static volatile bool s_ftm_ok;
static volatile uint32_t s_ftm_distance_cm;

static void ftm_event_handler(void *arg, esp_event_base_t base, int32_t id, void *data) {
    (void)arg;
    (void)base;
    if (id != WIFI_EVENT_FTM_REPORT) {
        return;
    }
    wifi_event_ftm_report_t *report = (wifi_event_ftm_report_t *)data;
    s_ftm_ok = report->status == FTM_STATUS_SUCCESS && report->dist_est > 0;
    s_ftm_distance_cm = report->dist_est;
    if (report->ftm_report_data != NULL) {
        free(report->ftm_report_data);
        report->ftm_report_data = NULL;
    }
    if (s_ftm_done != NULL) {
        xSemaphoreGive(s_ftm_done);
    }
    ESP_LOGI(TAG, "FTM status=%d dist=%lu cm", (int)report->status,
             (unsigned long)report->dist_est);
}

static bool fetch_anchor_data(uint8_t anchor_id, bm_anchor_response_t *response) {
    uint8_t request[BM_ANCHOR_REQUEST_SIZE];
    size_t request_len = bm_anchor_request_encode(request, sizeof(request), s_anchor_token);
    if (request_len == 0) {
        return false;
    }

    WiFiUDP udp;
    if (!udp.begin(0)) {
        return false;
    }
    udp.beginPacket(IPAddress(192, 168, 4, 1), BM_ANCHOR_UDP_PORT);
    udp.write(request, request_len);
    udp.endPacket();

    uint8_t buffer[BM_ANCHOR_RESPONSE_SIZE];
    uint32_t start = millis();
    bool ok = false;
    while ((uint32_t)(millis() - start) < 2500u) {
        int size = udp.parsePacket();
        if (size >= (int)sizeof(buffer)) {
            int read = udp.read(buffer, sizeof(buffer));
            if (read == (int)sizeof(buffer) &&
                bm_anchor_response_parse(buffer, sizeof(buffer), response) &&
                response->anchor_id == anchor_id) {
                ok = true;
                break;
            }
        }
        delay(20);
    }
    udp.stop();
    return ok;
}

static void sync_clock_if_possible(const bm_anchor_response_t *response) {
    if ((response->flags & BM_ANCHOR_FLAG_TIME_VALID) == 0) {
        return;
    }
    uint64_t old_tick = 0;
    uint64_t new_tick = 0;
    bool was_synced = false;
    if (bm_clock_apply_anchor_time((int64_t)response->utc_ms,
                                   bm_clock_now_mono_us(),
                                   &old_tick, &new_tick, &was_synced)) {
        bm_led_controller_on_clock_sync(old_tick, new_tick, was_synced);
        ESP_LOGI(TAG, "clock synced from anchor %u (was_synced=%d)",
                 response->anchor_id, (int)was_synced);
    }
}

static bool measure_anchor(const bm_anchor_entry_t *anchor, uint8_t batch_id,
                           uint8_t *valid_count) {
    ESP_LOGI(TAG, "connecting to %s", anchor->ssid);
    WiFi.begin(anchor->ssid, BM_ANCHOR_WIFI_PASSWORD);
    // FTM only yields valid measurements on a 20 MHz link (the anchor AP is
    // HT20 too); an HT40 association ends with "No valid FTM Measurements".
    esp_wifi_set_bandwidth(WIFI_IF_STA, WIFI_BW_HT20);
    uint32_t start = millis();
    while (WiFi.status() != WL_CONNECTED && (uint32_t)(millis() - start) < 12000u) {
        delay(100);
    }
    if (WiFi.status() != WL_CONNECTED) {
        ESP_LOGW(TAG, "connect to %s failed", anchor->ssid);
        WiFi.disconnect();
        return false;
    }

    wifi_ap_record_t ap = {};
    bool have_ap = esp_wifi_sta_get_ap_info(&ap) == ESP_OK;
    if (!have_ap) {
        WiFi.disconnect();
        return false;
    }

    bm_anchor_response_t response = {};
    if (fetch_anchor_data(anchor->id, &response)) {
        sync_clock_if_possible(&response);
    } else {
        ESP_LOGW(TAG, "anchor %u data request failed", anchor->id);
    }

    bool measured = false;
    if (s_ftm_done != NULL) {
        xSemaphoreTake(s_ftm_done, 0);
        s_ftm_ok = false;
        s_ftm_distance_cm = 0;

        wifi_ftm_initiator_cfg_t config = {};
        memcpy(config.resp_mac, ap.bssid, sizeof(config.resp_mac));
        config.channel = ap.primary;
        config.frm_count = 16;
        config.burst_period = 0;

        esp_err_t err = esp_wifi_ftm_initiate_session(&config);
        if (err == ESP_OK) {
            if (xSemaphoreTake(s_ftm_done, pdMS_TO_TICKS(8000)) == pdTRUE) {
                // The wire field is uint16 centimetres; a longer range would
                // be clamped and reported as a wrong (much shorter) distance.
                measured = s_ftm_ok &&
                           s_ftm_distance_cm > 0 &&
                           s_ftm_distance_cm <= 65535u;
            }
            esp_wifi_ftm_end_session();
        } else {
            ESP_LOGW(TAG, "FTM initiate failed: %s", esp_err_to_name(err));
        }
    }

    if (measured) {
        bm_ftm_report_t report = {
            .anchor_id = anchor->id,
            .distance_cm = (uint16_t)(s_ftm_distance_cm > 65535u ? 65535u : s_ftm_distance_cm),
            .timestamp = bm_clock_is_synced()
                             ? (uint32_t)(bm_clock_now_tick40() & 0xFFFFFFFFu)
                             : BM_FTM_TIMESTAMP_UNKNOWN,
            .batch_id = batch_id,
        };
        uint8_t payload[8];
        if (bm_encode_ftm_report(payload, sizeof(payload), &report) == 8 &&
            bm_balloon_mesh_send(BM_VENDOR_OPCODE(BM_OP_FTM_REPORT),
                                 BM_GROUP_FTM_REPORT, 7, payload, 8)) {
            (*valid_count)++;
            ESP_LOGI(TAG, "reported anchor %u distance %u cm", anchor->id,
                     report.distance_cm);
        } else {
            ESP_LOGW(TAG, "anchor %u measurement was not sent", anchor->id);
        }
    }

    WiFi.disconnect(true, false);
    delay(200);
    return measured;
}

static void positioning_task(void *argument) {
    (void)argument;
    ESP_ERROR_CHECK(esp_event_handler_register(
        WIFI_EVENT, WIFI_EVENT_FTM_REPORT, &ftm_event_handler, NULL));

    uint8_t batch_id = 0;
    bool first = true;
    for (;;) {
        uint8_t valid_count = 0;
        uint32_t cycle_start = millis();
        // Start probing at the beginning of the cycle.  Starting after a slow
        // anchor scan made the reports miss the server's 30 s FTM batch
        // window exactly when the fallback was needed.
        bm_neighbor_link_begin_collection(cycle_start);
        for (size_t i = 0; i < sizeof(BM_ANCHORS) / sizeof(BM_ANCHORS[0]); ++i) {
            measure_anchor(&BM_ANCHORS[i], batch_id, &valid_count);
        }

        if (valid_count <= 2) {
            uint32_t elapsed = (uint32_t)(millis() - cycle_start);
            if (elapsed < BM_NEIGHBOR_PROBE_WINDOW_MS) {
                vTaskDelay(pdMS_TO_TICKS(BM_NEIGHBOR_PROBE_WINDOW_MS - elapsed + 500u));
            }
            bm_neighbor_link_finish_collection(batch_id);
        } else {
            bm_neighbor_link_abort_collection();
            ESP_LOGI(TAG, "FTM success=%u, discarding BLE neighbour probes", valid_count);
        }

        batch_id = (uint8_t)(batch_id + 1u);
        uint32_t delay_ms = first ? 5000u : (180000u + (esp_random() % 30001u));
        first = false;
        ESP_LOGI(TAG, "cycle complete; next in %lu ms", (unsigned long)delay_ms);
        vTaskDelay(pdMS_TO_TICKS(delay_ms));
    }
}

void bm_positioning_start(void) {
    s_ftm_done = xSemaphoreCreateBinary();
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    /* BLE and Wi-Fi coexistence requires Wi-Fi modem sleep on ESP32-C3. */
    WiFi.setSleep(true);
    xTaskCreate(positioning_task, "positioning", 8192, NULL, 4, NULL);
}
