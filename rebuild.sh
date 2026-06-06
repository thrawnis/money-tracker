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
mkdir -p "$REPO_DIR/data/postgres"

echo "==> Building and restarting containers (no cache on code changes)"
docker compose build --pull
docker compose up -d --force-recreate --remove-orphans

echo "==> Removing dangling images"
docker image prune -f

echo "==> Done. App is running on port 3012."
docker compose ps
