# クラウドホスティング・デプロイガイド (AWS Lightsail)

BalloonMesh は、常時接続 WebSocket (`/ws/gateway`, `/hubs/monitor`)、高頻度な FTM 測位バッチ集約ワーカー（250ms タイマー）、および実機の 512 件 LED キューを維持するためのインメモリ状態管理を行うため、サーバーレス（Scale-to-Zero）ではなく **常時稼働 VPS（Amazon Lightsail 等）** でのホスティングを推奨します。

---

## 1. 前提条件 (Prerequisites)

手元のマシンからスクリプトでデプロイを行うには、以下の準備が必要です。

### 必要なツール
* **AWS CLI** (`aws --version`)
  * `aws configure` 等で認証情報が設定されていること
* **SSH** および **rsync**（macOS / Linux 標準）
* **SSH 秘密鍵**（`~/.ssh/id_rsa`, `~/.ssh/id_ed25519` 等）
* HTTPSで利用する**公開ドメイン**（例: `mesh.example.com`）

### HTTPSの前提

Lightsailインスタンス上では、CaddyがTLSを終端し、次の経路でアプリケーションへ転送します。

```text
https://mesh.example.com (80/443) -> Caddy -> server:5080
```

`compose.https.yml` はアプリケーションの `5080` をホストへ公開せず、画面・API・SignalR・Gateway WebSocketを同じHTTPSドメインで処理します。CaddyがLet's Encryptの証明書を自動取得・更新します。

デプロイ時にインスタンスへ静的IPがなければ、`<INSTANCE_NAME>-ip` という名前で作成して割り当てます。DNSプロバイダー側で、次のAレコードを設定してください。

```text
mesh.example.com.  A  <Lightsailの静的IP>
```

静的IPはインスタンスの停止・起動後も変わりません。インスタンスを削除して作り直す場合は、残した静的IPを新しいインスタンスへ再アタッチします。

### ファイアウォールについて

`scripts/lightsail-deploy.sh` は、別セッションや環境側で管理しているLightsailのファイアウォール/CIDR設定を変更しません。HTTPSを公開する時点で、環境側でTCP `80` と `443` が許可されている必要があります。既存の `5080` ルールもこのスクリプトからは変更しませんが、HTTPS構成ではアプリケーションコンテナの `5080` はホストへバインドされません。

### 必要最小限の AWS IAM 権限
AWS 公式には Lightsail 専用の事前定義マネージドポリシーが存在しないため、デプロイを実行する IAM ユーザーに以下の **インラインポリシー（またはカスタムポリシー）** を付与してください：

```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Effect": "Allow",
            "Action": [
                "lightsail:*"
            ],
            "Resource": "*"
        }
    ]
}
```

---

## 2. デプロイの実行

リポジトリルートから以下のスクリプトを実行します。Lightsailインスタンスが存在しない場合は自動新規作成され、既に存在する場合は差分のみを反映します。スクリプトはファイアウォールを変更しないため、先にDNSと80/443の許可を準備してください。

### デモモード（デフォルト）
実機ハードウェアが手元にない状態で、仮想の 50 台バルーンをシミュレーション稼働させます：
```bash
./scripts/lightsail-deploy.sh --domain mesh.example.com
```

### 本番・実機接続モード
シミュレーションを停止し、物理 ESP32 Gateway からの接続待ち受け（本番稼働）状態で起動します：
```bash
./scripts/lightsail-deploy.sh --domain mesh.example.com --prod
```

### オプション一覧
| オプション | 説明 | デフォルト値 |
|---|---|---|
| `--domain <DOMAIN>` | HTTPSで利用する公開ドメイン | 必須（`LIGHTSAIL_DOMAIN`でも指定可） |
| `--demo` | デモモード（仮想バルーン50台シミュレーション） | 有効（デフォルト） |
| `--prod` | 本番実機モード（シミュレーション停止、物理Gateway待受） | - |
| `--bundle <ID>` | プラン指定（`small_3_0`: 2GB/$12/月, `micro_3_0`: 1GB/$7/月） | `small_3_0` |
| `--key <PATH>` | SSH 秘密鍵パス | `~/.ssh/` 下の既存鍵を自動検出 |
| `--token <TOKEN>` | 旧方式の全 Gateway 共通認証トークン | `GATEWAY_TOKEN` 環境変数または未設定 |
| `--name <NAME>` | Lightsail インスタンス名 | `balloonmesh-server` |

デプロイ完了後のURLは `https://<DOMAIN>/`、Gateway WebSocketは `wss://<DOMAIN>/ws/gateway` です。ESP32 GatewayはHTTPS用環境を使い、同じHTTPSオリジンを指定してビルドします。

```bash
cd firmware/gateway
BM_SERVER_URL="https://mesh.example.com" pio run -e gateway-https
```

実機Gatewayの証明書検証を有効にする場合は、既存の `BM_TLS_CA_CERT` も本番用CAバンドルとして指定してください。

---

## 3. インスタンスの破棄（課金停止）

動作確認完了後、以下のスクリプトでインスタンス・ディスク・キーペアを消去できます。静的IPは、同じIPを再利用できるようデフォルトでは保持します。

```bash
./scripts/lightsail-destroy.sh
```

静的IPも削除する場合は、明示的にオプションを付けます。

```bash
./scripts/lightsail-destroy.sh --delete-static-ip
```

インスタンスに割り当てていない静的IPには料金が発生するため、再利用しない場合は削除してください。

---

## 4. コスト試算（参考）

* **検証時**: 1 時間あたり約 2.5 円（使った時間分の日割り計算）。検証後に破棄すれば数十円以下。
* **常時稼働時**:
  * `small_3_0`（2GB RAM / 2 vCPU）: **$12 / 月**（約 1,860 円 / 月）
  * `micro_3_0`（1GB RAM / 2 vCPU）: **$7 / 月**（約 1,085 円 / 月）  
  *(※ 実測メモリ消費量はアプリ＋DBで約 135MB、OS 全体で約 620MB のため、1GB プランでも安定稼働可能です)*

## 5. Gateway API キーの初期設定

本番では全 Gateway 共通の `GATEWAY_TOKEN` ではなく、管理画面で Gateway ごとの API キーを発行します。

1. サーバーをデプロイし、Admin ロールで Web UI にログインする。
2. **Gateway API keys** パネルへ Gateway ID（Gateway が Bluetooth MAC から生成する ID）を入力して **発行**する。
3. 表示された `bm_live_...` をその場で保管する。平文は再表示されない。
4. Gateway のローカルビルド環境で `BM_GATEWAY_TOKEN` に設定してビルド・書き込みする。
5. Web UI の Gateway 一覧で接続・Provisioning 状態を確認する。
6. ローテーション時は新しいキーを発行して再書き込みし、必要なら旧キーの失効状態を管理画面で確認する（再発行時は旧キーが自動失効）。

API キーは `gateway_api_keys` にハッシュのみ保存されます。

サーバー側の `Gateway:Token` は移行用の共有キーです。
- 個別キーへ移行した本番環境では設定しないでください。
- キー発行済みの Gateway には、この共有キーは使えません。
- 共有キーで認証した Gateway は、Mesh アドレスの払い出しも Mesh 鍵の取得もできません。
- 無認証の接続は `Gateway:AllowUnkeyedGateways=true`（開発用）のときだけ許可されます。

Mesh の NetKey/AppKey はサーバーが初回に乱数で生成し、`mesh_network_keys` テーブルに保存します。
- 失うと既存の Mesh を操作できなくなるので、DB のバックアップ対象に必ず含めてください。
- ファームウェアには本番鍵は含まれません。
- Gateway のビルドには `https://` の `BM_SERVER_URL` が必要です。CA は Let's Encrypt のルートを内蔵しているので、Let's Encrypt の証明書なら追加設定は不要です。
