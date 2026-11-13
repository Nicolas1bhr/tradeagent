#!/usr/bin/env bash
# The fleet's hands on the owner's always-on Linux host (U-host-recording item 3): read it, deploy to it, stop it.
#
#   tools/linux-host.sh recon                          read-only: what the machine is, before anything is installed
#   tools/linux-host.sh health                         the tape as the kit reads it (kit/tape-health), systemd's restarts
#   tools/linux-host.sh org-digest [--since <T0>] [--json]   the AI organisation as the app's databases record it (read-only; tools/org-digest.py)
#   tools/linux-host.sh limits                         read-only: Yama's ptrace_scope, core_pattern, the service's Max core file size line
#   tools/linux-host.sh install <tarball> "<word>"     stop -> back up the home's databases -> the kit -> health
#   tools/linux-host.sh stop "<word>"                  systemctl stop tradeagent.service; the data stays
#
# THE WINDOW'S PASSWORD IS THE OWNER'S (U-window-door). It is the key to his only door to the app on this host (a VNC viewer,
# Screen Sharing on his Mac), so he chooses it himself, in a dialog on the screen of the machine this runs on: on macOS
# osascript's hidden-answer dialog, in Git Bash on Windows a Windows Forms dialog with two masked fields (not Get-Credential: a
# ConsolePrompting setting turns that into a terminal prompt). This tool never generates one. It travels on ssh's stdin only, to a
# root bash that keeps it in a variable and pipes it straight into TigerVNC's password tool: it is never in an argument, this
# tool's output, a log, an environment variable or a file, on this machine or on the host, where only the kit's obfuscated
# ~/.vnc/vncauth is written. A dropped connection cannot leave it anywhere: no step writes it down. With no screen to ask on (a
# Linux seat, an ssh or scheduled session) the tool refuses and sends nothing; never a terminal prompt, never a generated fallback.
# TA_WINDOW_PASSWORD_FROM, a test seam for CI only, names a program whose first line of output is the password; the output then
# says the password did not come from the owner's dialog.
#
# THE OWNER'S DATED WORD: `install` and `stop` run only on his word, which is an argument — a date, then his words, e.g.
# "2026-10-22 yes, deploy it" — and the script refuses without one, or with any date but TODAY'S: the date in the host's own zone
# (TA_LINUX_ZONE, the zone the kit gives the host; default Europe/Brussels), because "today" is the owner's day and the day on the
# tape, not the day on whichever machine runs this. A word from yesterday or for tomorrow is a new question for him.
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

usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

# word_ok "<word>" "<zone>": today's date in that zone, then at least three characters of the owner's own words.
word_ok() {
    python3 -I - "${1:-}" "$2" << 'PY'
import datetime, re, sys, zoneinfo
m = re.match(r"^(20\d\d-\d\d-\d\d)[ :,-]+(\S.{2,})$", sys.argv[1])
if not m:
    sys.exit(1)
try:
    day = datetime.date.fromisoformat(m.group(1))
    today = datetime.datetime.now(zoneinfo.ZoneInfo(sys.argv[2])).date()
except (ValueError, zoneinfo.ZoneInfoNotFoundError):
    sys.exit(1)
sys.exit(0 if day == today else 1)
PY
}
# need_word "<word>": called once the env file is read, because the zone is the env file's; still before anything touches the host.
need_word() {
    word_ok "${1:-}" "${TA_LINUX_ZONE:-Europe/Brussels}" || {
        echo "linux-host: '$verb' runs only on the owner's dated word, as an argument: \"YYYY-MM-DD <his words>\", dated TODAY (the date in ${TA_LINUX_ZONE:-Europe/Brussels})." >&2
        echo "Refusing without it." >&2
        exit 2
    }
}

# pw_ok "<password>": 6 to 8 printable ASCII characters, no space (VNC authentication reads eight; TigerVNC refuses fewer
# than six; ASCII so the same bytes leave a Mac, a Windows dialog and reach the host). It never prints what it checks.
pw_ok() {
    # shellcheck disable=SC2034 # LC_ALL is read by bash itself: [[:graph:]] in the C locale is printable ASCII without a space
    local LC_ALL=C
    [[ ${1:-} =~ ^[[:graph:]]{6,8}$ ]]
}

window_words="Choose the password for TradeAgent's window on your server. You type it in Screen Sharing, or any VNC viewer, to see and use the app. It is kept only on your server, and in your keychain if you let your viewer remember it; nobody is shown it. 6 to 8 letters, digits or symbols, no spaces. You can change it later in TradeAgent's Settings."

# ask_on_mac: osascript's dialog with a hidden answer, twice, in the owner's own (Aqua) session; the password on stdout.
ask_on_mac() {
    [ "$(launchctl managername 2> /dev/null || true)" = Aqua ] || {
        echo "linux-host: no screen to ask the owner on (this is not his desktop session): the window's password is his to type in a dialog here. Nothing was sent." >&2
        return 1
    }
    osascript - "$window_words" << 'OSA'
on run argv
    set t to "TradeAgent's window on your server"
    set msg to item 1 of argv
    repeat 5 times
        set a to text returned of (display dialog msg default answer "" with hidden answer with title t buttons {"Cancel", "OK"} default button "OK" giving up after 300)
        if (length of a) < 6 or (length of a) > 8 then
            display dialog "The password must be 6 to 8 characters." with title t buttons {"OK"} default button "OK" giving up after 120
        else
            set b to text returned of (display dialog "Type the same password again." default answer "" with hidden answer with title t buttons {"Cancel", "OK"} default button "OK" giving up after 300)
            considering case
                if a is b then return a
            end considering
            display dialog "The two passwords were not the same. Try again." with title t buttons {"OK"} default button "OK" giving up after 120
        end if
    end repeat
    error number -128
end run
OSA
}

# ask_on_windows: a Windows Forms dialog, two masked fields, in the owner's interactive desktop session (Windows PowerShell 5.1,
# which every Windows has); the password on stdout. Its stdin is /dev/null, so nothing in it can ever read a terminal.
ask_on_windows() {
    local ps b64
    ps="$(cat << 'PS'
$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::UserInteractive) { [Console]::Error.WriteLine('not an interactive desktop session'); exit 3 }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $f = New-Object System.Windows.Forms.Form
    $f.Text = "TradeAgent's window on your server"
    $f.FormBorderStyle = 'FixedDialog'; $f.StartPosition = 'CenterScreen'; $f.TopMost = $true
    $f.MaximizeBox = $false; $f.MinimizeBox = $false
    $f.ClientSize = New-Object System.Drawing.Size(480, 270)
    $words = New-Object System.Windows.Forms.Label
    $words.Text = $env:TA_WINDOW_WORDS; $words.SetBounds(12, 12, 456, 96)
    $l1 = New-Object System.Windows.Forms.Label; $l1.Text = 'Password'; $l1.SetBounds(12, 118, 120, 22)
    $p1 = New-Object System.Windows.Forms.TextBox; $p1.UseSystemPasswordChar = $true; $p1.MaxLength = 8; $p1.SetBounds(140, 116, 200, 22)
    $l2 = New-Object System.Windows.Forms.Label; $l2.Text = 'Type it again'; $l2.SetBounds(12, 150, 120, 22)
    $p2 = New-Object System.Windows.Forms.TextBox; $p2.UseSystemPasswordChar = $true; $p2.MaxLength = 8; $p2.SetBounds(140, 148, 200, 22)
    $err = New-Object System.Windows.Forms.Label; $err.ForeColor = [System.Drawing.Color]::Firebrick; $err.SetBounds(12, 182, 456, 36)
    $ok = New-Object System.Windows.Forms.Button; $ok.Text = 'OK'; $ok.SetBounds(296, 228, 80, 28)
    $no = New-Object System.Windows.Forms.Button; $no.Text = 'Cancel'; $no.SetBounds(388, 228, 80, 28)
    $no.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $f.AcceptButton = $ok; $f.CancelButton = $no
    $ok.Add_Click({
        if ($p1.Text -cne $p2.Text) { $err.Text = 'The two passwords are not the same.' }
        elseif ($p1.Text -cnotmatch '^[\x21-\x7E]{6,8}$') { $err.Text = 'The password must be 6 to 8 letters, digits or symbols, with no spaces.' }
        else { $f.DialogResult = [System.Windows.Forms.DialogResult]::OK; $f.Close() }
    })
    $timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 300000
    $timer.Add_Tick({ $timer.Stop(); $f.DialogResult = [System.Windows.Forms.DialogResult]::Cancel; $f.Close() })
    $f.Controls.AddRange(@($words, $l1, $p1, $l2, $p2, $err, $ok, $no))
    $f.Add_Shown({ $f.Activate(); $p1.Focus(); $timer.Start() })
    if ($f.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 1 }
    [Console]::Out.Write($p1.Text + "`n")
    exit 0
} catch { [Console]::Error.WriteLine('the dialog could not open: ' + $_.Exception.Message); exit 3 }
PS
)"
    command -v powershell.exe > /dev/null || { echo "linux-host: no Windows PowerShell here to ask the owner with. Nothing was sent." >&2; return 1; }
    b64="$(printf '%s' "$ps" | iconv -f UTF-8 -t UTF-16LE | base64 -w 0)"
    TA_WINDOW_WORDS="$window_words" MSYS_NO_PATHCONV=1 powershell.exe -NoProfile -NoLogo -ExecutionPolicy Bypass -EncodedCommand "$b64" < /dev/null \
        || { echo "linux-host: the owner's dialog was cancelled, or could not open on this desktop. Nothing was sent." >&2; return 1; }
}

# ask_window_password: the owner's password on stdout, for the caller's variable alone; or a refusal, and nothing is sent.
ask_window_password() {
    local pw
    if [ -n "${TA_WINDOW_PASSWORD_FROM:-}" ]; then
        echo "== the window's password comes from TA_WINDOW_PASSWORD_FROM, a test seam: it did NOT come from the owner's dialog ==" >&2
        pw="$("$TA_WINDOW_PASSWORD_FROM" < /dev/null)" || { echo "linux-host: the test seam failed. Nothing was sent." >&2; return 1; }
    else
        case "$(uname -s)" in
            Darwin) pw="$(ask_on_mac)" || return 1 ;;
            MINGW* | MSYS* | CYGWIN*) pw="$(ask_on_windows)" || return 1 ;;
            *)
                echo "linux-host: no screen to ask the owner on: the window's password is his to choose, in a dialog on his Mac or his Windows desktop, never typed into a terminal or made up here. Run this from his desktop. Nothing was sent." >&2
                return 1 ;;
        esac
    fi
    pw="${pw%%$'\n'*}"
    pw="${pw%$'\r'}"
    pw_ok "$pw" || { echo "linux-host: the window's password must be 6 to 8 letters, digits or symbols, with no spaces (VNC reads eight). Nothing was sent." >&2; return 1; }
    printf '%s\n' "$pw"
}
od_since="" od_json=no
case "$verb" in
    recon | health | limits) [ $# -eq 0 ] || usage ;;
    org-digest)
        while [ $# -gt 0 ]; do
            case "$1" in
                --json) od_json=yes; shift ;;
                --since)
                    if [ $# -lt 2 ] || [ -z "$2" ]; then usage; fi
                    od_since="$2"; shift 2 ;;
                *) usage ;;
            esac
        done
        # T0 goes onto a remote command line: a date, a date-time or a span (72h), and nothing a shell would read as more.
        case "$od_since" in *[!0-9A-Za-z:.+-]*) usage ;; esac ;;
    install) [ $# -eq 2 ] || usage ;;
    stop) [ $# -eq 1 ] || usage ;;
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
case "$verb" in install) need_word "$2" ;; stop) need_word "$1" ;; esac

_opts=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 -o LogLevel=ERROR
       -o ServerAliveInterval=30 -o ServerAliveCountMax=4
       -i "$TA_LINUX_KEY" -o IdentitiesOnly=yes -o BatchMode=yes)
lx_ssh() { ssh "${_opts[@]}" -p "$port" "$TA_LINUX_USER@$TA_LINUX_HOST" "$@"; }
lx_scp() { scp "${_opts[@]}" -P "$port" "$@"; }
# lx_root <args...> < script: the script on stdin, run by bash as root with the arguments; each argument is quoted for the far shell.
lx_root() { lx_ssh "sudo -n bash -s -- $(printf '%q ' "$@")"; }
# lx_root_staged <stage> <name> <args...> < data: the script on THIS function's fd 3 is written into the host's 0700 stage over
# ssh's stdin, then run by bash as root with the arguments, and the caller's stdin is the run's stdin: data only (the window's
# password, or nothing), never a command, never on a command line.
lx_root_staged() {
    local stage="$1" name="$2"
    shift 2
    lx_ssh "cat > $(printf '%q' "$stage/$name")" <&3
    lx_ssh "sudo -n bash $(printf '%q ' "$stage/$name" "$@")"
}
# has_window_password: whether the host's app account already has its window's password (exit 0 yes, 1 no), read as root; an
# ssh failure stops the tool, because guessing "no" would ask the owner for a password his window already has.
has_window_password() {
    local answer
    answer="$(lx_ssh "sudo -n sh -c 'if [ -s /var/lib/tradeagent/.vnc/vncauth ]; then echo has-one; else echo has-none; fi'" < /dev/null || true)"
    case "$answer" in
        has-one) return 0 ;;
        has-none) return 1 ;;
        *) echo "linux-host: could not read whether the host's window has a password (no answer from ssh and sudo -n). Nothing was sent." >&2; exit 1 ;;
    esac
}

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

# org_digest: tools/org-digest.py on stdin, run by python3 as the app's own account, against the app's databases opened read-only.
# It reads state and writes nothing: no file is put on the host, nothing started or stopped, no credential or auth file opened.
org_digest() {
    local args=(--home /var/lib/tradeagent)
    [ -z "$od_since" ] || args+=(--since "$od_since")
    [ "$od_json" = no ] || args+=(--json)
    lx_ssh "sudo -n -u tradeagent python3 -I - $(printf '%q ' "${args[@]}")" < "$here/org-digest.py"
}

# limits: three lines the cells' proof depends on, read as the plain account: no sudo, nothing written, started or stopped. /proc/<pid>/limits is world-readable.
limits() {
    lx_ssh 'bash -s' << 'REMOTE'
set -u
printf 'ptrace_scope: %s\n' "$(cat /proc/sys/kernel/yama/ptrace_scope 2> /dev/null || echo 'no Yama on this kernel')"
printf 'core_pattern: %s\n' "$(cat /proc/sys/kernel/core_pattern)"
pid="$(systemctl show -p MainPID --value tradeagent.service 2> /dev/null || true)"
case "$pid" in
    '' | 0) echo 'Max core file size: tradeagent.service has no main process' >&2; exit 1 ;;
esac
grep '^Max core file size' "/proc/$pid/limits"
REMOTE
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
    # A FIRST INSTALL ASKS THE OWNER FOR HIS WINDOW'S PASSWORD before anything is put on the host: a refusal installs nothing.
    local pw=""
    if ! has_window_password; then
        echo "== first install: the window has no password yet; asking the owner to choose one, on this machine's screen =="
        pw="$(ask_window_password)" || exit 2
    fi
    echo "== installing $(basename "$tarball") with the kit of this tree ($(git -C "$here" rev-parse --short HEAD 2> /dev/null || echo unknown)); the owner's word: $word =="

    stage="$(lx_ssh 'umask 077 && mktemp -d')"
    case "$stage" in /*) ;; *) echo "linux-host: the host gave no staging folder" >&2; exit 1 ;; esac
    lx_scp "$tarball" "$sums" "$kit/install.sh" "$kit/display-prepare" "$kit/tape-health" \
        "$kit/tradeagent.service" "$kit/tradeagent-display.service" "$TA_LINUX_USER@$TA_LINUX_HOST:$stage/"

    # stdin carries the password's one line (a first install) or nothing at all; the script itself goes on fd 3.
    if [ -n "$pw" ]; then printf '%s\n' "$pw"; fi | lx_root_staged "$stage" linux-host-install.sh \
        "$stage" "$TA_LINUX_ADDRESS" "$zone" "$word" "$(basename "$tarball")" 3<< 'REMOTE'
set -euo pipefail
stage="$1" address="$2" zone="$3" word="$4" tgz="$5"
account=tradeagent home=/var/lib/tradeagent
state="$home/.local/share/TradeAgent/state"
was_active=no kit_owns_it=no
# THE WINDOW'S PASSWORD, if one came: stdin's one line, held in this shell's memory only and handed to the kit on a pipe.
pw=""
IFS= read -r pw || true
# EVERY way out of this script, before the kit takes over, leaves the recorder as it was found: a failed command, a refusal
# (`exit 1` runs no ERR trap — the 2026-10-22 review's R2: too little room for the backup left it stopped), a signal.
on_exit() { local rc=$?; trap - EXIT; if [ "$was_active" = yes ] && [ "$kit_owns_it" = no ]; then systemctl reset-failed tradeagent.service 2> /dev/null || true; systemctl start tradeagent.service || true; fi; rm -rf "$stage"; exit "$rc"; }
trap on_exit EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
cd "$stage"
sha256sum -c --ignore-missing SHA256SUMS
if [ -z "$pw" ] && [ ! -s "$home/.vnc/vncauth" ]; then
    echo "the window has no password and none came from the owner's dialog: nothing was stopped or installed" >&2
    exit 1
fi

# 1. STOP: the app's own clean quit (it records "TradeAgent stopped"); any exit from here to the kit starts the old one again.
if systemctl cat tradeagent.service > /dev/null 2>&1 && systemctl is-active --quiet tradeagent.service; then was_active=yes; fi
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

# 3. THE KIT. Its first run needs the window's password (the window is reachable with one or not at all): the one the owner
#    chose in his dialog, on this script's stdin, handed to the kit on a pipe. Nothing here makes one up or writes it down.
args=(--tarball "$stage/$tgz" --address "$address" --zone "$zone")
kit_owns_it=yes   # past this point the kit owns the service
if [ -n "$pw" ]; then
    printf '%s\n' "$pw" | bash ./install.sh "${args[@]}" --vnc-pass-stdin
    pw=""
    echo "== the window's password is the one the owner chose =="
else
    bash ./install.sh "${args[@]}" < /dev/null
fi
echo "== kit done =="
REMOTE
    echo "== health, straight after the start (the first fetches take a minute; run health again in five) =="
    health
}

case "$verb" in
    recon) recon ;;
    health) health ;;
    org-digest) org_digest ;;
    limits) limits ;;
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
