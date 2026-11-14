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

リポジトリルートから以下のスクリプトを実行します。  
**Lightsail インスタンスが存在しない場合は自動新規作成（ポート開放・初回デプロイ含む）され、既に存在する場合は差分のみを高速反映**します。

### デモモード（デフォルト）
実機ハードウェアが手元にない状態で、仮想の 50 台バルーンをシミュレーション稼働させます：
```bash
./scripts/lightsail-deploy.sh
```

### 本番・実機接続モード
シミュレーションを停止し、物理 ESP32 Gateway からの接続待ち受け（本番稼働）状態で起動します：
```bash
./scripts/lightsail-deploy.sh --prod
```

### オプション一覧
| オプション | 説明 | デフォルト値 |
|---|---|---|
| `--demo` | デモモード（仮想バルーン50台シミュレーション） | 有効（デフォルト） |
| `--prod` | 本番実機モード（シミュレーション停止、物理Gateway待受） | - |
| `--bundle <ID>` | プラン指定（`small_3_0`: 2GB/$12/月, `micro_3_0`: 1GB/$7/月） | `small_3_0` |
| `--key <PATH>` | SSH 秘密鍵パス | `~/.ssh/` 下の既存鍵を自動検出 |
| `--token <TOKEN>` | 旧方式の全 Gateway 共通認証トークン | `GATEWAY_TOKEN` 環境変数または未設定 |
| `--allow-cidr <CIDR>` | 5080ポート接続元の IP 制限（例: `219.104.138.249/32`） | 全開放 (`0.0.0.0/0`) |
| `--name <NAME>` | Lightsail インスタンス名 | `balloonmesh-server` |

---

## 3. インスタンスの破棄（課金停止）

動作確認完了後、以下のスクリプトでインスタンス・ディスク・キーペアを完全消去し、課金を即座に停止できます：

```bash
./scripts/lightsail-destroy.sh
```

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
