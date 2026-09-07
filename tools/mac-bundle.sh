#!/bin/bash
# Run the app on macOS INSIDE a throwaway .app bundle, so the desktop's screen control can address it.
#
# The bare apphost that tools/mac-run.sh launches has no bundle identifier, and macOS lists no application
# for it — so nothing that drives windows by application (the Claude desktop app's screen control, for one)
# can click it. This wraps the Debug output in a minimal bundle with the id below; the app itself is the
# same binary, on the same isolated home. The bundle is rebuilt from scratch on every run.
#
#   tools/mac-bundle.sh              # build, wrap, launch; prints the bundle id to request access for
#   TRADEAGENT_HOME=... tools/mac-bundle.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
cd "$ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export TRADEAGENT_HOME="${TRADEAGENT_HOME:-${TMPDIR:-/tmp}/tradeagent-dev}"
mkdir -p "$TRADEAGENT_HOME"
BUNDLE_ID="dev.tradeagent.mac"
BUNDLE="${TRADEAGENT_BUNDLE_DIR:-${TMPDIR:-/tmp}/tradeagent-bundle}/TradeAgent.app"
echo "TRADEAGENT_HOME=$TRADEAGENT_HOME   (delete it to replay first-run setup)"

# Only this bundle's executable — never a test host, never the bare app from mac-run.sh.
pkill -f "tradeagent-bundle/TradeAgent\.app/Contents/MacOS/TradeAgent$" 2>/dev/null || true
sleep 1
dotnet build src/TradeAgent.App/TradeAgent.App.csproj -v q --nologo

rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R src/TradeAgent.App/bin/Debug/net10.0/. "$BUNDLE/Contents/MacOS/"
cat > "$BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>TradeAgent</string>
<key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
<key>CFBundleName</key><string>TradeAgent</string>
<key>CFBundleDisplayName</key><string>TradeAgent</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSMinimumSystemVersion</key><string>11.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF

# The display must be awake or Avalonia aborts at startup (RenderTimer -6661).
caffeinate -u -t 5 || true
nohup "$BUNDLE/Contents/MacOS/TradeAgent" > "${TMPDIR:-/tmp}/tradeagent-app.log" 2>&1 &
sleep 8
echo "running as $BUNDLE_ID. log: ${TMPDIR:-/tmp}/tradeagent-app.log"
echo "screen control: request access for \"$BUNDLE_ID\"; background clicks work, scrolling needs full control (mouse wheel)."
