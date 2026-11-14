#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "bm_config.h"
#include "esp_err.h"

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

/* Mesh NetKey/AppKey delivered by the server.  Must be called before
 * bm_gateway_mesh_init() on Provisioner builds; development builds ignore it
 * and use the shared test keys. */
void bm_gateway_mesh_set_network_keys(const uint8_t net_key[16], const uint8_t app_key[16]);

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
/* Starts provisioning one device after the previous provisioning link has
 * closed (or link_timeout_ms elapsed).  Blocks the caller while waiting. */
esp_err_t bm_gateway_mesh_provision_device(const uint8_t uuid[16],
                                           const uint8_t addr[6],
                                           uint8_t addr_type,
                                           uint8_t bearer,
                                           uint16_t oob_info,
                                           uint16_t unicast_addr,
                                           uint32_t link_timeout_ms);

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
