#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# scripts/lightsail-destroy.sh
# 
# Usage:
#   ./scripts/lightsail-destroy.sh [INSTANCE_NAME] [--delete-static-ip]
# -----------------------------------------------------------------------------

INSTANCE_NAME="balloonmesh-server"
DELETE_STATIC_IP="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --delete-static-ip)
      DELETE_STATIC_IP="true"
      shift
      ;;
    --help|-h)
      sed -n '4,12p' "$0"
      echo "  --delete-static-ip  保持せず、静的IPも削除する"
      exit 0
      ;;
    *)
      INSTANCE_NAME="$1"
      shift
      ;;
  esac
done

KEY_NAME="${INSTANCE_NAME}-key"
STATIC_IP_NAME="${INSTANCE_NAME}-ip"

echo "==> Deleting Lightsail instance: ${INSTANCE_NAME}..."
if aws lightsail get-instance --instance-name "$INSTANCE_NAME" >/dev/null 2>&1; then
  aws lightsail delete-instance --instance-name "$INSTANCE_NAME" >/dev/null
  echo "Instance ${INSTANCE_NAME} deleted."
else
  echo "Instance ${INSTANCE_NAME} not found."
fi

if [[ "$DELETE_STATIC_IP" == "true" ]]; then
  echo "==> Deleting static IP: ${STATIC_IP_NAME}..."
  if aws lightsail get-static-ip --static-ip-name "$STATIC_IP_NAME" >/dev/null 2>&1; then
    aws lightsail delete-static-ip --static-ip-name "$STATIC_IP_NAME" >/dev/null
    echo "Static IP ${STATIC_IP_NAME} deleted."
  fi
else
  echo "Static IP ${STATIC_IP_NAME} is preserved for reuse."
  echo "To delete it explicitly: $0 ${INSTANCE_NAME} --delete-static-ip"
fi

echo "==> Deleting registered SSH key pair: ${KEY_NAME}..."
if aws lightsail get-key-pair --key-pair-name "$KEY_NAME" >/dev/null 2>&1; then
  aws lightsail delete-key-pair --key-pair-name "$KEY_NAME" >/dev/null
  echo "Key pair ${KEY_NAME} deleted."
fi

echo "=========================================================="
if [[ "$DELETE_STATIC_IP" == "true" ]]; then
  echo " Instance, key pair, and static IP deleted."
else
  echo " Instance and key pair deleted; static IP preserved."
fi
echo "=========================================================="
