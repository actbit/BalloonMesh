# BalloonMesh

ESP32-C3 の RGB バルーン群を、**PlatformIO (Arduino + ESP-IDF)**、**ASP.NET Core (.NET 10)**、**actbit/SvelteSharp の Client モード**、**PostgreSQL** で動かすプロトタイプ実装です。

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
  BalloonMesh.Protocol/         Protocol B codec (server + debug tool で共有)
  Data/                         PostgreSQL (Npgsql)
  Services/                     FTM 集約 / 測位 / LED 配信 / Gateway / シミュレーション
  Hubs/                         SignalR
  Views/Home.svelte             SvelteSharp Client モードの UI
  Views/Login.svelte            ログイン画面

tools/
  BalloonMesh.DebugTool/        Avalonia UI デスクトップデバッグツール
  BalloonMesh.DebugTool.Core/   API クライアント / Gateway エミュレータ / Protocol inspector
  BalloonMesh.DebugTool.Cli/    ヘッドレス用デバッグ CLI
  BalloonMesh.DebugTool.Tests/  xUnit テスト (サーバ不要)
  BalloonMesh.DebugTool.Cli.Tests/  CLI の xUnit テスト (サーバ不要)

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

### 0. Docker Compose での起動（推奨・最も簡単）

ローカルの .NET 10 SDK や PostgreSQL の手動インストールを行わずに、Docker Compose でサーバとデータベースを一括起動できます。

```bash
# デモモード（仮想バルーン50台を自動生成してUI上で確認、デフォルト）
docker compose up -d --build

# 本番・実機モード（シミュレーションを停止し、物理ESP32 Gatewayの接続を待受）
SIMULATION_ENABLED=false docker compose up -d --build
```

- Web UI: <http://localhost:5080/>
- ログ確認: `docker compose logs -f server`
- 停止: `docker compose down`

---

### 1. クラウド / AWS Lightsail へのデプロイ

常時接続 WebSocket (`/ws/gateway`, `/hubs/monitor`) や測位バッチ集約ワーカーが稼働するため、Vercel 等のサーバーレスではなく常時起動 VPS（Amazon Lightsail 等）でのホスティングを推奨します。手元から 1 コマンドで作成・更新・破棄ができるスクリプトを用意しています：

```bash
# デプロイ（インスタンスが無ければ自動新規作成、既にあれば差分更新）
./scripts/lightsail-deploy.sh

# 本番実機モード（シミュレーション停止、物理ESP32待受）でデプロイ
./scripts/lightsail-deploy.sh --prod

# 破棄（インスタンス削除・課金停止）
./scripts/lightsail-destroy.sh
```

必要最小限の **IAM 権限設定（インラインポリシー）**、オプション一覧、コスト試算などの詳細は **[docs/deployment.md](docs/deployment.md)** を参照してください。

---

### 2. ローカル直接起動 (dotnet run) の場合

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

- Web UI: <http://localhost:5080/>
- SignalR: `/hubs/monitor`
- Gateway WebSocket: `/ws/gateway`
- Gateway HTTP: `POST /api/gateway/{id}/events`, `GET /api/gateway/{id}/commands?waitMs=10000`

デモシミュレーションは既定で有効です。UI 右上の **Demo running / Start demo** で切り替えられます。無効化する場合:

```powershell
$env:Simulation__Enabled = "false"
```

### 3. テスト

```powershell
dotnet test server/BalloonMesh.Server.Tests/BalloonMesh.Server.Tests.csproj
```

PostgreSQL の `balloonmesh_tests` スキーマを使用します（`balloonmesh` DB 内、48 tests）。テスト前に自動で `TRUNCATE` と再シードを行います。

ファームウェアの native テスト:

```powershell
# MSYS2 の gcc が必要 (Windows)
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
cd firmware/tests
pio test -e native
```

（14 tests）

デバッグツールのテスト:

```powershell
dotnet test tools/BalloonMesh.DebugTool.Tests/BalloonMesh.DebugTool.Tests.csproj
```

（45 tests、PostgreSQL 不要）

デバッグ CLI のテスト:

```powershell
dotnet test tools/BalloonMesh.DebugTool.Cli.Tests/BalloonMesh.DebugTool.Cli.Tests.csproj
```

（50 tests、PostgreSQL 不要）

### 4. デバッグツール (Avalonia UI)

サーバと Gateway を実機なしで確認するための **Avalonia UI 12 / .NET 10** 製デスクトップツールです。Windows 10/11 と macOS (Intel / Apple Silicon) で動作します。

```powershell
dotnet run --project tools/BalloonMesh.DebugTool/BalloonMesh.DebugTool.csproj
```

- **Fleet**: バルーン・Gateway・予約済み Show step の一覧、Show step のキャンセル
- **Commands**: LED 即時 / 予約 / キャンセル / RESET / RESET_AND_SET
- **Gateway simulator**: WebSocket / HTTP ポーリングの擬似 Gateway。下りコマンドの Protocol B デコードと、FTM / QUEUE_STATUS / NEIGHBOR_REPORT の注入
- **Protocol**: 既知 Opcode の生成と、任意 opcode / payload のデコード
- **Decoded log**: 送受信と手動デコードを TX / RX / DECODE として記録。全フィールド、raw JSON、方向 / transport / 検索フィルタ、ログ保存
- **認証**: Development の Mock ログイン、または Session Cookie を貼り付け
- **設定**: 秘密情報を除く入力値を ApplicationData 配下に保存

詳細と配布用ビルド手順は [tools/BalloonMesh.DebugTool/README.md](tools/BalloonMesh.DebugTool/README.md) を参照してください。

### 5. デバッグ CLI

同じ `BalloonMesh.DebugTool.Core` を使うヘッドレス用の CLI です。GUI を起動できない環境や CI から、サーバ API・擬似 Gateway・Protocol B のエンコード / デコードを確認できます。

```powershell
$cli = "tools/BalloonMesh.DebugTool.Cli/BalloonMesh.DebugTool.Cli.csproj"

# ヘルスチェックと Fleet 状態
dotnet run --project $cli -- --role Admin health
dotnet run --project $cli -- --role Admin state

# LED の即時反映（Development の Mock ログイン）
dotnet run --project $cli -- --role Admin led now --target-kind balloon --target 0x0101 --color "#FF8000"

# 擬似 Gateway を 60 秒動かし、FTM レポートを 1 件注入
dotnet run --project $cli -- --gateway-token secret gateway run --id cli-gw --duration 60 `
  --event ftm:anchor=1,distance=3000,batch=1

# Protocol B のオフライン encode / decode
dotnet run --project $cli -- protocol encode --op FTM_REPORT --anchor 1 --distance 3000 --batch 2
dotnet run --project $cli -- protocol decode --opcode 0xC102E5 --payload E821F8BB00102030
```

`--json` を付けると機械可読な JSON（`gateway run` は JSON Lines）になります。全コマンドは `--help` で確認できます。詳細は [tools/BalloonMesh.DebugTool.Cli/README.md](tools/BalloonMesh.DebugTool.Cli/README.md) を参照してください。

## 認証・認可

Cookie 認証を基盤に、OIDC と Discord OAuth2 を利用できます。UI、REST API、SignalR はロールポリシーで保護されます。

| ポリシー | ロール | 用途 |
|---|---|---|
| Viewer | Viewer / Operator / Admin | 地図・状態・履歴の閲覧 |
| Operator | Operator / Admin | LED 命令、初期位置、予約キャンセル |
| Admin | Admin | 基準局更新、デモ、オンボーディング操作 |

- 保護された API は未ログインで `401`、権限不足で `403`（`/api/health`、`/favicon.ico`、認証エンドポイントは公開）
- 未ログインのページは `/login` へリダイレクト
- Gateway の `/api/gateway/*` と `/ws/gateway` はユーザー認証ではなく、Gateway ID に紐づく API キー（`X-Gateway-Token` / WebSocket hello の `token`）で認証
- SignalR `/hubs/monitor` は Viewer 以上を要求

### Gateway API キー

Admin でログインすると、Web UI の **Gateway API keys** パネルから Gateway ごとの API キーを発行・失効できます。1 Gateway につき有効キーは 1 本で、再発行すると旧キーは自動的に失効します。キーの平文は発行直後に一度だけ表示され、サーバーにはハッシュだけが保存されます。

認証時は、Gateway が送る `gatewayId`（HTTP の URL パラメータまたは WebSocket hello）と API キーを組み合わせて照合します。キー発行済みの Gateway A のキーを、キー発行済みの Gateway B の ID で使うことはできません。管理画面で発行したキーを、Gateway のローカルビルド時に `BM_GATEWAY_TOKEN` として設定してください。

```powershell
$env:BM_GATEWAY_TOKEN = "bm_live_..."
cd firmware/gateway
pio run -e gateway
```

`Gateway:Token` は既存環境からの移行用の全 Gateway 共通キーです。個別 API キーで運用する本番環境では空にしてください。キー未発行の Gateway は既定で拒否されます。開発・初回登録用に `Gateway:AllowUnkeyedGateways` を `true` にした場合（`appsettings.Development.json` では有効）に限り、`Gateway:Token` も空なら認証なしで通します。本番では `false` のままにしてください。API キーを発行した Gateway は、空キーや誤ったキーを拒否します。

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

実機 Gateway は BLE Mesh Provisioner として自動動作します。Web UI の **Admin 専用 MOCK オンボーディングパネル**は、実機なしで画面を確認するための別系統のシミュレーションです。

- `GET /api/onboarding/mock` — モックデバイス一覧
- `POST /api/onboarding/mock/{id}/provision` — 数秒かけて `unprovisioned → provisioning → provisioned` をシミュレーション
- `POST /api/onboarding/mock/reset` — 初期状態へ戻す
- SignalR `onboardingUpdated` で接続中の全クライアントに反映（REST API は Admin のみ）

実機の割り当てはサーバーが一元管理します。`POST /api/gateway/{id}/mesh-address` でGateway自身のユニキャストアドレスとノード範囲を払い出し、`POST /api/gateway/{id}/mesh-node-address` でUUIDごとの安定したノードアドレスを払い出します。各Gatewayは最初に64アドレス、使い切ると16アドレスずつ追加され、複数Gateway間で範囲は重複しません。既存の `balloons` 台帳に登録済みのMeshアドレスも予約範囲から除外します。同じUUIDを別Gatewayが先に取得済みならHTTP 409となり、二重Provisioningを防ぎます。

Web UI のGatewayパネルでは、Gatewayアドレス、払い出し済み範囲、範囲ごとの使用数を確認できます。

### 5. ファームウェア

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
| `firmware/gateway` | Mesh Gateway / Provisioner | BLE Mesh Provisioner、WebSocket（既定）/ HTTP ポーリング |

`anchor` は `platformio.ini` の `-DBM_ANCHOR_ID=N` で ID を変えてビルドします。`gateway` のIDはBluetooth MACから自動生成され、必要な場合だけ `firmware/shared/balloon_common/include/bm_config.h` の `BM_GATEWAY_ID` / `BM_SERVER_URL` / Wi-Fi 設定（または `platformio.ini` の `-D` 上書き）を変更してください。`balloon` は通常の `balloon` 環境に加え、測位を無効化した BLE Mesh 立ち上げ用の `balloon-mesh-bringup` 環境も利用できます。

一括ビルド:

```powershell
.\firmware\build-all.ps1
```

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
- 起動時と 401 件 `NEAR_FULL` / 350 件 `RECOVERED` の遷移時にキュー件数を通知（周期的な送信はしない）
- 前方ジャンプ再同期では通過した予約を実行せず破棄
- 未同期でも予約を保持し、初回同期で過去分を破棄
- LED キュー・cutoff・現在色を NVS に保存し、再起動時に復元（2 秒 debounce）

実装: `firmware/shared/balloon_core/src/balloon_led_queue.c`  
テスト: `firmware/tests/test/test_core/test_main.c`

### 測位

- バルーンは 180 秒 + 0〜30 秒の共通サイクルで、基準局データ取得 → FTM → 時刻補正を実行
- BLE 近隣探索はサイクル開始から 20 秒窓で並行し、FTM 成功が 2 局以下のときだけ結果を送信（3 局以上なら破棄）
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
- グループの作成・バルーン移動・空グループ削除（`FleetService` / `DataStore`。HTTP API / UI は未公開）
- 予約一覧と LED_CANCEL 連携のキャンセル
- 個体別 Provisioning UUID（Bluetooth MAC 由来）
- BLE Mesh 設定の NVS 永続化（アドレス・鍵・model bind・group subscription・SEQ/RPL）
- 基準局が使えない場合の Mesh 時刻フォールバック（TIME_REQUEST/TIME_REPORT。バルーン間で交換し、サーバは透過転送のみ）
- Gateway の NVS オフラインスプール（128 件、再接続後に再送）
- Gateway ごとの API キー認証（`gateway_api_keys` / `X-Gateway-Token`）
- UI の Gateway 状態（prov/unprov、spool 件数、transport）

### SvelteSharp Client モード

- `server/Views/Home.svelte` をビルド時に Svelte 5 コンパイラでクライアント graph に変換
- 実行時に `Svelte.View("Home", model, SvelteRenderMode.Client, ...)`
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
| NEIGHBOR_REPORT | `0x0A`, 8B（2 件/パケット、未使用スロットは `mesh_addr=0` の 0 埋め） |
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
| Gateway | Wi-Fi STA → WebSocket（既定）/ HTTP ポーリングでサーバへ |

※ ESP32-C3 は Wi-Fi と BLE の共存を行うため、実機での電力・アンテナ・FTM 精度試験は別途必要です。

---

## PostgreSQL スキーマ

- `balloon_groups` — ゾーン / グループ
- `balloons` — 現在位置、初期位置、RGB、キュー件数、推定件数
- `anchors` — 基準局座標・時刻有効性
- `positions` — 測位履歴（UI には測定時刻を表示しない）
- `show_steps` — 予約コマンドの履歴
- `gateway_api_keys` — Gateway ごとの API キー（平文ではなくハッシュを保存）

接続文字列は `ConnectionStrings:BalloonMesh`、テスト用スキーマは `Data:Schema` で指定します。Gateway の API キーは `gateway_api_keys` にハッシュとして保存され、管理者だけが管理画面から発行・失効できます。Gateway の WebSocket hello / HTTP ヘッダ（`X-Gateway-Token`）は URL の Gateway ID に紐づけて検証されます。

---

## 既知の制約・注意

1. **BLE Mesh Node / Provisioner のビルド構成**
   `balloon` と `gateway` は `framework = arduino, espidf` とし、ESP-IDF 4.4.7 を Arduino のコンポーネントとして組み込みます。`sdkconfig.defaults` で `CONFIG_BLE_MESH_NODE=y`、`CONFIG_BLE_MESH_PROVISIONER=y`、`CONFIG_BLE_MESH_CFG_CLI=y`、`CONFIG_BLE_MESH_SETTINGS=y` を有効にして Bluetooth スタックをソースからビルドするため、Arduino 同梱のプリビルド `libbt.a` は使いません。Provisioning 情報（ユニキャストアドレス、NetKey/AppKey、model bind、group subscription、SEQ/RPL）は NVS に保存され、再起動後も復元されます。初回ビルドは ESP-IDF 全体を作るため時間がかかりますが、2 回目以降は差分ビルドになります。詳細は [firmware/README.md](firmware/README.md) と [docs/decisions.md](docs/decisions.md) の項目 12 を参照してください。
2. **Gateway の Protocol C**  
   サーバは WebSocket と HTTP ポーリングの両方を実装しています。Arduino Gateway は既定で WebSocket（`BM_GATEWAY_USE_WEBSOCKET=1`）を使用し、`BM_SERVER_URL` が `https://` の場合は WSS になります。HTTP ポーリングへ戻す場合は `platformio.ini` のフラグを 0 にしてください。本番 `gateway` 環境は `https://` と CA（`bm_server_ca.h` の `BM_TLS_CA_CERT`）が必須で、証明書チェーンとホスト名を検証します。`http://` や検証なしの TLS は `gateway-dev`（`BM_ALLOW_INSECURE_TRANSPORT=1`）でのみ使えます。
3. **FTM timestamp** と **NEIGHBOR_REPORT の 2 件/パケット** は仕様未確定のため暫定値です。実装凍結前に [docs/decisions.md](docs/decisions.md) の項目を確定してください。
4. **即時命令の順序**  
   `LED_SET_NOW` の `issued_tick` と `LED_RESET_AND_SET` の `cutoff` は意味が異なるため、相互の新旧比較は行いません。遅延到着時の色戻りリスクは仕様どおり残ります。
5. **LED_CANCEL の tombstone** は 64 件のリングバッファです。大量のキャンセルを長時間保持する要件がある場合は拡張が必要です。
6. **デモシミュレーション** はサーバ内で FTM 報告を合成します。実機 Gateway がない状態でも UI と集約ロジックを確認できますが、電波・Mesh の実特性は再現しません。
7. **UI の CDN 依存**  MapLibre GL JS と SignalR ブラウザクライアントは CDN から遅延ロードします。閉域網ではこれらのファイルを `wwwroot` 等に配置し、`Home.svelte` の URL を差し替えてください。
8. **認証**  Gateway ごとの API キー発行・失効（`gateway_api_keys` / `X-Gateway-Token`）を実装済みです。`Gateway:Token` は移行用の共有キーとして残しています。本番では TLS 証明書検証を有効にし、キー発行後は共有キーを空にしてください。Protocol A は 8B 共有トークンのみ、WSS は CA 未設定時に証明書ピン留めなしです。
9. **Provisioner 鍵**  現在のGateway Provisionerは開発と本番の動作差を減らすため共有のプロトタイプ鍵を使用しています。本番投入前にNetKey/AppKeyを安全なNVS領域またはデバイス固有のProvisioning経路へ移し、鍵ローテーションを追加してください。
