#include "bm_device_id.h"

#include <stdbool.h>
#include <stdio.h>
#include <string.h>

#include "bm_config.h"
#include "esp_mac.h"

static char s_gateway_id[20];
static bool s_gateway_id_initialized;
static bool s_gateway_id_valid;

static bool read_device_mac(uint8_t mac[6]) {
    if (esp_read_mac(mac, ESP_MAC_BT) == ESP_OK) {
        return true;
    }
    return esp_read_mac(mac, ESP_MAC_WIFI_STA) == ESP_OK;
}

void bm_device_uuid(uint8_t uuid[16]) {
    static const uint8_t prefix[10] = {
        0xB1, 0xA1, 0x10, 0x00, 0x00,
        0x01, 0x11, 0x22, 0x33, 0x44
    };
    uint8_t mac[6] = {0};
    (void)read_device_mac(mac);
    memcpy(uuid, prefix, sizeof(prefix));
    memcpy(uuid + sizeof(prefix), mac, sizeof(mac));
}

uint16_t bm_device_unicast_addr(uint16_t min_addr, uint16_t max_addr) {
    if (min_addr == 0 || min_addr > max_addr || max_addr > 0x7FFFu) {
        return 0;
    }

    uint8_t mac[6] = {0};
    if (!read_device_mac(mac)) {
        return 0;
    }

    /* XIAO boards share the same OUI, so the lower MAC bits are unique for
     * normal lab-sized fleets and avoid random hash collisions.  Role-specific
     * ranges are selected by the caller to avoid cross-role collisions. */
    uint16_t mac_suffix = ((uint16_t)mac[4] << 8) | mac[5];
    uint32_t span = (uint32_t)max_addr - (uint32_t)min_addr + 1u;
    return (uint16_t)(min_addr + (mac_suffix % span));
}

const char *bm_device_gateway_id(void) {
    if (s_gateway_id_initialized) {
        return s_gateway_id_valid ? s_gateway_id : "gw-invalid";
    }
    s_gateway_id_initialized = true;

    if (BM_GATEWAY_ID[0] != '\0') {
        strncpy(s_gateway_id, BM_GATEWAY_ID, sizeof(s_gateway_id) - 1);
        s_gateway_id[sizeof(s_gateway_id) - 1] = '\0';
        s_gateway_id_valid = true;
        return s_gateway_id;
    }

    uint8_t mac[6] = {0};
    if (!read_device_mac(mac)) {
        return "gw-invalid";
    }

    /* Use the full MAC so distinct device MACs produce distinct IDs. */
    snprintf(s_gateway_id, sizeof(s_gateway_id),
             "gw-%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    s_gateway_id_valid = s_gateway_id[0] != '\0';
    return s_gateway_id_valid ? s_gateway_id : "gw-invalid";
}

bool bm_device_gateway_id_is_valid(void) {
    (void)bm_device_gateway_id();
    return s_gateway_id_valid;
}
