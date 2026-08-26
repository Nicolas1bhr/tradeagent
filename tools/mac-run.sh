#!/bin/bash
# Launch the app on macOS against an isolated install, so the real one is never touched.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
cd "$ROOT"

# dotnet is not on PATH on this machine; DOTNET_ROOT is required to run the apphost directly.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export TRADEAGENT_HOME="${TRADEAGENT_HOME:-${TMPDIR:-/tmp}/tradeagent-dev}"
mkdir -p "$TRADEAGENT_HOME"
echo "TRADEAGENT_HOME=$TRADEAGENT_HOME   (delete it to replay first-run setup)"

pkill -f "net10.0/TradeAgent" 2>/dev/null || true
sleep 1
dotnet build src/TradeAgent.App/TradeAgent.App.csproj -v q --nologo
nohup ./src/TradeAgent.App/bin/Debug/net10.0/TradeAgent > "${TMPDIR:-/tmp}/tradeagent-app.log" 2>&1 &
sleep 8
echo "running. log: ${TMPDIR:-/tmp}/tradeagent-app.log"
