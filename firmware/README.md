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
- `gateway` / `gateway-dev` 環境は起動後に BOOT ボタンを3秒長押しするとNVSを消去できる。`gateway-dev` はWi-Fiでサーバーから未使用の `0x4000-0x7FFF` を取得して開発用自動provisioningを行い、通常の `gateway` 環境は標準Provisionerによるprovisioning情報を使用する。
- Gateway IDはBluetooth MACから自動生成される。再現性が必要なテストや既存設備との互換性が必要な場合だけ `BM_GATEWAY_ID` をbuild flagで上書きする。`BM_SERVER_URL` / Wi-Fi 設定も `bm_config.h` で指定する。
- WSS/HTTPS の CA は `-DBM_TLS_CA_CERT=\"...\"` で指定

BOOTを押したままリセットするとESP32-C3のROM書き込みモードに入るため、NVS消去は通常起動後に実行する。

開発用アドレスはballoonが `0x0100-0x3FFF`、gatewayが `0x4000-0x7FFF` の範囲を使う。gatewayの開発用アドレスはサーバーの台帳で個体ごとに予約される。再現性が必要なテストでは `BM_DEV_UNICAST_ADDR` をbuild flagで上書きできる。

## 追加実装

- Provisioning UUID は Bluetooth MAC から個体別に生成
- 基準局が使えない場合、Mesh ピアへ `TIME_REQUEST` を送り、最初の `TIME_REPORT` で時計を補正
- Gateway は未送信 Mesh イベントを NVS に最大 128 件保持し、WebSocket/HTTP 再接続後に再送。`ageMs` で滞留時間を通知する
- Balloon は起動時としきい値遷移時に実際の LED キュー件数を送信（周期送信なし、失敗時は再試行）
- Balloon の BLE 近隣探索は測位サイクル開始から 20 秒窓で実行し、FTM が 3 局以上成功した場合は結果を破棄
- Gateway は `BM_GATEWAY_TOKEN` を hello / `X-Gateway-Token` ヘッダで送信
- サーバ側 `Gateway:Token` と一致しない接続は拒否
- `balloon` / `gateway` は BLE Mesh 設定を NVS に保存し、再起動後も Provisioning 状態と group subscription を復元

## BLE Mesh Node のビルド構成

Arduino-ESP32 のプリビルド `libbt.a` は `CONFIG_BLE_MESH_NODE` が無効な構成でビルドされています。`balloon` と `gateway` は Arduino を ESP-IDF 4.4.7 のコンポーネントとして組み込み、`sdkconfig.defaults` で Mesh Node と設定保存を有効化して Bluetooth スタックをソースからビルドします。そのため、どちらも `-DCONFIG_BLE_MESH_NODE=1` を指定せず、Arduino 同梱のプリビルド `libbt.a` も使いません。

- `CONFIG_BLE_MESH_SETTINGS=y` により、ユニキャストアドレス、NetKey/AppKey、model bind、group subscription、SEQ/RPL が NVS に保存されます
- Gateway は再起動後も Provisioning 済みのまま `0xC011` のテレメトリを受信できます
- 初回ビルドは ESP-IDF 全体をコンパイルするため時間がかかります。2 回目以降は差分ビルドです
- `CONFIG_BLE_MESH_MODEL_GROUP_COUNT` は既定値 3 のままで十分です（各モデルは 1 グループだけ購読）

詳細は `../docs/decisions.md` の項目 12 を参照してください。

## Mesh 宛先の変更

ブロードキャストは受信役割ごとに 2 グループへ分けます。バルーン向け（LED、時刻同期、近隣探索）は `0xC010`、ゲートウェイ向け（FTM、近隣レポート、キュー状態）は `0xC011` を使い、個別指示は端末のユニキャストアドレスに送ります。各モデルは自分の役割のグループだけを購読するため、購読数はモデルあたり 1 個です。ゾーン別の Mesh 宛先は使用しません。バルーン、ゲートウェイ、サーバーは同じバージョンへ更新してください。旧ファームウェアとの混在中は時刻同期やテレメトリが届かない場合があります。既存のゾーン宛て予約は自動的に全体へ変換されず、再作成が必要です。
