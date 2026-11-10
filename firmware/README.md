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

## Build all projects

```powershell
.\build-all.ps1
```

## Build individually
```powershell
cd balloon
pio run
pio run -t upload

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

### anchor
- `platformio.ini` の `-DBM_ANCHOR_ID=N`
- GPS UART ピンと SoftAP 設定は `bm_config.h`

### gateway
- `platformio.ini` の `BM_GATEWAY_USE_WEBSOCKET=1`（既定）
  - `0` にすると HTTP ポーリングへ切り替え
- `bm_config.h` の `BM_GATEWAY_ID` / `BM_SERVER_URL` / Wi-Fi 設定
- WSS/HTTPS の CA は `-DBM_TLS_CA_CERT=\"...\"` で指定

## 追加実装

- Provisioning UUID は Bluetooth MAC から個体別に生成
- 基準局が使えない場合、Mesh ピアへ `TIME_REQUEST` を送り、最初の `TIME_REPORT` で時計を補正
- Gateway は未送信 Mesh イベントを NVS に最大 128 件保持し、WebSocket/HTTP 再接続後に再送
- Gateway は `BM_GATEWAY_TOKEN` を hello / `X-Gateway-Token` ヘッダで送信
- サーバ側 `Gateway:Token` と一致しない接続は拒否

## Arduino BLE Mesh の注意

Arduino-ESP32 のプリビルド `libbt.a` は `CONFIG_BLE_MESH_NODE` が無効な sdkconfig でビルドされています。`balloon` / `gateway` はヘッダ構造体を有効化するため `-DCONFIG_BLE_MESH_NODE=1` を付けていますが、実機 Provisioning はプリビルド構成に依存します。詳細は `../docs/decisions.md` を参照してください。

## モバイルバッテリー停止防止（balloon）

LEDが消灯中は、12秒ごとにRGB各38/255（約15%）で1秒間点灯します。通常の点灯命令が有効な間は点灯しません。命令が点灯中に届いた場合も命令を優先します。この点灯は保存されるLED状態を変更しません。

実際のUSB電流はLEDや駆動回路に依存し、150mAや電源維持を保証しません。実機のUSB電流とバッテリーの停止防止効果を確認してください。
