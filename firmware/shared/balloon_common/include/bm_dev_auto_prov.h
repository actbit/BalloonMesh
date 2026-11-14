#pragma once

#include "bm_config.h"

/*
 * Development-only Mesh bring-up.
 *
 * The fixed NetKey/AppKey/DevKey below exist ONLY in BM_DEV_AUTO_PROV builds
 * (gateway-dev / balloon-mesh-bringup), so a production image never contains
 * a key that is shared by every device.  Production networks get their
 * NetKey/AppKey from the server (generated per deployment, see
 * DataStore.GetOrCreateMeshNetworkKeys) and each node receives its own
 * DevKey through PB-ADV/PB-GATT provisioning (ECDH).
 */
#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV

#if defined(BM_PRODUCTION_BUILD) && BM_PRODUCTION_BUILD
#error "BM_DEV_AUTO_PROV embeds shared development Mesh keys and must not be enabled in a production build"
#endif

#include <stdint.h>
#include <string.h>
#include "esp_log.h"
#include "esp_ble_mesh_defs.h"
#include "esp_ble_mesh_provisioning_api.h"
#include "test.h"
#include "mesh_main.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Development test NetKey, AppKey, DevKey (16 bytes each) */
static const uint8_t BM_DEV_NET_KEY[16] = {
    0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99,
    0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x00, 0x11
};

static const uint8_t BM_DEV_APP_KEY[16] = {
    0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0,
    0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0
};

static const uint8_t BM_DEV_DEV_KEY[16] = {
    0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
    0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10
};

/**
 * Automatically enters the Bluetooth Mesh network using ESP-IDF test API.
 * Ensures the node is provisioned, AppKey is bound to all models, and
 * group_addr is subscribed.
 */
static inline int bm_dev_auto_provision(uint16_t unicast_addr, uint16_t group_addr) {
    ESP_LOGW("bm_dev_prov", "DEVELOPMENT build: joining Mesh with shared test keys");
    ESP_LOGI("bm_dev_prov", "bm_dev_auto_provision entering: prov=%d addr=0x%04x",
             (int)esp_ble_mesh_node_is_provisioned(), unicast_addr);

    struct bt_mesh_device_network_info info = {
        .net_idx = BM_NET_IDX,
        .flags = 0,
        .iv_index = 0,
        .unicast_addr = unicast_addr,
        .app_idx = BM_APP_IDX,
        .group_addr = group_addr,
    };
    memcpy(info.net_key, BM_DEV_NET_KEY, 16);
    memcpy(info.app_key, BM_DEV_APP_KEY, 16);
    memcpy(info.dev_key, BM_DEV_DEV_KEY, 16);

    ESP_LOGI("bm_dev_prov", "calling bt_mesh_device_auto_enter_network...");
    int err = bt_mesh_device_auto_enter_network(&info);
    ESP_LOGI("bm_dev_prov", "auto_enter_network addr=0x%04x group=0x%04x result=%d",
             unicast_addr, group_addr, err);
    return err;
}

#ifdef __cplusplus
}
#endif

#endif /* BM_DEV_AUTO_PROV */
