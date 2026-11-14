# Firmware projects

デバイスごとに独立した PlatformIO プロジェクトです。共通ロジックは `shared/`、native テストは `tests/` に分離しています。

```text
firmware/
  balloon/    ESP32-C3 バルーン
  anchor/     GPS 基準局
  gateway/    Mesh Gateway
  shared/
    balloon_core/    機種非依存 C (protocol / LED queue / NMEA / neighbor)
    balloon_common/  共有 ESP32 設定と Protocol A codec
  tests/      native Unity テスト
```

## 前提

- PlatformIO Core (`pio`) が PATH 上にあること
- native テストは gcc が必要（Windows は MSYS2 の `C:\msys64\mingw64\bin` を PATH に追加）
- 初回ビルドは ESP-IDF の Bluetooth スタックもソースからビルドするため、数分〜十数分かかる

`build-all.ps1` は各 `pio` の終了コードを確認し、失敗時は非 0 で停止します。

## Build all projects

```powershell
.\build-all.ps1
```

初回は ESP-IDF の Bluetooth スタックを含めてソースからビルドするため時間がかかります。並列ビルドは `pio run -j 12` のように指定できます。2 回目以降は差分ビルドです。

## Build individually
```powershell
cd balloon
pio run
pio run -t upload

# BLE Mesh only bring-up (Protocol A / FTM disabled)
pio run -e balloon-mesh-bringup
pio run -e balloon-mesh-bringup -t upload

cd ../anchor
pio run
pio run -t upload

cd ../gateway
pio run
pio run -t upload
```

## Native tests

```powershell
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"   # Windows / MSYS2 gcc
cd tests
pio test -e native
```

## RGB LED（balloon）

balloon は XIAO ESP32C3 の **D0/D1/D2 (GPIO2/3/4)** を **LEDC 5kHz / 8bit** で初期化します。

- balloon: LED キュー / 現在色
- anchor / gateway: RGB LED なし（LED 制御は行いません）

実装: `shared/balloon_common/src/bm_led_output.c`  
設定: `shared/balloon_common/include/bm_config.h`

## 設定

### balloon
- `firmware/shared/balloon_common/include/bm_config.h`
  - 基準局 SSID / UDP ポート / トークン
  - LED GPIO
  - Group Address / Model ID / Company ID
- `balloon` 環境は通常ファームウェアとして `BM_ENABLE_POSITIONING=1`（Protocol A / FTM有効）でビルドする。起動後に BOOT ボタンを3秒長押しするとNVSを消去できる。
- `balloon-mesh-bringup` 環境は `BM_ENABLE_POSITIONING=0`（Wi-Fi/FTM無効）で、BLE Meshだけを確認するときに使う。開発用自動provisioningが有効になり、ユニキャストアドレスはBluetooth MACから自動生成される。

### anchor
- `platformio.ini` の `-DBM_ANCHOR_ID=N`
- GPS UART ピンと SoftAP 設定は `bm_config.h`

### gateway
- `platformio.ini` の `BM_GATEWAY_USE_WEBSOCKET=1`（既定）
  - `0` にすると HTTP ポーリングへ切り替え
- `gateway` / `gateway-dev` 環境は起動後に BOOT ボタンを3秒長押しするとNVSを消去できる。両環境ともGateway自身が標準Provisionerとして動作し、Wi-FiでサーバーからGatewayアドレスとノード範囲を取得する。ノード範囲は最初の64アドレスを使い切ると16アドレスずつ追加されるため、開発用と本番用でProvisionerの動作差はありません。
- Gateway IDはBluetooth MACから自動生成される。再現性が必要なテストや既存設備との互換性が必要な場合だけ `BM_GATEWAY_ID` をbuild flagで上書きする。
- 接続先は環境変数 `BM_SERVER_URL` でビルド時に指定する（空なら起動を拒否）。
- **本番 `gateway` 環境**（`BM_PRODUCTION_BUILD=1`）
  - `https://` 以外の接続先では起動しない。
  - サーバー証明書を発行したCAを `firmware/shared/balloon_common/include/bm_server_ca.h`（git管理外）に `#define BM_TLS_CA_CERT "-----BEGIN CERTIFICATE-----\n..."` として置く。無ければビルドエラーになる。
  - HTTPS/WSS ともCAチェーンとホスト名を検証する。
- **Mesh鍵**
  - 本番のNetKey/AppKeyはサーバーがデプロイごとに乱数生成し、DBの `mesh_network_keys` に保存する。Gatewayは `mesh-address` の応答で受け取る。
  - 鍵を受け取れるのは、そのGateway自身のAPIキーで認証したときだけ（共有 `Gateway:Token` では受け取れない）。
  - 各ノードのDevKeyはProvisioning時にECDHで個別に生成される。
  - 鍵はDBにしかないので、**DBのバックアップは必須**。DBを失うと既存ネットワークの鍵も失われる。
  - Gatewayは一度NVSに保存した鍵を優先し、サーバーの鍵と異なればエラーログを出す。鍵を変えるにはNVS消去と再Provisioningが必要（Key Refreshは未実装）。
- **`gateway-dev` / `balloon-mesh-bringup`**
  - 全デバイス共通の開発用テスト鍵と `http://` を使う。本番へは投入しない。
  - `BM_PRODUCTION_BUILD` と同時には有効にできない（ビルドエラー）。
- **Provisioning**
  - Provisioningは1台ずつ直列に行う。
  - 各ノードの設定（AppKey追加とモデルBind）はノードごとのジョブとして管理する。失敗しても破棄せず、バックオフしながら再試行し続ける。
  - 起動時にはNVSに保存済みの全ノードについて設定をやり直すので、設定の途中で電源が落ちても復旧する。
- Gateway APIキーはサーバーのAdmin画面でGateway IDごとに発行する。発行直後に表示された平文を保存し、ローカルのビルド時だけ `BM_GATEWAY_TOKEN` として渡す。キーはサーバー側ではハッシュのみ保存され、1 Gatewayにつき有効キーは1本（再発行時に旧キーは失効）である。

```powershell
$env:BM_GATEWAY_TOKEN = "bm_live_..."
$env:BM_SERVER_URL = "https://balloon.example.com"
$env:BM_WIFI_SSID = "..."
$env:BM_WIFI_PASS = "..."
cd firmware/gateway
pio run -e gateway
pio run -e gateway -t upload
```

`BM_GATEWAY_TOKEN` はソースへコミットしない。

**サーバー側の認証**
- APIキー発行済みのGatewayは、自分のAPIキーでしか認証できない。共有の `Gateway:Token`、空キー、別Gatewayのキーはすべて拒否される。
- `Gateway:Token` は移行用の共有キーで、キー未発行のGatewayにだけ有効。これで認証したGatewayはイベント送受信だけができ、Meshアドレスの払い出しとMesh鍵の取得はできない。
- 無認証の接続は `Gateway:AllowUnkeyedGateways=true`（開発用）のときだけ受け付ける。

BOOTを押したままリセットするとESP32-C3のROM書き込みモードに入るため、NVS消去は通常起動後に実行する。

MeshアドレスはGateway自身が `0x0001-0x00FF`、バルーンなどのノードが `0x0100-0x3FFF` を使う。Gateway自身のアドレスとノード範囲はサーバーの台帳で個体ごとに安定して予約され、複数Gatewayの範囲は重複しない。サーバーに既登録のバルーンアドレスも範囲予約から除外される。同じProvisioning UUIDを2台が検出した場合は先に取得したGatewayだけがProvisioningし、もう一方はサーバーの競合応答で処理を中止する。

## 追加実装

- Provisioning UUID は Bluetooth MAC から個体別に生成
- 基準局が使えない場合、Mesh ピアへ `TIME_REQUEST` を送り、最初の `TIME_REPORT` で時計を補正
- Gateway は未送信 Mesh イベントを NVS に最大 128 件保持し、WebSocket/HTTP 再接続後に再送。`ageMs` で滞留時間を通知する
- Balloon は起動時としきい値遷移時に実際の LED キュー件数を送信（周期送信なし、失敗時は再試行）
- Balloon の BLE 近隣探索は測位サイクル開始から 20 秒窓で実行し、FTM が 3 局以上成功した場合は結果を破棄
- Gateway は `BM_GATEWAY_TOKEN` を hello / `X-Gateway-Token` ヘッダで送信し、サーバーはGateway IDと組み合わせて検証
- サーバー管理画面で発行したAPIキーは、発行直後に一度だけ表示される
- `balloon` / `gateway` は BLE Mesh 設定を NVS に保存し、再起動後も Provisioning 状態と group subscription を復元
- `gateway` / `gateway-dev` は `POST /api/gateway/{id}/mesh-address` でGatewayアドレスを取得し、検出したノードごとに `POST /api/gateway/{id}/mesh-node-address` でサーバー払い出しのユニキャストアドレスを使ってProvisioningする

## BLE Mesh Node のビルド構成

Arduino-ESP32 のプリビルド `libbt.a` は `CONFIG_BLE_MESH_NODE` が無効な構成でビルドされています。`balloon` と `gateway` は Arduino を ESP-IDF 4.4.7 のコンポーネントとして組み込み、`sdkconfig.defaults` で Mesh Node と設定保存を有効化して Bluetooth スタックをソースからビルドします。そのため、どちらも `-DCONFIG_BLE_MESH_NODE=1` を指定せず、Arduino 同梱のプリビルド `libbt.a` も使いません。

- `CONFIG_BLE_MESH_SETTINGS=y` により、ユニキャストアドレス、NetKey/AppKey、model bind、group subscription、SEQ/RPL が NVS に保存されます
- Gateway は再起動後も Provisioning 済みのまま `0xC011` のテレメトリを受信できます
- 初回ビルドは ESP-IDF 全体をコンパイルするため時間がかかります。2 回目以降は差分ビルドです
- `CONFIG_BLE_MESH_MODEL_GROUP_COUNT` は既定値 3 のままで十分です（各モデルは 1 グループだけ購読）

詳細は `../docs/decisions.md` の項目 12 を参照してください。

## Mesh 宛先の変更

ブロードキャストは受信役割ごとに 2 グループへ分けます。バルーン向け（LED、時刻同期、近隣探索）は `0xC010`、ゲートウェイ向け（FTM、近隣レポート、キュー状態）は `0xC011` を使い、個別指示は端末のユニキャストアドレスに送ります。各モデルは自分の役割のグループだけを購読するため、購読数はモデルあたり 1 個です。ゾーン別の Mesh 宛先は使用しません。バルーン、ゲートウェイ、サーバーは同じバージョンへ更新してください。旧ファームウェアとの混在中は時刻同期やテレメトリが届かない場合があります。既存のゾーン宛て予約は自動的に全体へ変換されず、再作成が必要です。
