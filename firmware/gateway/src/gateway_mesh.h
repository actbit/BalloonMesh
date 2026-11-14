#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "bm_config.h"

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

/* The gateway address is server-assigned.  Provisioner node addresses are
 * requested from the server per device; the range values are returned for
 * diagnostics and future prefetching. */
void bm_gateway_mesh_init(uint16_t development_address,
                          uint16_t provisioner_address,
                          uint16_t node_range_start,
                          uint16_t node_range_end);

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
void bm_gateway_mesh_on_unprov_device(const uint8_t uuid[16],
                                      const uint8_t addr[6],
                                      uint8_t addr_type,
                                      uint16_t oob_info,
                                      uint8_t bearer);
#endif
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
