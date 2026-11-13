#include "gateway_ws.h"

#include <Arduino.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <Arduino.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_websocket_client.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "bm_config.h"
#include "gateway_mesh.h"
#include "gateway_spool.h"

#if defined(BM_GATEWAY_USE_WEBSOCKET) && BM_GATEWAY_USE_WEBSOCKET

static const char *TAG = "gateway-ws";
static esp_websocket_client_handle_t s_client;
static char s_uri[224];
static volatile bool s_connected;

static int hex_value(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static size_t hex_decode(const char *text, uint8_t *out, size_t out_size) {
    size_t length = strlen(text);
    if ((length & 1u) != 0u || length / 2 > out_size) {
        return 0;
    }
    for (size_t i = 0; i < length / 2; ++i) {
        int high = hex_value(text[i * 2]);
        int low = hex_value(text[i * 2 + 1]);
        if (high < 0 || low < 0) {
            return 0;
        }
        out[i] = (uint8_t)((high << 4) | low);
    }
    return length / 2;
}

static void hex_encode(const uint8_t *data, size_t length, char *out, size_t out_size) {
    static const char digits[] = "0123456789ABCDEF";
    size_t position = 0;
    for (size_t i = 0; i < length && position + 2 < out_size; ++i) {
        out[position++] = digits[data[i] >> 4];
        out[position++] = digits[data[i] & 0x0F];
    }
    out[position] = '\0';
}

static void build_uri(void) {
    const char *url = BM_SERVER_URL;
    const char *rest = NULL;
    const char *scheme = NULL;
    if (strncmp(url, "https://", 8) == 0) {
        scheme = "wss://";
        rest = url + 8;
    } else if (strncmp(url, "http://", 7) == 0) {
        scheme = "ws://";
        rest = url + 7;
    } else {
        scheme = "ws://";
        rest = url;
    }
    size_t rest_len = strlen(rest);
    while (rest_len > 0 && rest[rest_len - 1] == '/') {
        rest_len--;
    }
    snprintf(s_uri, sizeof(s_uri), "%s%.*s/ws/gateway", scheme, (int)rest_len, rest);
}

static bool send_hello(void) {
    char hello[256];
    snprintf(hello, sizeof(hello),
             "{\"type\":\"hello\",\"gatewayId\":\"%s\",\"firmware\":\"%s\","
             "\"token\":\"%s\",\"provisioned\":%s,\"spoolPending\":%u}",
             BM_GATEWAY_ID, BM_FIRMWARE_VERSION, BM_GATEWAY_TOKEN,
             bm_gateway_mesh_is_ready() ? "true" : "false",
             (unsigned)bm_gateway_spool_count());
    return esp_websocket_client_send_text(s_client, hello, (int)strlen(hello),
                                          pdMS_TO_TICKS(3000)) >= 0;
}

static void handle_downlink(const char *text, int length) {
    char *copy = (char *)malloc((size_t)length + 1);
    if (copy == NULL) {
        return;
    }
    memcpy(copy, text, (size_t)length);
    copy[length] = '\0';
    cJSON *root = cJSON_Parse(copy);
    free(copy);
    if (root == NULL) {
        return;
    }
    cJSON *type = cJSON_GetObjectItem(root, "type");
    if (cJSON_IsString(type) && type->valuestring != NULL &&
        strcmp(type->valuestring, "mesh") == 0) {
        cJSON *dst = cJSON_GetObjectItem(root, "dst");
        cJSON *opcode = cJSON_GetObjectItem(root, "opcode");
        cJSON *ttl = cJSON_GetObjectItem(root, "ttl");
        cJSON *payload = cJSON_GetObjectItem(root, "payload");
        if (cJSON_IsNumber(dst) && cJSON_IsNumber(opcode)) {
            uint8_t data[8];
            size_t data_len = 0;
            if (cJSON_IsString(payload) && payload->valuestring != NULL) {
                data_len = hex_decode(payload->valuestring, data, sizeof(data));
            }
            uint8_t ttl_value = cJSON_IsNumber(ttl) ? (uint8_t)ttl->valueint : 7;
            ESP_LOGI(TAG, "downlink dst=0x%04x opcode=0x%06lx len=%u",
                     (unsigned)dst->valueint, (unsigned long)opcode->valuedouble,
                     (unsigned)data_len);
            Serial.printf("[COMMAND RECEIVED] dst=0x%04x opcode=0x%06lx payload=%s len=%u\n",
                          (unsigned)dst->valueint, (unsigned long)opcode->valuedouble,
                          (cJSON_IsString(payload) && payload->valuestring) ? payload->valuestring : "",
                          (unsigned)data_len);
            Serial.flush();
            bool ok = bm_gateway_mesh_send((uint16_t)dst->valueint,
                                           (uint32_t)opcode->valuedouble,
                                           ttl_value, data, (uint16_t)data_len);
            Serial.printf("[MESH SEND] result=%d\n", (int)ok);
            Serial.flush();
        }
    }
    cJSON_Delete(root);
}

static void websocket_event_handler(void *arg, esp_event_base_t base, int32_t id, void *data) {
    (void)arg;
    (void)base;
    auto *event = (esp_websocket_event_data_t *)data;
    switch ((esp_websocket_event_id_t)id) {
        case WEBSOCKET_EVENT_CONNECTED:
            ESP_LOGI(TAG, "connected to %s", s_uri);
            Serial.printf("[GW WS] Connected to %s!\n", s_uri);
            Serial.flush();
            send_hello();
            s_connected = true;
            break;
        case WEBSOCKET_EVENT_DISCONNECTED:
        case WEBSOCKET_EVENT_CLOSED:
            s_connected = false;
            ESP_LOGW(TAG, "disconnected; auto-reconnect active");
            Serial.println("[GW WS] Disconnected, auto-reconnecting...");
            Serial.flush();
            break;
        case WEBSOCKET_EVENT_ERROR:
            s_connected = false;
            ESP_LOGW(TAG, "websocket error");
            Serial.println("[GW WS] WebSocket error");
            Serial.flush();
            break;
        case WEBSOCKET_EVENT_DATA:
            if (event != NULL && event->op_code == 0x01 && event->data_ptr != NULL &&
                event->payload_offset == 0 && event->payload_len == event->data_len) {
                handle_downlink(event->data_ptr, event->data_len);
            }
            break;
        default:
            break;
    }
}

static void ws_sender_task(void *argument) {
    (void)argument;
    bool last_ready = false;
    for (;;) {
        if (!s_connected || s_client == NULL) {
            last_ready = false;
            vTaskDelay(pdMS_TO_TICKS(500));
            continue;
        }
        // The initial hello is sent before provisioning/AppKey binding may
        // finish.  Send a fresh hello when readiness changes so the server
        // starts routing downlinks without waiting for a reconnect.  Only
        // commit the transition after a successful send.
        bool ready = bm_gateway_mesh_is_ready();
        if (ready != last_ready && send_hello()) {
            last_ready = ready;
        }
        bm_gateway_event_t event;
        uint32_t sequence = 0;
        if (!bm_gateway_spool_peek_seq(0, &event, &sequence)) {
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }
        char hex[17];
        uint32_t now_ms = (uint32_t)(esp_timer_get_time() / 1000);
        uint32_t age_ms = event.received_ms == 0
                              ? 120000u
                              : now_ms - event.received_ms;
        hex_encode(event.payload, event.length, hex, sizeof(hex));
        char json[352];
        int length = snprintf(json, sizeof(json),
                              "{\"type\":\"mesh\",\"src\":%u,\"dst\":%u,\"opcode\":%lu,"
                              "\"payload\":\"%s\",\"rssi\":%d,\"ttl\":%u,\"ageMs\":%lu}",
                              event.src, event.dst, (unsigned long)event.opcode, hex,
                              event.rssi, event.ttl, (unsigned long)age_ms);
        if (length > 0 && length < (int)sizeof(json)) {
            int sent = esp_websocket_client_send_text(s_client, json, length,
                                                      pdMS_TO_TICKS(3000));
            if (sent < 0) {
                ESP_LOGW(TAG, "send failed; retaining event");
            } else {
                bm_gateway_spool_drop_through(sequence);
            }
        }
    }
}

void bm_gateway_ws_start(void) {
    build_uri();
    esp_websocket_client_config_t config = {};
    config.uri = s_uri;
    config.disable_auto_reconnect = false;
    config.ping_interval_sec = 15;
    config.pingpong_timeout_sec = 30;
    config.skip_cert_common_name_check = true;
#ifdef BM_TLS_CA_CERT
    config.cert_pem = BM_TLS_CA_CERT;
#else
    ESP_LOGW(TAG, "no BM_TLS_CA_CERT; WSS certificate is not pinned");
#endif
    s_client = esp_websocket_client_init(&config);
    if (s_client == NULL) {
        ESP_LOGE(TAG, "websocket init failed");
        return;
    }
    esp_websocket_register_events(s_client, WEBSOCKET_EVENT_ANY,
                                  websocket_event_handler, NULL);
    esp_err_t err = esp_websocket_client_start(s_client);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "websocket start failed: %s", esp_err_to_name(err));
        return;
    }
    xTaskCreate(ws_sender_task, "gateway_ws_tx", 6144, NULL, 4, NULL);
    ESP_LOGI(TAG, "websocket transport started: %s", s_uri);
}

#else

void bm_gateway_ws_start(void) {
}

#endif
