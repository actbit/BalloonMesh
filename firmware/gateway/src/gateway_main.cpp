#include <Arduino.h>
#include <HTTPClient.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>

#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_ble_mesh_defs.h"
#include "esp_ble_mesh_provisioning_api.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
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

static bool server_url_is_https(void) {
    return strncmp(BM_SERVER_URL, "https://", 8) == 0;
}

/// Refuses to run with a transport that would expose the gateway API key or
/// the Mesh network keys.  Only BM_ALLOW_INSECURE_TRANSPORT=1 (development
/// envs) may use http:// or TLS without a CA.
static bool transport_policy_ok(void) {
    if (strlen(BM_SERVER_URL) == 0) {
        ESP_LOGE(TAG, "BM_SERVER_URL is empty; set it at build time");
        return false;
    }
#if BM_ALLOW_INSECURE_TRANSPORT
    if (!server_url_is_https()) {
        ESP_LOGW(TAG, "DEVELOPMENT build: talking to %s without TLS", BM_SERVER_URL);
    }
    return true;
#else
    if (!server_url_is_https()) {
        ESP_LOGE(TAG, "BM_SERVER_URL must be https:// (got %s)", BM_SERVER_URL);
        return false;
    }
    return true;
#endif
}

/// HTTP/HTTPS transport.  HTTPS always validates the chain against
/// BM_TLS_CA_CERT and the host name (WiFiClientSecure checks the SAN/CN).
class GatewayHttpClient {
public:
    GatewayHttpClient() {
        https_ = server_url_is_https();
        if (https_) {
#ifdef BM_TLS_CA_CERT
            secure_.setCACert(BM_TLS_CA_CERT);
#else
            /* Only reachable with BM_ALLOW_INSECURE_TRANSPORT=1 (bm_config.h
             * refuses to compile otherwise). */
            secure_.setInsecure();
            ESP_LOGW(TAG, "DEVELOPMENT build: HTTPS without certificate validation");
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

struct GatewayMeshConfig {
    uint16_t mesh_address = 0;
    uint16_t node_range_start = 0;
    uint16_t node_range_end = 0;
    bool keys_valid = false;
    uint8_t net_key[16] = {0};
    uint8_t app_key[16] = {0};
};

static bool parse_hex_key(const cJSON *item, uint8_t out[16]) {
    if (!cJSON_IsString(item) || item->valuestring == nullptr ||
        strlen(item->valuestring) != 32) {
        return false;
    }
    bool nonzero = false;
    for (size_t i = 0; i < 16; ++i) {
        char pair[3] = {item->valuestring[i * 2], item->valuestring[i * 2 + 1], '\0'};
        char *end = nullptr;
        unsigned long value = strtoul(pair, &end, 16);
        if (end != pair + 2) {
            return false;
        }
        out[i] = (uint8_t)value;
        nonzero = nonzero || value != 0;
    }
    return nonzero;
}

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
struct UnprovisionedDevice {
    uint8_t uuid[16];
    uint8_t addr[6];
    uint8_t addr_type;
    uint16_t oob_info;
    uint8_t bearer;
};

static StaticQueue_t s_unprovisioned_queue_storage;
static uint8_t s_unprovisioned_queue_buffer[4 * sizeof(UnprovisionedDevice)];
static QueueHandle_t s_unprovisioned_queue;
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

static bool request_gateway_mesh_address(GatewayMeshConfig *config) {
    if (config == nullptr || !bm_device_gateway_id_is_valid() ||
        WiFi.status() != WL_CONNECTED) {
        return false;
    }

    config->mesh_address = 0;
    config->node_range_start = BM_DEV_BALLOON_ADDR_MIN;
    config->node_range_end = BM_DEV_BALLOON_ADDR_MAX;

    char device_uuid[33];
    encode_device_uuid(device_uuid, sizeof(device_uuid));
    char url[224];
    snprintf(url, sizeof(url), "%s/api/gateway/%s/mesh-address", BM_SERVER_URL,
             bm_device_gateway_id());

    GatewayHttpClient transport;
    HTTPClient http;
    if (!http.begin(transport.client(), url)) {
        ESP_LOGW(TAG, "gateway Mesh address request could not start");
        return false;
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
        ESP_LOGW(TAG, "gateway Mesh address request failed HTTP %d", code);
        http.end();
        return false;
    }

    String response = http.getString();
    http.end();
    cJSON *root = cJSON_Parse(response.c_str());
    if (root == NULL) {
        ESP_LOGW(TAG, "gateway Mesh address response was not JSON");
        return false;
    }
    cJSON *mesh_address = cJSON_GetObjectItem(root, "meshAddress");
    bool valid = cJSON_IsNumber(mesh_address) &&
                 mesh_address->valueint >= BM_DEV_GATEWAY_ADDR_MIN &&
                 mesh_address->valueint <= BM_DEV_GATEWAY_ADDR_MAX;
    if (valid) {
        config->mesh_address = (uint16_t)mesh_address->valueint;
    }
    cJSON *range_start = cJSON_GetObjectItem(root, "rangeStart");
    cJSON *range_end = cJSON_GetObjectItem(root, "rangeEnd");
    bool range_valid = cJSON_IsNumber(range_start) && cJSON_IsNumber(range_end) &&
                       range_start->valueint >= BM_DEV_BALLOON_ADDR_MIN &&
                       range_end->valueint <= BM_DEV_BALLOON_ADDR_MAX &&
                       range_start->valueint <= range_end->valueint;
    if (range_valid) {
        config->node_range_start = (uint16_t)range_start->valueint;
        config->node_range_end = (uint16_t)range_end->valueint;
    }
    config->keys_valid =
        parse_hex_key(cJSON_GetObjectItem(root, "netKey"), config->net_key) &&
        parse_hex_key(cJSON_GetObjectItem(root, "appKey"), config->app_key);
    cJSON_Delete(root);
    if (!valid || !range_valid) {
        ESP_LOGW(TAG, "gateway Mesh address response was invalid");
    }
#if !(defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV)
    if (!config->keys_valid) {
        ESP_LOGW(TAG, "gateway Mesh address response carried no valid network keys");
        return false;
    }
#endif
    return valid && range_valid;
}

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
static bool request_gateway_mesh_node_address(const uint8_t uuid[16],
                                              uint16_t *mesh_address) {
    if (uuid == nullptr || mesh_address == nullptr || WiFi.status() != WL_CONNECTED) {
        return false;
    }
    *mesh_address = 0;
    char uuid_text[33];
    static const char digits[] = "0123456789ABCDEF";
    for (size_t i = 0; i < 16; ++i) {
        uuid_text[i * 2] = digits[uuid[i] >> 4];
        uuid_text[i * 2 + 1] = digits[uuid[i] & 0x0F];
    }
    uuid_text[32] = '\0';

    char url[240];
    snprintf(url, sizeof(url), "%s/api/gateway/%s/mesh-node-address", BM_SERVER_URL,
             bm_device_gateway_id());
    GatewayHttpClient transport;
    HTTPClient http;
    if (!http.begin(transport.client(), url)) {
        return false;
    }
    http.setTimeout(5000);
    http.addHeader("Content-Type", "application/json");
    if (strlen(BM_GATEWAY_TOKEN) > 0) {
        http.addHeader("X-Gateway-Token", BM_GATEWAY_TOKEN);
    }
    char body[64];
    snprintf(body, sizeof(body), "{\"deviceUuid\":\"%s\"}", uuid_text);
    int code = http.POST((uint8_t *)body, strlen(body));
    if (code < 200 || code >= 300) {
        ESP_LOGW(TAG, "node Mesh address request failed HTTP %d", code);
        http.end();
        return false;
    }
    String response = http.getString();
    http.end();
    cJSON *root = cJSON_Parse(response.c_str());
    if (root == nullptr) {
        return false;
    }
    cJSON *value = cJSON_GetObjectItem(root, "meshAddress");
    bool valid = cJSON_IsNumber(value) &&
                 value->valueint >= BM_DEV_BALLOON_ADDR_MIN &&
                 value->valueint <= BM_DEV_BALLOON_ADDR_MAX;
    if (valid) {
        *mesh_address = (uint16_t)value->valueint;
    }
    cJSON_Delete(root);
    return valid;
}

static void provisioner_task(void *argument) {
    (void)argument;
    UnprovisionedDevice device;
    for (;;) {
        if (xQueueReceive(s_unprovisioned_queue, &device, portMAX_DELAY) != pdTRUE) {
            continue;
        }
        if (!connect_wifi_once()) {
            ESP_LOGW(TAG, "cannot allocate node address while Wi-Fi is offline");
            continue;
        }
        uint16_t mesh_address = 0;
        if (!request_gateway_mesh_node_address(device.uuid, &mesh_address)) {
            ESP_LOGW(TAG, "node address was not allocated; another Provisioner may own it");
            continue;
        }
        /* Waits for the previous provisioning link to close, so devices are
         * provisioned one at a time.  Remote configuration is tracked per
         * node inside gateway_mesh.c. */
        esp_err_t err = bm_gateway_mesh_provision_device(
            device.uuid, device.addr, device.addr_type, device.bearer,
            device.oob_info, mesh_address, 60000);
        if (err != ESP_OK) {
            ESP_LOGW(TAG, "starting provisioning for 0x%04x failed: %s",
                     mesh_address, esp_err_to_name(err));
        } else {
            ESP_LOGI(TAG, "provisioning queued: addr=0x%04x", mesh_address);
        }
    }
}

extern "C" void bm_gateway_mesh_on_unprov_device(const uint8_t uuid[16],
                                                   const uint8_t addr[6],
                                                   uint8_t addr_type,
                                                   uint16_t oob_info,
                                                   uint8_t bearer) {
    if (s_unprovisioned_queue == nullptr || uuid == nullptr || addr == nullptr) {
        return;
    }
    UnprovisionedDevice device{};
    memcpy(device.uuid, uuid, sizeof(device.uuid));
    memcpy(device.addr, addr, sizeof(device.addr));
    device.addr_type = addr_type;
    device.oob_info = oob_info;
    device.bearer = bearer;
    if (xQueueSend(s_unprovisioned_queue, &device, 0) != pdTRUE) {
        ESP_LOGW(TAG, "unprovisioned device queue full");
    }
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

static void start_gateway_transport_tasks(void) {
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

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
static void start_gateway_runtime_tasks(void) {
    xTaskCreate(provisioner_task, "gateway_provision", 6144, NULL, 5, NULL);
    Serial.println("[GW] provisioner task started");
    Serial.flush();

    xTaskCreate(wifi_task, "gateway_wifi", 4096, NULL, 5, NULL);
    Serial.println("[GW] wifi_task started");
    Serial.flush();

    start_gateway_transport_tasks();
}

static void mesh_bootstrap_task(void *argument) {
    (void)argument;
    for (;;) {
        GatewayMeshConfig mesh_config;
        if (connect_wifi_once() && request_gateway_mesh_address(&mesh_config)) {
            Serial.println("[GW] central gateway Mesh allocation received");
            Serial.flush();
            bm_gateway_mesh_set_network_keys(mesh_config.net_key, mesh_config.app_key);
            bm_gateway_mesh_init(mesh_config.mesh_address,
                                 mesh_config.mesh_address,
                                 mesh_config.node_range_start,
                                 mesh_config.node_range_end);
            Serial.println("[GW] mesh initialized");
            Serial.flush();
            start_gateway_runtime_tasks();
            vTaskDelete(NULL);
            return;
        }

        ESP_LOGW(TAG, "central gateway Mesh allocation unavailable; retrying in 10s");
        vTaskDelay(pdMS_TO_TICKS(10000));
    }
}
#endif

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
    if (!transport_policy_ok()) {
        ESP_LOGE(TAG, "insecure server transport; refusing to start");
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

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
    s_unprovisioned_queue = xQueueCreateStatic(
        4, sizeof(UnprovisionedDevice), s_unprovisioned_queue_buffer,
        &s_unprovisioned_queue_storage);
    prepare_wifi();
    xTaskCreate(mesh_bootstrap_task, "gateway_mesh_boot", 6144, NULL, 5, NULL);
    Serial.println("[GW] mesh bootstrap task started");
    Serial.flush();
#else
    GatewayMeshConfig mesh_config;
    uint16_t development_address = 0;
    prepare_wifi();
    if (connect_wifi_once()) {
        (void)request_gateway_mesh_address(&mesh_config);
        development_address = mesh_config.mesh_address;
    }

    Serial.println("[GW] calling bm_gateway_mesh_init...");
    Serial.flush();
    bm_gateway_mesh_init(development_address,
                         mesh_config.mesh_address,
                         mesh_config.node_range_start,
                         mesh_config.node_range_end);
    Serial.println("[GW] mesh initialized");
    Serial.flush();

    xTaskCreate(wifi_task, "gateway_wifi", 4096, NULL, 5, NULL);
    Serial.println("[GW] wifi_task started");
    Serial.flush();

    start_gateway_transport_tasks();
#endif
}

void loop() {
    bm_check_boot_button_nvs_reset(TAG);
    delay(100);
}
