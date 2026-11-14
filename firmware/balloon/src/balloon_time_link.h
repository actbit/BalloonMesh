#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void bm_time_link_init(void);
bool bm_time_link_request(void);
void bm_time_link_on_request(void);
void bm_time_link_on_report(const uint8_t *data, uint16_t length);
/* Called from the mesh send-completion event when an uplink failed on air. */
void bm_time_link_on_send_failed(uint32_t opcode);

#ifdef __cplusplus
}
#endif
