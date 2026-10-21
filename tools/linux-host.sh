#!/usr/bin/env bash
# The fleet's hands on the owner's always-on Linux host (U-host-recording item 3): read it, deploy to it, stop it.
#
#   tools/linux-host.sh recon                          read-only: what the machine is, before anything is installed
#   tools/linux-host.sh health                         the tape as the kit reads it (kit/tape-health), systemd's restarts
#   tools/linux-host.sh install <tarball> "<word>"     stop -> back up the home's databases -> the kit -> health
#   tools/linux-host.sh stop "<word>"                  systemctl stop tradeagent.service; the data stays
#
# THE OWNER'S DATED WORD: `install` and `stop` run only on his word, which is an argument — a date, then his words, e.g.
# "2026-10-22 yes, deploy it" — and the script refuses without one, or with one more than three days old or in the future.
# The word is kept in the backup folder beside what it allowed. It is the fleet's to run, never the app's and never a build
# unit's: nothing in this repository names the host, so there is nothing for a unit to point it at, and this file never
# reads a credential from anywhere but the env file below.
#
# THE HOST IS NOT NAMED HERE. ~/.tradeagent/linux.env (outside the repo, chmod 600) sets
#   TA_LINUX_HOST     the host's name or address        TA_LINUX_USER   its administrator account (needs `sudo -n`)
#   TA_LINUX_KEY      the private key's path (key auth only; BatchMode, so a refused key fails now)
#   TA_LINUX_ADDRESS  the address the display listens on (the kit's --address; the private network's, or 127.0.0.1) — install
#   TA_LINUX_ZONE     the owner's IANA zone (default Europe/Brussels) — install        TA_LINUX_PORT  ssh port (default 22)
#
# NEVER SEEDS THE HOME. The host's home becomes the in-app organisation's home; a seeded AI_AUTHENTICATED would be a false
# record. `install` writes the kit and a backup, and nothing of the app's own data; a first start is a fresh home.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
kit="$here/../packaging/linux"
verb="${1:-}"
[ $# -gt 0 ] && shift

usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

# word_ok "<word>": a date within three days of today, then at least three characters of the owner's own words.
word_ok() {
    python3 -I - "${1:-}" << 'PY'
import datetime, re, sys
m = re.match(r"^(20\d\d-\d\d-\d\d)[ :,-]+(\S.{2,})$", sys.argv[1])
if not m:
    sys.exit(1)
try:
    day = datetime.date.fromisoformat(m.group(1))
except ValueError:
    sys.exit(1)
sys.exit(0 if abs((datetime.date.today() - day).days) <= 3 and day <= datetime.date.today() + datetime.timedelta(days=1) else 1)
PY
}
need_word() {
    word_ok "${1:-}" || {
        echo "linux-host: '$verb' runs only on the owner's dated word, as an argument: \"YYYY-MM-DD <his words>\", dated within three days." >&2
        echo "Refusing without it." >&2
        exit 2
    }
}

case "$verb" in
    recon | health) [ $# -eq 0 ] || usage ;;
    install) [ $# -eq 2 ] || usage; need_word "$2" ;;
    stop) [ $# -eq 1 ] || usage; need_word "$1" ;;
    *) usage ;;
esac

env_file="$HOME/.tradeagent/linux.env"
if [ ! -f "$env_file" ]; then
    echo "linux-host: $env_file does not exist; see tools/README.md. Refusing rather than guessing a host." >&2
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
lx_ssh() { ssh "${_opts[@]}" -p "$port" "$TA_LINUX_USER@$TA_LINUX_HOST" "$@"; }
lx_scp() { scp "${_opts[@]}" -P "$port" "$@"; }
# lx_root <args...> < script: the script on stdin, run by bash as root with the arguments; each argument is quoted for the far shell.
lx_root() { lx_ssh "sudo -n bash -s -- $(printf '%q ' "$@")"; }

recon() {
    lx_ssh 'bash -s' << 'REMOTE'
set -u
show() { printf '\n== %s ==\n' "$1"; shift; "$@" 2>&1 || echo "(exit $?)"; }
show 'kernel and architecture (uname -mr)' uname -mr
show 'os-release' cat /etc/os-release
show 'security modules (landlock listed for the cells)' cat /sys/kernel/security/lsm
show 'systemd' systemctl --version
show 'cgroup filesystem (want cgroup2fs)' stat -fc %T /sys/fs/cgroup
show 'clock: the tape assumes no step back of more than 10 minutes, so NTP must be on' timedatectl
show 'memory' free -m
show 'disk under /var' df -h /var
show 'cpus' nproc
show 'listening tcp sockets (display :1 is port 5901)' ss -ltn
show 'the account tradeagent (absent before the first install)' getent passwd tradeagent
show 'sudo without a password' sudo -n true
show 'apt present (the kit installs its packages with it)' command -v apt-get
show 'sqlite3 present (the kit installs it)' command -v sqlite3
REMOTE
}

health() {
    lx_ssh 'sudo -n /opt/tradeagent/kit/tape-health' \
        || { echo "linux-host: tape-health did not run: the kit is not installed yet, or its version predates it (run install)." >&2; exit 1; }
}

install_kit() {
    local tarball="$1" word="$2" sums stage
    : "${TA_LINUX_ADDRESS:?set TA_LINUX_ADDRESS in $env_file: the address the display listens on (the kit --address argument)}"
    local zone="${TA_LINUX_ZONE:-Europe/Brussels}"
    [ -f "$tarball" ] || { echo "linux-host: no tarball at $tarball" >&2; exit 2; }
    case "$(basename "$tarball")" in TradeAgent-*-linux-x64.tar.gz) ;; *) echo "linux-host: $(basename "$tarball") is not a TradeAgent-*-linux-x64.tar.gz" >&2; exit 2 ;; esac
    sums="$(dirname "$tarball")/SHA256SUMS"
    [ -f "$sums" ] || { echo "linux-host: no SHA256SUMS beside the tarball (the kit checks it)" >&2; exit 2; }
    local f
    for f in install.sh display-prepare tape-health tradeagent.service tradeagent-display.service; do
        [ -f "$kit/$f" ] || { echo "linux-host: the kit file $kit/$f is missing" >&2; exit 2; }
    done
    echo "== installing $(basename "$tarball") with the kit of this tree ($(git -C "$here" rev-parse --short HEAD 2> /dev/null || echo unknown)); the owner's word: $word =="

    stage="$(lx_ssh 'umask 077 && mktemp -d')"
    case "$stage" in /*) ;; *) echo "linux-host: the host gave no staging folder" >&2; exit 1 ;; esac
    lx_scp "$tarball" "$sums" "$kit/install.sh" "$kit/display-prepare" "$kit/tape-health" \
        "$kit/tradeagent.service" "$kit/tradeagent-display.service" "$TA_LINUX_USER@$TA_LINUX_HOST:$stage/"

    lx_root "$stage" "$TA_LINUX_ADDRESS" "$zone" "$word" "$(basename "$tarball")" << 'REMOTE'
set -euo pipefail
stage="$1" address="$2" zone="$3" word="$4" tgz="$5"
account=tradeagent home=/var/lib/tradeagent
state="$home/.local/share/TradeAgent/state"
was_active=no
cleanup() { rm -rf "$stage"; }
trap cleanup EXIT
cd "$stage"
sha256sum -c --ignore-missing SHA256SUMS

# 1. STOP: the app's own clean quit (it records "TradeAgent stopped"); a failure from here to the kit starts the old one again.
if systemctl cat tradeagent.service > /dev/null 2>&1 && systemctl is-active --quiet tradeagent.service; then was_active=yes; fi
restore() { [ "$was_active" = yes ] && systemctl start tradeagent.service || true; }
trap 'restore; cleanup' ERR
if [ "$was_active" = yes ]; then echo "== stopping tradeagent.service =="; systemctl stop tradeagent.service; fi

# 2. BACK UP the databases (the tape's and the app's, WAL files with them) to a dated folder outside the home, each copy
#    compared to its source while the app is stopped. Nothing is overwritten and nothing is deleted.
shopt -s nullglob
files=("$state"/tape.db* "$state"/tradeagent.db*)
if [ "${#files[@]}" -gt 0 ]; then
    need_kb=$(( $(du -kc "${files[@]}" | tail -n 1 | cut -f1) * 2 + 1024 ))
    install -d -m 0700 /var/backups/tradeagent
    have_kb="$(df -Pk /var/backups/tradeagent | awk 'NR == 2 { print $4 }')"
    [ "$have_kb" -gt "$need_kb" ] || { echo "not enough room under /var/backups: $have_kb kB free, $need_kb kB needed" >&2; exit 1; }
    dest="/var/backups/tradeagent/$(date -u +%Y%m%dT%H%M%SZ)"
    install -d -m 0700 "$dest"
    for f in "${files[@]}"; do
        cp -a "$f" "$dest/"
        cmp -s "$f" "$dest/$(basename "$f")" || { echo "the copy of $f differs from it" >&2; exit 1; }
    done
    printf 'the owner'"'"'s word: %s\ntarball: %s\n' "$word" "$tgz" > "$dest/WORD.txt"
    echo "== backed up ${#files[@]} files to $dest =="
else
    echo "== no database in the home yet: nothing to back up (a first install) =="
fi

# 3. THE KIT. Its first run needs the VNC password (the window is reachable with one or not at all); it is made here, kept
#    root-only for the fleet to hand the owner once, and never printed.
args=(--tarball "$stage/$tgz" --address "$address" --zone "$zone")
if [ ! -s "$home/.vnc/vncauth" ]; then
    pf=/root/tradeagent-vnc-pass
    (umask 077; head -c 512 /dev/urandom | tr -dc 'A-Za-z0-9' | head -c 8 > "$pf")
    args+=(--vnc-pass-file "$pf")
    echo "== first install: the VNC password is in $pf (root-only, not printed); hand it over once, then delete it =="
fi
trap cleanup EXIT   # past this point the kit owns the service
trap - ERR
bash ./install.sh "${args[@]}"
echo "== kit done =="
REMOTE
    echo "== health, straight after the start (the first fetches take a minute; run health again in five) =="
    health
}

case "$verb" in
    recon) recon ;;
    health) health ;;
    install) install_kit "$1" "$2" ;;
    stop)
        lx_root "$1" << 'REMOTE'
set -euo pipefail
echo "== stopping tradeagent.service on the owner's word: $1 =="
systemctl stop tradeagent.service
echo "state: $(systemctl is-active tradeagent.service || true)"
REMOTE
        ;;
esac
