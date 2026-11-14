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

/* Gateway Provisioners use the low unicast range for their own element and
 * the node range below for balloons.  The server reserves node addresses per
 * Provisioner in chunks (64 initially, then 16). */
#define BM_DEV_BALLOON_ADDR_MIN 0x0100u
#define BM_DEV_BALLOON_ADDR_MAX 0x3FFFu
#define BM_DEV_GATEWAY_ADDR_MIN 0x0001u
#define BM_DEV_GATEWAY_ADDR_MAX 0x00FFu

#ifndef BM_GATEWAY_PROVISIONER
#define BM_GATEWAY_PROVISIONER 1
#endif

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
#define BM_ANCHOR_CONNECT_TIMEOUT_MS 2500u

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
#define BM_GATEWAY_ID ""
#endif
/* Server base URL.  Production gateways must use https://; set it with the
 * BM_SERVER_URL environment variable (see platformio.ini). */
#ifndef BM_SERVER_URL
#define BM_SERVER_URL ""
#endif
/* CA certificate (PEM) that signs the server certificate.  Provide it either
 * through build_flags or by creating the git-ignored header
 * firmware/shared/balloon_common/include/bm_server_ca.h containing:
 *   #define BM_TLS_CA_CERT "-----BEGIN CERTIFICATE-----\n...\n-----END CERTIFICATE-----\n"
 */
#if !defined(BM_TLS_CA_CERT) && defined(__has_include)
#if __has_include("bm_server_ca.h")
#include "bm_server_ca.h"
#endif
#endif
/* Default: the server certificate is issued by Let's Encrypt, so trust its
 * roots (ISRG Root X1 RSA + X2 ECDSA).  The host name is still verified. */
#ifndef BM_TLS_CA_CERT
#include "bm_letsencrypt_roots.h"
#define BM_TLS_CA_CERT BM_LETSENCRYPT_ROOTS_PEM
#endif
/* Plain HTTP/WS or unverified TLS is only allowed in explicit development
 * builds.  Everything else must verify the server certificate chain and host
 * name, because the gateway API key and Mesh network keys cross this link. */
#ifndef BM_ALLOW_INSECURE_TRANSPORT
#define BM_ALLOW_INSECURE_TRANSPORT 0
#endif
#if defined(BM_ROLE_GATEWAY) && !BM_ALLOW_INSECURE_TRANSPORT && !defined(BM_TLS_CA_CERT)
#error "Gateway builds require BM_TLS_CA_CERT (see bm_config.h) unless BM_ALLOW_INSECURE_TRANSPORT=1"
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
