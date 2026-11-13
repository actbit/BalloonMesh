#include "gateway_mesh.h"

#include <errno.h>
#include <string.h>

#include "esp_bt.h"
#include "esp_bt_main.h"
#include "esp_log.h"
#include "esp_timer.h"

#include "esp_ble_mesh_common_api.h"
#include "esp_ble_mesh_config_model_api.h"
#include "esp_ble_mesh_defs.h"
#include "esp_ble_mesh_health_model_api.h"
#include "esp_ble_mesh_local_data_operation_api.h"
#include "esp_ble_mesh_networking_api.h"
#include "esp_ble_mesh_provisioning_api.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/semphr.h"

#include "balloon_protocol.h"
#include "bm_config.h"
#include "bm_device_id.h"
#include "bm_dev_auto_prov.h"
#include "gateway_spool.h"

static const char *TAG = "gateway-mesh";

#define BM_MESH_STATE_PROVISIONED BIT0
#define BM_MESH_STATE_CONTROL_BOUND BIT1
#define BM_MESH_STATE_TELEMETRY_BOUND BIT2
#define BM_MESH_STATE_BINDING BIT3
#define BM_BIND_RETRY_MAX_ATTEMPTS 8

static uint8_t s_prov_uuid[16];
static StaticEventGroup_t s_state_events_storage;
static EventGroupHandle_t s_state_events;
static StaticSemaphore_t s_state_lock_storage;
static SemaphoreHandle_t s_state_lock;
static volatile uint16_t s_primary_addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
static esp_timer_handle_t s_bind_retry_timer;
static uint32_t s_bind_retry_delay_ms = 1000;
static uint8_t s_bind_retry_attempts;
static esp_timer_handle_t s_prov_retry_timer;
static uint32_t s_prov_retry_delay_ms = 1000;

static bool vendor_models_ready(void) {
    if (s_state_events == NULL) {
        return false;
    }
    EventBits_t bits = xEventGroupGetBits(s_state_events);
    return (bits & (BM_MESH_STATE_CONTROL_BOUND | BM_MESH_STATE_TELEMETRY_BOUND)) ==
           (BM_MESH_STATE_CONTROL_BOUND | BM_MESH_STATE_TELEMETRY_BOUND);
}

static bool mesh_is_provisioned(void) {
    return s_state_events != NULL &&
           (xEventGroupGetBits(s_state_events) & BM_MESH_STATE_PROVISIONED) != 0;
}

static void state_lock(void) {
    if (s_state_lock != NULL) {
        xSemaphoreTake(s_state_lock, portMAX_DELAY);
    }
}

static void state_unlock(void) {
    if (s_state_lock != NULL) {
        xSemaphoreGive(s_state_lock);
    }
}

static void set_model_bound(uint16_t model_id, bool bound) {
    EventBits_t bit = 0;
    if (model_id == BM_MODEL_ID_CONTROL) {
        bit = BM_MESH_STATE_CONTROL_BOUND;
    } else if (model_id == BM_MODEL_ID_TELEMETRY) {
        bit = BM_MESH_STATE_TELEMETRY_BOUND;
    }
    if (bit == 0 || s_state_events == NULL) {
        return;
    }
    if (bound) {
        xEventGroupSetBits(s_state_events, bit);
    } else {
        xEventGroupClearBits(s_state_events, bit);
    }
}

static void stop_bind_retry(void) {
    if (s_bind_retry_timer != NULL) {
        esp_err_t err = esp_timer_stop(s_bind_retry_timer);
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
            ESP_LOGW(TAG, "stopping bind retry timer failed: %s", esp_err_to_name(err));
        }
    }
}

static void stop_prov_retry(void) {
    if (s_prov_retry_timer != NULL) {
        esp_err_t err = esp_timer_stop(s_prov_retry_timer);
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
            ESP_LOGW(TAG, "stopping provisioning retry timer failed: %s",
                     esp_err_to_name(err));
        }
    }
}

static void schedule_bind_retry(void) {
    if (s_bind_retry_timer == NULL || !mesh_is_provisioned()) {
        return;
    }
    state_lock();
    uint32_t delay_ms = s_bind_retry_delay_ms;
    if (delay_ms == 0) {
        delay_ms = 1000;
    }
    if (s_bind_retry_attempts >= BM_BIND_RETRY_MAX_ATTEMPTS) {
        /* Keep a slow background retry: a provisioner may add the AppKey long
         * after boot, and giving up would leave the node permanently deaf. */
        delay_ms = 30000;
    } else {
        s_bind_retry_delay_ms = delay_ms < 30000 ? delay_ms * 2 : 30000;
        s_bind_retry_attempts++;
    }
    state_unlock();
    (void)esp_timer_stop(s_bind_retry_timer);
    esp_err_t err = esp_timer_start_once(s_bind_retry_timer,
                                         (uint64_t)delay_ms * 1000);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "scheduling vendor model bind retry failed: %s", esp_err_to_name(err));
    }
}

static void schedule_prov_retry(void) {
    if (s_prov_retry_timer == NULL || mesh_is_provisioned()) {
        return;
    }
    (void)esp_timer_stop(s_prov_retry_timer);
    uint32_t delay_ms = s_prov_retry_delay_ms;
    if (delay_ms == 0) {
        delay_ms = 1000;
    }
    s_prov_retry_delay_ms = delay_ms < 30000 ? delay_ms * 2 : 30000;
    esp_err_t err = esp_timer_start_once(s_prov_retry_timer,
                                         (uint64_t)delay_ms * 1000);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "scheduling provisioning retry failed: %s", esp_err_to_name(err));
    }
}

static void start_provisioning(void) {
    esp_err_t err = esp_ble_mesh_node_prov_enable(
        (esp_ble_mesh_prov_bearer_t)(ESP_BLE_MESH_PROV_ADV | ESP_BLE_MESH_PROV_GATT));
    if (err == ESP_OK) {
        s_prov_retry_delay_ms = 1000;
        stop_prov_retry();
        return;
    }
    ESP_LOGW(TAG, "provisioning enable failed: %s", esp_err_to_name(err));
    schedule_prov_retry();
}

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

static esp_ble_mesh_model_op_t s_telemetry_ops[] = {
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_FTM_REPORT), 8),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_NEIGHBOR_REPORT), 8),
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_QUEUE_STATUS), 3),
    ESP_BLE_MESH_MODEL_OP_END,
};

static esp_ble_mesh_model_t s_root_models[] = {
    ESP_BLE_MESH_MODEL_CFG_SRV(&s_config_server),
    ESP_BLE_MESH_MODEL_HEALTH_SRV(&s_health_server, &s_health_pub),
};

/* The downlink sender must be a vendor CLIENT model.  ESP-IDF's
 * esp_ble_mesh_client_model_send_msg() resolves model->user_data as an
 * esp_ble_mesh_client_t and returns -EINVAL ("Invalid client user data")
 * when it is NULL, so a plain send-only vendor model cannot transmit. */
static esp_ble_mesh_client_t s_control_client;
static esp_ble_mesh_model_op_t s_control_client_ops[] = {
    ESP_BLE_MESH_MODEL_OP(BM_VENDOR_OPCODE(BM_OP_LED_SET_NOW), 0),
    ESP_BLE_MESH_MODEL_OP_END,
};

static esp_ble_mesh_model_t s_vendor_models[] = {
    ESP_BLE_MESH_VENDOR_MODEL(BM_COMPANY_ID_LOCAL, BM_MODEL_ID_CONTROL,
                              s_control_client_ops, NULL, &s_control_client),
    ESP_BLE_MESH_VENDOR_MODEL(BM_COMPANY_ID_LOCAL, BM_MODEL_ID_TELEMETRY,
                              s_telemetry_ops, NULL, NULL),
};

static esp_ble_mesh_elem_t s_elements[] = {
    ESP_BLE_MESH_ELEMENT(0, s_root_models, s_vendor_models),
};

static esp_ble_mesh_comp_t s_composition = {
    .cid = BM_COMPANY_ID_LOCAL,
    .pid = 0x0002,
    .vid = 0x0001,
    .element_count = sizeof(s_elements) / sizeof(s_elements[0]),
    .elements = s_elements,
};

static esp_ble_mesh_prov_t s_prov = {
    .uuid = s_prov_uuid,
};

static void bind_vendor_models(uint16_t addr, bool reset_backoff);

static void prov_retry_timer_cb(void *arg) {
    (void)arg;
    if (!mesh_is_provisioned()) {
        start_provisioning();
    }
}

static void bind_retry_timer_cb(void *arg) {
    (void)arg;
    if (mesh_is_provisioned()) {
        bind_vendor_models(s_primary_addr, false);
    }
}

static void bind_vendor_models(uint16_t addr, bool reset_backoff) {
    if (s_state_events == NULL) {
        return;
    }
    if (reset_backoff) {
        stop_bind_retry();
        state_lock();
        s_bind_retry_delay_ms = 1000;
        s_bind_retry_attempts = 0;
        state_unlock();
    }
    state_lock();
    xEventGroupClearBits(s_state_events,
                         BM_MESH_STATE_CONTROL_BOUND | BM_MESH_STATE_TELEMETRY_BOUND);
    xEventGroupSetBits(s_state_events, BM_MESH_STATE_BINDING);
    state_unlock();

    if (addr == ESP_BLE_MESH_ADDR_UNASSIGNED) {
        state_lock();
        xEventGroupClearBits(s_state_events, BM_MESH_STATE_BINDING);
        state_unlock();
        return;
    }
    if (esp_ble_mesh_node_get_local_app_key(BM_APP_IDX) == NULL) {
        ESP_LOGI(TAG, "waiting for AppKey %u before binding vendor models", BM_APP_IDX);
        state_lock();
        xEventGroupClearBits(s_state_events, BM_MESH_STATE_BINDING);
        state_unlock();
        /* The AppKey Add callback normally follows, but schedule a retry in
         * case this path is reached after a missed/racy callback. */
        schedule_bind_retry();
        return;
    }

    const uint16_t model_ids[] = { BM_MODEL_ID_CONTROL, BM_MODEL_ID_TELEMETRY };
    for (size_t i = 0; i < sizeof(model_ids) / sizeof(model_ids[0]); ++i) {
        esp_err_t err = esp_ble_mesh_node_bind_app_key_to_local_model(
            addr, BM_COMPANY_ID_LOCAL, model_ids[i], BM_APP_IDX);
        if (err != ESP_OK) {
            ESP_LOGW(TAG, "bind request for model 0x%04x failed: %s",
                     model_ids[i], esp_err_to_name(err));
        }
    }
    /* The bind API reports completion asynchronously. Keep retrying until both
     * completion events arrive, so a transient local stack error cannot leave
     * a provisioned node permanently unusable. */
    schedule_bind_retry();
}

static void configure_vendor_models(void) {
    uint16_t addr = esp_ble_mesh_get_primary_element_address();
    if (addr == ESP_BLE_MESH_ADDR_UNASSIGNED) {
        return;
    }
    /* Free slots occupied by subscriptions from older firmware. */
    const uint16_t legacy_groups[] = { 0xC001u, 0xC002u, 0xC003u };
    for (size_t i = 0; i < sizeof(legacy_groups) / sizeof(legacy_groups[0]); ++i) {
        (void)esp_ble_mesh_model_unsubscribe_group_addr(
            addr, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_TELEMETRY, legacy_groups[i]);
    }
    esp_err_t err = esp_ble_mesh_model_subscribe_group_addr(
        addr, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_TELEMETRY, BM_GROUP_GATEWAY);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "subscribe 0x%04x: %s", BM_GROUP_GATEWAY, esp_err_to_name(err));
    }
    /* Both directions need this bind: access.c silently drops received
     * messages whose app_idx is not bound to the target model, and rejects
     * sends from unbound models (model_has_key).  AppKey Add is delivered
     * after provisioning, so bind_vendor_models() also runs from the config
     * server callback when the key becomes available. */
    bind_vendor_models(addr, true);
    ESP_LOGI(TAG, "gateway mesh primary address 0x%04x", addr);
}

static void handle_vendor_model_bind_result(const esp_ble_mesh_prov_cb_param_t *param) {
    if (param == NULL) {
        return;
    }
    state_lock();
    if (!mesh_is_provisioned() ||
        param->node_bind_app_key_to_model_comp.element_addr != s_primary_addr) {
        state_unlock();
        return;
    }
    if ((xEventGroupGetBits(s_state_events) & BM_MESH_STATE_BINDING) == 0) {
        state_unlock();
        return;
    }
    const int bind_err = param->node_bind_app_key_to_model_comp.err_code;
    const uint16_t model_id = param->node_bind_app_key_to_model_comp.model_id;
    if (param->node_bind_app_key_to_model_comp.app_idx != BM_APP_IDX ||
        param->node_bind_app_key_to_model_comp.company_id != BM_COMPANY_ID_LOCAL) {
        state_unlock();
        return;
    }

    bool success = bind_err == ESP_OK || bind_err == -EALREADY;
    if (model_id != BM_MODEL_ID_CONTROL && model_id != BM_MODEL_ID_TELEMETRY) {
        state_unlock();
        return;
    }
    set_model_bound(model_id, success);
    bool ready = vendor_models_ready();
    if (ready) {
        xEventGroupClearBits(s_state_events, BM_MESH_STATE_BINDING);
    }
    state_unlock();

    if (!success) {
        ESP_LOGW(TAG, "bind model 0x%04x to AppKey %u failed: %d",
                 model_id, BM_APP_IDX, bind_err);
        schedule_bind_retry();
        return;
    }
    if (ready) {
        stop_bind_retry();
        state_lock();
        s_bind_retry_delay_ms = 1000;
        s_bind_retry_attempts = 0;
        state_unlock();
        ESP_LOGI(TAG, "vendor models ready with AppKey %u", BM_APP_IDX);
    }
}

static void prov_cb(esp_ble_mesh_prov_cb_event_t event,
                    esp_ble_mesh_prov_cb_param_t *param) {
    if (event == ESP_BLE_MESH_NODE_PROV_ENABLE_COMP_EVT) {
        int err_code = param == NULL ? ESP_FAIL : param->node_prov_enable_comp.err_code;
        if (err_code == ESP_OK) {
            s_prov_retry_delay_ms = 1000;
            stop_prov_retry();
        } else {
            ESP_LOGW(TAG, "provisioning enable completion failed: %d", err_code);
            schedule_prov_retry();
        }
    } else if (event == ESP_BLE_MESH_NODE_PROV_COMPLETE_EVT) {
        state_lock();
        xEventGroupSetBits(s_state_events, BM_MESH_STATE_PROVISIONED);
        state_unlock();
        stop_prov_retry();
        s_prov_retry_delay_ms = 1000;
        s_primary_addr = esp_ble_mesh_get_primary_element_address();
        configure_vendor_models();
    } else if (event == ESP_BLE_MESH_NODE_BIND_APP_KEY_TO_MODEL_COMP_EVT) {
        handle_vendor_model_bind_result(param);
    } else if (event == ESP_BLE_MESH_NODE_PROV_RESET_EVT) {
        state_lock();
        xEventGroupClearBits(s_state_events, BM_MESH_STATE_PROVISIONED |
                             BM_MESH_STATE_CONTROL_BOUND | BM_MESH_STATE_TELEMETRY_BOUND |
                             BM_MESH_STATE_BINDING);
        state_unlock();
        stop_bind_retry();
        state_lock();
        s_bind_retry_delay_ms = 1000;
        s_bind_retry_attempts = 0;
        state_unlock();
        s_primary_addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
        start_provisioning();
    }
}

static void config_server_cb(esp_ble_mesh_cfg_server_cb_event_t event,
                             esp_ble_mesh_cfg_server_cb_param_t *param) {
    if (event != ESP_BLE_MESH_CFG_SERVER_STATE_CHANGE_EVT || param == NULL) {
        return;
    }

    switch (param->ctx.recv_op) {
        case ESP_BLE_MESH_MODEL_OP_APP_KEY_ADD:
            if (param->value.state_change.appkey_add.net_idx == BM_NET_IDX &&
                param->value.state_change.appkey_add.app_idx == BM_APP_IDX) {
                ESP_LOGI(TAG, "AppKey %u added; binding vendor models", BM_APP_IDX);
                bind_vendor_models(esp_ble_mesh_get_primary_element_address(), true);
            }
            break;
        case ESP_BLE_MESH_MODEL_OP_APP_KEY_UPDATE:
            if (param->value.state_change.appkey_update.net_idx == BM_NET_IDX &&
                param->value.state_change.appkey_update.app_idx == BM_APP_IDX &&
                !vendor_models_ready()) {
                bind_vendor_models(esp_ble_mesh_get_primary_element_address(), true);
            }
            break;
        case ESP_BLE_MESH_MODEL_OP_APP_KEY_DELETE:
            if (param->value.state_change.appkey_delete.net_idx == BM_NET_IDX &&
                param->value.state_change.appkey_delete.app_idx == BM_APP_IDX) {
                state_lock();
                xEventGroupClearBits(s_state_events,
                                     BM_MESH_STATE_CONTROL_BOUND |
                                     BM_MESH_STATE_TELEMETRY_BOUND |
                                     BM_MESH_STATE_BINDING);
                state_unlock();
                stop_bind_retry();
                state_lock();
                s_bind_retry_delay_ms = 1000;
                s_bind_retry_attempts = 0;
                state_unlock();
            }
            break;
        case ESP_BLE_MESH_MODEL_OP_NET_KEY_DELETE:
            if (esp_ble_mesh_node_get_local_app_key(BM_APP_IDX) == NULL) {
                state_lock();
                xEventGroupClearBits(s_state_events,
                                     BM_MESH_STATE_CONTROL_BOUND |
                                     BM_MESH_STATE_TELEMETRY_BOUND |
                                     BM_MESH_STATE_BINDING);
                state_unlock();
                stop_bind_retry();
                state_lock();
                s_bind_retry_delay_ms = 1000;
                s_bind_retry_attempts = 0;
                state_unlock();
            }
            break;
        case ESP_BLE_MESH_MODEL_OP_MODEL_APP_BIND:
            if (param->value.state_change.mod_app_bind.element_addr == s_primary_addr &&
                param->value.state_change.mod_app_bind.company_id == BM_COMPANY_ID_LOCAL &&
                param->value.state_change.mod_app_bind.app_idx == BM_APP_IDX) {
                state_lock();
                set_model_bound(param->value.state_change.mod_app_bind.model_id, true);
                bool ready = vendor_models_ready();
                if (ready) {
                    xEventGroupClearBits(s_state_events, BM_MESH_STATE_BINDING);
                }
                state_unlock();
                if (ready) {
                    stop_bind_retry();
                    state_lock();
                    s_bind_retry_delay_ms = 1000;
                    s_bind_retry_attempts = 0;
                    state_unlock();
                }
            }
            break;
        case ESP_BLE_MESH_MODEL_OP_MODEL_APP_UNBIND:
            if (param->value.state_change.mod_app_unbind.element_addr == s_primary_addr &&
                param->value.state_change.mod_app_unbind.company_id == BM_COMPANY_ID_LOCAL &&
                param->value.state_change.mod_app_unbind.app_idx == BM_APP_IDX) {
                state_lock();
                set_model_bound(param->value.state_change.mod_app_unbind.model_id, false);
                xEventGroupClearBits(s_state_events, BM_MESH_STATE_BINDING);
                state_unlock();
                stop_bind_retry();
                state_lock();
                s_bind_retry_delay_ms = 1000;
                s_bind_retry_attempts = 0;
                state_unlock();
            }
            break;
        default:
            break;
    }
}

static void health_server_cb(esp_ble_mesh_health_server_cb_event_t event,
                             esp_ble_mesh_health_server_cb_param_t *param) {
    (void)event;
    (void)param;
}

static void custom_model_cb(esp_ble_mesh_model_cb_event_t event,
                            esp_ble_mesh_model_cb_param_t *param) {
    if (event == ESP_BLE_MESH_MODEL_SEND_COMP_EVT) {
        ESP_LOGI(TAG, "model send comp: err=%d opcode=0x%06lx",
                 param->model_send_comp.err_code,
                 (unsigned long)param->model_send_comp.opcode);
        return;
    }
    if (event == ESP_BLE_MESH_CLIENT_MODEL_SEND_TIMEOUT_EVT) {
        ESP_LOGW(TAG, "client model send timeout: opcode=0x%06lx",
                 (unsigned long)param->client_send_timeout.opcode);
        return;
    }
    if (event != ESP_BLE_MESH_MODEL_OPERATION_EVT) {
        return;
    }
    bm_gateway_event_t mesh_event = {0};
    mesh_event.src = param->model_operation.ctx->addr;
    mesh_event.dst = param->model_operation.ctx->recv_dst;
    mesh_event.opcode = param->model_operation.opcode;
    mesh_event.rssi = param->model_operation.ctx->recv_rssi;
    mesh_event.ttl = param->model_operation.ctx->recv_ttl;
    mesh_event.received_ms = (uint32_t)(esp_timer_get_time() / 1000);
    mesh_event.length = param->model_operation.length > sizeof(mesh_event.payload)
                            ? sizeof(mesh_event.payload)
                            : (uint8_t)param->model_operation.length;
    if (mesh_event.length > 0) {
        memcpy(mesh_event.payload, param->model_operation.msg, mesh_event.length);
    }
    bm_gateway_spool_add(&mesh_event);
}

void bm_gateway_mesh_init(void) {
    ESP_LOGI(TAG, "step 1: event group and mutex");
    s_state_events = xEventGroupCreateStatic(&s_state_events_storage);
    if (s_state_events == NULL) {
        ESP_LOGE(TAG, "mesh state event group creation failed");
        return;
    }
    s_state_lock = xSemaphoreCreateMutexStatic(&s_state_lock_storage);
    if (s_state_lock == NULL) {
        ESP_LOGE(TAG, "mesh state mutex creation failed");
        return;
    }
    ESP_LOGI(TAG, "step 2: timers");
    const esp_timer_create_args_t bind_retry_args = {
        .callback = bind_retry_timer_cb,
        .arg = NULL,
        .dispatch_method = ESP_TIMER_TASK,
        .name = "bm_bind_retry",
    };
    ESP_ERROR_CHECK(esp_timer_create(&bind_retry_args, &s_bind_retry_timer));
    const esp_timer_create_args_t prov_retry_args = {
        .callback = prov_retry_timer_cb,
        .arg = NULL,
        .dispatch_method = ESP_TIMER_TASK,
        .name = "bm_prov_retry",
    };
    ESP_ERROR_CHECK(esp_timer_create(&prov_retry_args, &s_prov_retry_timer));
    ESP_LOGI(TAG, "step 3: bt controller init");
    esp_bt_controller_config_t bt_cfg = BT_CONTROLLER_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_bt_controller_init(&bt_cfg));
    ESP_LOGI(TAG, "step 4: bt controller enable");
    ESP_ERROR_CHECK(esp_bt_controller_enable(ESP_BT_MODE_BLE));
    ESP_LOGI(TAG, "step 5: bluedroid init");
    ESP_ERROR_CHECK(esp_bluedroid_init());
    ESP_LOGI(TAG, "step 6: bluedroid enable");
    ESP_ERROR_CHECK(esp_bluedroid_enable());
    bm_device_uuid(s_prov_uuid);

    ESP_LOGI(TAG, "step 7: register callbacks");
    ESP_ERROR_CHECK(esp_ble_mesh_register_prov_callback(prov_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_config_server_callback(config_server_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_health_server_callback(health_server_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_custom_model_callback(custom_model_cb));
    ESP_LOGI(TAG, "step 8: esp_ble_mesh_init");
    ESP_ERROR_CHECK(esp_ble_mesh_init(&s_prov, &s_composition));
    ESP_LOGI(TAG, "step 9: client_model_init");
    /* Allocates the client's internal_data; without it every
     * esp_ble_mesh_client_model_send_msg() fails with -EINVAL. */
    ESP_ERROR_CHECK(esp_ble_mesh_client_model_init(&s_vendor_models[0]));

    ESP_LOGI(TAG, "step 10: auto prov");
#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
#if BM_DEV_UNICAST_ADDR == 0
#error "BM_DEV_UNICAST_ADDR must be set for BM_DEV_AUTO_PROV"
#endif
    int auto_err = bm_dev_auto_provision(BM_DEV_UNICAST_ADDR, BM_GROUP_GATEWAY);
    if (auto_err == 0) {
        state_lock();
        xEventGroupSetBits(s_state_events,
                           BM_MESH_STATE_PROVISIONED |
                           BM_MESH_STATE_CONTROL_BOUND |
                           BM_MESH_STATE_TELEMETRY_BOUND);
        state_unlock();
        s_primary_addr = esp_ble_mesh_get_primary_element_address();
        ESP_LOGI(TAG, "dev auto-provisioned successfully: addr=0x%04x", s_primary_addr);
    } else {
        ESP_LOGE(TAG, "dev auto-provision failed: %d", auto_err);
        start_provisioning();
    }
#else
    if (esp_ble_mesh_node_is_provisioned()) {
        state_lock();
        xEventGroupSetBits(s_state_events, BM_MESH_STATE_PROVISIONED);
        state_unlock();
        s_primary_addr = esp_ble_mesh_get_primary_element_address();
        configure_vendor_models();
    } else {
        start_provisioning();
    }
#endif
    ESP_LOGI(TAG, "gateway mesh initialized (provisioned=%d)", (int)mesh_is_provisioned());
}

bool bm_gateway_mesh_is_provisioned(void) {
    return mesh_is_provisioned();
}

bool bm_gateway_mesh_is_ready(void) {
    return mesh_is_provisioned() && vendor_models_ready();
}

uint16_t bm_gateway_mesh_primary_addr(void) {
    return s_primary_addr;
}

bool bm_gateway_mesh_send(uint16_t dst,
                          uint32_t opcode,
                          uint8_t ttl,
                          const uint8_t *payload,
                          uint16_t length) {
    state_lock();
    if (!mesh_is_provisioned() || !vendor_models_ready()) {
        state_unlock();
        return false;
    }
    esp_ble_mesh_msg_ctx_t ctx = {
        .net_idx = BM_NET_IDX,
        .app_idx = BM_APP_IDX,
        .addr = dst,
        .send_ttl = ttl,
    };
    esp_err_t err = esp_ble_mesh_client_model_send_msg(
        &s_vendor_models[0], &ctx, opcode, length, (uint8_t *)payload, 0, false,
        ROLE_NODE);
    state_unlock();
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "mesh send 0x%06lx: %s", (unsigned long)opcode,
                 esp_err_to_name(err));
        return false;
    }
    return true;
}
