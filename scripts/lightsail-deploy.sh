#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# scripts/lightsail-deploy.sh
#
# AWS Lightsail への HTTPS デプロイスクリプト。
# インスタンスが存在しない場合は自動作成し、静的IPを割り当てます。
# TLSはCaddyが終端し、アプリケーションはComposeネットワーク内の
# server:5080で待ち受けます。
#
# このスクリプトはLightsailのファイアウォール/CIDR設定を変更しません。
# 80/443の許可や既存ルールの変更は、環境側で明示的に行ってください。
#
# Usage:
#   ./scripts/lightsail-deploy.sh --domain <DOMAIN> [OPTIONS]
#
# Options:
#   --domain <DOMAIN>  HTTPSで利用する公開ドメイン（必須）
#   --demo             デモモード（仮想バルーン50台を自動生成、デフォルト）
#   --prod             本番実機モード（シミュレーション停止、物理ESP32待受）
#   --name <NAME>      インスタンス名（デフォルト: balloonmesh-server）
#   --key <PATH>       SSH秘密鍵のパス（未指定時は ~/.ssh/ から自動検出）
#   --bundle <ID>      プラン（small_3_0 / micro_3_0。デフォルト: small_3_0）
#   --token <TOKEN>    Gateway 認証トークン
#   --help             ヘルプを表示
#
# Environment:
#   AUTH_MODE=Local
#   BALLOONMESH_ADMIN_PASSWORD=<初期adminパスワード>
#
# Examples:
#   ./scripts/lightsail-deploy.sh --domain mesh.example.com
#   ./scripts/lightsail-deploy.sh --domain mesh.example.com --prod --token secret123
# -----------------------------------------------------------------------------

INSTANCE_NAME="balloonmesh-server"
KEY_PATH="${LIGHTSAIL_KEY:-}"
BUNDLE_ID="small_3_0"
MODE="--demo"
DOMAIN="${LIGHTSAIL_DOMAIN:-}"
GATEWAY_TOKEN="${GATEWAY_TOKEN:-}"
AUTH_MODE="${AUTH_MODE:-Local}"
ADMIN_PASSWORD="${BALLOONMESH_ADMIN_PASSWORD:-}"
REGION="${AWS_DEFAULT_REGION:-ap-northeast-1}"
REMOTE_USER="ubuntu"
REMOTE_DIR="/home/ubuntu/BalloonMesh"
EXPLICIT_HOST=""
CREATED_INSTANCE="false"

usage() {
  sed -n '4,34p' "$0"
}

aws_lightsail() {
  aws --region "$REGION" lightsail "$@"
}

# 引数解析
while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain)
      [[ $# -ge 2 ]] || { echo "Error: --domain には値が必要です。" >&2; exit 2; }
      DOMAIN="$2"
      shift 2
      ;;
    --prod)
      MODE="--prod"
      shift
      ;;
    --demo)
      MODE="--demo"
      shift
      ;;
    --name)
      [[ $# -ge 2 ]] || { echo "Error: --name には値が必要です。" >&2; exit 2; }
      INSTANCE_NAME="$2"
      shift 2
      ;;
    --key)
      [[ $# -ge 2 ]] || { echo "Error: --key には値が必要です。" >&2; exit 2; }
      KEY_PATH="$2"
      shift 2
      ;;
    --bundle)
      [[ $# -ge 2 ]] || { echo "Error: --bundle には値が必要です。" >&2; exit 2; }
      BUNDLE_ID="$2"
      shift 2
      ;;
    --token)
      [[ $# -ge 2 ]] || { echo "Error: --token には値が必要です。" >&2; exit 2; }
      GATEWAY_TOKEN="$2"
      shift 2
      ;;
    --allow-cidr)
      echo "Error: ファイアウォール設定は別セッションで管理するため、--allow-cidr は使用できません。" >&2
      exit 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      # IP アドレスが直接渡された場合の後方互換。静的IPの自動割り当ては行いません。
      if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        EXPLICIT_HOST="$1"
        shift
      else
        echo "Error: 不明な引数です: $1" >&2
        usage >&2
        exit 2
      fi
      ;;
  esac
done

if [[ -z "$DOMAIN" ]]; then
  echo "Error: HTTPSデプロイには公開ドメインが必要です。--domain mesh.example.com を指定してください。" >&2
  exit 2
fi

case "$AUTH_MODE" in
  Local|local)
    if [[ -z "$ADMIN_PASSWORD" ]]; then
      echo "Error: Local認証にはBALLOONMESH_ADMIN_PASSWORDの設定が必要です。" >&2
      exit 2
    fi
    ;;
esac

if [[ "$DOMAIN" =~ [/:[:space:]] || "$DOMAIN" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
  echo "Error: --domain にはスキームやポートを含めず、ホスト名だけを指定してください: $DOMAIN" >&2
  exit 2
fi

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
  echo "Error: SSH秘密鍵が見つかりません。--key <PATH> で指定してください。" >&2
  exit 1
fi
KEY_PATH="${KEY_PATH/#\~/$HOME}"
KEY_NAME="${INSTANCE_NAME}-key"
STATIC_IP_NAME="${INSTANCE_NAME}-ip"

echo "==> 動作モード: ${MODE_LABEL}"
echo "==> HTTPSドメイン: ${DOMAIN}"

# 1. インスタンスの存在確認と自動作成
HOST="$EXPLICIT_HOST"

if [[ -z "$HOST" ]]; then
  if ! aws_lightsail get-instance --instance-name "$INSTANCE_NAME" >/dev/null 2>&1; then
    echo "==> Lightsail インスタンス [${INSTANCE_NAME}] が存在しないため、新規作成します..."

    # SSH公開鍵のインポート
    PUBKEY=$(ssh-keygen -y -f "$KEY_PATH")
    if ! aws_lightsail get-key-pair --key-pair-name "$KEY_NAME" >/dev/null 2>&1; then
      aws_lightsail import-key-pair \
        --key-pair-name "$KEY_NAME" \
        --public-key-base64 "$PUBKEY" >/dev/null
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

    aws_lightsail create-instances \
      --instance-names "$INSTANCE_NAME" \
      --availability-zone "${REGION}a" \
      --blueprint-id ubuntu_24_04 \
      --bundle-id "$BUNDLE_ID" \
      --key-pair-name "$KEY_NAME" \
      --user-data "file://$TMP_USERDATA" >/dev/null
    rm -f "$TMP_USERDATA"
    CREATED_INSTANCE="true"
    echo "インスタンス作成リクエストを送信しました (${BUNDLE_ID})"

    # 起動待機
    echo "==> インスタンスの起動を待機しています..."
    while true; do
      STATE=$(aws_lightsail get-instance --instance-name "$INSTANCE_NAME" \
        --query "instance.state.name" --output text 2>/dev/null || echo "pending")
      HOST=$(aws_lightsail get-instance --instance-name "$INSTANCE_NAME" \
        --query "instance.publicIpAddress" --output text 2>/dev/null || echo "None")
      if [[ "$STATE" == "running" && -n "$HOST" && "$HOST" != "None" ]]; then
        echo "インスタンスが起動しました (IP: $HOST)"
        break
      fi
      sleep 4
    done
  else
    HOST=$(aws_lightsail get-instance --instance-name "$INSTANCE_NAME" \
      --query "instance.publicIpAddress" --output text)
    echo "==> 既存のインスタンス [${INSTANCE_NAME}] (IP: ${HOST}) にデプロイします。"
  fi
fi

# 静的IPをインスタンスに割り当てる。すでに別名の静的IPが付いている場合はそれを維持する。
if [[ -z "$EXPLICIT_HOST" ]]; then
  IS_STATIC_IP=$(aws_lightsail get-instance --instance-name "$INSTANCE_NAME" \
    --query "instance.isStaticIp" --output text)
  if [[ "$IS_STATIC_IP" != "True" && "$IS_STATIC_IP" != "true" ]]; then
    if ! aws_lightsail get-static-ip --static-ip-name "$STATIC_IP_NAME" >/dev/null 2>&1; then
      echo "==> 静的IP [${STATIC_IP_NAME}] を作成しています..."
      aws_lightsail allocate-static-ip \
        --static-ip-name "$STATIC_IP_NAME" >/dev/null
    fi

    ATTACHED_TO=$(aws_lightsail get-static-ip --static-ip-name "$STATIC_IP_NAME" \
      --query "staticIp.attachedTo" --output text)
    if [[ -z "$ATTACHED_TO" || "$ATTACHED_TO" == "None" ]]; then
      echo "==> 静的IP [${STATIC_IP_NAME}] を ${INSTANCE_NAME} に割り当てています..."
      aws_lightsail attach-static-ip \
        --static-ip-name "$STATIC_IP_NAME" \
        --instance-name "$INSTANCE_NAME" >/dev/null
    elif [[ "$ATTACHED_TO" != "$INSTANCE_NAME" ]]; then
      echo "Error: 静的IP [${STATIC_IP_NAME}] は別のインスタンス [${ATTACHED_TO}] に割り当て済みです。" >&2
      exit 1
    fi
  fi

  HOST=$(aws_lightsail get-instance --instance-name "$INSTANCE_NAME" \
    --query "instance.publicIpAddress" --output text)
  echo "==> 使用する静的IP: ${HOST}"
  echo "==> DNS Aレコードを ${DOMAIN} -> ${HOST} に設定してください。"
else
  echo "Warning: 直接指定したIPを使用するため、静的IPの自動割り当てはスキップします。"
fi

SSH_OPTS=(-i "$KEY_PATH" -o "StrictHostKeyChecking=accept-new" -o "ConnectTimeout=10")

# ファイアウォールは別セッションの設定を尊重し、ここでは変更しない。
echo "==> Lightsailファイアウォールは変更しません（80/443の許可状態を維持）。"

# SSH と Docker の準備待機
echo "==> SSH および Docker の準備完了を待機しています..."
READY="false"
for attempt in $(seq 1 60); do
  if ssh "${SSH_OPTS[@]}" "${REMOTE_USER}@${HOST}" "docker --version" >/dev/null 2>&1; then
    READY="true"
    break
  fi
  sleep 4
done
if [[ "$READY" != "true" ]]; then
  echo "Error: SSHまたはDockerの準備が完了しませんでした: ${REMOTE_USER}@${HOST}" >&2
  exit 1
fi

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

# 3. Caddy + アプリケーションのビルドと起動
echo "==> HTTPSコンテナをビルド・起動しています (SIMULATION_ENABLED=${SIMULATION_ENABLED})..."
REMOTE_ENV="SIMULATION_ENABLED=$(printf '%q' "$SIMULATION_ENABLED") GATEWAY_TOKEN=$(printf '%q' "$GATEWAY_TOKEN") PUBLIC_DOMAIN=$(printf '%q' "$DOMAIN") AUTH_MODE=$(printf '%q' "$AUTH_MODE") MOCK_AUTH_ENABLED=false BALLOONMESH_ADMIN_PASSWORD=$(printf '%q' "$ADMIN_PASSWORD")"
ssh "${SSH_OPTS[@]}" "${REMOTE_USER}@${HOST}" \
  "cd ${REMOTE_DIR} && sudo env ${REMOTE_ENV} docker compose -f compose.yml -f compose.https.yml up -d --build"

# 4. HTTPSヘルスチェック
echo "==> HTTPSの疎通確認中..."
HEALTH_URL="https://${DOMAIN}/api/health"
HTTP_STATUS="000"
for attempt in $(seq 1 30); do
  if HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 "$HEALTH_URL"); then
    :
  else
    HTTP_STATUS="000"
  fi
  [[ "$HTTP_STATUS" == "200" ]] && break
  sleep 2
done

if [[ "$HTTP_STATUS" == "200" ]]; then
  echo "=========================================================="
  echo " 🎉 HTTPSデプロイが完了しました！ [${MODE_LABEL}]"
  echo " Web UI: https://${DOMAIN}/"
  echo " Gateway WebSocket: wss://${DOMAIN}/ws/gateway"
  echo "=========================================================="
else
  echo "Warning: HTTPSヘルスチェックで HTTP $HTTP_STATUS が返されました: $HEALTH_URL" >&2
  echo "DNSが ${HOST} を向いているか、Lightsailの80/443許可、Caddyログを確認してください。"
  echo "リモートログ: ssh ${SSH_OPTS[*]} ${REMOTE_USER}@${HOST} 'cd ${REMOTE_DIR} && sudo docker compose -f compose.yml -f compose.https.yml logs -f caddy server'"
fi
