/* Compile-time deployment settings for the prototype firmware. */
#pragma once

#include "esp_err.h"

#define BM_FIRMWARE_VERSION "0.1.0"

/* Normal balloon firmware includes Protocol A / FTM positioning.  The
 * BLE-Mesh-only bring-up profile overrides this at build time. */
#ifndef BM_ENABLE_POSITIONING
#define BM_ENABLE_POSITIONING 1
#endif

/* Bluetooth Mesh addressing.  Production deployments should provision these
 * values rather than compiling them in. */
#define BM_COMPANY_ID_LOCAL 0x02E5u
#define BM_NET_IDX 0u
#define BM_APP_IDX 0u
#define BM_MODEL_ID_CONTROL 0x0001u
#define BM_MODEL_ID_TELEMETRY 0x0002u

/* Two broadcast groups by receiver role; individual nodes use unicast addresses.
 * Each model subscribes to only one of them (well under the library's
 * CONFIG_BLE_MESH_MODEL_GROUP_COUNT limit of 3). */
#define BM_GROUP_BALLOON 0xC010u /* balloon-facing: LED, time, neighbour probe */
#define BM_GROUP_GATEWAY 0xC011u /* gateway-facing: FTM/neighbour/queue reports */

#define BM_GROUP_FTM_REPORT BM_GROUP_GATEWAY
#define BM_GROUP_NEIGHBOR_REPORT BM_GROUP_GATEWAY
#define BM_GROUP_QUEUE_STATUS BM_GROUP_GATEWAY
#define BM_GROUP_NEIGHBOR_PROBE BM_GROUP_BALLOON
#define BM_GROUP_TIME BM_GROUP_BALLOON
#define BM_GROUP_LED_ALL BM_GROUP_BALLOON

/* Provisioning identity (PB-ADV/PB-GATT) is generated per device from the
 * Bluetooth MAC by bm_device_uuid(); do not hard-code a shared UUID here. */

/* Optional development address override.  Zero derives a stable address from
 * the device Bluetooth MAC instead of requiring per-device build flags. */
#ifndef BM_DEV_UNICAST_ADDR
#define BM_DEV_UNICAST_ADDR 0u
#endif

/* Separate development ranges keep gateway and balloon addresses apart. */
#define BM_DEV_BALLOON_ADDR_MIN 0x0100u
#define BM_DEV_BALLOON_ADDR_MAX 0x3FFFu
#define BM_DEV_GATEWAY_ADDR_MIN 0x4000u
#define BM_DEV_GATEWAY_ADDR_MAX 0x7FFFu

/* XIAO ESP32C3 BOOT button.  The button is active-low and is checked after
 * normal application startup, not while the ROM bootloader samples straps. */
#ifndef BM_BOOT_BUTTON_GPIO
#define BM_BOOT_BUTTON_GPIO 9
#endif
#ifndef BM_NVS_RESET_HOLD_MS
#define BM_NVS_RESET_HOLD_MS 3000u
#endif

/* Anchor SoftAP and Protocol A. */
#define BM_ANCHOR_SSID_PREFIX "BM-ANCHOR-"
#define BM_ANCHOR_WIFI_PASSWORD "balloonmesh"
#define BM_ANCHOR_AP_IP "192.168.4.1"
#define BM_ANCHOR_UDP_PORT 3232
#define BM_ANCHOR_TOKEN_BYTES { 'B', 'A', 'L', 'L', 'O', 'O', 'N', '1' }
#define BM_ANCHOR_MAX 8u
#define BM_ANCHOR_WIFI_CHANNEL 6u

typedef struct {
    uint8_t id;
    char ssid[32];
} bm_anchor_entry_t;

static const bm_anchor_entry_t BM_ANCHORS[] = {
    { 1, "BM-ANCHOR-1" },
    { 2, "BM-ANCHOR-2" },
    { 3, "BM-ANCHOR-3" },
    { 4, "BM-ANCHOR-4" },
};

/* LED PWM pins (Seeed XIAO ESP32C3: D0=GPIO2, D1=GPIO3, D2=GPIO4). */
#define BM_LED_R_GPIO 2
#define BM_LED_G_GPIO 3
#define BM_LED_B_GPIO 4
#define BM_LED_PWM_FREQ_HZ 5000
#define BM_LED_PWM_RESOLUTION 8

/* Perceptual gamma applied to the physical LED output only.  Linear duty
 * makes low colour values look far brighter than intended (the eye is
 * non-linear and 1W-class chips stay bright even at small duty), so the
 * commanded 0-255 value is mapped through (value/255)^gamma before it
 * reaches LEDC.  Colours stored in the queue/DB/NVS stay linear 0-255;
 * only bm_led_output_set() applies the curve.  Override per deployment
 * with -DBM_LED_GAMMA=0 (linear) or e.g. -DBM_LED_GAMMA=2.8. */
#ifndef BM_LED_GAMMA
#define BM_LED_GAMMA 2.2f
#endif

/* Anchor UART (NMEA). */
#define BM_GPS_UART_PORT 1
#define BM_GPS_TX_GPIO 6
#define BM_GPS_RX_GPIO 7
#define BM_GPS_BAUD 9600

/* Gateway configuration (override with -D in platformio.ini). */
#ifndef BM_GATEWAY_TOKEN
#define BM_GATEWAY_TOKEN ""
#endif
#ifndef BM_GATEWAY_ID
#define BM_GATEWAY_ID "gw-01"
#endif
#ifndef BM_SERVER_URL
#define BM_SERVER_URL "http://192.168.1.10:5080"
#endif
/* Optional PEM CA bundle for HTTPS gateways. Define through build_flags. */
#ifndef BM_TLS_CA_CERT
/* #define BM_TLS_CA_CERT "-----BEGIN CERTIFICATE-----\n..." */
#endif
#ifndef BM_GATEWAY_WIFI_SSID
#define BM_GATEWAY_WIFI_SSID "balloon-mesh"
#endif
#ifndef BM_GATEWAY_WIFI_PASSWORD
#define BM_GATEWAY_WIFI_PASSWORD "balloonmesh"
#endif

/* Neighbour probe timing. */
#define BM_NEIGHBOR_PROBE_WINDOW_MS 20000u
#define BM_PROBE_RESPONSE_SPAN_MS 19000u
