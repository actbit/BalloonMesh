#include "balloon_time_link.h"

#include <string.h>

#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "balloon_clock.h"
#include "balloon_led_controller.h"
#include "balloon_mesh.h"
#include "balloon_protocol.h"
#include "bm_config.h"

static const char *TAG = "time-link";

/* TIME_REQUEST and TIME_REPORT both go to the balloon group, so a naive
 * "answer immediately" turns one request from an unsynced node into N
 * simultaneous replies from every synced node.  A reply is therefore delayed
 * by a random amount, dropped when another node answered in the meantime, and
 * rate limited per node. */
static volatile bool s_reply_armed;
static volatile uint32_t s_reply_at_ms;
static volatile uint32_t s_last_report_seen_ms;
static volatile uint32_t s_last_reply_ms;
static volatile bool s_has_replied;

static uint32_t now_ms(void) {
    return (uint32_t)(esp_timer_get_time() / 1000);
}

static void send_report(void) {
    if (!bm_clock_is_synced()) {
        return;
    }
    int64_t utc_ms = bm_clock_now_ms();
    uint8_t payload[8];
    if (bm_encode_time_report(payload, sizeof(payload), utc_ms) != 8) {
        return;
    }
    if (bm_balloon_mesh_send(BM_VENDOR_OPCODE(BM_OP_TIME_REPORT),
                             BM_GROUP_TIME, 7, payload, 8)) {
        s_last_reply_ms = now_ms();
        s_has_replied = true;
    }
}

static void time_link_task(void *argument) {
    (void)argument;
    for (;;) {
        if (s_reply_armed) {
            uint32_t now = now_ms();
            if ((int32_t)(now - s_reply_at_ms) >= 0) {
                s_reply_armed = false;
                /* Someone else already broadcast the time: the requester has
                 * what it needs and the air stays quiet. */
                bool suppressed =
                    s_last_report_seen_ms != 0 &&
                    (uint32_t)(now - s_last_report_seen_ms) < BM_TIME_REPLY_SUPPRESS_MS;
                if (!suppressed && s_has_replied &&
                    (uint32_t)(now - s_last_reply_ms) < BM_TIME_REPLY_MIN_INTERVAL_MS) {
                    suppressed = true;
                }
                if (!suppressed) {
                    send_report();
                }
            }
        }
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}

void bm_time_link_init(void) {
    xTaskCreate(time_link_task, "time_link", 3072, NULL, 4, NULL);
}

bool bm_time_link_request(void) {
    bool sent = bm_balloon_mesh_send(BM_VENDOR_OPCODE(BM_OP_TIME_REQUEST),
                                     BM_GROUP_TIME, 7, NULL, 0);
    ESP_LOGI(TAG, "time fallback request sent=%d", (int)sent);
    return sent;
}

void bm_time_link_on_request(void) {
    if (!bm_clock_is_synced()) {
        return;
    }
    if (s_reply_armed) {
        return;
    }
    s_reply_at_ms = now_ms() + (esp_random() % BM_TIME_REPLY_JITTER_MS);
    s_reply_armed = true;
}

void bm_time_link_on_send_failed(uint32_t opcode) {
    if (opcode == BM_VENDOR_OPCODE(BM_OP_TIME_REPORT)) {
        /* Nothing went out, so the rate limit must not hold back the next
         * reply to a request. */
        s_has_replied = false;
        s_last_reply_ms = 0;
    }
}

void bm_time_link_on_report(const uint8_t *data, uint16_t length) {
    int64_t utc_ms = 0;
    if (!bm_decode_time_report(data, length, &utc_ms)) {
        return;
    }
    /* Recorded even when this node is already synced: it is what suppresses a
     * pending reply of our own. */
    s_last_report_seen_ms = now_ms();
    if (bm_clock_is_synced()) {
        return;
    }
    uint64_t old_tick = 0;
    uint64_t new_tick = 0;
    bool was_synced = false;
    if (bm_clock_apply_anchor_time(utc_ms, esp_timer_get_time(),
                                   &old_tick, &new_tick, &was_synced)) {
        bm_led_controller_on_clock_sync(old_tick, new_tick, was_synced);
        ESP_LOGI(TAG, "clock synced from Mesh peer");
    }
}
