#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint16_t src;
    uint16_t dst;
    uint32_t opcode;
    uint8_t length;
    uint8_t payload[8];
    int8_t rssi;
    uint8_t ttl;
    uint32_t received_ms;
} bm_gateway_event_t;

/* development_address is supplied by the central dev allocator when the
 * development auto-provisioning profile is enabled; zero disables that path. */
void bm_gateway_mesh_init(uint16_t development_address);
bool bm_gateway_mesh_is_provisioned(void);
/* True only when provisioning is complete and both vendor models are bound to
 * the AppKey.  Downlinks can only be sent in this state. */
bool bm_gateway_mesh_is_ready(void);
uint16_t bm_gateway_mesh_primary_addr(void);
bool bm_gateway_mesh_send(uint16_t dst,
                          uint32_t opcode,
                          uint8_t ttl,
                          const uint8_t *payload,
                          uint16_t length);

#ifdef __cplusplus
}
#endif
