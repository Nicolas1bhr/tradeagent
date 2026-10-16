#!/usr/bin/env bash
# THE HOST PROOF (U-linux-host item 5), run by CI's linux-host job as root after packaging/linux/install.sh
# has installed the job's own TradeAgent-linux-x64 artifact on an ubuntu-24.04 runner. Every venue the app
# knows is mapped to 127.0.0.1 before it starts, so nothing is asked of any exchange from a US runner; the
# one source that answers is this job's own loopback server, added through tape-sources.json.
#
# It reads the app's home and nothing else of the app — sqlite3 -readonly as the app's own account, never
# the pipe — and asserts the observable result of the unit:
#   - the kit's first start, stopped on its setup screen, ends within 15 s with its quit recorded;
#   - a seeded paper home (onboarding done, paper, GDELT off) records a loopback tape row;
#   - after kill -9 the app records again within 30 s: systemd restarted it and the new process took the
#     single-instance lock the dead one held;
#   - the only network listener of the app's account is Xvnc, on the one address, offering VNC
#     authentication only, and another account on the host cannot open the display;
#   - systemctl stop ends it within 15 s with its quit recorded ("TradeAgent stopped");
#   - after the seeded start and again after kill -9, the unit's cgroup is the account's (Delegate=yes), the app's
#     main process is in its supervisor/ leaf, +pids +memory are enabled below it, and no attempt-* is left;
#   - no built-in tape source got an answer from anywhere.
# Evidence lands in $OUT: summary.txt, samples.csv (RSS and the tape's growth), the window's screenshot,
# the units' journal, the activity log's tail and the tape's fetch counts.
set -euo pipefail

account="${ACCOUNT:-tradeagent}"
home="${HOME_DIR:-/var/lib/tradeagent}"
address="${ADDRESS:?ADDRESS: the address the display listens on}"
display="${DISPLAY_NUMBER:-1}"
minutes="${RUN_MINUTES:-20}"
out="${OUT:?OUT: where the evidence goes}"
here="$(cd "$(dirname "$0")" && pwd)"

port=$((5900 + display))
data="$home/.local/share/TradeAgent"     # Paths.Home: ~/.local/share/TradeAgent for an account with no XDG_DATA_HOME
db="$data/state/tradeagent.db"
tape="$data/state/tape.db"
source_id=ci-loopback
loopback_root=/srv/ta-ci-loopback

mkdir -p "$out"
summary="$out/summary.txt"
: > "$summary"

note() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$summary"; }
pass() { note "PASS  $*"; }
fail() { note "FAIL  $*"; echo "::error title=linux-host::$*"; exit 1; }
now() { date +%s; }
now_ms() { echo $(( $(date +%s%N) / 1000000 )); }

# Read-only, as the app's own account: no file of the app's is created, rewritten or re-owned by this.
read_db() { sudo -u "$account" sqlite3 -readonly -batch -noheader -cmd '.timeout 5000' "$1" "$2"; }
activity_count() { read_db "$db" "SELECT count(*) FROM activity WHERE text = '$1';" 2> /dev/null || echo 0; }
loopback_max_id() {
    read_db "$tape" "SELECT coalesce(max(id), 0) FROM tape_fetch WHERE source = '$source_id' AND http_status = 200;" 2> /dev/null || echo 0
}
main_pid() { systemctl show -p MainPID --value tradeagent.service; }

# wait_until <seconds> <function> [args]: true as soon as the function is, polled once a second.
wait_until() {
    local limit="$1" t0
    shift
    t0="$(now)"
    until "$@"; do
        [ $(( $(now) - t0 )) -lt "$limit" ] || return 1
        sleep 1
    done
}

first_start_done() { [ -f "$db" ] && [ "$(activity_count 'TradeAgent started')" -ge 1 ]; }

# THE DELEGATED CGROUP (U-cell-linux-b): the unit's cgroup is the account's, the app moved its main process into
# supervisor/, +pids +memory are enabled below the unit, and no attempt-* is left. Asked of the kernel, not the app.
unit_cgroup() { echo "/sys/fs/cgroup$(systemctl show -p ControlGroup --value tradeagent.service)"; }
main_in_supervisor() { [ "$(sed -n 's/^0:://p' "/proc/$(main_pid)/cgroup" 2> /dev/null)" = "$(systemctl show -p ControlGroup --value tradeagent.service)/supervisor" ]; }
assert_cgroup() {
    local when="$1" dir owner control leftover
    dir="$(unit_cgroup)"
    [ -d "$dir" ] || fail "$when: the unit has no cgroup at $dir"
    owner="$(stat -c %U "$dir")"
    [ "$owner" = "$account" ] || fail "$when: the unit's cgroup $dir belongs to $owner, not $account: Delegate=yes did not take"
    wait_until 30 main_in_supervisor \
        || fail "$when: the app's main process $(main_pid) is in $(sed -n 's/^0:://p' "/proc/$(main_pid)/cgroup" 2> /dev/null), not $dir/supervisor"
    control="$(cat "$dir/cgroup.subtree_control")"
    case " $control " in *" pids "*) ;; *) fail "$when: pids is not enabled below $dir (subtree_control: $control)" ;; esac
    case " $control " in *" memory "*) ;; *) fail "$when: memory is not enabled below $dir (subtree_control: $control)" ;; esac
    leftover="$(find "$dir" -mindepth 1 -maxdepth 1 -type d -name 'attempt-*' | tr '\n' ' ')"
    [ -z "$leftover" ] || fail "$when: attempt cgroups are left under $dir: $leftover"
    pass "$when: the unit's cgroup $dir is $account's, the app's main process $(main_pid) is in supervisor/, subtree_control '$control', no attempt-* left"
}
loopback_row_after() { [ "$(loopback_max_id)" -gt "$1" ]; }

# ---- evidence, whatever happens ---------------------------------------------------------------------
sampler_pid=""
collect() {
    set +e
    [ -n "$sampler_pid" ] && kill "$sampler_pid" 2> /dev/null
    journalctl -u tradeagent.service -u tradeagent-display.service -u ta-ci-loopback.service --no-pager -o short-iso \
        > "$out/journal.txt" 2>&1
    read_db "$db" "SELECT at, level, text FROM activity ORDER BY id DESC LIMIT 60;" > "$out/activity.txt" 2>&1
    read_db "$tape" "SELECT source, coalesce(http_status, 'none'), count(*) FROM tape_fetch GROUP BY 1, 2 ORDER BY 1, 2;" \
        > "$out/tape-fetch-counts.txt" 2>&1
    ss -H -ltnupe > "$out/listeners.txt" 2>&1
    chmod -R a+rX "$out"
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        { echo '### linux-host'; echo; echo '```'; cat "$summary"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"
    fi
}
trap collect EXIT

# ---- 0. the kit's first start makes the home ---------------------------------------------------------
note "phase 0: the kit's first start, which makes the home"
wait_until 180 first_start_done || fail "no database with 'TradeAgent started' in it within 180 s of the kit's start"
pass "first start: $db made, its start recorded"
# THE FIRST START'S STOP, ON THE SETUP SCREEN, seconds after the start. Before U-linux-host's fix a refresh of the
# closed host looped on that screen and systemd killed the app 30 s later (run 37917137298).
t_first_stop_ms="$(now_ms)"
systemctl stop tradeagent.service
first_stop_ms=$(( $(now_ms) - t_first_stop_ms ))
note "the first start's stop took $first_stop_ms ms; Result=$(systemctl show -p Result --value tradeagent.service)," \
     "ExecMainStatus=$(systemctl show -p ExecMainStatus --value tradeagent.service)"
[ "$first_stop_ms" -le 15000 ] || fail "systemctl stop on the setup screen took $first_stop_ms ms, more than 15 s"
[ "$(activity_count 'TradeAgent stopped')" -ge 1 ] || fail "the first start's stop recorded no 'TradeAgent stopped'"
pass "the first start, stopped on its setup screen, ended in $first_stop_ms ms with its quit recorded"

# ---- 1. the seeded paper home, and the one source that answers ---------------------------------------
note "phase 1: seeding the home (onboarding done, paper, GDELT off) and the loopback source"
seeded_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
sql=""
for step in WELCOME SYSTEM_CHECK AI_RUNTIME_SELECTED AI_RUNTIME_INSTALLED AI_AUTHENTICATED TRADING_PLATFORM_SELECTED \
            ATAS_INSTALLED ATAS_BRIDGE_INSTALLED ATAS_BRIDGE_CONNECTED TRADING_CONNECTION_FOUND ACCOUNT_SELECTED \
            MARKET_DATA_VERIFIED ORDER_ACCESS_VERIFIED WORKSPACE_CREATED AGENT_READY SETUP_COMPLETE; do
    sql="$sql INSERT INTO onboarding(step, completed_at, detail) VALUES ('$step', '$seeded_at', 'seeded by CI linux-host')
              ON CONFLICT(step) DO NOTHING;"
done
sql="$sql INSERT INTO kv(key, value) VALUES ('connector', 'paper') ON CONFLICT(key) DO UPDATE SET value = excluded.value;"
sql="$sql INSERT INTO kv(key, value) VALUES ('settings', '{\"selected_account_id\":\"PAPER-1\",\"record_gdelt_news\":false}')
          ON CONFLICT(key) DO UPDATE SET value = json_patch(value, excluded.value);"
# Written as the app's account, with the app stopped: the one write this script makes to the home.
sudo -u "$account" sqlite3 -batch "$db" "$sql"
install -m 0600 -o "$account" -g "$account" "$here/tape-sources.json" "$data/tape-sources.json"

mkdir -p "$loopback_root"
install -m 0644 "$here/loopback/quote.json" "$loopback_root/quote.json"
systemd-run --quiet --unit=ta-ci-loopback --property=DynamicUser=yes \
    python3 -m http.server 8765 --bind 127.0.0.1 --directory "$loopback_root"
loopback_answers() { curl -fsS -o /dev/null "http://127.0.0.1:8765/quote.json"; }
wait_until 20 loopback_answers || fail "the loopback source never answered"

# ---- 2. the seeded start records a loopback row --------------------------------------------------------
note "phase 2: the seeded start"
started="$(activity_count 'TradeAgent started')"
t_start="$(now)"
systemctl start tradeagent.service
wait_until 120 loopback_row_after 0 || fail "no loopback tape row within 120 s of the seeded start"
pass "a loopback tape row $(( $(now) - t_start )) s after the seeded start"
assert_cgroup "after the seeded start"

# The tape's size and the app's memory, every 15 s, to the end.
(
    set +e +o pipefail   # a sample that cannot be taken is a short row, never the end of the sampling
    echo "utc,main_pid,vm_rss_kb,vm_hwm_kb,xvnc_rss_kb,tape_bytes,tape_fetch_rows,loopback_200_rows"
    while true; do
        s_pid="$(main_pid)"
        s_rss="$(awk '/^VmRSS:/ { print $2 }' "/proc/$s_pid/status" 2> /dev/null || echo 0)"
        s_hwm="$(awk '/^VmHWM:/ { print $2 }' "/proc/$s_pid/status" 2> /dev/null || echo 0)"
        s_xpid="$(systemctl show -p MainPID --value tradeagent-display.service)"
        s_xrss="$(awk '/^VmRSS:/ { print $2 }' "/proc/$s_xpid/status" 2> /dev/null || echo 0)"
        s_bytes="$(cat "$tape" "$tape-wal" 2> /dev/null | wc -c | tr -d ' ')"
        s_rows="$(read_db "$tape" "SELECT count(*) FROM tape_fetch;" 2> /dev/null || echo 0)"
        s_loop="$(read_db "$tape" "SELECT count(*) FROM tape_fetch WHERE source = '$source_id' AND http_status = 200;" 2> /dev/null || echo 0)"
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$s_pid,${s_rss:-0},${s_hwm:-0},${s_xrss:-0},$s_bytes,$s_rows,$s_loop"
        sleep 15
    done
) > "$out/samples.csv" &
sampler_pid=$!

# ---- 3. the network: one listener, one address, VNC authentication only; the display refuses strangers
# The app's own broker door (U-thinking-seats-b) listens too — on 127.0.0.1 only, an ephemeral port, the app's own process:
# loopback is no network, and the door answers only a live launch grant from a peer inside that launch's container. So the
# account has exactly ONE listener on any other address (Xvnc), and at most one on loopback, which must be the app's.
note "phase 3: what listens, and who may open the display"
uid="$(id -u "$account")"
listeners="$(ss -H -ltnupe | grep "uid:$uid " || true)"
printf '%s\n' "$listeners" > "$out/listeners-of-$account.txt"
loopback="$(printf '%s\n' "$listeners" | grep '^tcp .* 127\.0\.0\.1:[0-9][0-9]* ' || true)"
network="$(printf '%s\n' "$listeners" | grep . | grep -v '^tcp .* 127\.0\.0\.1:[0-9][0-9]* ' || true)"
[ "$(printf '%s\n' "$network" | grep -c .)" = 1 ] \
    || fail "the account $account has $(printf '%s\n' "$network" | grep -c .) network listeners, not one: $network"
printf '%s\n' "$network" | grep "^tcp .* $address:$port " > /dev/null \
    || fail "the account's one network listener is not Xvnc on $address:$port: $network"
[ "$(printf '%s\n' "$loopback" | grep -c .)" -le 1 ] \
    || fail "the account $account has more than one loopback listener: $loopback"
if [ -n "$loopback" ]; then
    printf '%s\n' "$loopback" | grep "pid=$(main_pid)," > /dev/null \
        || fail "the account's loopback listener is not the app's own broker door (pid $(main_pid)): $loopback"
fi
pass "the account's only network listener is $address:$port (Xvnc); the app listens on no network, only on loopback for its broker door"

python3 -I - "$address" "$port" << 'PY' || fail "the VNC port does not offer VNC authentication alone"
import socket, sys
s = socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=10)
banner = s.recv(12)
assert banner.startswith(b"RFB 003."), banner
s.sendall(b"RFB 003.008\n")
count = s.recv(1)[0]
types = list(s.recv(count))
print("security types offered:", types)
assert types == [2], types          # 2 = VNC authentication; 1 (None) would be no password at all
PY
pass "the VNC port offers VNC authentication and nothing else"

if python3 -I -c "import socket; socket.create_connection(('127.0.0.1', $port), timeout=5)" 2> /dev/null; then
    fail "the VNC port also answers on 127.0.0.1: it is not bound to $address alone"
fi
pass "nothing answers on 127.0.0.1:$port"

sudo -u nobody python3 -I - "$display" << 'PY' || fail "an account without the X cookie could open the display"
import socket, struct, sys
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
s.connect("/tmp/.X11-unix/X" + sys.argv[1])
s.sendall(struct.pack("<cxHHHHxx", b"l", 11, 0, 0, 0))   # an X connection setup with no authorization
reply = s.recv(8)
status, reason_length = reply[0], reply[1]
reason = s.recv(reason_length).decode("latin-1", "replace") if status == 0 else ""
print("X server answered", {0: "Failed", 1: "Success", 2: "Authenticate"}.get(status, status), reason)
sys.exit(0 if status == 0 else 1)
PY
pass "another account (nobody) is refused by the display without the cookie"

# ---- 4. kill -9: restarted, and recording again within 30 s ------------------------------------------
until [ $(( $(now) - t_start )) -ge $(( minutes * 60 * 2 / 5 )) ]; do sleep 5; done
note "phase 4: kill -9"
pid="$(main_pid)"
[ "$pid" -gt 0 ] || fail "tradeagent has no main process to kill"
restarts_before="$(systemctl show -p NRestarts --value tradeagent.service)"
started_before="$(activity_count 'TradeAgent started')"
t_kill_ms="$(now_ms)"
kill -9 "$pid"
# The row baseline is read once the killed process is GONE (SIGKILL: it can commit nothing after that), and a row
# only counts once seen after it, so a row the dying process committed in its last instant can never count. The
# baseline is deliberately not read after the restart: the restarted app records its first loopback row at once
# (run 38125438221 saw it land before the restart check returned), and a baseline taken after that row would wait
# for the next one a whole 60 s cadence later. One loop, bounded by the 30 s from the kill, polls all three: the
# killed process gone, the restart conditions, and the row, whose time is taken the moment it is first seen, so
# "recorded again N ms after the kill" is kill -> first row and never a later check. Every condition is still
# required within the 30 s.
old_process_gone() { [ ! -e "/proc/$pid" ]; }
restart_done() {
    new_pid="$(main_pid)"
    restarts_after="$(systemctl show -p NRestarts --value tradeagent.service)"
    started_after="$(activity_count 'TradeAgent started')"
    [ "$new_pid" -gt 0 ] && [ "$new_pid" != "$pid" ] && [ "$restarts_after" -gt "$restarts_before" ] \
        && [ "$started_after" -gt "$started_before" ]
}
restart_missing() {
    local missing=""
    { [ "$new_pid" -gt 0 ] && [ "$new_pid" != "$pid" ]; } || missing="$missing new-main-process"
    [ "$restarts_after" -gt "$restarts_before" ] || missing="$missing NRestarts-increase"
    [ "$started_after" -gt "$started_before" ] || missing="$missing recorded-start"
    echo "${missing# }: main process $new_pid (was $pid), NRestarts $restarts_after (was $restarts_before), TradeAgent started $started_after (was $started_before)"
}
new_pid=0 restarts_after="$restarts_before" started_after="$started_before"
gone=0 before2="" restart_ms="" recorded_after_ms=""
while :; do
    if [ "$gone" = 0 ] && old_process_gone; then
        gone=1
        before2="$(loopback_max_id)"
    fi
    if [ -z "$recorded_after_ms" ] && [ "$gone" = 1 ] && loopback_row_after "$before2"; then
        recorded_after_ms=$(( $(now_ms) - t_kill_ms ))
    fi
    if [ -z "$restart_ms" ] && restart_done; then
        restart_ms=$(( $(now_ms) - t_kill_ms ))
    fi
    [ -n "$restart_ms" ] && [ -n "$recorded_after_ms" ] && break
    if [ $(( $(now_ms) - t_kill_ms )) -ge 30000 ]; then
        missing_now=""
        [ "$gone" = 1 ] || missing_now="$missing_now killed-process-gone (/proc/$pid still there)"
        [ -n "$restart_ms" ] || missing_now="$missing_now restart: $(restart_missing)"
        [ -n "$recorded_after_ms" ] || missing_now="$missing_now loopback-row-from-the-restarted-process (newest row id $(loopback_max_id), was ${before2:-unread} once the killed process was gone)"
        fail "within 30 s of kill -9 of $pid the recovery was incomplete, missing:${missing_now}"
    fi
    if [ "$gone" = 1 ]; then sleep 1; else sleep 0.1; fi
done
# The loop breaks as soon as both are seen, which can be a late iteration past its own deadline check: each is bounded here.
[ "$restart_ms" -le 30000 ] || fail "systemd's restart was confirmed only $restart_ms ms after kill -9 (the budget is 30000 ms)"
[ "$recorded_after_ms" -le 30000 ] || fail "recorded again only $recorded_after_ms ms after kill -9"
pass "kill -9 of $pid: systemd restarted it as $new_pid, which took the lock, started (${restart_ms} ms after the kill) and recorded again ${recorded_after_ms} ms after the kill"
assert_cgroup "after kill -9 and the restart"

# ---- 5. the window, as the owner would see it --------------------------------------------------------
until [ $(( $(now) - t_start )) -ge $(( minutes * 60 - 90 )) ]; do sleep 5; done
note "phase 5: the window's screenshot"
# shellcheck disable=SC2024 # the file is root's on purpose: only xwd runs as the account, with its cookie
sudo -u "$account" env DISPLAY=":$display" XAUTHORITY="$home/.Xauthority" xwd -root -silent > "$out/window.xwd" \
    || fail "xwd could not read display :$display with the account's cookie"
xwdtopnm "$out/window.xwd" 2> /dev/null > "$out/window.ppm"
pnmtopng "$out/window.ppm" > "$out/linux-host-window.png" 2> /dev/null
colours="$(ppmhist -noheader "$out/window.ppm" 2> /dev/null | grep -c .)"
rm -f "$out/window.xwd" "$out/window.ppm"
[ "$colours" -ge 16 ] || fail "the screenshot holds $colours colours: nothing was drawn on display :$display"
pass "the window's screenshot holds $colours colours (linux-host-window.png)"

# ---- 6. the stop: within 15 s, its quit recorded -------------------------------------------------------
until [ $(( $(now) - t_start )) -ge $(( minutes * 60 )) ]; do sleep 5; done
note "phase 6: systemctl stop"
memory_peak="$(cat /sys/fs/cgroup/system.slice/tradeagent.service/memory.peak 2> /dev/null || echo unknown)"
stopped_before="$(activity_count 'TradeAgent stopped')"
t_stop_ms="$(now_ms)"
systemctl stop tradeagent.service
stop_ms=$(( $(now_ms) - t_stop_ms ))
kill "$sampler_pid" 2> /dev/null || true
sampler_pid=""
stopped_after="$(activity_count 'TradeAgent stopped')"
note "the stop took $stop_ms ms; Result=$(systemctl show -p Result --value tradeagent.service)," \
     "ExecMainStatus=$(systemctl show -p ExecMainStatus --value tradeagent.service)"
[ "$stop_ms" -le 15000 ] || fail "systemctl stop took $stop_ms ms, more than 15 s"
[ "$stopped_after" -gt "$stopped_before" ] || fail "the stop recorded no 'TradeAgent stopped': the quit did not run"
pass "systemctl stop ended it in $stop_ms ms with its quit recorded"

# ---- 7. no venue answered, and what the run weighed --------------------------------------------------
answered="$(read_db "$tape" "SELECT count(*) FROM tape_fetch WHERE source <> '$source_id' AND http_status IS NOT NULL;")"
asked="$(read_db "$tape" "SELECT count(*) FROM tape_fetch WHERE source <> '$source_id';")"
[ "$answered" = 0 ] || fail "$answered of the built-in sources' $asked fetches got an HTTP answer: something reached a venue"
pass "no venue answered: the built-in sources' $asked fetches all went to 127.0.0.1 and were refused"

peak_rss_kb="$(awk -F, 'NR > 1 && $3 > m { m = $3 } END { print m + 0 }' "$out/samples.csv")"
first_bytes="$(awk -F, 'NR == 2 { print $6 }' "$out/samples.csv")"
last_bytes="$(awk -F, 'END { print $6 }' "$out/samples.csv")"
note "peak RSS of the app's main process: $peak_rss_kb kB (sampled); the unit's cgroup memory.peak since its restart: $memory_peak bytes"
note "the tape: $first_bytes bytes at the first sample, $last_bytes at the last, over $(( $(grep -c . "$out/samples.csv") - 1 )) samples 15 s apart"
note "loopback rows recorded: $(read_db "$tape" "SELECT count(*) FROM tape_fetch WHERE source = '$source_id' AND http_status = 200;"); starts recorded: $(activity_count 'TradeAgent started') (was $started before the seeded start)"
pass "linux-host: $minutes minutes unattended"
