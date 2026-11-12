#include <Arduino.h>
#include <HardwareSerial.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include <string.h>

#include "esp_log.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "balloon_nmea.h"
#include "bm_anchor_protocol.h"
#include "bm_config.h"

#ifndef BM_ANCHOR_ID
#define BM_ANCHOR_ID 1
#endif

static const char *TAG = "anchor";
static const uint8_t s_token[8] = BM_ANCHOR_TOKEN_BYTES;
static HardwareSerial s_gps(BM_GPS_UART_PORT);
static portMUX_TYPE s_state_lock = portMUX_INITIALIZER_UNLOCKED;
static bm_nmea_state_t s_state;

static void gps_task(void *argument) {
    (void)argument;
    char line[160];
    size_t length = 0;
    for (;;) {
        while (s_gps.available() > 0) {
            char c = (char)s_gps.read();
            if (c == '\n') {
                line[length] = '\0';
                if (length > 0) {
                    bm_nmea_state_t parsed;
                    portENTER_CRITICAL(&s_state_lock);
                    parsed = s_state;
                    portEXIT_CRITICAL(&s_state_lock);

                    if (bm_nmea_feed_line(&parsed, line)) {
                        portENTER_CRITICAL(&s_state_lock);
                        s_state = parsed;
                        portEXIT_CRITICAL(&s_state_lock);
                    }
                }
                length = 0;
            } else if (length + 1 < sizeof(line)) {
                line[length++] = c;
            } else {
                length = 0;
            }
        }
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}

static void udp_task(void *argument) {
    (void)argument;

    char id_text[8];
    snprintf(id_text, sizeof(id_text), "%d", BM_ANCHOR_ID);
    char ssid[32];
    snprintf(ssid, sizeof(ssid), "%s%s", BM_ANCHOR_SSID_PREFIX, id_text);

    // Wi-Fi must be initialised before WiFiUDP::begin() creates and binds the
    // lwIP socket; otherwise the bind fails and the anchor silently dies.
    WiFi.mode(WIFI_AP);
    // Configure the AP in a single esp_wifi_set_config() call with
    // ftm_responder set.  Enabling it by re-writing the config after
    // WiFi.softAP() does not stick (the flag reads back as 0) and the anchor
    // then rejects every FTM request (FTM_STATUS_CONF_REJECTED).
    wifi_config_t ap_config = {};
    strncpy((char *)ap_config.ap.ssid, ssid, sizeof(ap_config.ap.ssid) - 1);
    ap_config.ap.ssid_len = (uint8_t)strlen(ssid);
    strncpy((char *)ap_config.ap.password, BM_ANCHOR_WIFI_PASSWORD,
            sizeof(ap_config.ap.password) - 1);
    ap_config.ap.channel = BM_ANCHOR_WIFI_CHANNEL;
    ap_config.ap.authmode = WIFI_AUTH_WPA2_PSK;
    ap_config.ap.max_connection = 4;
    ap_config.ap.ftm_responder = true;
    esp_err_t err = esp_wifi_set_config(WIFI_IF_AP, &ap_config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "SoftAP config failed: %s", esp_err_to_name(err));
    }
    // FTM measurements are invalid ("No valid FTM Measurements") on a 40 MHz
    // link, so keep the AP at 20 MHz; the balloon station does the same.
    esp_wifi_set_bandwidth(WIFI_IF_AP, WIFI_BW_HT20);
    esp_wifi_ftm_resp_set_offset(0);

    WiFiUDP udp;
    while (!udp.begin(BM_ANCHOR_UDP_PORT)) {
        ESP_LOGE(TAG, "UDP bind failed; retrying");
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
    ESP_LOGI(TAG, "UDP anchor data server on port %u", BM_ANCHOR_UDP_PORT);
    ESP_LOGI(TAG, "SoftAP '%s' started, IP=%s, channel=%u, FTM responder",
             ssid, WiFi.softAPIP().toString().c_str(), BM_ANCHOR_WIFI_CHANNEL);

    uint8_t request[BM_ANCHOR_REQUEST_SIZE];
    uint8_t response[BM_ANCHOR_RESPONSE_SIZE];
    for (;;) {
        int size = udp.parsePacket();
        if (size > 0) {
            int read = udp.read(request, sizeof(request));
            if (read == (int)sizeof(request) &&
                bm_anchor_request_validate(request, sizeof(request), s_token)) {
                bm_nmea_state_t state;
                portENTER_CRITICAL(&s_state_lock);
                state = s_state;
                portEXIT_CRITICAL(&s_state_lock);

                uint8_t flags = BM_ANCHOR_FLAG_AUTHENTICATED;
                if (state.time_valid) {
                    flags |= BM_ANCHOR_FLAG_TIME_VALID;
                }
                if (state.position_valid) {
                    flags |= BM_ANCHOR_FLAG_POSITION_VALID;
                }
                size_t length = bm_anchor_response_encode(
                    response, sizeof(response), BM_ANCHOR_ID, flags,
                    state.time_valid ? state.utc_ms : 0,
                    state.lat_e7, state.lon_e7, state.alt_cm,
                    state.h_acc_cm, s_token);
                if (length > 0) {
                    udp.beginPacket(udp.remoteIP(), udp.remotePort());
                    udp.write(response, length);
                    udp.endPacket();
                }
            }
        }
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

void setup() {
    Serial.begin(115200);
    delay(200);
    ESP_LOGI(TAG, "BalloonMesh anchor %d firmware %s", BM_ANCHOR_ID,
             BM_FIRMWARE_VERSION);

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    bm_nmea_init(&s_state);
    s_gps.begin(BM_GPS_BAUD, SERIAL_8N1, BM_GPS_RX_GPIO, BM_GPS_TX_GPIO);
    xTaskCreate(gps_task, "gps", 4096, NULL, 5, NULL);
    xTaskCreate(udp_task, "anchor_udp", 6144, NULL, 6, NULL);
}

void loop() {
    delay(1000);
}
