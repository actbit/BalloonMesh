#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# scripts/lightsail-deploy.sh
# 
# AWS Lightsail へのデプロイスクリプト。
# インスタンスが存在しない場合は自動作成し、存在する場合は差分のみを高速反映します。
#
# Usage:
#   ./scripts/lightsail-deploy.sh [OPTIONS]
#
# Options:
#   --demo           デモモード（仮想バルーン50台を自動生成、デフォルト）
#   --prod           本番実機モード（シミュレーション停止、物理ESP32待受）
#   --name <NAME>    インスタンス名（デフォルト: balloonmesh-server）
#   --key <PATH>     SSH秘密鍵のパス（未指定時は ~/.ssh/ から自動検出）
#   --bundle <ID>    プラン（small_3_0: 2GB/$12, micro_3_0: 1GB/$7。デフォルト: small_3_0）
#   --allow-cidr <CIDR> 5080ポートの接続元IP制限（例: 219.104.138.249/32。未指定時は全開放）
#
# Examples:
#   ./scripts/lightsail-deploy.sh                # デモモードで自動作成または更新
#   ./scripts/lightsail-deploy.sh --prod         # 本番実機モードでデプロイ
#   ./scripts/lightsail-deploy.sh --prod --allow-cidr 219.104.138.249/32
#   ./scripts/lightsail-deploy.sh --bundle micro_3_0  # $7/月の最小プランで作成
# -----------------------------------------------------------------------------

INSTANCE_NAME="balloonmesh-server"
KEY_PATH="${LIGHTSAIL_KEY:-}"
BUNDLE_ID="small_3_0"
MODE="--demo"
ALLOW_CIDR=""
REGION="${AWS_DEFAULT_REGION:-ap-northeast-1}"
PORT="5080"
REMOTE_USER="ubuntu"
REMOTE_DIR="~/BalloonMesh"

# 引数解析
while [[ $# -gt 0 ]]; do
  case "$1" in
    --prod)
      MODE="--prod"
      shift
      ;;
    --demo)
      MODE="--demo"
      shift
      ;;
    --name)
      INSTANCE_NAME="$2"
      shift 2
      ;;
    --key)
      KEY_PATH="$2"
      shift 2
      ;;
    --bundle)
      BUNDLE_ID="$2"
      shift 2
      ;;
    --allow-cidr)
      ALLOW_CIDR="$2"
      shift 2
      ;;
    *)
      # IP アドレスまたはホスト名が直接渡された場合の後方互換
      if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        EXPLICIT_HOST="$1"
      fi
      shift
      ;;
  esac
done

if [[ "$MODE" == "--prod" ]]; then
  SIMULATION_ENABLED="false"
  MODE_LABEL="本番・実機接続モード（シミュレーション停止）"
else
  SIMULATION_ENABLED="true"
  MODE_LABEL="デモモード（仮想バルーン50台シミュレーション）"
fi

# SSH 鍵の自動検出
if [[ -z "$KEY_PATH" ]]; then
  for candidate in ~/.ssh/isucon-key.pem ~/.ssh/id_ed25519 ~/.ssh/id_rsa; do
    if [[ -f "$candidate" ]]; then
      KEY_PATH="$candidate"
      break
    fi
  done
fi

if [[ -z "$KEY_PATH" || ! -f "${KEY_PATH/#\~/$HOME}" ]]; then
  echo "Error: SSH秘密鍵が見つかりません。--key <PATH> で指定してください。"
  exit 1
fi
KEY_PATH="${KEY_PATH/#\~/$HOME}"
KEY_NAME="${INSTANCE_NAME}-key"

echo "==> 動作モード: ${MODE_LABEL}"

# 1. インスタンスの存在確認と自動作成
HOST="${EXPLICIT_HOST:-}"

if [[ -z "$HOST" ]]; then
  if ! aws lightsail get-instance --instance-name "$INSTANCE_NAME" >/dev/null 2>&1; then
    echo "==> Lightsail インスタンス [${INSTANCE_NAME}] が存在しないため、新規作成します..."

    # SSH公開鍵のインポート
    PUBKEY=$(ssh-keygen -y -f "$KEY_PATH")
    if ! aws lightsail get-key-pair --key-pair-name "$KEY_NAME" >/dev/null 2>&1; then
      aws lightsail import-key-pair --key-pair-name "$KEY_NAME" --public-key-base64 "$PUBKEY" >/dev/null
      echo "SSHキーペアを登録しました: ${KEY_NAME}"
    fi

    # cloud-init で Docker を自動セットアップ
    TMP_USERDATA=$(mktemp)
    cat << 'EOF' > "$TMP_USERDATA"
#!/bin/bash
apt-get update
apt-get install -y docker.io docker-compose-v2 git rsync
usermod -aG docker ubuntu
EOF

    aws lightsail create-instances \
      --instance-names "$INSTANCE_NAME" \
      --availability-zone "${REGION}a" \
      --blueprint-id ubuntu_24_04 \
      --bundle-id "$BUNDLE_ID" \
      --key-pair-name "$KEY_NAME" \
      --user-data "file://$TMP_USERDATA" >/dev/null
    rm -f "$TMP_USERDATA"
    echo "インスタンス作成リクエストを送信しました (${BUNDLE_ID})"

    # 起動待機
    echo "==> インスタンスの起動を待機しています..."
    while true; do
      STATE=$(aws lightsail get-instance --instance-name "$INSTANCE_NAME" --query "instance.state.name" --output text 2>/dev/null || echo "pending")
      HOST=$(aws lightsail get-instance --instance-name "$INSTANCE_NAME" --query "instance.publicIpAddress" --output text 2>/dev/null || echo "None")
      if [[ "$STATE" == "running" && -n "$HOST" && "$HOST" != "None" ]]; then
        echo "インスタンスが起動しました！ (IP: $HOST)"
        break
      fi
      sleep 4
    done

    # ポート開放
    if [[ -n "$ALLOW_CIDR" ]]; then
      [[ "$ALLOW_CIDR" != *"/"* ]] && ALLOW_CIDR="${ALLOW_CIDR}/32"
      PORT_INFO="fromPort=${PORT},toPort=${PORT},protocol=tcp,cidrs=${ALLOW_CIDR}"
      echo "==> ポート ${PORT} を CIDR [${ALLOW_CIDR}] に制限して開放しています..."
    else
      PORT_INFO="fromPort=${PORT},toPort=${PORT},protocol=tcp"
      echo "==> ポート ${PORT} を全開放しています (0.0.0.0/0)..."
    fi
    aws lightsail open-instance-public-ports \
      --instance-name "$INSTANCE_NAME" \
      --port-info "$PORT_INFO" >/dev/null

    # SSH & Docker の準備待機
    echo "==> SSH および Docker の準備完了を待機しています..."
    while ! ssh -i "$KEY_PATH" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 "${REMOTE_USER}@${HOST}" "docker --version" >/dev/null 2>&1; do
      sleep 4
    done
  else
    HOST=$(aws lightsail get-instance --instance-name "$INSTANCE_NAME" --query "instance.publicIpAddress" --output text)
    echo "==> 既存のインスタンス [${INSTANCE_NAME}] (IP: ${HOST}) にデプロイします。"
    
    if [[ -n "$ALLOW_CIDR" ]]; then
      [[ "$ALLOW_CIDR" != *"/"* ]] && ALLOW_CIDR="${ALLOW_CIDR}/32"
      PORT_INFO="fromPort=${PORT},toPort=${PORT},protocol=tcp,cidrs=${ALLOW_CIDR}"
      echo "==> ポート ${PORT} の許可 CIDR を [${ALLOW_CIDR}] に更新しています..."
      aws lightsail open-instance-public-ports \
        --instance-name "$INSTANCE_NAME" \
        --port-info "$PORT_INFO" >/dev/null || true
    fi
  fi
fi

SSH_OPTS=(-i "$KEY_PATH" -o "StrictHostKeyChecking=accept-new" -o "ConnectTimeout=10")

# 2. ファイルの差分同期
echo "==> プロジェクト差分を同期しています (${REMOTE_USER}@${HOST}:${REMOTE_DIR})..."
rsync -avz \
  --delete \
  --exclude='.git' \
  --exclude='.worktrees' \
  --exclude='firmware' \
  --exclude='docs' \
  --exclude='bin' \
  --exclude='obj' \
  -e "ssh ${SSH_OPTS[*]}" \
  ./ "${REMOTE_USER}@${HOST}:${REMOTE_DIR}/"

# 3. コンテナのビルドと起動
echo "==> コンテナをビルド・起動しています (SIMULATION_ENABLED=${SIMULATION_ENABLED})..."
ssh "${SSH_OPTS[@]}" "${REMOTE_USER}@${HOST}" "cd ${REMOTE_DIR} && sudo SIMULATION_ENABLED=${SIMULATION_ENABLED} docker compose up -d --build"

# 4. ヘルスチェック
echo "==> デプロイの疎通確認中..."
sleep 3
HEALTH_URL="http://${HOST}:${PORT}/api/health"
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$HEALTH_URL" || echo "000")

if [[ "$HTTP_STATUS" == "200" ]]; then
  echo "=========================================================="
  echo " 🎉 デプロイが完了しました！ [${MODE_LABEL}]"
  echo " Web UI: http://${HOST}:${PORT}/"
  echo "=========================================================="
else
  echo "Warning: ヘルスチェックで HTTP $HTTP_STATUS が返されました: $HEALTH_URL"
  echo "リモートログの確認: ssh ${SSH_OPTS[*]} ${REMOTE_USER}@${HOST} 'cd ${REMOTE_DIR} && docker compose logs -f server'"
fi
