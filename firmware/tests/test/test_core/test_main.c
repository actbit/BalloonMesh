#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

#include "unity.h"

#include "balloon_led_queue.h"
#include "balloon_keepalive.h"
#include "balloon_neighbor.h"
#include "balloon_nmea.h"
#include "balloon_protocol.h"

void setUp(void) {}
void tearDown(void) {}

static void test_tick40_roundtrip(void) {
    int64_t now_ms = 1762560000123LL; /* arbitrary instant after 2025 */
    uint64_t tick = bm_tick40_from_unix_ms(now_ms);
    uint8_t packed[5];
    bm_pack_tick40_le(tick, packed);
    TEST_ASSERT_EQUAL_UINT64(tick, bm_unpack_tick40_le(packed));
    TEST_ASSERT_EQUAL_INT64(1762560000120LL, bm_tick40_to_unix_ms(tick));
    TEST_ASSERT_EQUAL_UINT64(1, bm_tick40_from_unix_ms(BM_LED_TICK_ORIGIN_UNIX_MS + 10));
}

static void test_led_wire_formats(void) {
    uint8_t buffer[8];
    bm_led_set_t set = {.execute_tick = 0x0102030405ULL, .r = 1, .g = 2, .b = 3};
    TEST_ASSERT_EQUAL_UINT(8, bm_encode_led_set(buffer, sizeof(buffer), &set));
    TEST_ASSERT_EQUAL_UINT8(0x05, buffer[0]);
    TEST_ASSERT_EQUAL_UINT8(0x04, buffer[1]);
    bm_led_set_t decoded;
    TEST_ASSERT_TRUE(bm_decode_led_set(buffer, 8, &decoded));
    TEST_ASSERT_EQUAL_UINT64(set.execute_tick, decoded.execute_tick);
    TEST_ASSERT_EQUAL_UINT8(3, decoded.b);

    bm_ftm_report_t report = {
        .anchor_id = 7, .distance_cm = 1234, .timestamp = 0xDEADBEEF, .batch_id = 9};
    TEST_ASSERT_EQUAL_UINT(8, bm_encode_ftm_report(buffer, sizeof(buffer), &report));
    TEST_ASSERT_EQUAL_UINT8(0xD2, buffer[1]); /* 1234 = 0x04D2 */
    bm_ftm_report_t report2;
    TEST_ASSERT_TRUE(bm_decode_ftm_report(buffer, 8, &report2));
    TEST_ASSERT_EQUAL_UINT16(1234, report2.distance_cm);
    TEST_ASSERT_EQUAL_UINT32(0xDEADBEEF, report2.timestamp);
    TEST_ASSERT_EQUAL_UINT8(9, report2.batch_id);

    bm_neighbor_report_t neighbor = {.batch_id = 3, .packet_index = 1, .entry_count = 2};
    neighbor.entries[0].mesh_addr = 0x1234;
    neighbor.entries[0].rssi_dbm = -55;
    neighbor.entries[1].mesh_addr = 0x00AA;
    neighbor.entries[1].rssi_dbm = -77;
    TEST_ASSERT_EQUAL_UINT(8, bm_encode_neighbor_report(buffer, sizeof(buffer), &neighbor));
    bm_neighbor_report_t neighbor2;
    TEST_ASSERT_TRUE(bm_decode_neighbor_report(buffer, 8, &neighbor2));
    TEST_ASSERT_EQUAL_UINT8(2, neighbor2.entry_count);
    TEST_ASSERT_EQUAL_UINT16(0x1234, neighbor2.entries[0].mesh_addr);
    TEST_ASSERT_EQUAL_INT8(-77, neighbor2.entries[1].rssi_dbm);
}

static void test_queue_raw_size_and_overwrite(void) {
    TEST_ASSERT_EQUAL_UINT(8, sizeof(bm_led_entry_t));
    TEST_ASSERT_EQUAL_UINT(4096, sizeof(((bm_led_queue_t *)0)->entries));

    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t red = {255, 0, 0};
    bm_rgb_t blue = {0, 0, 255};
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100, red, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_UPDATED, bm_ledq_set(&queue, 100, blue, 0, false));
    TEST_ASSERT_EQUAL_UINT16(1, queue.count);
    TEST_ASSERT_EQUAL_UINT8(255, queue.entries[0].b);
}

static void test_past_and_unsynced_behaviour(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t red = {255, 0, 0};
    TEST_ASSERT_EQUAL(BM_LEDQ_REJECTED_PAST, bm_ledq_set(&queue, 10, red, 20, true));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 10, red, 20, false));
    TEST_ASSERT_EQUAL_UINT16(1, bm_ledq_on_first_sync(&queue, 20));
    TEST_ASSERT_EQUAL_UINT16(0, queue.count);

    bm_ledq_init(&queue);
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100, red, 20, false));
    TEST_ASSERT_EQUAL_UINT16(0, bm_ledq_on_first_sync(&queue, 50));
    TEST_ASSERT_EQUAL_UINT16(1, queue.count);
}

static void test_capacity_policy(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t rgb = {1, 2, 3};
    for (uint32_t i = 0; i < BM_LED_QUEUE_CAPACITY; ++i) {
        TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100 + i, rgb, 0, false));
    }
    TEST_ASSERT_EQUAL_UINT16(BM_LED_QUEUE_CAPACITY, queue.count);
    TEST_ASSERT_EQUAL(BM_LEDQ_REJECTED_FULL, bm_ledq_set(&queue, 1000, rgb, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 50, rgb, 0, false));
    TEST_ASSERT_EQUAL_UINT16(BM_LED_QUEUE_CAPACITY, queue.count);
    TEST_ASSERT_EQUAL_UINT64(50, bm_led_entry_tick(&queue.entries[0]));
    TEST_ASSERT_EQUAL_UINT64(100 + BM_LED_QUEUE_CAPACITY - 2,
                             bm_led_entry_tick(&queue.entries[queue.count - 1]));
}

static void test_reset_cutoff_and_cancel(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t rgb = {4, 5, 6};
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 10, rgb, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 20, rgb, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 30, rgb, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_reset(&queue, 20));
    TEST_ASSERT_EQUAL_UINT16(1, queue.count);
    TEST_ASSERT_EQUAL_UINT64(30, bm_led_entry_tick(&queue.entries[0]));
    TEST_ASSERT_EQUAL(BM_LEDQ_REJECTED_CUTOFF, bm_ledq_set(&queue, 20, rgb, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_IGNORED, bm_ledq_reset(&queue, 20));
    TEST_ASSERT_EQUAL(BM_LEDQ_IGNORED, bm_ledq_reset(&queue, 15));

    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_cancel(&queue, 40, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_IGNORED, bm_ledq_cancel(&queue, 40, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_REJECTED_CANCELLED, bm_ledq_set(&queue, 40, rgb, 0, false));
}

static void test_set_now_and_execution(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t red = {255, 0, 0};
    bm_rgb_t green = {0, 255, 0};
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set_now(&queue, 50, red));
    TEST_ASSERT_EQUAL(BM_LEDQ_IGNORED, bm_ledq_set_now(&queue, 50, green));
    TEST_ASSERT_EQUAL_UINT8(255, queue.current.r);

    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100, green, 20, true));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 110, red, 20, true));
    TEST_ASSERT_EQUAL_UINT16(0, bm_ledq_on_first_sync(&queue, 20));
    bm_led_entry_t entry;
    TEST_ASSERT_TRUE(bm_ledq_take_due(&queue, 100, &entry));
    TEST_ASSERT_EQUAL_UINT64(100, bm_led_entry_tick(&entry));
    TEST_ASSERT_EQUAL_UINT8(255, queue.current.g);
    TEST_ASSERT_FALSE(bm_ledq_take_due(&queue, 105, &entry));
    TEST_ASSERT_TRUE(bm_ledq_take_due(&queue, 120, &entry));
    TEST_ASSERT_EQUAL_UINT64(110, bm_led_entry_tick(&entry));
}

static void test_forward_resync_discards_without_executing(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t rgb = {9, 9, 9};
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100, rgb, 50, true));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 200, rgb, 50, true));
    TEST_ASSERT_EQUAL_UINT16(1, bm_ledq_on_resync(&queue, 60, 150));
    TEST_ASSERT_EQUAL_UINT16(1, queue.count);
    TEST_ASSERT_EQUAL_UINT64(200, bm_led_entry_tick(&queue.entries[0]));
    TEST_ASSERT_EQUAL(BM_LEDQ_REJECTED_EXECUTED, bm_ledq_set(&queue, 120, rgb, 150, true));
}

static void test_queue_snapshot_roundtrip(void) {
    bm_led_queue_t queue;
    bm_ledq_init(&queue);
    bm_rgb_t red = {255, 0, 0};
    bm_rgb_t green = {0, 255, 0};
    bm_rgb_t blue = {0, 0, 255};
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 100, red, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set(&queue, 200, green, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_set_now(&queue, 10, blue));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_cancel(&queue, 300, 0, false));
    TEST_ASSERT_EQUAL(BM_LEDQ_APPLIED, bm_ledq_reset(&queue, 50));

    size_t capacity = bm_ledq_serialized_size();
    uint8_t *buffer = (uint8_t *)malloc(capacity);
    TEST_ASSERT_NOT_NULL(buffer);
    size_t written = bm_ledq_serialize(&queue, buffer, capacity);
    TEST_ASSERT_GREATER_THAN(0, written);
    TEST_ASSERT_EQUAL_UINT(0, bm_ledq_serialize(&queue, buffer, 4));

    bm_led_queue_t restored;
    TEST_ASSERT_TRUE(bm_ledq_deserialize(&restored, buffer, written));
    TEST_ASSERT_EQUAL_UINT16(2, restored.count);
    TEST_ASSERT_TRUE(restored.has_cutoff);
    TEST_ASSERT_EQUAL_UINT64(50, restored.cutoff_tick);
    TEST_ASSERT_TRUE(restored.has_last_now);
    TEST_ASSERT_EQUAL_UINT64(10, restored.last_now_tick);
    TEST_ASSERT_EQUAL_UINT8(255, restored.current.b);
    TEST_ASSERT_EQUAL_UINT8(1, restored.tombstone_count);
    TEST_ASSERT_EQUAL_UINT64(300, restored.tombstones[0]);
    TEST_ASSERT_FALSE(restored.clock_synced);
    TEST_ASSERT_EQUAL_UINT64(100, bm_led_entry_tick(&restored.entries[0]));
    TEST_ASSERT_EQUAL_UINT64(200, bm_led_entry_tick(&restored.entries[1]));

    TEST_ASSERT_EQUAL_UINT16(1, bm_ledq_on_first_sync(&restored, 150));
    TEST_ASSERT_EQUAL_UINT16(1, restored.count);
    TEST_ASSERT_EQUAL_UINT64(200, bm_led_entry_tick(&restored.entries[0]));

    buffer[0] = 'X';
    TEST_ASSERT_FALSE(bm_ledq_deserialize(&restored, buffer, written));
    free(buffer);
}

static void test_queue_monitor_hysteresis(void) {
    bm_queue_monitor_t monitor = {0};
    TEST_ASSERT_EQUAL_INT(0, bm_queue_monitor_update(&monitor, 300));
    TEST_ASSERT_EQUAL_INT(1, bm_queue_monitor_update(&monitor, 401));
    TEST_ASSERT_EQUAL_INT(0, bm_queue_monitor_update(&monitor, 400));
    TEST_ASSERT_EQUAL_INT(0, bm_queue_monitor_update(&monitor, 351));
    TEST_ASSERT_EQUAL_INT(2, bm_queue_monitor_update(&monitor, 350));
    TEST_ASSERT_EQUAL_INT(1, bm_queue_monitor_update(&monitor, 402));
}

static void make_nmea(const char *body, char *out, size_t size) {
    uint8_t checksum = 0;
    for (const char *p = body; *p != '\0'; ++p) {
        checksum ^= (uint8_t)*p;
    }
    snprintf(out, size, "$%s*%02X", body, checksum);
}

static void test_time_codec(void) {
    uint8_t buffer[8];
    int64_t value = 1762560000123LL;
    TEST_ASSERT_EQUAL_UINT(8, bm_encode_time_report(buffer, sizeof(buffer), value));
    int64_t decoded = 0;
    TEST_ASSERT_TRUE(bm_decode_time_report(buffer, sizeof(buffer), &decoded));
    TEST_ASSERT_EQUAL_INT64(value, decoded);
    TEST_ASSERT_FALSE(bm_decode_time_report(buffer, 7, &decoded));
}

static void test_nmea_parser(void) {
    bm_nmea_state_t state;
    bm_nmea_init(&state);
    char sentence[160];

    make_nmea("GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W",
              sentence, sizeof(sentence));
    TEST_ASSERT_TRUE(bm_nmea_feed_line(&state, sentence));
    TEST_ASSERT_TRUE(state.time_valid);
    TEST_ASSERT_TRUE(state.position_valid);
    TEST_ASSERT_EQUAL_INT64(764426119000LL, state.utc_ms);
    TEST_ASSERT_INT_WITHIN(2, 481173000, state.lat_e7);
    TEST_ASSERT_INT_WITHIN(2, 115166667, state.lon_e7);

    make_nmea("GNGGA,123520.00,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,",
              sentence, sizeof(sentence));
    TEST_ASSERT_TRUE(bm_nmea_feed_line(&state, sentence));
    TEST_ASSERT_EQUAL_INT64(764426120000LL, state.utc_ms);
    TEST_ASSERT_EQUAL_INT32(54540, state.alt_cm);

    make_nmea("GNGGA,123520.00,4807.038,N,01131.000,E,0,00,99.9,0.0,M,0.0,M,,",
              sentence, sizeof(sentence));
    TEST_ASSERT_TRUE(bm_nmea_feed_line(&state, sentence));
    TEST_ASSERT_FALSE(state.position_valid);
    TEST_ASSERT_FALSE(bm_nmea_feed_line(&state, "$GNRMC,broken*00"));
}

static void test_neighbor_median_and_top10(void) {
    int8_t values1[] = {-80, -40, -60};
    TEST_ASSERT_EQUAL_INT8(-60, bm_median_i8(values1, 3));
    int8_t values2[] = {-80, -41};
    TEST_ASSERT_EQUAL_INT8(-60, bm_median_i8(values2, 2)); /* -121 / 2 truncates */

    bm_neighbor_collector_t collector;
    bm_neighbor_init(&collector);
    bm_neighbor_begin(&collector, 1000, 20000);
    for (uint16_t i = 0; i < 12; ++i) {
        int8_t rssi = (int8_t)(-90 + (int8_t)i);
        TEST_ASSERT_TRUE(bm_neighbor_add(&collector, (uint16_t)(0x100 + i), rssi, 1000));
        TEST_ASSERT_TRUE(bm_neighbor_add(&collector, (uint16_t)(0x100 + i), (int8_t)(rssi + 4), 2000));
    }
    TEST_ASSERT_FALSE(bm_neighbor_add(&collector, 0x999, -50, 22000));
    bm_neighbor_end(&collector);
    bm_neighbor_result_t results[BM_NEIGHBOR_REPORT_COUNT];
    uint8_t count = bm_neighbor_results(&collector, results, BM_NEIGHBOR_REPORT_COUNT);
    TEST_ASSERT_EQUAL_UINT8(10, count);
    TEST_ASSERT_EQUAL_UINT16(0x10B, results[0].mesh_addr);
    TEST_ASSERT_EQUAL_UINT16(0x102, results[9].mesh_addr);
}

static void test_keepalive_respects_commands_and_rollover(void) {
    bm_keepalive_t state = {0};
    TEST_ASSERT_FALSE(bm_keepalive_update(&state, 11999, false));
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 12000, false));
    TEST_ASSERT_TRUE(state.active);
    TEST_ASSERT_FALSE(bm_keepalive_update(&state, 12999, false));
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 13000, false));
    TEST_ASSERT_FALSE(state.active);
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 24000, false));
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 24500, true));
    TEST_ASSERT_FALSE(state.active);
    TEST_ASSERT_FALSE(bm_keepalive_update(&state, 40000, true));
    TEST_ASSERT_FALSE(bm_keepalive_update(&state, 51999, false));
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 52000, false));
    state.last_start_ms = UINT32_MAX - 500u;
    state.active = false;
    TEST_ASSERT_TRUE(bm_keepalive_update(&state, 11499, false));
}

int main(void) {
    UNITY_BEGIN();
    RUN_TEST(test_keepalive_respects_commands_and_rollover);
    RUN_TEST(test_tick40_roundtrip);
    RUN_TEST(test_led_wire_formats);
    RUN_TEST(test_queue_raw_size_and_overwrite);
    RUN_TEST(test_past_and_unsynced_behaviour);
    RUN_TEST(test_capacity_policy);
    RUN_TEST(test_reset_cutoff_and_cancel);
    RUN_TEST(test_set_now_and_execution);
    RUN_TEST(test_forward_resync_discards_without_executing);
    RUN_TEST(test_queue_snapshot_roundtrip);
    RUN_TEST(test_queue_monitor_hysteresis);
    RUN_TEST(test_time_codec);
    RUN_TEST(test_nmea_parser);
    RUN_TEST(test_neighbor_median_and_top10);
    return UNITY_END();
}
