#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Builds a stable per-device provisioning UUID from the Bluetooth MAC.
 * The first 10 bytes identify BalloonMesh; the last 6 bytes are the MAC. */
void bm_device_uuid(uint8_t uuid[16]);

/* Derives a stable development unicast address from the Bluetooth MAC.
 * The caller supplies a role-specific valid unicast range. */
uint16_t bm_device_unicast_addr(uint16_t min_addr, uint16_t max_addr);

/* Returns the configured gateway ID or a stable MAC-derived ID. */
const char *bm_device_gateway_id(void);

/* False means neither the configured ID nor the device MAC was available. */
bool bm_device_gateway_id_is_valid(void);

#ifdef __cplusplus
}
#endif
