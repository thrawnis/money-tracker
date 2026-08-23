#!/bin/bash
# Prepares a Claude Code on the web container so `dotnet build`, `dotnet test`,
# `npm run build`, and `npm run lint` all work without per-session setup.
#
# The repo is cloned fresh into an ephemeral container, which ships with Node
# but no .NET SDK and no node_modules — so without this, the backend can't be
# compiled and the frontend can't be linted or built.
set -euo pipefail

# Local machines already have their own toolchains; only the remote container
# needs provisioning.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

REPO="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"

# ── .NET SDK 8 ──────────────────────────────────────────────────────────────
# From Ubuntu's own archive: dot.net and the Microsoft CDN are blocked by the
# egress proxy, but the distro package is allowed. `apt-get update` first is
# required, not optional — a stale index resolves to package versions that have
# already been superseded, and every download 404s.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Installing .NET SDK 8…"
  $SUDO apt-get update -qq
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
fi
dotnet --version

# Opt out of the first-run banner and telemetry ping so tool output stays clean.
echo 'export DOTNET_NOLOGO=1' >> "${CLAUDE_ENV_FILE:-/dev/null}"
echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "${CLAUDE_ENV_FILE:-/dev/null}"

# ── dotnet-ef ───────────────────────────────────────────────────────────────
# Migrations in this repo are hand-authored when no SDK is available, so the
# tool that checks them against the model (`dotnet ef migrations add` on a
# scratch name should produce an EMPTY Up/Down) is worth having on hand.
export PATH="$PATH:/root/.dotnet/tools:$HOME/.dotnet/tools"
if ! command -v dotnet-ef >/dev/null 2>&1; then
  dotnet tool install --global dotnet-ef --version '8.*' >/dev/null 2>&1 || true
fi
echo 'export PATH="$PATH:/root/.dotnet/tools:$HOME/.dotnet/tools"' >> "${CLAUDE_ENV_FILE:-/dev/null}"

# ── Restore/build so the first real command isn't a cold start ──────────────
dotnet restore "$REPO/backend/MoneyTracker.Api.csproj"
dotnet restore "$REPO/backend.Tests/MoneyTracker.Tests.csproj"

# ── Docker daemon ───────────────────────────────────────────────────────────
# The integration tests spin up Postgres via Testcontainers. Docker is
# installed here but the daemon isn't running by default, so start it.
#
# Best-effort on purpose: in a default web container the daemon starts fine but
# the image pull is refused by the egress policy (403 from the registry blob
# CDN), so the ~9 Testcontainers tests still fail while the ~65 unit tests pass.
# Use `dotnet test --filter "FullyQualifiedName!~Integration"` there. Nothing
# below depends on this succeeding.
if command -v dockerd >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
  ($SUDO dockerd >/tmp/dockerd.log 2>&1 &) || true
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

# ── Frontend ────────────────────────────────────────────────────────────────
# `npm install` rather than `npm ci`: the container image is cached after this
# hook completes, and install reuses that cache instead of deleting the tree.
cd "$REPO/frontend"
npm install --no-audit --no-fund

echo "Session setup complete."
