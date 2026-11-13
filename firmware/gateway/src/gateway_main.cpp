#include <Arduino.h>
#include <HTTPClient.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>

#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "bm_config.h"
#include "bm_device_id.h"
#include "bm_nvs_reset.h"
#include "gateway_mesh.h"
#include "gateway_spool.h"
#include "gateway_ws.h"

static const char *TAG = "gateway";

static int vprintf_to_serial(const char *fmt, va_list args) {
    char buf[256];
    int len = vsnprintf(buf, sizeof(buf), fmt, args);
    if (len > 0) {
        Serial.print(buf);
    }
    return len;
}

/// HTTP/HTTPS transport selection.  Define BM_TLS_CA_CERT in the build flags
/// for certificate validation; without it the prototype uses setInsecure().
class GatewayHttpClient {
public:
    GatewayHttpClient() {
        https_ = strncmp(BM_SERVER_URL, "https://", 8) == 0;
        if (https_) {
#ifdef BM_TLS_CA_CERT
            secure_.setCACert(BM_TLS_CA_CERT);
#else
            secure_.setInsecure();
            ESP_LOGW(TAG, "HTTPS without pinned CA (set BM_TLS_CA_CERT for production)");
#endif
            secure_.setTimeout(15000);
        }
    }

    WiFiClient &client() {
        return https_ ? static_cast<WiFiClient &>(secure_) : plain_;
    }

private:
    bool https_ = false;
    WiFiClient plain_;
    WiFiClientSecure secure_;
};

#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
static void encode_device_uuid(char *out, size_t out_size) {
    uint8_t uuid[16] = {0};
    bm_device_uuid(uuid);
    static const char digits[] = "0123456789ABCDEF";
    size_t position = 0;
    for (size_t i = 0; i < sizeof(uuid) && position + 2 < out_size; ++i) {
        out[position++] = digits[uuid[i] >> 4];
        out[position++] = digits[uuid[i] & 0x0F];
    }
    out[position] = '\0';
}
#endif

static void prepare_wifi(void) {
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    /* ESP32-C3 shares one radio between Wi-Fi and BLE Mesh.  Keep modem sleep
     * enabled for coexistence (the balloon firmware does the same). */
    WiFi.setSleep(true);
}

static bool connect_wifi_once(void) {
    if (WiFi.status() == WL_CONNECTED) {
        return true;
    }

    ESP_LOGI(TAG, "connecting Wi-Fi SSID=%s", BM_GATEWAY_WIFI_SSID);
    Serial.printf("[GW] connecting Wi-Fi SSID=%s\n", BM_GATEWAY_WIFI_SSID);
    Serial.flush();
    WiFi.begin(BM_GATEWAY_WIFI_SSID, BM_GATEWAY_WIFI_PASSWORD);
    uint32_t start = millis();
    while (WiFi.status() != WL_CONNECTED &&
           (uint32_t)(millis() - start) < 15000u) {
        delay(200);
    }
    if (WiFi.status() != WL_CONNECTED) {
        ESP_LOGW(TAG, "Wi-Fi connection failed");
        Serial.println("[GW] Wi-Fi connection failed, retrying...");
        Serial.flush();
        return false;
    }

    ESP_LOGI(TAG, "Wi-Fi connected, IP=%s", WiFi.localIP().toString().c_str());
    Serial.printf("[GW] Wi-Fi connected! IP=%s\n", WiFi.localIP().toString().c_str());
    Serial.flush();
    return true;
}

#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
static uint16_t request_development_mesh_address(void) {
    if (!bm_device_gateway_id_is_valid() || WiFi.status() != WL_CONNECTED) {
        return 0;
    }

    char device_uuid[33];
    encode_device_uuid(device_uuid, sizeof(device_uuid));
    char url[224];
    snprintf(url, sizeof(url), "%s/api/gateway/%s/mesh-address", BM_SERVER_URL,
             bm_device_gateway_id());

    GatewayHttpClient transport;
    HTTPClient http;
    if (!http.begin(transport.client(), url)) {
        ESP_LOGW(TAG, "development Mesh address request could not start");
        return 0;
    }
    http.setTimeout(5000);
    http.addHeader("Content-Type", "application/json");
    if (strlen(BM_GATEWAY_TOKEN) > 0) {
        http.addHeader("X-Gateway-Token", BM_GATEWAY_TOKEN);
    }

    char body[64];
    snprintf(body, sizeof(body), "{\"deviceUuid\":\"%s\"}", device_uuid);
    int code = http.POST((uint8_t *)body, strlen(body));
    if (code < 200 || code >= 300) {
        ESP_LOGW(TAG, "development Mesh address request failed HTTP %d", code);
        http.end();
        return 0;
    }

    String response = http.getString();
    http.end();
    cJSON *root = cJSON_Parse(response.c_str());
    if (root == NULL) {
        ESP_LOGW(TAG, "development Mesh address response was not JSON");
        return 0;
    }
    cJSON *mesh_address = cJSON_GetObjectItem(root, "meshAddress");
    uint16_t result = 0;
    if (cJSON_IsNumber(mesh_address) && mesh_address->valueint >= 0x4000 &&
        mesh_address->valueint <= 0x7FFF) {
        result = (uint16_t)mesh_address->valueint;
    }
    cJSON_Delete(root);
    if (result == 0) {
        ESP_LOGW(TAG, "development Mesh address response was invalid");
    }
    return result;
}
#endif

#if !defined(BM_GATEWAY_USE_WEBSOCKET) || !BM_GATEWAY_USE_WEBSOCKET

static void hex_encode(const uint8_t *data, size_t length, char *out, size_t out_size) {
    static const char digits[] = "0123456789ABCDEF";
    size_t position = 0;
    for (size_t i = 0; i < length && position + 2 < out_size; ++i) {
        out[position++] = digits[data[i] >> 4];
        out[position++] = digits[data[i] & 0x0F];
    }
    out[position] = '\0';
}

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

static bool server_available(void) {
    return WiFi.status() == WL_CONNECTED;
}

static void upload_events_task(void *argument) {
    (void)argument;
    GatewayHttpClient transport;
    for (;;) {
        if (!server_available()) {
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }
        bm_gateway_event_t events[12];
        uint32_t sequences[12];
        uint16_t count = bm_gateway_spool_snapshot(events, sequences, 12);
        uint32_t last_sequence = count > 0 ? sequences[count - 1] : 0;
        if (count == 0) {
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }

        size_t capacity = 512 + (size_t)count * 256;
        char *body = (char *)malloc(capacity);
        if (body == NULL) {
            vTaskDelay(pdMS_TO_TICKS(500));
            continue;
        }
        int written = snprintf(body, capacity,
                               "{\"gatewayId\":\"%s\",\"firmware\":\"%s\","
                               "\"provisioned\":%s,\"spoolPending\":%u,\"events\":[",
                               bm_device_gateway_id(), BM_FIRMWARE_VERSION,
                               bm_gateway_mesh_is_ready() ? "true" : "false",
                               (unsigned)bm_gateway_spool_count());
        bool body_ok = written > 0 && (size_t)written < capacity;
        uint32_t now_ms = (uint32_t)(esp_timer_get_time() / 1000);
        for (uint16_t i = 0; body_ok && i < count; ++i) {
            char hex[17];
            uint32_t age_ms = events[i].received_ms == 0
                                  ? 120000u
                                  : now_ms - events[i].received_ms;
            hex_encode(events[i].payload, events[i].length, hex, sizeof(hex));
            size_t remaining = capacity - (size_t)written;
            int appended = snprintf(body + written, remaining,
                                "%s{\"src\":%u,\"dst\":%u,\"opcode\":%lu,"
                                "\"payload\":\"%s\",\"rssi\":%d,\"ttl\":%u,"
                                "\"receivedMs\":%lu,\"ageMs\":%lu}",
                                i == 0 ? "" : ",",
                                events[i].src, events[i].dst,
                                (unsigned long)events[i].opcode, hex,
                                events[i].rssi, events[i].ttl,
                                (unsigned long)events[i].received_ms,
                                (unsigned long)age_ms);
            if (appended < 0 || (size_t)appended >= remaining) {
                body_ok = false;
                break;
            }
            written += appended;
        }
        if (body_ok) {
            size_t remaining = capacity - (size_t)written;
            int appended = snprintf(body + written, remaining, "]}");
            if (appended < 0 || (size_t)appended >= remaining) {
                body_ok = false;
            }
        }
        if (!body_ok) {
            ESP_LOGW(TAG, "upload body too large; retaining %u events", (unsigned)count);
            free(body);
            vTaskDelay(pdMS_TO_TICKS(500));
            continue;
        }

        char url[192];
        snprintf(url, sizeof(url), "%s/api/gateway/%s/events", BM_SERVER_URL,
                 bm_device_gateway_id());
        HTTPClient http;
        http.setTimeout(5000);
        if (http.begin(transport.client(), url)) {
            http.addHeader("Content-Type", "application/json");
            if (strlen(BM_GATEWAY_TOKEN) > 0) {
                http.addHeader("X-Gateway-Token", BM_GATEWAY_TOKEN);
            }
            int code = http.POST((uint8_t *)body, strlen(body));
            if (code >= 200 && code < 300) {
                bm_gateway_spool_drop_through(last_sequence);
            } else {
                ESP_LOGW(TAG, "upload failed HTTP %d; retaining %u events",
                         code, (unsigned)count);
                vTaskDelay(pdMS_TO_TICKS(500));
            }
            http.end();
        } else {
            ESP_LOGW(TAG, "HTTP begin failed: %s", url);
        }
        free(body);
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}

static void poll_commands_task(void *argument) {
    (void)argument;
    GatewayHttpClient transport;
    for (;;) {
        if (!server_available() || !bm_gateway_mesh_is_ready()) {
            vTaskDelay(pdMS_TO_TICKS(2000));
            continue;
        }
        char url[224];
        snprintf(url, sizeof(url),
                 "%s/api/gateway/%s/commands?waitMs=10000", BM_SERVER_URL,
                 bm_device_gateway_id());
        HTTPClient http;
        http.setTimeout(15000);
        if (!http.begin(transport.client(), url)) {
            vTaskDelay(pdMS_TO_TICKS(1000));
            continue;
        }
        if (strlen(BM_GATEWAY_TOKEN) > 0) {
            http.addHeader("X-Gateway-Token", BM_GATEWAY_TOKEN);
        }
        int code = http.GET();
        if (code == 200) {
            String response = http.getString();
            cJSON *root = cJSON_Parse(response.c_str());
            if (root != NULL) {
                cJSON *commands = cJSON_GetObjectItem(root, "commands");
                if (cJSON_IsArray(commands)) {
                    cJSON *item = NULL;
                    cJSON_ArrayForEach(item, commands) {
                        cJSON *dst = cJSON_GetObjectItem(item, "dst");
                        cJSON *opcode = cJSON_GetObjectItem(item, "opcode");
                        cJSON *ttl = cJSON_GetObjectItem(item, "ttl");
                        cJSON *payload = cJSON_GetObjectItem(item, "payload");
                        if (!cJSON_IsNumber(dst) || !cJSON_IsNumber(opcode)) {
                            continue;
                        }
                        uint8_t data[8];
                        size_t length = 0;
                        if (cJSON_IsString(payload) && payload->valuestring != NULL) {
                            length = hex_decode(payload->valuestring, data, sizeof(data));
                        }
                        uint8_t ttl_value = cJSON_IsNumber(ttl)
                                                ? (uint8_t)ttl->valueint
                                                : 7;
                        ESP_LOGI(TAG, "downlink dst=0x%04x opcode=0x%06lx len=%u",
                                 (unsigned)dst->valueint,
                                 (unsigned long)opcode->valuedouble, (unsigned)length);
                        bm_gateway_mesh_send((uint16_t)dst->valueint,
                                             (uint32_t)opcode->valuedouble,
                                             ttl_value, data, (uint16_t)length);
                    }
                }
                cJSON_Delete(root);
            }
        } else if (code != 204 && code != 408) {
            ESP_LOGW(TAG, "command poll HTTP %d", code);
            vTaskDelay(pdMS_TO_TICKS(1000));
        }
        http.end();
        vTaskDelay(pdMS_TO_TICKS(100));
    }
}

#endif

static void wifi_task(void *argument) {
    (void)argument;
    prepare_wifi();
    for (;;) {
        (void)connect_wifi_once();
        vTaskDelay(pdMS_TO_TICKS(5000));
    }
}

void setup() {
    Serial.begin(115200);
    delay(500);
    esp_log_set_vprintf(vprintf_to_serial);
    Serial.println("[GW] setup starting...");
    Serial.flush();

    if (!bm_device_gateway_id_is_valid()) {
        ESP_LOGE(TAG, "gateway identity unavailable; refusing to start with a shared ID");
        while (true) {
            delay(1000);
        }
    }

    bool reset_nvs = bm_nvs_reset_pending();
    if (reset_nvs) {
        ESP_LOGW(TAG, "BOOT requested NVS erase");
        ESP_ERROR_CHECK(nvs_flash_erase());
        bm_nvs_reset_clear();
    }

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);
    Serial.println("[GW] NVS initialized");
    Serial.flush();
    bm_check_boot_button_nvs_reset(TAG);

    bm_gateway_spool_init();
    Serial.println("[GW] spool initialized");
    Serial.flush();

    uint16_t development_address = 0;
#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
    prepare_wifi();
    if (connect_wifi_once()) {
        development_address = request_development_mesh_address();
    }
    if (development_address == 0) {
        ESP_LOGE(TAG, "central development Mesh address unavailable; auto-provisioning disabled");
    }
#endif

    Serial.println("[GW] calling bm_gateway_mesh_init...");
    Serial.flush();
    bm_gateway_mesh_init(development_address);
    Serial.println("[GW] mesh initialized");
    Serial.flush();

    xTaskCreate(wifi_task, "gateway_wifi", 4096, NULL, 5, NULL);
    Serial.println("[GW] wifi_task started");
    Serial.flush();

#if defined(BM_GATEWAY_USE_WEBSOCKET) && BM_GATEWAY_USE_WEBSOCKET
    bm_gateway_ws_start();
    Serial.println("[GW] websocket started");
    Serial.flush();
#else
    xTaskCreate(upload_events_task, "gateway_upload", 6144, NULL, 4, NULL);
    xTaskCreate(poll_commands_task, "gateway_poll", 8192, NULL, 4, NULL);
    Serial.println("[GW] HTTP transport tasks started");
    Serial.flush();
#endif
}

void loop() {
    bm_check_boot_button_nvs_reset(TAG);
    delay(100);
}
