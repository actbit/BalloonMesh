#include "gateway_mesh.h"

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
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
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

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
#define BM_CONFIG_RETRY_MAX_ATTEMPTS 8

static uint8_t s_prov_uuid[16];
static StaticEventGroup_t s_state_events_storage;
static EventGroupHandle_t s_state_events;
static StaticSemaphore_t s_state_lock_storage;
static SemaphoreHandle_t s_state_lock;
static volatile uint16_t s_primary_addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
static volatile uint16_t s_provisioner_address = ESP_BLE_MESH_ADDR_UNASSIGNED;
static uint16_t s_node_range_start = BM_DEV_BALLOON_ADDR_MIN;
static uint16_t s_node_range_end = BM_DEV_BALLOON_ADDR_MAX;
static esp_timer_handle_t s_bind_retry_timer;
static uint32_t s_bind_retry_delay_ms = 1000;
static uint8_t s_bind_retry_attempts;
static esp_timer_handle_t s_prov_retry_timer;
static uint32_t s_prov_retry_delay_ms = 1000;
/* bm_gateway_mesh_send() waits for the real outcome from the send-completion
 * event, so that a failed air transmission is reported to the downlink retry
 * loop instead of being counted as sent. */
static StaticSemaphore_t s_send_done_storage;
static SemaphoreHandle_t s_send_done;
static StaticSemaphore_t s_send_mutex_storage;
static SemaphoreHandle_t s_send_mutex;
static volatile esp_err_t s_send_result = ESP_FAIL;
#define BM_DOWNLINK_SEND_TIMEOUT_MS 2000u

/* Paced downlink queue.  See bm_gateway_mesh_enqueue() in the header. */
#define BM_DOWNLINK_QUEUE_LENGTH 96
/* One non-segmented access message with net_transmit(2, 20) occupies the
 * advertising bearer for roughly this long. */
#define BM_DOWNLINK_GAP_MS 60u
#define BM_DOWNLINK_RETRY_DELAY_MS 200u
#define BM_DOWNLINK_MAX_ATTEMPTS 5

typedef struct {
    uint16_t dst;
    uint32_t opcode;
    uint8_t ttl;
    uint8_t length;
    uint8_t payload[8];
} bm_downlink_t;

static QueueHandle_t s_downlink_queue;
static void downlink_task(void *argument);

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

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
static esp_ble_mesh_client_t s_config_client;
#endif

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
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER
    ESP_BLE_MESH_MODEL_CFG_CLI(&s_config_client),
#endif
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
#if defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    .prov_uuid = s_prov_uuid,
    .prov_unicast_addr = 0x0001,
    .prov_start_address = BM_DEV_BALLOON_ADDR_MIN,
    .prov_attention = 0,
    .prov_algorithm = 0,
    .prov_pub_key_oob = 0,
    .prov_static_oob_val = NULL,
    .prov_static_oob_len = 0,
    .flags = 0,
    .iv_index = 0,
#endif
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

#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER

/*
 * Remote node configuration (AppKey Add + two Model App Binds) is tracked per
 * node in a job table, keyed by the node's unicast address:
 *
 *  - every Config Client response is matched to its job by the source
 *    address, so concurrent Provisioning completions can never send a step to
 *    the wrong node (previously one global target/step was shared);
 *  - exactly one Config Client message is in flight at a time, which keeps
 *    the segmented-message buffers and the BLE/Wi-Fi coexistence load bounded;
 *  - every failure (API error, timeout, error callback, non-success remote
 *    status) reschedules the job with exponential backoff and the job is never
 *    dropped, so a node can not stay "provisioned but unbound";
 *  - on start-up every node already in the Provisioner's persisted node table
 *    is re-enqueued.  All steps are idempotent (AppKey "already stored" counts
 *    as success), so a reboot or power loss in the middle of configuration is
 *    recovered without re-provisioning.
 */
#define BM_CONFIG_STEP_APP_KEY_ADD 1
#define BM_CONFIG_STEP_BIND_CONTROL 2
#define BM_CONFIG_STEP_BIND_TELEMETRY 3
#define BM_CONFIG_STEP_DONE 4
#define BM_CONFIG_RETRY_MIN_MS 1000u
#define BM_CONFIG_RETRY_MAX_MS 30000u
#define BM_CONFIG_RETRY_SLOW_MS 60000u
#define BM_CONFIG_JOB_MAX CONFIG_BLE_MESH_MAX_PROV_NODES

typedef struct {
    uint16_t addr;          /* ESP_BLE_MESH_ADDR_UNASSIGNED = free slot */
    uint8_t step;
    uint8_t attempts;
    uint32_t delay_ms;
    int64_t due_us;
} config_job_t;

static config_job_t s_config_jobs[BM_CONFIG_JOB_MAX];
static int s_config_active = -1;      /* job whose message is in flight */
static StaticSemaphore_t s_config_lock_storage;
static SemaphoreHandle_t s_config_lock;
static bool s_provisioner_ready;
static esp_timer_handle_t s_config_retry_timer;

/* Provisioning link serialisation: set while no PB-ADV/PB-GATT link is open. */
#define BM_PROV_LINK_IDLE BIT0
static StaticEventGroup_t s_prov_link_events_storage;
static EventGroupHandle_t s_prov_link_events;

/* Mesh network keys.  Production keys come from the server
 * (bm_gateway_mesh_set_network_keys); development builds use the shared test
 * keys so that BM_DEV_AUTO_PROV balloons can join the same network. */
static uint8_t s_net_key[16];
static uint8_t s_app_key[16];
static bool s_keys_set;

static void config_pump_locked(void);

static void config_lock(void) {
    if (s_config_lock != NULL) {
        xSemaphoreTake(s_config_lock, portMAX_DELAY);
    }
}

static void config_unlock(void) {
    if (s_config_lock != NULL) {
        xSemaphoreGive(s_config_lock);
    }
}

static config_job_t *config_find_job(uint16_t addr) {
    for (size_t i = 0; i < BM_CONFIG_JOB_MAX; ++i) {
        if (s_config_jobs[i].addr == addr) {
            return &s_config_jobs[i];
        }
    }
    return NULL;
}

/* (Re)starts configuration of a node from the first step.  Called for a newly
 * provisioned node and for every stored node at boot. */
static void config_enqueue_locked(uint16_t addr) {
    if (addr == ESP_BLE_MESH_ADDR_UNASSIGNED) {
        return;
    }
    config_job_t *job = config_find_job(addr);
    if (job == NULL) {
        job = config_find_job(ESP_BLE_MESH_ADDR_UNASSIGNED);
    }
    if (job == NULL) {
        /* The table is sized to the node table, so this cannot happen unless
         * the node table itself is full. */
        ESP_LOGE(TAG, "remote config job table full; node 0x%04x not configured", addr);
        return;
    }
    int index = (int)(job - s_config_jobs);
    if (index == s_config_active) {
        /* A response for the old attempt may still arrive; it is ignored
         * because the step is restarted below and matched by step too. */
        s_config_active = -1;
    }
    job->addr = addr;
    job->step = BM_CONFIG_STEP_APP_KEY_ADD;
    job->attempts = 0;
    job->delay_ms = BM_CONFIG_RETRY_MIN_MS;
    job->due_us = esp_timer_get_time();
}

static void config_arm_timer_locked(void) {
    if (s_config_retry_timer == NULL) {
        return;
    }
    (void)esp_timer_stop(s_config_retry_timer);
    if (s_config_active >= 0) {
        return;  /* the response/timeout callback re-pumps */
    }
    int64_t now = esp_timer_get_time();
    int64_t next = INT64_MAX;
    for (size_t i = 0; i < BM_CONFIG_JOB_MAX; ++i) {
        if (s_config_jobs[i].addr != ESP_BLE_MESH_ADDR_UNASSIGNED &&
            s_config_jobs[i].due_us < next) {
            next = s_config_jobs[i].due_us;
        }
    }
    if (next == INT64_MAX) {
        return;
    }
    int64_t wait_us = next > now ? next - now : 1000;
    esp_err_t err = esp_timer_start_once(s_config_retry_timer, (uint64_t)wait_us);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "scheduling remote config failed: %s", esp_err_to_name(err));
    }
}

static void config_job_failed_locked(config_job_t *job, const char *reason) {
    uint32_t delay_ms = job->delay_ms == 0 ? BM_CONFIG_RETRY_MIN_MS : job->delay_ms;
    if (job->attempts < BM_CONFIG_RETRY_MAX_ATTEMPTS) {
        job->attempts++;
        job->delay_ms = delay_ms < BM_CONFIG_RETRY_MAX_MS ? delay_ms * 2 : BM_CONFIG_RETRY_MAX_MS;
    } else {
        /* Keep trying forever, slowly: the node may be out of range or
         * powered off and will be configured once it is back. */
        delay_ms = BM_CONFIG_RETRY_SLOW_MS;
    }
    ESP_LOGW(TAG, "node 0x%04x config step %u failed (%s); retry in %lums (attempt %u)",
             job->addr, job->step, reason, (unsigned long)delay_ms, job->attempts);
    job->due_us = esp_timer_get_time() + (int64_t)delay_ms * 1000;
    if (s_config_active == (int)(job - s_config_jobs)) {
        s_config_active = -1;
    }
}

static void config_job_succeeded_locked(config_job_t *job) {
    if (s_config_active == (int)(job - s_config_jobs)) {
        s_config_active = -1;
    }
    job->step++;
    job->attempts = 0;
    job->delay_ms = BM_CONFIG_RETRY_MIN_MS;
    job->due_us = esp_timer_get_time();
    if (job->step >= BM_CONFIG_STEP_DONE) {
        ESP_LOGI(TAG, "node 0x%04x configured (AppKey + models bound)", job->addr);
        memset(job, 0, sizeof(*job));
        job->addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
    }
}

static void config_client_set_common(esp_ble_mesh_client_common_param_t *common,
                                      uint32_t opcode, uint16_t addr) {
    memset(common, 0, sizeof(*common));
    common->opcode = opcode;
    common->model = s_config_client.model;
    common->ctx.net_idx = BM_NET_IDX;
    common->ctx.app_idx = BM_APP_IDX;
    common->ctx.addr = addr;
    common->ctx.send_ttl = 7;
    common->ctx.send_rel = false;
    common->msg_timeout = 5000;
    common->msg_role = ROLE_PROVISIONER;
}

static esp_err_t config_send_step(const config_job_t *job) {
    esp_ble_mesh_client_common_param_t common;
    esp_ble_mesh_cfg_client_set_state_t set_state = {0};
    if (job->step == BM_CONFIG_STEP_APP_KEY_ADD) {
        config_client_set_common(&common, ESP_BLE_MESH_MODEL_OP_APP_KEY_ADD, job->addr);
        set_state.app_key_add.net_idx = BM_NET_IDX;
        set_state.app_key_add.app_idx = BM_APP_IDX;
        memcpy(set_state.app_key_add.app_key, s_app_key, sizeof(s_app_key));
    } else {
        config_client_set_common(&common, ESP_BLE_MESH_MODEL_OP_MODEL_APP_BIND, job->addr);
        set_state.model_app_bind.element_addr = job->addr;
        set_state.model_app_bind.model_app_idx = BM_APP_IDX;
        set_state.model_app_bind.company_id = BM_COMPANY_ID_LOCAL;
        set_state.model_app_bind.model_id = job->step == BM_CONFIG_STEP_BIND_CONTROL
                                                ? BM_MODEL_ID_CONTROL
                                                : BM_MODEL_ID_TELEMETRY;
    }
    return esp_ble_mesh_config_client_set_state(&common, &set_state);
}

/* Sends the next due step if nothing is in flight.  Must hold config_lock. */
static void config_pump_locked(void) {
    if (!s_provisioner_ready) {
        return;
    }
    while (s_config_active < 0) {
        int64_t now = esp_timer_get_time();
        int best = -1;
        for (size_t i = 0; i < BM_CONFIG_JOB_MAX; ++i) {
            const config_job_t *job = &s_config_jobs[i];
            if (job->addr == ESP_BLE_MESH_ADDR_UNASSIGNED || job->due_us > now) {
                continue;
            }
            if (best < 0 || job->due_us < s_config_jobs[best].due_us) {
                best = (int)i;
            }
        }
        if (best < 0) {
            break;
        }
        config_job_t *job = &s_config_jobs[best];
        esp_err_t err = config_send_step(job);
        if (err == ESP_OK) {
            s_config_active = best;
            break;
        }
        config_job_failed_locked(job, esp_err_to_name(err));
    }
    config_arm_timer_locked();
}

static void config_retry_timer_cb(void *arg) {
    (void)arg;
    config_lock();
    config_pump_locked();
    config_unlock();
}

/* Re-enqueue every node the Provisioner already knows (persisted in NVS). */
static void config_resync_stored_nodes_locked(void) {
    const esp_ble_mesh_node_t **nodes = esp_ble_mesh_provisioner_get_node_table_entry();
    if (nodes == NULL) {
        return;
    }
    uint16_t count = 0;
    for (size_t i = 0; i < CONFIG_BLE_MESH_MAX_PROV_NODES; ++i) {
        if (nodes[i] == NULL || nodes[i]->unicast_addr == s_provisioner_address) {
            continue;
        }
        config_enqueue_locked(nodes[i]->unicast_addr);
        count++;
    }
    if (count > 0) {
        ESP_LOGI(TAG, "re-checking configuration of %u stored node(s)", count);
    }
}

static void provisioner_bind_local_models(void) {
    const uint16_t model_ids[] = {
        ESP_BLE_MESH_MODEL_ID_CONFIG_CLI,
        BM_MODEL_ID_CONTROL,
        BM_MODEL_ID_TELEMETRY,
    };
    const uint16_t company_ids[] = {
        ESP_BLE_MESH_CID_NVAL,
        BM_COMPANY_ID_LOCAL,
        BM_COMPANY_ID_LOCAL,
    };
    for (size_t i = 0; i < sizeof(model_ids) / sizeof(model_ids[0]); ++i) {
        esp_err_t err = esp_ble_mesh_provisioner_bind_app_key_to_local_model(
            s_provisioner_address, BM_APP_IDX, model_ids[i], company_ids[i]);
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
            ESP_LOGW(TAG, "bind local model 0x%04x failed: %s",
                     model_ids[i], esp_err_to_name(err));
        }
    }
}

static void provisioner_mark_ready(void) {
    esp_err_t subscribe_err = esp_ble_mesh_model_subscribe_group_addr(
        s_provisioner_address, BM_COMPANY_ID_LOCAL, BM_MODEL_ID_TELEMETRY,
        BM_GROUP_GATEWAY);
    if (subscribe_err != ESP_OK && subscribe_err != ESP_ERR_INVALID_STATE) {
        ESP_LOGW(TAG, "subscribe gateway telemetry group failed: %s",
                 esp_err_to_name(subscribe_err));
    }
    s_primary_addr = s_provisioner_address;
    state_lock();
    xEventGroupSetBits(s_state_events,
                       BM_MESH_STATE_PROVISIONED |
                       BM_MESH_STATE_CONTROL_BOUND |
                       BM_MESH_STATE_TELEMETRY_BOUND);
    state_unlock();
    config_lock();
    s_provisioner_ready = true;
    config_resync_stored_nodes_locked();
    config_pump_locked();
    config_unlock();
    ESP_LOGI(TAG, "gateway Provisioner ready: addr=0x%04x node-range=0x%04x..0x%04x",
             s_provisioner_address, s_node_range_start, s_node_range_end);
}

static void provisioner_enable_after_keys(void) {
    if (s_provisioner_ready) {
        return;
    }
    if (esp_ble_mesh_provisioner_get_local_net_key(BM_NET_IDX) == NULL ||
        esp_ble_mesh_provisioner_get_local_app_key(BM_NET_IDX, BM_APP_IDX) == NULL) {
        return;
    }
    provisioner_bind_local_models();
    esp_err_t err = esp_ble_mesh_provisioner_prov_enable(
        (esp_ble_mesh_prov_bearer_t)(ESP_BLE_MESH_PROV_ADV | ESP_BLE_MESH_PROV_GATT));
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
        ESP_LOGW(TAG, "Provisioner enable failed: %s", esp_err_to_name(err));
        return;
    }
    provisioner_mark_ready();
}

static void provisioner_warn_on_key_mismatch(void) {
    const uint8_t *stored_net = esp_ble_mesh_provisioner_get_local_net_key(BM_NET_IDX);
    const uint8_t *stored_app =
        esp_ble_mesh_provisioner_get_local_app_key(BM_NET_IDX, BM_APP_IDX);
    if ((stored_net != NULL && memcmp(stored_net, s_net_key, 16) != 0) ||
        (stored_app != NULL && memcmp(stored_app, s_app_key, 16) != 0)) {
        /* Keys persisted in NVS win: replacing them would orphan every node
         * already provisioned.  A deliberate change needs a Mesh Key Refresh
         * or an NVS erase plus re-provisioning. */
        ESP_LOGE(TAG, "stored Mesh keys differ from the server's; keeping stored keys");
    }
}

static void provisioner_start(uint16_t address, uint16_t range_start) {
    if (address == ESP_BLE_MESH_ADDR_UNASSIGNED ||
        address > BM_DEV_GATEWAY_ADDR_MAX ||
        range_start < BM_DEV_BALLOON_ADDR_MIN ||
        range_start > BM_DEV_BALLOON_ADDR_MAX) {
        ESP_LOGE(TAG, "invalid Provisioner identity addr=0x%04x range=0x%04x",
                 address, range_start);
        return;
    }
    if (!s_keys_set) {
        ESP_LOGE(TAG, "no Mesh network keys; Provisioner not started");
        return;
    }
    s_provisioner_address = address;
    s_node_range_start = range_start;
    s_prov.prov_start_address = range_start;
    esp_err_t err = esp_ble_mesh_provisioner_set_primary_elem_addr(address);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "setting Provisioner primary address failed: %s",
                 esp_err_to_name(err));
        return;
    }
    provisioner_warn_on_key_mismatch();

    if (esp_ble_mesh_provisioner_get_local_net_key(BM_NET_IDX) == NULL) {
        err = esp_ble_mesh_provisioner_add_local_net_key(s_net_key, BM_NET_IDX);
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
            ESP_LOGE(TAG, "adding Provisioner NetKey failed: %s", esp_err_to_name(err));
            return;
        }
    } else if (esp_ble_mesh_provisioner_get_local_app_key(BM_NET_IDX, BM_APP_IDX) == NULL) {
        err = esp_ble_mesh_provisioner_add_local_app_key(s_app_key, BM_NET_IDX, BM_APP_IDX);
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
            ESP_LOGE(TAG, "adding Provisioner AppKey failed: %s", esp_err_to_name(err));
            return;
        }
    } else {
        provisioner_enable_after_keys();
    }
}

static bool remote_config_status_succeeded(uint32_t opcode, uint8_t status) {
    return status == ESP_BLE_MESH_CFG_STATUS_SUCCESS ||
           (opcode == ESP_BLE_MESH_MODEL_OP_APP_KEY_ADD &&
            status == ESP_BLE_MESH_CFG_STATUS_KEY_INDEX_ALREADY_STORED);
}

static uint32_t config_step_opcode(uint8_t step) {
    return step == BM_CONFIG_STEP_APP_KEY_ADD ? ESP_BLE_MESH_MODEL_OP_APP_KEY_ADD
                                              : ESP_BLE_MESH_MODEL_OP_MODEL_APP_BIND;
}

static void config_client_cb(esp_ble_mesh_cfg_client_cb_event_t event,
                             esp_ble_mesh_cfg_client_cb_param_t *param) {
    if (param == NULL || param->params == NULL) {
        /* Cannot tell which node this was for; fail the in-flight job so it
         * is retried instead of stalling the queue. */
        config_lock();
        if (s_config_active >= 0) {
            config_job_failed_locked(&s_config_jobs[s_config_active], "no parameters");
        }
        config_pump_locked();
        config_unlock();
        return;
    }

    const uint16_t addr = param->params->ctx.addr;
    const uint32_t opcode = param->params->opcode;
    config_lock();
    config_job_t *job = config_find_job(addr);
    if (job == NULL || s_config_active != (int)(job - s_config_jobs) ||
        config_step_opcode(job->step) != opcode) {
        /* Stale response (e.g. job restarted after re-provisioning). */
        config_unlock();
        return;
    }

    if (event == ESP_BLE_MESH_CFG_CLIENT_TIMEOUT_EVT) {
        config_job_failed_locked(job, "timeout");
    } else if (event != ESP_BLE_MESH_CFG_CLIENT_SET_STATE_EVT || param->error_code != ESP_OK) {
        config_job_failed_locked(job, "client error");
    } else {
        uint8_t status = opcode == ESP_BLE_MESH_MODEL_OP_APP_KEY_ADD
                             ? param->status_cb.appkey_status.status
                             : param->status_cb.model_app_status.status;
        if (remote_config_status_succeeded(opcode, status)) {
            config_job_succeeded_locked(job);
        } else {
            char reason[24];
            snprintf(reason, sizeof(reason), "remote status %u", status);
            config_job_failed_locked(job, reason);
        }
    }
    config_pump_locked();
    config_unlock();
}

static void provisioner_prov_cb(esp_ble_mesh_prov_cb_event_t event,
                                esp_ble_mesh_prov_cb_param_t *param) {
    if (param == NULL) {
        return;
    }
    switch (event) {
        case ESP_BLE_MESH_PROVISIONER_PROV_ENABLE_COMP_EVT:
            ESP_LOGI(TAG, "Provisioner enable result=%d",
                     param->provisioner_prov_enable_comp.err_code);
            break;
        case ESP_BLE_MESH_PROVISIONER_RECV_UNPROV_ADV_PKT_EVT:
            if (!s_provisioner_ready) {
                return;
            }
            bm_gateway_mesh_on_unprov_device(
                param->provisioner_recv_unprov_adv_pkt.dev_uuid,
                param->provisioner_recv_unprov_adv_pkt.addr,
                (uint8_t)param->provisioner_recv_unprov_adv_pkt.addr_type,
                param->provisioner_recv_unprov_adv_pkt.oob_info,
                (uint8_t)param->provisioner_recv_unprov_adv_pkt.bearer);
            break;
        case ESP_BLE_MESH_PROVISIONER_PROV_LINK_CLOSE_EVT:
            /* Fires after both success and failure. */
            if (s_prov_link_events != NULL) {
                xEventGroupSetBits(s_prov_link_events, BM_PROV_LINK_IDLE);
            }
            break;
        case ESP_BLE_MESH_PROVISIONER_PROV_COMPLETE_EVT:
            ESP_LOGI(TAG, "Provisioned node addr=0x%04x elements=%u",
                     param->provisioner_prov_complete.unicast_addr,
                     param->provisioner_prov_complete.element_num);
            config_lock();
            config_enqueue_locked(param->provisioner_prov_complete.unicast_addr);
            config_pump_locked();
            config_unlock();
            break;
        case ESP_BLE_MESH_PROVISIONER_ADD_LOCAL_NET_KEY_COMP_EVT:
            if (param->provisioner_add_net_key_comp.err_code == ESP_OK ||
                param->provisioner_add_net_key_comp.err_code == -EALREADY) {
                if (esp_ble_mesh_provisioner_get_local_app_key(BM_NET_IDX, BM_APP_IDX) == NULL) {
                    (void)esp_ble_mesh_provisioner_add_local_app_key(
                        s_app_key, BM_NET_IDX, BM_APP_IDX);
                } else {
                    provisioner_enable_after_keys();
                }
            }
            break;
        case ESP_BLE_MESH_PROVISIONER_ADD_LOCAL_APP_KEY_COMP_EVT:
            if (param->provisioner_add_app_key_comp.err_code == ESP_OK ||
                param->provisioner_add_app_key_comp.err_code == -EALREADY) {
                provisioner_enable_after_keys();
            }
            break;
        default:
            break;
    }
}

esp_err_t bm_gateway_mesh_provision_device(const uint8_t uuid[16],
                                           const uint8_t addr[6],
                                           uint8_t addr_type,
                                           uint8_t bearer,
                                           uint16_t oob_info,
                                           uint16_t unicast_addr,
                                           uint32_t link_timeout_ms) {
    if (s_prov_link_events == NULL || !s_provisioner_ready) {
        return ESP_ERR_INVALID_STATE;
    }
    /* One provisioning link at a time: wait for the previous one to close. */
    if ((xEventGroupWaitBits(s_prov_link_events, BM_PROV_LINK_IDLE, pdFALSE, pdTRUE,
                             pdMS_TO_TICKS(link_timeout_ms)) & BM_PROV_LINK_IDLE) == 0) {
        ESP_LOGW(TAG, "previous provisioning link did not close; continuing");
    }
    xEventGroupClearBits(s_prov_link_events, BM_PROV_LINK_IDLE);
    esp_ble_mesh_bd_addr_t bd_addr = {0};
    memcpy(bd_addr, addr, sizeof(bd_addr));
    esp_err_t err = esp_ble_mesh_provisioner_prov_device_with_addr(
        uuid, bd_addr, (esp_ble_mesh_addr_type_t)addr_type,
        (esp_ble_mesh_prov_bearer_t)bearer, oob_info, unicast_addr);
    if (err != ESP_OK) {
        xEventGroupSetBits(s_prov_link_events, BM_PROV_LINK_IDLE);
    }
    return err;
}

#endif

void bm_gateway_mesh_set_network_keys(const uint8_t net_key[16], const uint8_t app_key[16]) {
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
    (void)net_key;
    (void)app_key;
    ESP_LOGW(TAG, "DEVELOPMENT build: ignoring server Mesh keys, using shared test keys");
    memcpy(s_net_key, BM_DEV_NET_KEY, sizeof(s_net_key));
    memcpy(s_app_key, BM_DEV_APP_KEY, sizeof(s_app_key));
#else
    if (net_key == NULL || app_key == NULL) {
        return;
    }
    memcpy(s_net_key, net_key, sizeof(s_net_key));
    memcpy(s_app_key, app_key, sizeof(s_app_key));
#endif
    s_keys_set = true;
#else
    (void)net_key;
    (void)app_key;
#endif
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
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    if (event >= ESP_BLE_MESH_PROVISIONER_PROV_ENABLE_COMP_EVT) {
        provisioner_prov_cb(event, param);
        if (event == ESP_BLE_MESH_PROVISIONER_ADD_LOCAL_NET_KEY_COMP_EVT ||
            event == ESP_BLE_MESH_PROVISIONER_ADD_LOCAL_APP_KEY_COMP_EVT) {
            provisioner_enable_after_keys();
        }
        return;
    }
#endif
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
        s_send_result = param->model_send_comp.err_code;
        if (s_send_done != NULL) {
            xSemaphoreGive(s_send_done);
        }
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

void bm_gateway_mesh_init(uint16_t development_address,
                          uint16_t provisioner_address,
                          uint16_t node_range_start,
                          uint16_t node_range_end) {
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
    s_send_done = xSemaphoreCreateBinaryStatic(&s_send_done_storage);
    s_send_mutex = xSemaphoreCreateMutexStatic(&s_send_mutex_storage);
    if (s_send_done == NULL || s_send_mutex == NULL) {
        ESP_LOGE(TAG, "mesh send synchronisation creation failed");
        return;
    }
    s_downlink_queue = xQueueCreate(BM_DOWNLINK_QUEUE_LENGTH, sizeof(bm_downlink_t));
    if (s_downlink_queue == NULL) {
        ESP_LOGE(TAG, "downlink queue creation failed");
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
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    const esp_timer_create_args_t config_retry_args = {
        .callback = config_retry_timer_cb,
        .arg = NULL,
        .dispatch_method = ESP_TIMER_TASK,
        .name = "bm_config_retry",
    };
    ESP_ERROR_CHECK(esp_timer_create(&config_retry_args, &s_config_retry_timer));
    s_config_lock = xSemaphoreCreateMutexStatic(&s_config_lock_storage);
    s_prov_link_events = xEventGroupCreateStatic(&s_prov_link_events_storage);
    if (s_config_lock == NULL || s_prov_link_events == NULL) {
        ESP_LOGE(TAG, "Provisioner sync primitives creation failed");
        return;
    }
    xEventGroupSetBits(s_prov_link_events, BM_PROV_LINK_IDLE);
    for (size_t i = 0; i < BM_CONFIG_JOB_MAX; ++i) {
        s_config_jobs[i].addr = ESP_BLE_MESH_ADDR_UNASSIGNED;
    }
#endif
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
#if defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    if (node_range_start >= BM_DEV_BALLOON_ADDR_MIN &&
        node_range_start <= BM_DEV_BALLOON_ADDR_MAX) {
        s_prov.prov_start_address = node_range_start;
    }
#endif

    ESP_LOGI(TAG, "step 7: register callbacks");
    ESP_ERROR_CHECK(esp_ble_mesh_register_prov_callback(prov_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_config_server_callback(config_server_cb));
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    ESP_ERROR_CHECK(esp_ble_mesh_register_config_client_callback(config_client_cb));
#endif
    ESP_ERROR_CHECK(esp_ble_mesh_register_health_server_callback(health_server_cb));
    ESP_ERROR_CHECK(esp_ble_mesh_register_custom_model_callback(custom_model_cb));
    ESP_LOGI(TAG, "step 8: esp_ble_mesh_init");
    ESP_ERROR_CHECK(esp_ble_mesh_init(&s_prov, &s_composition));
    ESP_LOGI(TAG, "step 9: client_model_init");
    /* Allocates the client's internal_data; without it every
     * esp_ble_mesh_client_model_send_msg() fails with -EINVAL. */
    ESP_ERROR_CHECK(esp_ble_mesh_client_model_init(&s_vendor_models[0]));

    ESP_LOGI(TAG, "step 10: mesh role setup");
#if defined(BM_GATEWAY_PROVISIONER) && BM_GATEWAY_PROVISIONER && \
    defined(CONFIG_BLE_MESH_PROVISIONER) && CONFIG_BLE_MESH_PROVISIONER
    s_node_range_end = node_range_end >= node_range_start
                           ? node_range_end
                           : BM_DEV_BALLOON_ADDR_MAX;
    provisioner_start(provisioner_address, node_range_start);
#else
#if defined(BM_DEV_AUTO_PROV) && BM_DEV_AUTO_PROV
    uint16_t auto_addr = BM_DEV_UNICAST_ADDR != 0
                             ? BM_DEV_UNICAST_ADDR
                             : (development_address != 0
                                    ? development_address
                                    : bm_device_dev_unicast(
                                          BM_DEV_GATEWAY_ADDR_MIN,
                                          (uint16_t)(BM_DEV_GATEWAY_ADDR_MAX -
                                                     BM_DEV_GATEWAY_ADDR_MIN + 1u)));
    if (esp_ble_mesh_node_is_provisioned()) {
        state_lock();
        xEventGroupSetBits(s_state_events, BM_MESH_STATE_PROVISIONED);
        state_unlock();
        s_primary_addr = esp_ble_mesh_get_primary_element_address();
        configure_vendor_models();
        ESP_LOGI(TAG, "development node restored from NVS: addr=0x%04x", s_primary_addr);
    } else if (auto_addr == 0 || auto_addr < BM_DEV_GATEWAY_ADDR_MIN ||
               auto_addr > BM_DEV_GATEWAY_ADDR_MAX) {
        ESP_LOGE(TAG, "no valid centrally allocated development address");
        start_provisioning();
    } else {
        ESP_LOGI(TAG, "development auto-provision address=0x%04x", auto_addr);
        int auto_err = bm_dev_auto_provision(auto_addr, BM_GROUP_GATEWAY);
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
#endif
    xTaskCreate(downlink_task, "gateway_dl", 4096, NULL, 5, NULL);
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
    if (s_send_mutex == NULL || s_send_done == NULL) {
        return false;
    }
    /* One downlink in flight at a time, so the completion event read below
     * belongs to this message. */
    if (xSemaphoreTake(s_send_mutex, pdMS_TO_TICKS(BM_DOWNLINK_SEND_TIMEOUT_MS)) != pdTRUE) {
        return false;
    }
    (void)xSemaphoreTake(s_send_done, 0);
    state_lock();
    if (!mesh_is_provisioned() || !vendor_models_ready()) {
        state_unlock();
        xSemaphoreGive(s_send_mutex);
        return false;
    }
    esp_ble_mesh_msg_ctx_t ctx = {
        .net_idx = BM_NET_IDX,
        .app_idx = BM_APP_IDX,
        .addr = dst,
        .send_ttl = ttl,
    };
    /* Provisioner role: the keys live in the Provisioner's local key lists. */
    esp_err_t err = esp_ble_mesh_client_model_send_msg(
        &s_vendor_models[0], &ctx, opcode, length, (uint8_t *)payload, 0, false,
        ROLE_PROVISIONER);
    state_unlock();
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "mesh send 0x%06lx: %s", (unsigned long)opcode,
                 esp_err_to_name(err));
        xSemaphoreGive(s_send_mutex);
        return false;
    }
    /* esp_ble_mesh_client_model_send_msg() only queues the message: the air
     * outcome is reported by ESP_BLE_MESH_MODEL_SEND_COMP_EVT. */
    bool done = xSemaphoreTake(s_send_done, pdMS_TO_TICKS(BM_DOWNLINK_SEND_TIMEOUT_MS)) == pdTRUE;
    esp_err_t result = done ? s_send_result : ESP_ERR_TIMEOUT;
    xSemaphoreGive(s_send_mutex);
    if (result != ESP_OK) {
        ESP_LOGW(TAG, "mesh send 0x%06lx failed: %s", (unsigned long)opcode,
                 esp_err_to_name(result));
        return false;
    }
    return true;
}

static void downlink_task(void *argument) {
    (void)argument;
    bm_downlink_t item;
    for (;;) {
        if (s_downlink_queue == NULL ||
            xQueueReceive(s_downlink_queue, &item, portMAX_DELAY) != pdTRUE) {
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }
        bool sent = false;
        for (int attempt = 0; attempt < BM_DOWNLINK_MAX_ATTEMPTS; ++attempt) {
            sent = bm_gateway_mesh_send(item.dst, item.opcode, item.ttl,
                                        item.length > 0 ? item.payload : NULL,
                                        item.length);
            if (sent) {
                break;
            }
            /* Not ready yet, or the advertising buffers are full: wait instead
             * of discarding the command. */
            vTaskDelay(pdMS_TO_TICKS(BM_DOWNLINK_RETRY_DELAY_MS));
        }
        if (!sent) {
            ESP_LOGW(TAG, "downlink dropped after %d attempts: dst=0x%04x opcode=0x%06lx",
                     BM_DOWNLINK_MAX_ATTEMPTS, item.dst,
                     (unsigned long)item.opcode);
        }
        vTaskDelay(pdMS_TO_TICKS(BM_DOWNLINK_GAP_MS));
    }
}

bool bm_gateway_mesh_enqueue(uint16_t dst,
                             uint32_t opcode,
                             uint8_t ttl,
                             const uint8_t *payload,
                             uint16_t length) {
    if (s_downlink_queue == NULL) {
        return bm_gateway_mesh_send(dst, opcode, ttl, payload, length);
    }
    bm_downlink_t item = {
        .dst = dst,
        .opcode = opcode,
        .ttl = ttl,
        .length = (uint8_t)(length > sizeof(item.payload) ? sizeof(item.payload)
                                                          : length),
    };
    if (item.length > 0 && payload != NULL) {
        memcpy(item.payload, payload, item.length);
    } else {
        item.length = 0;
    }
    /* Brief backpressure on the transport task rather than a silent drop. */
    return xQueueSend(s_downlink_queue, &item, pdMS_TO_TICKS(500)) == pdTRUE;
}

uint16_t bm_gateway_mesh_pending_downlinks(void) {
    if (s_downlink_queue == NULL) {
        return 0;
    }
    return (uint16_t)uxQueueMessagesWaiting(s_downlink_queue);
}
