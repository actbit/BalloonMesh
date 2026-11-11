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
- `balloon` 環境は通常ファームウェアとして `BM_ENABLE_POSITIONING=1`（Protocol A / FTM有効）でビルドする。
- `balloon-mesh-bringup` 環境は `BM_ENABLE_POSITIONING=0`（Wi-Fi/FTM無効）で、BLE Meshだけを確認するときに使う。

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

Arduino-ESP32 のプリビルド `libbt.a` は `CONFIG_BLE_MESH_NODE` が無効な構成でビルドされています。`balloon` はArduinoをESP-IDF 4.4.7のコンポーネントとして組み込み、`sdkconfig.defaults`でMesh Nodeを有効化してBluetoothスタックを再ビルドします。そのため、バルーン側では `-DCONFIG_BLE_MESH_NODE=1` を指定しません。

`gateway` は現時点ではArduino単体の構成で、Mesh Node対応を含むBluetoothスタックの再ビルドは未実施です。GatewayのESP-IDF統合と実機Provisioningは別途検証します。詳細は `../docs/decisions.md` を参照してください。

## Mesh 宛先の変更

ブロードキャストは受信役割ごとに 2 グループへ分けます。バルーン向け（LED、時刻同期、近隣探索）は `0xC010`、ゲートウェイ向け（FTM、近隣レポート、キュー状態）は `0xC011` を使い、個別指示は端末のユニキャストアドレスに送ります。各モデルは自分の役割のグループだけを購読するため、購読数はモデルあたり 1 個です。ゾーン別の Mesh 宛先は使用しません。バルーン、ゲートウェイ、サーバーは同じバージョンへ更新してください。旧ファームウェアとの混在中は時刻同期やテレメトリが届かない場合があります。既存のゾーン宛て予約は自動的に全体へ変換されず、再作成が必要です。
