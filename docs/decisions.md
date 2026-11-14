# 実装上の暫定決定（未確定仕様の扱い）

この文書は、引継ぎ仕様の「未確定事項」に対して **今回のプロトタイプで採用した値** を記録するものです。仕様の確定事項を変更するものではありません。実装凍結前に、ここに書かれた暫定値を関係者で確定してください。

## 1. FTM_REPORT の 4B timestamp

**暫定:** `LED tick40` の下位 32bit（2025-01-01 UTC 起点、10ms 単位）を格納する。`0xFFFFFFFF` は時刻不明。

理由:

- LED 側の 40bit tick と同じ時間軸で扱える
- 30 秒バッチ内の新旧比較には 10ms 分解能が十分
- 32bit で約 497 日ごとに周回するため、比較は `(int32_t)(a - b)` のラップ比較で行う
- サーバはバッチの `t0` と組み合わせて絶対時刻を復元できる

代替案: Unix 秒（uint32）。分解能は 1 秒で、2106 年まで表現可能。

## 2. NEIGHBOR_REPORT のパケット形式

**暫定:** 2 件/非分割パケット(合計 8B)、最大 5 パケットで 10 台。

| Offset | Size | 内容 |
|---|---:|---|
| 0 | 1B | `batch_id` |
| 1 | 1B | `packet_index` (0〜4) |
| 2–4 | 3B | entry 0: `mesh_addr` LE 2B + `rssi_dbm` int8 |
| 5–7 | 3B | entry 1: `mesh_addr` LE 2B + `rssi_dbm` int8 |

エントリ数フィールドは廃止した。非分割アクセスメッセージのペイロード上限が 8B のため、未使用スロットを `mesh_addr = 0`(0 埋め)で表現し、受信側は 0 以外のスロットだけを有効エントリとして扱う。`mesh_addr = 0` は Mesh ユニキャストアドレスとして無効なため衝突しない。並び順は RSSI 中央値の強い順。

## 3. Vendor Opcode / Model / Group

**暫定:**

- Company ID: `0x02E5`（Espressif）
- Vendor Opcode: `0xC00000 | (op << 16) | company_id`
  - ESP-IDF `ESP_BLE_MESH_MODEL_OP_3(op, company_id)` と同一の数値表現
- op: `0x01`〜`0x0C`（README の表）
- Model ID
  - `0x0001`: control（LED コマンド）
  - `0x0002`: telemetry（FTM / NEIGHBOR / QUEUE）
- Group Address: README の表

本番では Company ID を取得し、opcode と Model ID を凍結してください。

## 4. LED_CANCEL の tombstone

**暫定:** デバイス内に 64 件のリングバッファでキャンセル済み tick を保持する。

- `LED_CANCEL` 受信時に予約が未着でも tombstone を追加
- その後届いた同一 tick の `LED_SET` を拒否
- cutoff / 初回同期 / 前方ジャンプ時に古い tombstone を破棄
- 64 件を超えると最も古い tombstone を上書き

仕様で「履歴を持つか」が未確定のため、まずは小さい固定領域で実装しています。

## 5. RESET cutoff の発行規則

**暫定:**

- サーバは対象バルーンごとに最後に送った cutoff を保持
- 新しい RESET の cutoff は `max(要求値, 前回 cutoff + 1)` に単調増加補正
- `cutoffAt` 未指定時は現在時刻
- 未来の cutoff も許可（指定期間の打ち切り）
- `LED_RESET_AND_SET` で RESET が無視された場合（等しい/古い cutoff）は、即時色変更も行わない

## 6. 即時命令の順序

**暫定:**

- `LED_SET_NOW` は `issued_tick`、`LED_RESET_AND_SET` は `cutoff_tick` を使う
- 両者は意味が異なるため相互比較しない
- `LED_SET_NOW` は同じ `issued_tick` 以下を無視
- サーバは対象バルーン集合の最大 `issued_tick` を基に単調増加を保証
- 古い `LED_RESET_AND_SET` が新しい `LED_SET_NOW` の後に遅延到着すると色が戻る可能性は仕様どおり許容

## 7. Gateway ↔ Server (Protocol C)

**暫定:** JSON over HTTP と WebSocket の両方。

- HTTP uplink: `POST /api/gateway/{id}/events`
- HTTP downlink: `GET /api/gateway/{id}/commands?waitMs=...`
- WebSocket: `/ws/gateway`
  - hello: `{"type":"hello","gatewayId":"gw-01","firmware":"0.1.0","token":"...","provisioned":true,"spoolPending":0}`
  - uplink: `{"type":"mesh","src":257,"dst":49169,"opcode":...,"payload":"...HEX...","rssi":-55,"ttl":7,"ageMs":0}`（`49169` = `0xC011`、ゲートウェイ向けテレメトリグループ。`ageMs` は spool 内で待った時間）
  - downlink: `{"type":"mesh","dst":...,"opcode":...,"payload":"...HEX...","ttl":7}`

payload は hex。ACK は実装していません。Gateway は重複排除・集約をせず、受信した全メッセージを転送します。

## 8. サーバ LED 配信

**暫定:**

- バルーンごとの送信済み予約 tick をリストで保持し、実行時刻を過ぎたものを除去
- サーバ再起動時は DB の `sent` かつ未来の show step から送信済み tick を復元する
- `QUEUE_STATUS` の実件数と送信済み推定件数の大きい方を有効件数とする
- バルーンは起動時としきい値遷移時に実際のキュー件数を送る（周期送信はしない）
- 有効件数が 350 未満のバルーンにだけ予約を送る
- Gateway が 0 台のときは pending のまま保持し、Gateway 接続後に送る
- キューが満杯の Gateway だけをスキップし、余裕のある Gateway へは配信する（全台満杯のときだけ失敗）
- 全台向けは 1 メッセージを `0xC010` に送り、対象バルーン全員の推定件数を加算
- ゾーン / グループ宛の LED 命令は現在のサーバ API では受け付けない（全台またはユニキャストを使用）
- 実行時刻を過ぎた pending は破棄
- キャンセル / RESET が Gateway に届いたら、未送信の pending も同じ tick / cutoff で取り消す

## 9. 近隣探索の詳細

**暫定:**

- REQUEST は測位サイクル開始時に 1 回送信し（TTL=0、バルーングループ `0xC010`）、FTM が 3 局以上成功した場合は結果を破棄
- REQUEST を受けたバルーンは、直後〜20 秒の窓で 3 回の PROBE をランダム間隔で送信
- 要求元は `recv_ttl == 0` の PROBE だけを RSSI 収集対象にする
- 同一近隣の複数サンプルは整数 median（偶数個は切り捨て平均）
- 上位 10 台を RSSI 降順で報告
- REQUEST/PROBE の request_id は持たない（同時 REQUEST は最初の 1 件のみ応答）
- 複数要求の重なり制御、REPORT の欠落耐性は今後の課題

## 10. 測位計算

**暫定:**

- 2 局: 2 円交点の 2 候補から前回位置に近い方
- 3 局以上: 重み付き線形最小二乗
- 1 局: 前回位置からの方向を保ち、距離だけ更新（前回位置がない場合は解を出さない）
- 近隣 RSSI: ログ距離モデルで距離に変換し、重み 0.15 の補助制約
- FTM_REPORT が 1 件も届かない場合でも、NEIGHBOR_REPORT がバッチを開いて近隣のみで測位を試みる
- 前回位置がない 2 局ケース: 候補 1 を選択
- 局所平面は equirectangular 近似（数百 m 規模）

## 11. 時刻同期・再同期

**暫定:**

- 基準局の `utc_ms` を受信した瞬間の `esp_timer` を基準に時計を即時補正
- 初回同期: `now` 以前の予約を実行せず破棄
- 再同期で 1 秒を超える前方ジャンプ: 通過した予約を実行せず破棄
- 後方ジャンプ: 予約は保持し、実行済み tick 以下の再登録を拒否
- 100ms 同期精度は実機の GNSS / Mesh 遅延を含めて未検証

## 12. BLE Mesh Node のビルド構成

**重要:** PlatformIO の `framework = arduino`（espressif32 6.9.0 / Arduino-ESP32 2.0.17）が同梱する `libbt.a` は、`CONFIG_BLE_MESH_NODE` を無効にした sdkconfig でビルドされています。本実装では `balloon` と `gateway` の両方を次の構成に統一しています。

- `balloon` / `gateway`: `framework = arduino, espidf` とし、ESP-IDF 4.4.7 を Arduino のコンポーネントとして組み込む。各プロジェクトの `sdkconfig.defaults` で `CONFIG_BLE_MESH_NODE=y`、`CONFIG_BLE_MESH_SETTINGS=y` ほかを有効化して Bluetooth スタックをソースからビルドし、`src/CMakeLists.txt` でアプリのソースを ESP-IDF に登録する。Arduino 同梱のプリビルド `libbt.a` と `-DCONFIG_BLE_MESH_NODE=1` は使わない。
- `CONFIG_BLE_MESH_SETTINGS=y` により、ユニキャストアドレス、NetKey/AppKey、model bind、group subscription、シーケンス番号、Replay Protection List が NVS に保存され、再起動後も Provisioning 状態が復元される。
- Gateway は Wi-Fi STA と BLE Mesh Node を同時に動かすため、実機で Wi-Fi/BLE 共存と再起動後の購読復元を確認する。
- `CONFIG_BLE_MESH_MODEL_GROUP_COUNT` は既定値 3 のままでよい。現行コードはモデルごとに `0xC010` または `0xC011` の 1 グループだけを購読する。ゾーン購読を再導入する場合は増やす。

Arduino を維持する場合の推奨確認手順:

1. 実機で `esp_ble_mesh_node_prov_enable()` が成功するか
2. Provisioner から UUID が見え、Provisioning が完了するか
3. Provision 完了後に group subscribe と `client_model_send_msg` が成功するか

## 13. 実装済みの追加項目

- バルーン LED キューの NVS スナップショット保存・復元（2 秒 debounce）。スナップショットは NVS の 1 ページ（実質約 4KB）に収まるよう合計 495 件（予約 + tombstone）を上限とし、あふれた場合は最も遠い未来の予約から落とす
- BLE Mesh 設定（アドレス・鍵・model bind・group subscription・SEQ/RPL）の NVS 永続化（`CONFIG_BLE_MESH_SETTINGS=y`、balloon / gateway）
- サーバのグループ作成・バルーン移動・空グループ削除（`FleetService` / `DataStore`。HTTP API / UI は未公開）
- 予約コマンド一覧と LED_CANCEL 連携キャンセル
- Gateway の WebSocket（WSS 対応）/ HTTPS HTTP ポーリング二系統
- Gateway の HTTPS 接続（CA 未設定時はピン留めなし）
- サーバ再起動時の queued 予約再投入
- 基準局座標の更新 API（PUT /api/anchors/{id}）
- 個体別 Provisioning UUID（Bluetooth MAC 由来）
- Mesh 時刻フォールバック（TIME_REQUEST/TIME_REPORT、バルーングループ 0xC010）
- Gateway の NVS オフラインスプール（128 件、再接続後に再送）
- Gateway 共有トークン認証（hello / X-Gateway-Token）
- UI の Gateway 状態（prov/unprov、spool 件数、transport）
- Cookie + OIDC / Discord 認証
- Viewer / Operator / Admin のロールポリシー
- Admin 専用 MOCK オンボーディング UI / API / SignalR

## 14. 未実装 / 今後の課題

- `LED_EFFECT_NOW` / `LED_FLOW_NOW`
- Mesh Relay の選択的 ON/OFF、LPN/Friend 構成
- Provisioner 用 NetKey/AppKey の安全な本番保管・ローテーション（現状の自動Provisioning経路はプロトタイプ固定鍵）
- Protocol C の ACK・再送・TLS 証明書検証
- 複数 Gateway 同時送信時の帯域・衝突評価
- 50 台実機での 100ms 同期精度・電力試験
- 初回 Wi-Fi 資格情報設定（SmartConfig / BLE Wi-Fi provisioning）
- 実機Provisioningの電波環境・再試行・二台Gateway同時検出時の実機評価（サーバー側のUUID競合防止と64→16アドレス払い出しは実装済み）
- 音楽タイムライン編集
