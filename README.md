# BalloonMesh

ESP32-C3 の RGB バルーン群を、**PlatformIO (Arduino framework)**、**ASP.NET Core (.NET 10)**、**actbit/SvelteSharp の Client モード**、**PostgreSQL** で動かすプロトタイプ実装です。

引継ぎ仕様のうち、確定事項（40bit LED tick、512 件キュー、RESET cutoff、共通測位サイクル、FTM の 10 秒/30 秒集約、複数 Gateway の透明転送など）を実装し、未確定事項は [docs/decisions.md](docs/decisions.md) に「今回採用した暫定値」として分離しています。

---

## 構成

```text
firmware/
  balloon/                      PlatformIO project: ESP32-C3 バルーン
  anchor/                       PlatformIO project: GPS 基準局
  gateway/                      PlatformIO project: Mesh Gateway (WebSocket / HTTP)
  shared/
    balloon_core/               機種非依存 C: protocol / LED queue / NMEA / neighbor
    balloon_common/             共有設定と Protocol A codec
  tests/                        PlatformIO project: native Unity テスト

server/                         ASP.NET Core
  Auth/                         Cookie + OIDC / Discord + role policies
  Protocol/                     Protocol B codec
  Data/                         PostgreSQL (Npgsql)
  Services/                     FTM 集約 / 測位 / LED 配信 / エフェクト / Gateway / シミュレーション
  Hubs/                         SignalR
  Views/Effects.svelte          既定画面: 抽象エフェクト・コンポーザー
  Views/Home.svelte             詳細設定: 地図・LED 予約・Gateway 状態
  Views/lib/                    effect-model / fleet-map の .svelte.ts モジュール
  Views/Login.svelte            ログイン画面

third_party/SvelteSharp/        git submodule (actbit/SvelteSharp, commit pinned)
docs/decisions.md               未確定事項に対する今回の暫定決定
```

### SvelteSharp サブモジュール

SvelteSharp は git submodule として登録されています。初回取得時は再帰 clone してください。

```powershell
git clone --recurse-submodules <repository-url>
```

既に clone 済みの場合は:

```powershell
git submodule update --init --recursive
```

現在のピン留め commit は `9852875a1a21ce1d12c43144b60e498f00f9005c` です。サーバのプロジェクト参照は `third_party/SvelteSharp/` を直接参照します。

---

## クイックスタート

### 1. PostgreSQL

ローカル PostgreSQL 16 を想定しています。開発用ロールと DB を作成します。

```sql
CREATE ROLE balloonmesh LOGIN PASSWORD 'balloonmesh';
CREATE DATABASE balloonmesh OWNER balloonmesh;
```

接続文字列は `server/appsettings.json` の `ConnectionStrings:BalloonMesh` で変更できます。環境変数で上書きする場合:

```powershell
$env:ConnectionStrings__BalloonMesh = "Host=127.0.0.1;Port=5432;Database=balloonmesh;Username=balloonmesh;Password=balloonmesh"
```

テーブルは起動時に `CREATE TABLE IF NOT EXISTS` で作成され、初回は 50 台のバルーン・4 台の基準局・ゾーングループがシードされます。

### 2. サーバ起動

```powershell
dotnet run --project server/BalloonMesh.Server.csproj --urls http://0.0.0.0:5080
```

- Web UI（既定）: <http://localhost:5080/> — 抽象エフェクト・コンポーザー
- 詳細設定: <http://localhost:5080/control> — 地図・LED 予約・Gateway 状態
- SignalR: `/hubs/monitor`
- Gateway WebSocket: `/ws/gateway`
- Gateway HTTP: `POST /api/gateway/{id}/events`, `GET /api/gateway/{id}/commands?waitMs=10000`

デモシミュレーションは既定で有効です。詳細設定画面の右上 **Demo running / Start demo** で切り替えられます。無効化する場合:

```powershell
$env:Simulation__Enabled = "false"
```

### 3. テスト

```powershell
dotnet test server/BalloonMesh.Server.Tests/BalloonMesh.Server.Tests.csproj
```

PostgreSQL の `balloonmesh_tests` スキーマを使用します（`balloonmesh` DB 内、22 tests）。テスト前に自動で `TRUNCATE` と再シードを行います。

ファームウェアの native テスト:

```powershell
# MSYS2 の gcc が必要 (Windows)
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
cd firmware/tests
pio test -e native
```

（13 tests）

## 認証・認可

Cookie 認証を基盤に、OIDC と Discord OAuth2 を利用できます。UI、REST API、SignalR はロールポリシーで保護されます。

| ポリシー | ロール | 用途 |
|---|---|---|
| Viewer | Viewer / Operator / Admin | 地図・状態・履歴の閲覧 |
| Operator | Operator / Admin | LED 命令、初期位置、予約キャンセル |
| Admin | Admin | グループ管理、基準局更新、デモ、オンボーディング操作 |

- 未ログインの API は `401`、権限不足は `403`
- 未ログインのページは `/login` へリダイレクト
- Gateway の `/api/gateway/*` と `/ws/gateway` はユーザー認証ではなく `Gateway:Token` で認証
- SignalR `/hubs/monitor` は Viewer 以上を要求

### OIDC

`server/appsettings.json`:

```json
"Authentication": {
  "Oidc": {
    "Authority": "https://your-idp.example/realms/balloonmesh",
    "ClientId": "balloonmesh-web",
    "ClientSecret": "...",
    "AdminRoles": "balloon-admin",
    "OperatorRoles": "balloon-operator",
    "DefaultRole": "Viewer"
  }
}
```

- Callback: `/signin-oidc`
- Scope: `openid profile email`
- IdP の `role` / `roles` / `groups` クレームから Admin / Operator を判定
- Admin / Operator 以外は `DefaultRole`（既定 Viewer）。`None` にすると閲覧も拒否

### Discord

```json
"Authentication": {
  "Discord": {
    "ClientId": "...",
    "ClientSecret": "...",
    "AdminUserIds": "123456789012345678",
    "OperatorUserIds": "234567890123456789",
    "DefaultRole": "Viewer"
  }
}
```

- Callback: `/signin-discord`
- `identify` / `email` スコープ
- Discord ユーザー ID で Admin / Operator を付与

### ローカル開発用ログイン

`appsettings.Development.json` で `Authentication:Mock:Enabled=true` にすると `/auth/login/mock` が使えます。

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project server/BalloonMesh.Server.csproj --urls http://localhost:5080
```

ロールを試す場合:

- `/auth/login/mock` → Admin
- `/auth/login/mock?role=Operator`
- `/auth/login/mock?role=Viewer`

本番では Mock を無効にしてください。

## オンボーディング表示（MOCK）

実機 BLE Mesh Provisioner はまだ未実装のため、Web UI に **Admin 専用の MOCK オンボーディングパネル**を表示します。

- `GET /api/onboarding/mock` — モックデバイス一覧
- `POST /api/onboarding/mock/{id}/provision` — 数秒かけて `unprovisioned → provisioning → provisioned` をシミュレーション
- `POST /api/onboarding/mock/reset` — 初期状態へ戻す
- SignalR `onboardingUpdated` で全 Admin に反映

実際の Provisioner / 鍵配布 / model bind / group subscribe は未実装です。

### 4. ファームウェア

各デバイスは独立した PlatformIO プロジェクトです。

```powershell
cd firmware/balloon
pio run
pio run -t upload

cd ../anchor
pio run
pio run -t upload

cd ../gateway
pio run
pio run -t upload
```

| project | 役割 | 主な設定 |
|---|---|---|
| `firmware/balloon` | バルーン | Wi-Fi FTM Initiator、BLE Mesh Node、RGB LEDC |
| `firmware/anchor` | GPS 基準局 | NMEA UART、SoftAP、FTM Responder、UDP |
| `firmware/gateway` | Mesh Gateway | BLE Mesh Node、WebSocket（既定）/ HTTP ポーリング |

`anchor` は `platformio.ini` の `-DBM_ANCHOR_ID=N` で ID を変えてビルドします。`gateway` は `firmware/gateway/platformio.ini` の `BM_GATEWAY_ID` / `BM_SERVER_URL` / Wi-Fi 設定を環境に合わせて変更してください。

一括ビルド:

``powershell
.\firmware\build-all.ps1
``n
詳細は [firmware/README.md](firmware/README.md) を参照してください。

---

## 実装済みの主な仕様

### LED 時刻・キュー

- 40bit `execute_tick`（2025-01-01 UTC 起点、10ms、little-endian）
- `LED_SET` / `LED_CANCEL` / `LED_RESET_ALL` / `LED_SET_NOW` / `LED_RESET_AND_SET`
- 1 時刻 1 予約、同時刻は後着 RGB で上書き
- 実行済み・cutoff 以前・キャンセル済みの遅延到着を拒否
- 512 件満杯時は「最も遠い未来」を追い出し、近い予約を保護
- cutoff は単調増加、等しい/古い RESET を無視
- 401 件で `NEAR_FULL`、350 件で `RECOVERED` を 1 回通知（ヒステリシス）
- 前方ジャンプ再同期では通過した予約を実行せず破棄
- 未同期でも予約を保持し、初回同期で過去分を破棄
- LED キュー・cutoff・現在色を NVS に保存し、再起動時に復元（2 秒 debounce）

実装: `firmware/shared/balloon_core/src/balloon_led_queue.c`  
テスト: `firmware/tests/test/test_core/test_main.c`

### 測位

- バルーンは 180 秒 + 0〜30 秒の共通サイクルで、基準局データ取得 → FTM → 時刻補正 → 条件付き BLE 近隣探索を順に実行
- FTM 成功が 2 局以下のときだけ 20 秒の近隣探索
- `NEIGHBOR_REQUEST` / `NEIGHBOR_PROBE` は TTL=0、`NEIGHBOR_REPORT` は通常 TTL
- サーバ側で `(src, batch_id)` ごとに集約し、`t0+10s` で初回計算、`t0+30s` まで再計算
- 完全重複を破棄、同一 anchor は新しい timestamp を採用
- 2 局なら円交点 2 候補から前回位置に近い方を選択
- 3 局以上は重み付き最小二乗、近隣 RSSI は低重みの補助制約
- 古いバッチの計算結果が地図を上書きすることを許容

### サーバ / UI

- ASP.NET Core Minimal API + SignalR
- PostgreSQL にバルーン、履歴、基準局、グループ、演出ステップを保存
- 複数 Gateway の HTTP ポーリング / WebSocket を受ける透明転送
- 全 Gateway へ同一下りコマンドを配信
- バルーンごとの未実行キューを推定し、350 件を目標にローリング配信
- MapLibre GL JS + OpenStreetMap の 2D 地図
- 初回位置の地図クリック入力
- LED 即時 / 予約 / キャンセル / RESET / RESET_AND_SET
- デモシミュレーション（2 Gateway 相当の重複報告を含む）
- ゾーン作成・バルーン移動・空グループ削除の API / UI
- 予約一覧と LED_CANCEL 連携のキャンセル
- 個体別 Provisioning UUID（Bluetooth MAC 由来）
- 基準局が使えない場合の Mesh 時刻フォールバック（TIME_REQUEST/TIME_REPORT）
- Gateway の NVS オフラインスプール（128 件、再接続後に再送）
- Gateway 共有トークン認証（`Gateway:Token` / `X-Gateway-Token`）
- UI の Gateway 状態（prov/unprov、spool 件数、transport）

### 抽象エフェクト

既定画面（`/`）は、レイヤーやノードごとの色指定ではなく「光の動き」を選ぶコンポーザーです。

- `flow`: 選んだノードを起点に、光の帯が外へ流れる
- `wave`: 指定した角度へ面状の波が流れる
- `pulse`: 全ノードが同期して明滅する
- `rainbow`: 色相がノード間を流れる
- `twinkle`: ノードごとに異なるタイミングで明滅する
- `alternate`: アドレスの偶数・奇数の2組が交互に明滅する
- `solid`: 選んだ色ですべてのノードを点灯する

`EffectService` は約30秒先の色をフレームごとに計算し、既存の時刻指定 `LED_SET` として段階的に配信します。全ノードが同じ色の場合はグループ宛ての1命令にまとめます。開始直後は約30秒の準備時間があります。各ノードで時計が同期している必要があります。ファームウェアに新しいオペコードは不要です。ブラウザ側のプレビューは `Views/lib/effect-model.svelte.ts`、実機配信は `Services/EffectService.cs` が同じ式で計算します。

- `GET /api/effects` — 現在のエフェクト状態
- `POST /api/effects/start` — 抽象エフェクトを開始（Operator 以上）。実行中の変更は次の周期から反映
- `POST /api/effects/stop` — 次の周期の終わりに消灯し、配信を終了（途中での強制停止は行わない）
- SignalR `effectChanged` — 開始・変更予約・終了予約・終了を全クライアントへ通知。終了予約中は `stopAt`、変更予約中は `nextKind` と `nextAt` が入る

画面では繰り返し点灯が既定です。各周期の終わりに全ノード向けの消灯を予約し、40ms後に次の周期を予約します。色や動きの変更と送信終了は、予約済みの命令を取り消さず、その後の周期の区切りに反映します。周期は0.2秒以上です。30秒の先行時間は通信遅延を吸収する目安であり、配送を保証するものではありません。ノード数や再送・中継を含むMeshの処理能力は実機で測定してください。

ゲートウェイへ渡す前に予約が期限切れになった場合、画面に警告を表示します。ゲートウェイへの投入後に発生したMesh送信失敗やノードへの未到達は、この警告では検出できません。

地図の「送信命令からの予測」は、ゲートウェイに引き渡した命令をサーバー上の仮想キューでも消費して表示します。実機の時計が同期しており命令が届いたと仮定した予測であり、受信確認ではありません。ゲートウェイ未接続時は仮想キューも進みません。仮想キューはメモリー上にあり、サーバー再起動後は実機状態が不明になります。

### SvelteSharp Client モード

- `server/Views/Effects.svelte`（既定）と `Home.svelte`（詳細設定）をビルド時に Svelte 5 クライアント graph に変換
- 実行時に `Svelte.View("Effects" | "Home", model, SvelteRenderMode.Client, ...)`
- 再利用ロジックは `Views/lib/*.svelte.ts` に分離。UI 部品は Svelte 5 snippet でコンポーネント化
- Node.js / npm / Vite はアプリ実行時に不要（SvelteSharp のツールチェーン復元のみ初回ビルドで使用）
- MapLibre と SignalR はブラウザで CDN から遅延ロード

---

## Protocol B の暫定値

確定仕様の「Vendor Opcode 3B + 最大 8B」に合わせ、以下を今回の実装値としています。詳細は [docs/decisions.md](docs/decisions.md)。

| 項目 | 値 |
|---|---|
| Company ID | `0x02E5` (Espressif) |
| Vendor Opcode | `0xC00000 \| (op << 16) \| company_id` (ESP-IDF `ESP_BLE_MESH_MODEL_OP_3` と同一) |
| LED_SET | `0x01`, 8B |
| LED_CANCEL | `0x02`, 5B |
| LED_RESET_ALL | `0x03`, 5B |
| LED_SET_NOW | `0x04`, 8B |
| LED_RESET_AND_SET | `0x05`, 8B |
| QUEUE_STATUS | `0x06`, 3B |
| FTM_REPORT | `0x07`, 8B |
| NEIGHBOR_REQUEST | `0x08`, 0B |
| NEIGHBOR_PROBE | `0x09`, 0B |
| NEIGHBOR_REPORT | `0x0A`, 8B（2 件/パケット） |
| TIME_REQUEST | `0x0B`, 0B（Mesh 時刻フォールバック） |
| TIME_REPORT | `0x0C`, 8B（UTC ms、LE） |

Group Address:

| 用途 | Address |
|---|---:|
| バルーン向け（LED、時刻同期、近隣探索） | `0xC010` |
| ゲートウェイ向け（FTM、近隣レポート、キュー状態） | `0xC011` |

個別指示には各端末のユニキャストアドレスを使用します。各モデルは自分の役割のグループだけを購読するため、購読数はモデルあたり 1 個です。旧バージョンとの混在時は通信できないメッセージがあるため、バルーン、ゲートウェイ、サーバーを揃えて更新してください。

FTM_REPORT の 4B timestamp は、確定仕様で未決定のため **LED tick40 の下位 32bit（10ms 単位）** を採用しています。`0xFFFFFFFF` は時刻不明です。

---

## ハードウェア前提

| 機能 | ピン / 設定 |
|---|---|
| LED R/G/B (balloon) | XIAO ESP32C3 D0/D1/D2 (GPIO 2/3/4), LEDC 5kHz, 8bit |
| GPS UART | UART1, RX=7, TX=6, 9600bps |
| 基準局 SoftAP | `BM-ANCHOR-<id>`, WPA2, channel 6, FTM responder |
| バルーン Wi-Fi | 各基準局 SoftAP へ順に接続し、UDP で座標・時刻を取得 |
| Gateway | Wi-Fi STA → HTTP でサーバへ |

※ ESP32-C3 は Wi-Fi と BLE の共存を行うため、実機での電力・アンテナ・FTM 精度試験は別途必要です。

---

## PostgreSQL スキーマ

- `balloon_groups` — ゾーン / グループ
- `balloons` — 現在位置、初期位置、RGB、キュー件数、推定件数
- `anchors` — 基準局座標・時刻有効性
- `positions` — 測位履歴（UI には測定時刻を表示しない）
- `show_steps` — 予約コマンドの履歴

接続文字列は `ConnectionStrings:BalloonMesh`、テスト用スキーマは `Data:Schema` で指定します。Gateway:Token を設定すると Gateway の WebSocket hello / HTTP ヘッダ（`X-Gateway-Token`）が一致しない接続を拒否します。

---

## 既知の制約・注意

1. **Arduino framework の BLE Mesh Node**  
   Arduino-ESP32 のプリビルド `libbt.a` は `CONFIG_BLE_MESH_NODE` を無効にした sdkconfig でビルドされています。本実装はヘッダの構造体を有効化するため `-DCONFIG_BLE_MESH_NODE=1` を付けていますが、実機での Provisioning はコアのプリビルド構成に依存します。BLE Mesh Node を確実に使う場合は、`CONFIG_BLE_MESH_NODE=y` でビルドした Arduino-ESP32 / ESP-IDF ライブラリが必要です。Arduino を維持しつつ確実に動作させるための最重要確認事項です。
2. **Gateway の Protocol C**  
   サーバは WebSocket と HTTP ポーリングの両方を実装しています。Arduino Gateway は既定で WebSocket（`BM_GATEWAY_USE_WEBSOCKET=1`）を使用し、`BM_SERVER_URL` が `https://` の場合は WSS になります。HTTP ポーリングへ戻す場合は `platformio.ini` のフラグを 0 にしてください。CA を `BM_TLS_CA_CERT` で指定しない場合は証明書ピン留めなしになるため、本番では必ず CA を設定してください。
3. **FTM timestamp** と **NEIGHBOR_REPORT の 2 件/パケット** は仕様未確定のため暫定値です。実装凍結前に [docs/decisions.md](docs/decisions.md) の項目を確定してください。
4. **即時命令の順序**  
   `LED_SET_NOW` の `issued_tick` と `LED_RESET_AND_SET` の `cutoff` は意味が異なるため、相互の新旧比較は行いません。遅延到着時の色戻りリスクは仕様どおり残ります。
5. **LED_CANCEL の tombstone** は 64 件のリングバッファです。大量のキャンセルを長時間保持する要件がある場合は拡張が必要です。
6. **デモシミュレーション** はサーバ内で FTM 報告を合成します。実機 Gateway がない状態でも UI と集約ロジックを確認できますが、電波・Mesh の実特性は再現しません。
7. **UI の CDN 依存**  MapLibre GL JS と SignalR ブラウザクライアントは CDN から遅延ロードします。閉域網ではこれらのファイルを `wwwroot` 等に配置し、`Home.svelte` の URL を差し替えてください。
8. **認証**  Gateway の共有トークン認証（`Gateway:Token`）は実装済みです。Protocol A は 8B 共有トークンのみ、WSS は CA 未設定時に証明書ピン留めなしです。本番では TLS 証明書検証 / トークンローテーション / 署名付きコマンドを追加してください。
