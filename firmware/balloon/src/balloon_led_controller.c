#include "balloon_led_controller.h"

#include <stdlib.h>
#include <string.h>

#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#include "balloon_clock.h"
#include "balloon_keepalive.h"
#include "bm_led_output.h"
#include "balloon_persistence.h"
#include "balloon_protocol.h"

static const char *TAG = "ledq";

typedef struct {
    SemaphoreHandle_t lock;
    bm_led_queue_t queue;
    bm_queue_monitor_t monitor;
    bm_queue_status_sender_t sender;
    bool initialized;
    bool dirty;
    uint32_t last_save_ms;
    bm_keepalive_t keepalive;
} bm_led_controller_t;

static bm_led_controller_t s_controller;

static void send_monitor_event_locked(int event, uint16_t count) {
    if (event == 0 || s_controller.sender == NULL) {
        return;
    }
    uint8_t kind = event == 1 ? BM_QUEUE_STATUS_NEAR_FULL : BM_QUEUE_STATUS_RECOVERED;
    s_controller.sender(kind, count);
}

static void mark_dirty_locked(void) {
    s_controller.dirty = true;
}

static void refresh_monitor_locked(void) {
    int event = bm_queue_monitor_update(&s_controller.monitor, s_controller.queue.count);
    send_monitor_event_locked(event, s_controller.queue.count);
}

/* Caller holds the controller lock so pulses cannot overwrite a new command. */
static void update_output_locked(uint32_t now_ms, bool force) {
    bm_rgb_t rgb = s_controller.queue.current;
    bool lit = rgb.r != 0 || rgb.g != 0 || rgb.b != 0;
    bool changed = bm_keepalive_update(&s_controller.keepalive, now_ms, lit);
    if (force || changed) {
        if (s_controller.keepalive.active) {
            rgb.r = rgb.g = rgb.b = 38; /* 15% of 255, rounded */
        }
        bm_led_output_set(rgb.r, rgb.g, rgb.b);
    }
}

static void led_task(void *argument) {
    (void)argument;
    bm_led_entry_t entry;
    for (;;) {
        bool changed = false;
        bool has_output = false;
        bm_led_queue_t *snapshot = NULL;
        bm_rgb_t output = {0, 0, 0};

        if (xSemaphoreTake(s_controller.lock, pdMS_TO_TICKS(50)) == pdTRUE) {
            if (bm_clock_is_synced()) {
                uint64_t now = bm_clock_now_tick40();
                for (int i = 0; i < 32; ++i) {
                    if (!bm_ledq_take_due(&s_controller.queue, now, &entry)) {
                        break;
                    }
                    output.r = entry.r;
                    output.g = entry.g;
                    output.b = entry.b;
                    has_output = true;
                    changed = true;
                    mark_dirty_locked();
                }
                if (changed) {
                    refresh_monitor_locked();
                }
            }

            uint32_t now_ms = (uint32_t)(esp_timer_get_time() / 1000);
            if (s_controller.dirty &&
                (uint32_t)(now_ms - s_controller.last_save_ms) >= 2000u) {
                snapshot = (bm_led_queue_t *)malloc(sizeof(*snapshot));
                if (snapshot != NULL) {
                    *snapshot = s_controller.queue;
                    s_controller.dirty = false;
                    s_controller.last_save_ms = now_ms;
                }
            }
            update_output_locked(now_ms, has_output);
            xSemaphoreGive(s_controller.lock);
        }

        if (has_output) {
            ESP_LOGI(TAG, "executed RGB %u,%u,%u", output.r, output.g, output.b);
        }

        if (snapshot != NULL) {
            bool saved = bm_persistence_save_queue(snapshot);
            free(snapshot);
            if (!saved &&
                xSemaphoreTake(s_controller.lock, pdMS_TO_TICKS(50)) == pdTRUE) {
                s_controller.dirty = true;
                xSemaphoreGive(s_controller.lock);
            }
        }
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

void bm_led_controller_init(bm_queue_status_sender_t sender) {
    if (s_controller.initialized) {
        return;
    }
    memset(&s_controller, 0, sizeof(s_controller));
    s_controller.lock = xSemaphoreCreateMutex();
    bm_ledq_init(&s_controller.queue);
    if (bm_persistence_load_queue(&s_controller.queue)) {
        bm_led_output_set(s_controller.queue.current.r,
                          s_controller.queue.current.g,
                          s_controller.queue.current.b);
    }
    s_controller.sender = sender;
    s_controller.last_save_ms = (uint32_t)(esp_timer_get_time() / 1000);
    s_controller.keepalive.last_start_ms = s_controller.last_save_ms;
    s_controller.initialized = true;
    xTaskCreate(led_task, "led_queue", 4096, NULL, 6, NULL);
}

void bm_led_controller_handle_opcode(uint32_t opcode,
                                     const uint8_t *data,
                                     uint16_t length) {
    if (!s_controller.initialized || data == NULL) {
        return;
    }
    bool synced = bm_clock_is_synced();
    uint64_t now = synced ? bm_clock_now_tick40() : 0;
    bool emit_output = false;

    if (xSemaphoreTake(s_controller.lock, pdMS_TO_TICKS(100)) != pdTRUE) {
        ESP_LOGW(TAG, "queue lock timeout");
        return;
    }

    bm_ledq_result_t result = BM_LEDQ_INVALID;
    if (opcode == BM_VENDOR_OPCODE(BM_OP_LED_SET)) {
        bm_led_set_t value;
        if (bm_decode_led_set(data, length, &value)) {
            bm_rgb_t rgb = {value.r, value.g, value.b};
            result = bm_ledq_set(&s_controller.queue, value.execute_tick, rgb, now, synced);
        }
    } else if (opcode == BM_VENDOR_OPCODE(BM_OP_LED_CANCEL)) {
        uint64_t tick = 0;
        if (bm_decode_led_cancel(data, length, &tick)) {
            result = bm_ledq_cancel(&s_controller.queue, tick, now, synced);
        }
    } else if (opcode == BM_VENDOR_OPCODE(BM_OP_LED_RESET_ALL)) {
        uint64_t cutoff = 0;
        if (bm_decode_led_reset_all(data, length, &cutoff)) {
            result = bm_ledq_reset(&s_controller.queue, cutoff);
        }
    } else if (opcode == BM_VENDOR_OPCODE(BM_OP_LED_SET_NOW)) {
        bm_led_set_now_t value;
        if (bm_decode_led_set_now(data, length, &value)) {
            bm_rgb_t rgb = {value.r, value.g, value.b};
            result = bm_ledq_set_now(&s_controller.queue, value.issued_tick, rgb);
            if (result == BM_LEDQ_APPLIED) {
                emit_output = true;
            }
        }
    } else if (opcode == BM_VENDOR_OPCODE(BM_OP_LED_RESET_AND_SET)) {
        uint64_t cutoff = 0;
        bm_rgb_t rgb = {0, 0, 0};
        if (bm_decode_led_reset_and_set(data, length, &cutoff, &rgb)) {
            result = bm_ledq_reset_and_set(&s_controller.queue, cutoff, rgb);
            if (result == BM_LEDQ_APPLIED) {
                emit_output = true;
            }
        }
    }

    if (result == BM_LEDQ_APPLIED || result == BM_LEDQ_UPDATED) {
        mark_dirty_locked();
        refresh_monitor_locked();
    }
    uint16_t count = s_controller.queue.count;
    if (emit_output) {
        update_output_locked((uint32_t)(esp_timer_get_time() / 1000), true);
    }
    xSemaphoreGive(s_controller.lock);
    if (result != BM_LEDQ_INVALID) {
        ESP_LOGI(TAG, "opcode=0x%06lx result=%d queue=%u", (unsigned long)opcode,
                 (int)result, count);
    }
}

void bm_led_controller_on_clock_sync(uint64_t old_tick,
                                     uint64_t new_tick,
                                     bool was_synced) {
    if (!s_controller.initialized) {
        return;
    }
    if (xSemaphoreTake(s_controller.lock, pdMS_TO_TICKS(200)) != pdTRUE) {
        return;
    }
    uint16_t discarded = was_synced
        ? bm_ledq_on_resync(&s_controller.queue, old_tick, new_tick)
        : bm_ledq_on_first_sync(&s_controller.queue, new_tick);
    mark_dirty_locked();
    refresh_monitor_locked();
    uint16_t count = s_controller.queue.count;
    xSemaphoreGive(s_controller.lock);
    ESP_LOGI(TAG, "clock sync discarded=%u queue=%u", discarded, count);
}

void bm_led_controller_get_state(bm_led_controller_state_t *out) {
    if (out == NULL) {
        return;
    }
    if (!s_controller.initialized) {
        memset(out, 0, sizeof(*out));
        return;
    }
    if (xSemaphoreTake(s_controller.lock, pdMS_TO_TICKS(100)) == pdTRUE) {
        out->count = s_controller.queue.count;
        out->current = s_controller.queue.current;
        out->clock_synced = s_controller.queue.clock_synced;
        out->has_cutoff = s_controller.queue.has_cutoff;
        out->cutoff_tick = s_controller.queue.cutoff_tick;
        out->near_full = s_controller.monitor.near_full;
        xSemaphoreGive(s_controller.lock);
    }
}
