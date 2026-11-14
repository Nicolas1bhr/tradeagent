#!/usr/bin/env bash
# THE OWNER'S DOOR TO HIS WINDOW (U-window-door), run by CI's linux-host job AS THE RUNNER (it drives tools/linux-host.sh over a
# real ssh to this host, as the fleet does, and uses sudo for what only root can read), once the crash loop has left the app
# stopped on the home prove.sh seeded. Every login below is a REAL VNC login (packaging/linux/ci/vnc-auth.py: RFB 3.8, VNC
# authentication, the password on its stdin) against the kit's Xvnc. Asserted:
#   1. A FIRST INSTALL ON A HOME WITH NO vncauth (the branch that minted the owner's password on 2026-11-07, which CI never ran):
#      the tool asks for the password (here the test seam, which the output names), the kit takes it on stdin, and the display
#      accepts exactly that password and refuses another.
#   2. window-password: the next login refuses the old password and accepts the new one, with both units' MainPID unchanged
#      (Xvnc reads its -PasswordFile at each login: this is the proof the verb ships on), and the file an older tool left in
#      /root is removed without being read (it is a named pipe here: reading it would hang the verb, and the verb runs under a
#      timeout).
#   3. The kit's own password-only change (install.sh --vnc-pass-stdin, same release) restarts neither unit, and the new
#      password is the one that logs in.
#   4. None of the three passwords is in any file under the host's usual places, in the tool's output, or in the journal.
# Each password is made here, by CI, for CI: the product never makes one. They live in this script's memory and reach the tool
# through the seam program, which prints the one in its environment.
set -uo pipefail

repo="$(cd "$(dirname "$0")/../../.." && pwd)"
here="$repo/packaging/linux/ci"
address="${ADDRESS:?ADDRESS: the address the display listens on}"
key="${KEY:?KEY: the throwaway ssh key that the tool step authorized}"
out="${OUT:?OUT: where the evidence goes}"
tarball="${TARBALL:?TARBALL: the artifact the app runs}"
port=$((5900 + ${DISPLAY_NUMBER:-1}))
home=/var/lib/tradeagent

mkdir -p "$out"
summary="$out/door.txt"
: > "$summary"
fails=0
note() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$summary"; }
pass() { note "PASS  $*"; }
fail() { note "FAIL  $*"; echo "::error title=door::$*"; fails=$((fails + 1)); }

# login <password> <expect: accepted|refused>: one real VNC login, the password on stdin; its answer kept in the evidence.
login() {
    local pw="$1" want="$2" answer rc=0
    answer="$(printf '%s\n' "$pw" | python3 -I "$here/vnc-auth.py" "$address" "$port" 2>&1)" || rc=$?
    echo "$answer" >> "$out/door-logins.txt"
    case "$want:$rc" in
        accepted:0 | refused:1) return 0 ;;
        *) note "the login answered (exit $rc): $answer"; return 1 ;;
    esac
}
# check "<passed>" "<failed>" <command...>: one assertion, the command's exit its verdict
check() {
    local passed="$1" failed="$2"
    shift 2
    if "$@"; then pass "$passed"; else fail "$failed"; fi
}
pids() { echo "display=$(systemctl show -p MainPID --value tradeagent-display.service) app=$(systemctl show -p MainPID --value tradeagent.service)"; }
newpw() { head -c 256 /dev/urandom | tr -dc 'A-Za-z0-9' | head -c 8; }
dayof0() { python3 -I -c 'import datetime, zoneinfo; print(datetime.datetime.now(zoneinfo.ZoneInfo("Europe/Brussels")).date().isoformat())'; }

mkdir -p ~/.tradeagent
printf 'TA_LINUX_HOST=127.0.0.1\nTA_LINUX_USER=%s\nTA_LINUX_KEY=%s\nTA_LINUX_ADDRESS=%s\nTA_LINUX_ZONE=Europe/Brussels\n' \
    "$USER" "$key" "$address" > ~/.tradeagent/linux.env
chmod 600 ~/.tradeagent/linux.env
seam="${RUNNER_TEMP:-/tmp}/door-seam"
cat > "$seam" << 'SEAM'
#!/bin/sh
printf '%s\n' "$DOOR_PW"
SEAM
chmod 700 "$seam"

pw_a="$(newpw)" pw_b="$(newpw)" pw_c="$(newpw)" wrong="$(newpw)"

# ---- 1. a first install on a home with no vncauth -----------------------------------------------------------------
sudo systemctl stop tradeagent.service
sudo rm -f "$home/.vnc/vncauth"
sudo rm -f /root/tradeagent-vnc-pass
sudo mkfifo -m 0600 /root/tradeagent-vnc-pass   # what an older tool left; a named pipe, so reading it would hang
note "1. a first install on a home with no vncauth, through the fleet's tool"
DOOR_PW="$pw_a" TA_WINDOW_PASSWORD_FROM="$seam" timeout 900 "$repo/tools/linux-host.sh" install "$tarball" "$(dayof0) CI door check" \
    > "$out/door-install.txt" 2>&1 < /dev/null
rc=$?
if [ "$rc" = 0 ]; then pass "the tool's first install ran (exit 0)"; else fail "the tool's first install exited $rc: $(tail -n 5 "$out/door-install.txt")"; fi
for line in "first install: the window has no password yet" "did NOT come from the owner's dialog" "the window's password is the one the owner chose"; do
    check "the install says: $line" "the install does not say: $line" grep -qF "$line" "$out/door-install.txt"
done
check "the kit wrote $home/.vnc/vncauth" "no $home/.vnc/vncauth after the first install" sudo test -s "$home/.vnc/vncauth"
check "vncauth is the account's, 0600" "vncauth is $(sudo stat -c '%U %a' "$home/.vnc/vncauth" 2> /dev/null)" [ "$(sudo stat -c '%U %a' "$home/.vnc/vncauth" 2> /dev/null)" = "tradeagent 600" ]
check "the app runs after the first install" "the app is not running after the first install" systemctl is-active --quiet tradeagent.service
# THE WINDOW KNOWS ITS DOOR: the kit rendered it into the app's unit, and the running app was started with it.
unit_env="$(systemctl show -p Environment --value tradeagent.service)"
tool_line="$(printf '%s\n' "$unit_env" | tr ' ' '\n' | sed -n 's/^TA_WINDOW_PASSWORD_TOOL=//p')"
check "the app's unit names its door: TA_WINDOW_ADDRESS=$address TA_WINDOW_PORT=$port TA_WINDOW_PASSWORD_FILE=$home/.vnc/vncauth" \
    "the app's unit does not name its door: $unit_env" \
    grep -q "TA_WINDOW_ADDRESS=$address .*TA_WINDOW_PORT=$port .*TA_WINDOW_PASSWORD_FILE=$home/.vnc/vncauth" <<< "$unit_env"
check "the unit's password tool $tool_line is an absolute path to a program" "the unit's password tool '$tool_line' is not a program" \
    test -x "$tool_line"
sleep 3
check "a real VNC login with the password the seam gave is accepted" "the password the seam gave does not log in" login "$pw_a" accepted
check "a real VNC login with another password is refused" "another password was not refused" login "$wrong" refused

# ---- 2. window-password: the next login, nothing restarted --------------------------------------------------------
note "2. window-password on the owner's word"
before="$(pids)"
DOOR_PW="$pw_b" TA_WINDOW_PASSWORD_FROM="$seam" timeout 120 "$repo/tools/linux-host.sh" window-password "$(dayof0) CI door check" \
    > "$out/door-window-password.txt" 2>&1 < /dev/null
rc=$?
if [ "$rc" = 0 ]; then pass "window-password ran (exit 0, within its 120 s timeout: the old file was not read)"; else fail "window-password exited $rc: $(tail -n 5 "$out/door-window-password.txt")"; fi
check "window-password says nothing was restarted" "window-password does not say what it restarted" grep -q "nothing was restarted" "$out/door-window-password.txt"
check "the file an older tool left in /root is removed" "the file an older tool left in /root is still there" sudo test ! -e /root/tradeagent-vnc-pass
after="$(pids)"
check "window-password restarted neither unit ($after)" "window-password restarted a unit: $before -> $after" [ "$after" = "$before" ]
check "the next login refuses the old password" "the old password still logs in after window-password: Xvnc did not read the new file" login "$pw_a" refused
check "the next login accepts the new password: Xvnc reads its -PasswordFile at each login" "the new password does not log in without a restart: Xvnc reads its -PasswordFile only at its start" login "$pw_b" accepted
check "both units' MainPID are unchanged after the logins ($(pids))" "a unit restarted during the logins: $before -> $(pids)" [ "$(pids)" = "$before" ]

# ---- 3. the kit's own password-only change -------------------------------------------------------------------------
note "3. the kit's password-only change (install.sh --vnc-pass-stdin, the release that runs)"
before="$(pids)"
# shellcheck disable=SC2024 # the redirect is the evidence file, the runner's own: only the kit runs as root
printf '%s\n' "$pw_c" | sudo "$repo/packaging/linux/install.sh" --tarball "$tarball" --address "$address" --zone Europe/Brussels --vnc-pass-stdin \
    > "$out/door-kit.txt" 2>&1
rc=$?
if [ "$rc" = 0 ]; then pass "the kit ran (exit 0)"; else fail "the kit exited $rc: $(tail -n 5 "$out/door-kit.txt")"; fi
after="$(pids)"
check "a password-only change in the kit restarted neither unit ($after)" "a password-only change in the kit restarted a unit: $before -> $after" [ "$after" = "$before" ]
sleep 3
check "after the kit's change the previous password is refused" "after the kit's change the previous password still logs in" login "$pw_b" refused
check "after the kit's change its password logs in" "after the kit's change its password does not log in" login "$pw_c" accepted

# ---- 4. no plaintext anywhere -------------------------------------------------------------------------------------
note "4. none of the passwords is written down on the host"
# The patterns go to grep on its stdin (-f -), never as arguments: sudo logs its command line to auth.log and the journal, so
# a scan with the passwords in its arguments wrote them there itself (run 43530435382, this step's first run).
hits="$(printf '%s\n' "$pw_a" "$pw_b" "$pw_c" | sudo grep -rlaF -D skip --exclude-dir=proc --exclude-dir=sys -f - \
    /root /tmp /var/tmp /var/lib/tradeagent /var/backups /var/log /opt/tradeagent /etc /home /dev/shm /run "$out" 2> /dev/null || true)"
check "no file under /root /tmp /var/tmp the app's home /var/backups /var/log /opt/tradeagent /etc /home /dev/shm /run or the evidence holds one" "a file holds a password in plaintext: $hits" [ -z "$hits" ]
patterns="$(printf '%s\n' "$pw_a" "$pw_b" "$pw_c")"
if sudo journalctl --no-pager -o cat --since "-60min" 2> /dev/null | grep -qaF -f <(printf '%s\n' "$patterns"); then fail "the journal holds a password"; else pass "the journal holds none of them"; fi

rm -f ~/.tradeagent/linux.env "$seam"
sudo systemctl stop tradeagent.service
if [ "$fails" = 0 ]; then note "door: all passed"; else note "door: $fails FAILED"; exit 1; fi
