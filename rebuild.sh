#!/usr/bin/env bash
# rebuild.sh — Pull latest dev branch and redeploy with Docker
# Usage: ./rebuild.sh
# Run from the repository root on your server.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
BRANCH="dev"

echo "==> Pulling latest code from branch: $BRANCH"
cd "$REPO_DIR"
git fetch origin "$BRANCH"
git checkout "$BRANCH"
git reset --hard "origin/$BRANCH"

echo "==> Ensuring data directories exist"
mkdir -p "$REPO_DIR/data/postgres" || { echo "ERROR: cannot create data/postgres"; exit 1; }

# -- Determine Ollama profile -------------------------------------------------
# Read only the two variables we need from .env using grep.
_env_get() {
  local key="$1"
  grep -m1 "^${key}=" "$REPO_DIR/.env" 2>/dev/null \
    | cut -d= -f2- \
    | sed "s/[[:space:]]*#.*//; s/^['\"]//; s/['\"]$//" \
    || true
}

RECEIPT_PROVIDER="ollama"
OLLAMA_EXTERNAL_URL=""

if [ -f "$REPO_DIR/.env" ]; then
  _val="$(_env_get RECEIPT_PROVIDER)";   [ -n "$_val" ] && RECEIPT_PROVIDER="$_val"
  _val="$(_env_get OLLAMA_EXTERNAL_URL)"; [ -n "$_val" ] && OLLAMA_EXTERNAL_URL="$_val"
fi

OLLAMA_PROFILE=""

if [ "$RECEIPT_PROVIDER" = "ollama" ] && [ -z "$OLLAMA_EXTERNAL_URL" ]; then
  echo "==> Using local Ollama container (no OLLAMA_EXTERNAL_URL set)"
  OLLAMA_PROFILE="--profile local-ollama"
  mkdir -p "$REPO_DIR/data/ollama" || { echo "ERROR: cannot create data/ollama"; exit 1; }
else
  echo "==> Skipping local Ollama (provider=$RECEIPT_PROVIDER, external=${OLLAMA_EXTERNAL_URL:-n/a})"
fi

# -- Build and start containers -----------------------------------------------

echo "==> Building containers"
docker compose build --pull

# Bring up db without --force-recreate so existing connections are preserved.
echo "==> Starting db"
# shellcheck disable=SC2086
docker compose $OLLAMA_PROFILE up -d db

if [ -n "$OLLAMA_PROFILE" ]; then
  echo "==> Starting ollama"
  docker compose $OLLAMA_PROFILE up -d ollama
fi

# Only force-recreate the application containers that actually changed.
echo "==> Deploying api and frontend"
# shellcheck disable=SC2086
docker compose $OLLAMA_PROFILE up -d --force-recreate --remove-orphans api frontend

# -- Pull Ollama model if running locally -------------------------------------

if [ -n "$OLLAMA_PROFILE" ]; then
  OLLAMA_MODEL="${OLLAMA_MODEL:-llava}"
  echo "==> Pulling Ollama model: $OLLAMA_MODEL (this may take a while on first run)"
  for i in $(seq 1 30); do
    if docker compose exec -T ollama ollama list > /dev/null 2>&1; then
      break
    fi
    echo "   Waiting for Ollama to start ($i/30)..."
    sleep 2
  done
  docker compose exec -T ollama ollama pull "$OLLAMA_MODEL" || \
    echo "   Warning: could not pull model '$OLLAMA_MODEL' -- it may already be cached."
fi

echo "==> Removing dangling images"
docker image prune -f

echo "==> Done. App is running on port 3012."
# shellcheck disable=SC2086
docker compose $OLLAMA_PROFILE ps
