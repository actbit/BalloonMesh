#pragma once
#include <stdbool.h>
#include <stdint.h>

/* The interval is measured between pulse starts, and resets while lit. */
typedef struct {
    uint32_t last_start_ms;
    bool active;
} bm_keepalive_t;

static inline bool bm_keepalive_update(bm_keepalive_t *state, uint32_t now_ms,
                                       bool commanded_lit) {
    bool previous = state->active;
    if (commanded_lit) {
        state->active = false;
        state->last_start_ms = now_ms;
    } else if (state->active) {
        if ((uint32_t)(now_ms - state->last_start_ms) >= 1000u) {
            state->active = false;
        }
    } else if ((uint32_t)(now_ms - state->last_start_ms) >= 12000u) {
        state->active = true;
        state->last_start_ms = now_ms;
    }
    return previous != state->active;
}
