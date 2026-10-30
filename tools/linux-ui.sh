#!/usr/bin/env bash
# The fleet's eyes and hands on the app's window on the owner's Linux host (M-org1): see it, press it.
#
#   tools/linux-ui.sh setup                     install the X tools it needs on the host (xdotool, imagemagick), apt via sudo -n
#   tools/linux-ui.sh shot <local.png>          capture the app's display and copy the PNG back here
#   tools/linux-ui.sh click <x> <y> [double]    a left click at display pixels (the shot's pixels: 1:1)
#   tools/linux-ui.sh scroll <x> <y> <up|down> [n]   n wheel ticks (default 3) over that point
#   tools/linux-ui.sh type <text>               types the text into whatever has focus
#   tools/linux-ui.sh key <keysym>              one key or chord in xdotool's names: Return, Tab, ctrl+a, Escape
#
# Same env file and the same rule as tools/linux-host.sh: THE HOST IS NOT NAMED HERE — ~/.tradeagent/linux.env sets
# TA_LINUX_HOST, TA_LINUX_USER, TA_LINUX_KEY (key auth only, BatchMode) and TA_LINUX_PORT.
#
# WHICH DISPLAY: the one systemd starts the app on. DISPLAY, XAUTHORITY and the account are read from tradeagent.service
# itself (systemctl show), never assumed, and every X call runs as that account with that cookie in place: the cookie is
# never copied, printed or moved off the host, and the VNC pass file is never touched (this goes around VNC, not through it).
#
# WHAT IT IS NOT: a way around the owner's authority. It presses what the owner could press in the window, so it is used only
# inside a sitting the owner has said yes to, and never on live mode, real money, a credential field or his sign-in. It
# reads nothing of the app's state; the app's database is read with read-only SQL, elsewhere.
set -euo pipefail

verb="${1:-}"
[ $# -gt 0 ] && shift
usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

case "$verb" in
    setup) [ $# -eq 0 ] || usage ;;
    shot) [ $# -eq 1 ] || usage ;;
    click) { [ $# -eq 2 ] || { [ $# -eq 3 ] && [ "$3" = double ]; }; } || usage ;;
    scroll) [ $# -eq 3 ] || [ $# -eq 4 ] || usage ;;
    type | key) [ $# -eq 1 ] && [ -n "$1" ] || usage ;;
    *) usage ;;
esac
num() { case "$1" in '' | *[!0-9]*) echo "linux-ui: '$1' is not a whole number of pixels" >&2; exit 2 ;; esac; }
case "$verb" in
    click) num "$1"; num "$2" ;;
    scroll) num "$1"; num "$2"; num "${4:-3}"; case "$3" in up | down) ;; *) usage ;; esac ;;
esac

env_file="$HOME/.tradeagent/linux.env"
if [ ! -f "$env_file" ]; then
    echo "linux-ui: $env_file does not exist; see tools/README.md. Refusing rather than guessing a host." >&2
    exit 2
fi
# shellcheck source=/dev/null
source "$env_file"
: "${TA_LINUX_HOST:?set TA_LINUX_HOST in $env_file}"
: "${TA_LINUX_USER:?set TA_LINUX_USER in $env_file}"
: "${TA_LINUX_KEY:?set TA_LINUX_KEY in $env_file: key authentication only}"
port="${TA_LINUX_PORT:-22}"

_opts=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 -o LogLevel=ERROR
       -o ServerAliveInterval=30 -o ServerAliveCountMax=4
       -i "$TA_LINUX_KEY" -o IdentitiesOnly=yes -o BatchMode=yes)
# lx_x <op> [args...]: the op run as root, which reads the app's display from its unit and runs the X tool as the app's account.
lx_x() {
    ssh "${_opts[@]}" -p "$port" "$TA_LINUX_USER@$TA_LINUX_HOST" "sudo -n bash -s -- $(printf '%q ' "$@")" << 'REMOTE'
set -euo pipefail
op="$1"; shift
if [ "$op" = setup ]; then
    need=()
    command -v xdotool > /dev/null || need+=(xdotool)
    command -v import > /dev/null || need+=(imagemagick)
    if [ "${#need[@]}" -eq 0 ]; then echo "linux-ui: xdotool and imagemagick are already there" >&2; exit 0; fi
    echo "linux-ui: installing ${need[*]}" >&2
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${need[@]}" < /dev/null > /dev/null
    exit 0
fi
systemctl cat tradeagent.service > /dev/null 2>&1 || { echo "linux-ui: tradeagent.service is not installed" >&2; exit 1; }
account="$(systemctl show -p User --value tradeagent.service)"
display="" xauth=""
for kv in $(systemctl show -p Environment --value tradeagent.service); do
    case "$kv" in DISPLAY=*) display="${kv#DISPLAY=}" ;; XAUTHORITY=*) xauth="${kv#XAUTHORITY=}" ;; esac
done
[ -n "$account" ] && [ -n "$display" ] && [ -n "$xauth" ] \
    || { echo "linux-ui: tradeagent.service names no User, DISPLAY or XAUTHORITY" >&2; exit 1; }
for t in xdotool import; do command -v "$t" > /dev/null || { echo "linux-ui: $t is missing on the host: run 'tools/linux-ui.sh setup'" >&2; exit 1; }; done
as_app() { sudo -n -u "$account" env DISPLAY="$display" XAUTHORITY="$xauth" "$@" < /dev/null; }
case "$op" in
    shot) as_app import -window root png:- ;;
    click) as_app xdotool mousemove --sync "$1" "$2" click --repeat "$( [ "${3:-}" = double ] && echo 2 || echo 1)" 1 ;;
    scroll) as_app xdotool mousemove --sync "$1" "$2" click --repeat "$4" "$( [ "$3" = up ] && echo 4 || echo 5)" ;;
    type) as_app xdotool type --delay 40 -- "$1" ;;
    key) as_app xdotool key -- "$1" ;;
    *) echo "linux-ui: unknown op $op" >&2; exit 2 ;;
esac
REMOTE
}

case "$verb" in
    setup) lx_x setup ;;
    shot)
        out="$1" tmp="$1.part"
        lx_x shot > "$tmp" || { rm -f "$tmp"; exit 1; }
        # A PNG or nothing: an empty or foreign file is not left behind looking like a screenshot.
        if [ "$(head -c 8 "$tmp" | od -An -tx1 | tr -d ' \n')" != 89504e470d0a1a0a ]; then
            rm -f "$tmp"; echo "linux-ui: the host sent no PNG" >&2; exit 1
        fi
        mv -f "$tmp" "$out"
        echo "$out"
        ;;
    click) lx_x click "$1" "$2" "${3:-}" ;;
    scroll) lx_x scroll "$1" "$2" "$3" "${4:-3}" ;;
    type) lx_x type "$1" ;;
    key) lx_x key "$1" ;;
esac
