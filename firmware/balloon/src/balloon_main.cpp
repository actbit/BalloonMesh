#include <Arduino.h>

#include "esp_log.h"
#include "nvs_flash.h"

#include "balloon_clock.h"
#include "balloon_led_controller.h"
#include "bm_led_output.h"
#include "balloon_mesh.h"
#include "balloon_neighbor_link.h"
#include "balloon_positioning.h"
#include "balloon_time_link.h"
#include "bm_config.h"

static const char *TAG = "balloon";

void setup() {
    Serial.begin(115200);
    delay(200);
    ESP_LOGI(TAG, "BalloonMesh balloon firmware %s", BM_FIRMWARE_VERSION);

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    bm_clock_init();
    bm_led_output_init();
    bm_led_controller_init(bm_balloon_mesh_publish_queue_status);
    bm_neighbor_link_init();
    bm_time_link_init();
    bm_balloon_mesh_init();
    bm_positioning_start();
}

void loop() {
    static uint32_t last = 0;
    uint32_t now = millis();
    if ((uint32_t)(now - last) >= 30000u) {
        last = now;
        bm_led_controller_state_t state = {};
        bm_led_controller_get_state(&state);
        ESP_LOGI(TAG, "addr=0x%04x provisioned=%d synced=%d queue=%u rgb=%u,%u,%u",
                 bm_balloon_mesh_primary_addr(),
                 (int)bm_balloon_mesh_is_provisioned(),
                 (int)state.clock_synced, state.count,
                 state.current.r, state.current.g, state.current.b);
    }
    delay(1000);
}
