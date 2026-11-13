#pragma once

#include <Arduino.h>

#include "bm_config.h"
#include "esp_attr.h"
#include "esp_log.h"
#include "esp_system.h"
#include "nvs_flash.h"

#define BM_NVS_RESET_MAGIC 0x424D4E56u

/* RTC no-init memory survives esp_restart(), allowing a runtime button press
 * to request an NVS erase before the next application starts. */
static RTC_NOINIT_ATTR uint32_t bm_nvs_reset_magic;

static inline bool bm_nvs_reset_pending(void) {
    return bm_nvs_reset_magic == BM_NVS_RESET_MAGIC;
}

static inline void bm_nvs_reset_clear(void) {
    bm_nvs_reset_magic = 0;
}

/* The XIAO ESP32C3 BOOT button is active-low on GPIO9.  Call this from setup
 * and periodically from loop; holding it during reset enters the ROM download
 * mode before this code can run. */
static inline void bm_check_boot_button_nvs_reset(const char *tag) {
    pinMode(BM_BOOT_BUTTON_GPIO, INPUT_PULLUP);
    if (digitalRead(BM_BOOT_BUTTON_GPIO) != LOW) {
        return;
    }

    ESP_LOGW(tag, "BOOT held; release before %u ms to cancel NVS reset",
             (unsigned)BM_NVS_RESET_HOLD_MS);
    uint32_t start = millis();
    while (digitalRead(BM_BOOT_BUTTON_GPIO) == LOW &&
           (uint32_t)(millis() - start) < BM_NVS_RESET_HOLD_MS) {
        delay(10);
    }
    if (digitalRead(BM_BOOT_BUTTON_GPIO) != LOW) {
        ESP_LOGI(tag, "BOOT NVS reset canceled");
        return;
    }

    ESP_LOGW(tag, "BOOT held for %u ms; scheduling NVS erase and restarting",
             (unsigned)BM_NVS_RESET_HOLD_MS);
    bm_nvs_reset_magic = BM_NVS_RESET_MAGIC;
    delay(100);
    esp_restart();
}
