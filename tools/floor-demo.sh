#!/bin/bash
# The floor with something happening on it: a scratch home of SYNTHETIC history, the app opened on it, and a
# player appending rows in real time. For looking at the landing page without spending a model turn.
# Never the owner's home: the seeding program refuses any folder not named demo-….
#
#   tools/floor-demo.sh            seed (once), start the app on the demo home, play into it
#   tools/floor-demo.sh stop       end the player and the app this script started
#
# NEVER START THE AI ON THIS HOME: its queue holds real wakes, and it would spend real turns on invented work.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
cd "$ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
DEMO="${TA_FLOOR_DEMO_HOME:-${TMPDIR:-/tmp}/demo-floor}"
PIDS="$DEMO/.floor-demo.pids"
# ITS OWN PIPES, NEVER THE REAL APP'S. A second app on the default pipe names is handed every new connection meant for the first, with
# no error on either side: the real app's agents, and the ATAS bridge, would be talking to a demo. So the demo app is given names of
# its own before it starts, whatever else is running on this machine.
export TRADEAGENT_PIPE="ta-floor-demo-$$"
export TRADEAGENT_BRIDGE_PIPE="ta-floor-demo-bridge-$$"

stop() {
    [ -f "$PIDS" ] || return 0
    while read -r pid; do kill "$pid" 2>/dev/null || true; done < "$PIDS"
    rm -f "$PIDS"
}

if [ "${1:-}" = "stop" ]; then stop; echo "stopped."; exit 0; fi
stop

dotnet build src/TradeAgent.App/TradeAgent.App.csproj -v q --nologo
dotnet build tests/TradeAgent.FloorDemo/TradeAgent.FloorDemo.csproj -v q --nologo
if [ ! -f "$DEMO/state/tradeagent.db" ]; then
    dotnet run --no-build --project tests/TradeAgent.FloorDemo -- seed "$DEMO"
fi

TRADEAGENT_HOME="$DEMO" nohup ./src/TradeAgent.App/bin/Debug/net10.0/TradeAgent > "$DEMO/app.log" 2>&1 &
echo $! > "$PIDS"
# The app marks any turn left open as lost when it starts, so the player begins once the window is up.
sleep 8
nohup dotnet run --no-build --project tests/TradeAgent.FloorDemo -- play "$DEMO" --minutes "${TA_FLOOR_DEMO_MINUTES:-30}" > "$DEMO/play.log" 2>&1 &
echo $! >> "$PIDS"
echo "the app is open on $DEMO and synthetic work is being played into it for ${TA_FLOOR_DEMO_MINUTES:-30} minutes."
echo "press Replay on the floor for the seeded history.   tools/floor-demo.sh stop   ends it."
