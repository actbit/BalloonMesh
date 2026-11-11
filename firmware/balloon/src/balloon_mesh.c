#include "balloon_mesh.h"

#include <string.h>

#include "esp_bt.h"
#include "esp_bt_main.h"
#include "esp_log.h"

#include "esp_ble_mesh_common_api.h"
#include "esp_ble_mesh_config_model_api.h"
#include "esp_ble_mesh_defs.h"
#include "esp_ble_mesh_health_model_api.h"
#include "esp_ble_mesh_local_data_operation_api.h"
#include "esp_ble_mesh_networking_api.h"
#include "esp_ble_mesh_provisioning_api.h"

#include "balloon_led_controller.h"
#include "balloon_neighbor_link.h"
#include "balloon_protocol.h"
#include "balloon_time_link.h"
#include "bm_config.h"
#include "bm_device_id.h"

static const char *TAG = "balloon-mesh";

static uint8_t s_prov_uuid[16];
static bool s_provisioned;
static uint16_t s_primary_addr;

static esp_ble_mesh_cfg_srv_t s_config_server = {
    .net_transmit = ESP_BLE_MESH_TRANSMIT(2, 20),
    .relay = ESP_BLE_MESH_RELAY_ENABLED,
    .relay_retransmit = ESP_BLE_MESH_TRANSMIT(2, 20),
    .beacon = ESP_BLE_MESH_BEACON_ENABLED,
    .gatt_proxy = ESP_BLE_MESH_GATT_PROXY_ENABLED,
    .friend_state = ESP_BLE_MESH_FRIEND_NOT_SUPPORTED,
    .default_ttl = 7,
};

ESP_BLE_MESH_HEALTH_PUB_DEFINE(s_health_pub, 0, ROLE_NODE);
static const uint8_t s_health_test_ids[] = {
    ESP_BLE_MESH_HEALTH_STANDARD_TEST,
};
static esp_ble_mesh_health_srv_t s_health_server = {
    .health_test = {
        .id_count = sizeof(s_health_test_ids),
        .test_ids = s_health_test_ids,
        .company_id = BM_COMPANY_ID_LOCAL,
    },
};

static esp_ble_mesh_model_op_t s_control_ops[] = {
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_SET), 8),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_CANCEL), 5),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_RESET_ALL), 5),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_SET_NOW), 8),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_RESET_AND_SET), 8),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_NEIGHBOR_REQUEST), 0),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_NEIGHBOR_PROBE), 0),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_TIME_REQUEST), 0),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_TIME_REPORT), 8),
    ESP_BLE_MESH_MODEL_OP_END,
};

static esp_ble_mesh_model_t s_root_models[] = {
    ESP_BLE_MESH_MODEL_CFG_SRV(&s_config_server),
    ESP_BLE_MESH_MODEL_HEALTH_SRV(&s_health_server, &s_health_pub),
};

static esp_ble_mesh_model_t s_vendor_models[] = {
    ESP_BLE_MESH_VENDOR_MODEL(BM_COMPANY_ID_LOCAL, BM_MODEL_ID_CONTROL,
                              s_control_ops, NULL, NULL),
    ESP_BLE_MESH_VENDOR_MODEL(BM_COMPANY_ID_LOCAL, BM_MODEL_ID_TELEMETRY,
                              NULL, NULL, NULL),
};

static esp_ble_mesh_elem_t s_elements[] = {
    ESP_BLE_MESH_ELEMENT(0, s_root_models, s_vendor_models),
};

static esp_ble_mesh_comp_t s_composition = {
    .cid = BM_COMPANY_ID_LOCAL,
    .pid = 0x0001,
    .vid = 0x0001,
    .element_count = sizeof(s_elements) / sizeof(s_elements[0]),
    .elements = s_elements,
};

static esp_ble_mesh_prov_t s_prov = {
    .uuid = s_prov_uuid,
};

static void subscribe_control_groups(void) {
    uint16_t addr = esp_ble_mesh_get_primary_element_address();
    if (addr == ESP_BLE_MESH_ADDR_UNASSIGNED) {
        return;
    }
    /* Free slots occupied by subscriptions from older firmware. */
    const uint16_t legacy_groups[] = { 0xC004u, 0xC002u, 0xC005u };
    for (size_t i = 0; i < sizeof(legacy_groups) / sizeof(legacy_groups[0]); ++i) {
        (void)esp_ble_mesh_model_unsubscribe_group_addr(
            addr, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_CONTROL, legacy_groups[i]);
    }
    for (uint16_t group = 0xC100u; group < 0xC110u; ++group) {
        (void)esp_ble_mesh_model_unsubscribe_group_addr(
            addr, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_CONTROL, group);
    }
    esp_err_t err = esp_ble_mesh_model_subscribe_group_addr(
        addr, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_CONTROL, BM_GROUP_BALLOON);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "subscribe 0x%04x failed: %s", BM_GROUP_BALLOON, esp_err_to_name(err));
    }
    ESP_LOGI(TAG, "provisioned with primary address 0x%04x", addr);
}

static void prov_cb(esp_ble_mesh_prov_cb_event_t event,
                    esp_ble_mesh_prov_cb_param_t *param) {
    switch (event) {
        case ESP_BLE_MESH_NODE_PROV_ENABLE_COMP_EVT:
            ESP_LOGI(TAG, "provisioning enabled (err=%d)",
                     param->node_prov_enable_comp.err_code);
            break;
        case ESP_BLE_MESH_NODE_PROV_COMPLETE_EVT:
            s_provisioned = true;
            s_primary_addr = esp_ble_mesh_get_primary_element_address();
            subscribe_control_groups();
            break;
        case ESP_BLE_MESH_NODE_PROV_RESET_EVT:
            s_provisioned = false;
            s_primary_addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
            ESP_LOGW(TAG, "node reset");
            esp_err_t err = esp_ble_mesh_node_prov_enable(
                (esp_ble_mesh_prov_bearer_t)(ESP_BLE_MESH_PROV_ADV | ESP_BLE_MESH_PROV_GATT));
            if (err != ESP_OK) {
                ESP_LOGE(TAG, "restart provisioning failed: %s", esp_err_to_name(err));
            }
            break;
        default:
            break;
    }
}

static void config_server_cb(esp_ble_mesh_cfg_server_cb_event_t event,
                             esp_ble_mesh_cfg_server_cb_param_t *param) {
    (void)event;
    (void)param;
}

static void health_server_cb(esp_ble_mesh_health_server_cb_event_t event,
                             esp_ble_mesh_health_server_cb_param_t *param) {
    (void)event;
    (void)param;
}

static void custom_model_cb(esp_ble_mesh_model_cb_event_t event,
                            esp_ble_mesh_model_cb_param_t *param) {
    if (event != ESP_BLE_MESH_MODEL_OPERATION_EVT) {
        return;
    }
    uint32_t opcode = param->model_operation.opcode;
    esp_ble_mesh_msg_ctx_t *ctx = param->model_operation.ctx;
    const uint8_t *data = param->model_operation.msg;
    uint16_t length = param->model_operation.length;

    if (opcode == BM_VENDOR_OPCODE(BM_OP_NEIGHBOR_REQUEST)) {
        bm_neighbor_link_on_request();
        return;
    }
    if (opcode == BM_VENDOR_OPCODE(BM_OP_NEIGHBOR_PROBE)) {
        bm_neighbor_link_on_probe(ctx->addr, ctx->recv_rssi, ctx->recv_ttl);
        return;
    }
    if (opcode == BM_VENDOR_OPCODE(BM_OP_TIME_REQUEST)) {
        bm_time_link_on_request();
        return;
    }
    if (opcode == BM_VENDOR_OPCODE(BM_OP_TIME_REPORT)) {
        bm_time_link_on_report(data, length);
        return;
    }
    bm_led_controller_handle_opcode(opcode, data, length);
}

void bm_balloon_mesh_init(void) {
    esp_bt_controller_config_t bt_cfg = BT_CONTROLLER_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_bt_controller_init(&bt_cfg));
    ESP_ERROR_CHECK(esp_bt_controller_enable(ESP_BT_MODE_BLE));
    ESP_ERROR_CHECK(esp_bluedroid_init());
    ESP_ERROR_CHECK(esp_bluedroid_enable());
    bm_device_uuid(s_prov_uuid);

    ESP_ERROR_CHECK(esp_ble_mesh_register_prov_callback(prov_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_config_server_callback(config_server_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_health_server_callback(health_server_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_custom_model_callback(custom_model_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_init(&s_prov, &s_composition));

    if (esp_ble_mesh_node_is_provisioned()) {
        s_provisioned = true;
        s_primary_addr = esp_ble_mesh_get_primary_element_address();
        subscribe_control_groups();
    } else {
        ESP_ERROR_CHECK(esp_ble_mesh_node_prov_enable(
            (esp_ble_mesh_prov_bearer_t)(ESP_BLE_MESH_PROV_ADV | ESP_BLE_MESH_PROV_GATT)));
    }
    ESP_LOGI(TAG, "mesh initialized (provisioned=%d)", (int)s_provisioned);
}

bool bm_balloon_mesh_is_provisioned(void) {
    return s_provisioned;
}

uint16_t bm_balloon_mesh_primary_addr(void) {
    return s_primary_addr;
}

static esp_ble_mesh_model_t *telemetry_model(void) {
    return &s_vendor_models[1];
}

bool bm_balloon_mesh_send(uint32_t opcode,
                          uint16_t dst,
                          uint8_t ttl,
                          const uint8_t *data,
                          uint16_t length) {
    if (!s_provisioned) {
        return false;
    }
    esp_ble_mesh_msg_ctx_t ctx = {
        .net_idx = BM_NET_IDX,
        .app_idx = BM_APP_IDX,
        .addr = dst,
        .send_ttl = ttl,
    };
    esp_err_t err = esp_ble_mesh_client_model_send_msg(
        telemetry_model(), &ctx, opcode, length, (uint8_t *)data, 0, false, ROLE_NODE);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "send opcode 0x%06lx failed: %s",
                 (unsigned long)opcode, esp_err_to_name(err));
        return false;
    }
    return true;
}

void bm_balloon_mesh_publish_queue_status(uint8_t kind, uint16_t queue_count) {
    bm_queue_status_t status = {.kind = kind, .queue_count = queue_count};
    uint8_t payload[3];
    if (bm_encode_queue_status(payload, sizeof(payload), &status) == 3) {
        bm_balloon_mesh_send(BM_VENDOR_OPCODE(BM_OP_QUEUE_STATUS),
                             BM_GROUP_QUEUE_STATUS, 7, payload, 3);
    }
}
