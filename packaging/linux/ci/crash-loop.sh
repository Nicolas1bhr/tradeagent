#!/usr/bin/env bash
# THE CRASH LOOP (U-service-limits; R31 D12 § H), run by CI's linux-host job as root once the rollback step has left the app stopped,
# on the home prove.sh seeded. A crash is `systemctl kill --signal=SIGKILL`: the whole unit dies with nothing written, as a crash
# does, and Restart=always brings it back 10 s later. Asserted, against the unit's observable result:
#   - the unit's figures took effect (StartLimitBurst=6, StartLimitIntervalSec=10min);
#   - with the AI set to work on its own, every start after a killed one says the last start did not close properly and that
#     the AI waits, and none resumes it (the app's half);
#   - the loop stops at the limit: after six starts systemd refuses the seventh, leaves the unit failed with
#     Result=start-limit-hit, and nothing starts it again;
#   - a hand start works after `systemctl reset-failed` (what the kit and the fleet's tool run), and that start waits too;
#   - a hand `systemctl restart` is an orderly quit, not a crash: the start after it does not wait;
#   - the owner's pause survives a crash: a paused AI stays paused, and nothing waits.
# Recorded, not asserted, because they are systemd's answers and not the app's: whether a bare `systemctl restart` works while
# the limit stands, and what NRestarts reads after a hand restart (D12 asked whether it resets).
set -euo pipefail

account="${ACCOUNT:-tradeagent}"
home="${HOME_DIR:-/var/lib/tradeagent}"
out="${OUT:?OUT: where the evidence goes}"
unit=tradeagent.service
db="$home/.local/share/TradeAgent/state/tradeagent.db"

mkdir -p "$out"
summary="$out/crash-loop.txt"
: > "$summary"
note() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$summary"; }
pass() { note "PASS  $*"; }
fail() {
    note "FAIL  $*"
    journalctl -u "$unit" -n 60 --no-pager > "$out/crash-loop-journal.txt" 2>&1 || true
    echo "::error title=crash-loop::$*"
    exit 1
}
now() { date +%s; }
show() { systemctl show -p "$1" --value "$unit"; }
read_db() { sudo -u "$account" sqlite3 -readonly -batch -noheader -cmd '.timeout 5000' "$db" "$1"; }
started() { read_db "SELECT count(*) FROM activity WHERE text = 'TradeAgent started';" 2> /dev/null || echo 0; }
lines_like() { read_db "SELECT count(*) FROM activity WHERE text LIKE '$1%';" 2> /dev/null || echo 0; }
noticed() { lines_like 'The last time TradeAgent started, at '; }
waits() { lines_like 'The AI waits until '; }
works_on_its_own() { read_db "SELECT coalesce(json_extract(value, '\$.ai_works_on_its_own'), 0) FROM kv WHERE key = 'settings';"; }
set_owners_word() {   # written as the app's own account, with the app stopped
    sudo -u "$account" sqlite3 -batch "$db" \
        "UPDATE kv SET value = json_patch(value, '{\"ai_works_on_its_own\":$1,\"resume_ai_on_start\":true}') WHERE key = 'settings';"
}
wait_until() { local s=$1; shift; local end=$(( $(now) + s )); until "$@"; do [ "$(now)" -lt "$end" ] || return 1; sleep 1; done; }
started_past() { [ "$(started)" -gt "$1" ] && [ "$(show MainPID)" -gt 0 ]; }
waits_past() { [ "$(waits)" -gt "$1" ]; }
noticed_past() { [ "$(noticed)" -gt "$1" ]; }
limit_hit() { [ "$(show Result)" = start-limit-hit ]; }
started_or_limit() { started_past "$1" || limit_hit; }
crash() { systemctl kill --signal=SIGKILL "$unit"; wait_until 20 gone "$1" || fail "the killed process $1 is still alive 20 s on"; }
gone() { [ ! -e "/proc/$1" ]; }

# ---- 0. the figures ------------------------------------------------------------------------------------
note "StartLimitBurst=$(show StartLimitBurst) StartLimitIntervalUSec=$(show StartLimitIntervalUSec) RestartUSec=$(show RestartUSec) Restart=$(show Restart)"
[ "$(show StartLimitBurst)" = 6 ] || fail "StartLimitBurst is $(show StartLimitBurst), not the unit's 6"
[ "$(show StartLimitIntervalUSec)" = 10min ] || fail "StartLimitIntervalUSec is $(show StartLimitIntervalUSec), not the unit's 10min"
systemctl is-active --quiet "$unit" && fail "the app is running: the step before should have stopped it"
note "disk: $(df -h --output=avail,pcent "$home" | tail -n 1 | tr -s ' '); inodes: $(df -i --output=iavail,ipcent "$home" | tail -n 1 | tr -s ' ')"

# ---- 1. a crash loop with the AI set to work on its own --------------------------------------------------
set_owners_word true
systemctl reset-failed "$unit"
s0="$(started)"; w0="$(waits)"; n0="$(noticed)"
t0="$(now)"
systemctl start "$unit"
starts=0 kills=0 seen="$s0"
while :; do
    wait_until 120 started_or_limit "$seen" || fail "no start and no start limit within 120 s (starts $starts, kills $kills, state $(show ActiveState), result $(show Result))"
    if limit_hit; then break; fi
    starts=$((starts + 1)); seen="$(started)"
    # Every start after a killed one says so and waits; the first follows the rollback step's orderly stop.
    if [ "$starts" -gt 1 ]; then
        wait_until 60 waits_past "$((w0 + starts - 2))" || fail "start $starts after a crash did not say the AI waits ($(waits) wait lines; disk pause? $(lines_like 'The AI was paused'))"
    else
        sleep 5
    fi
    [ "$starts" -le 7 ] || fail "systemd started the app $starts times: the start limit did not stop the loop"
    pid="$(show MainPID)"
    crash "$pid"; kills=$((kills + 1))
done
t_limit="$(now)"
note "the loop: $starts starts, $kills kills, start limit hit $(( t_limit - t0 )) s after the first start; NRestarts=$(show NRestarts), ActiveState=$(show ActiveState), Result=$(show Result)"
[ "$starts" -eq 6 ] || fail "the loop stopped after $starts starts, not 6"
[ $(( t_limit - t0 )) -lt 600 ] || fail "the limit took $(( t_limit - t0 )) s, past the 10-minute window"
[ "$(( $(waits) - w0 ))" -eq 5 ] || fail "$(( $(waits) - w0 )) wait lines over 5 starts after a crash"
[ "$(( $(noticed) - n0 ))" -eq 5 ] || fail "$(( $(noticed) - n0 )) did-not-close lines over 5 starts after a crash"
[ "$(works_on_its_own)" = 1 ] || fail "the loop changed the owner's word: ai_works_on_its_own reads $(works_on_its_own)"
s_limit="$(started)"
sleep 30
[ "$(show ActiveState)" = failed ] || fail "30 s after the limit the unit is $(show ActiveState), not failed"
[ "$(started)" = "$s_limit" ] || fail "something started the app after the limit"
pass "a crash loop stopped at the limit: $starts starts in $(( t_limit - t0 )) s, the seventh refused (Result=start-limit-hit), nothing started since; each of the 5 starts after a crash said the AI waits"

# ---- 2. a hand restart -----------------------------------------------------------------------------------
# A bare restart first, as an operator would type it; where systemd refuses it while the limit stands, reset-failed (what the
# kit and the fleet's tool run) and a start. Either way the start that follows comes after a killed one, so it waits.
seen="$(started)"; w1="$(waits)"
if systemctl restart "$unit" > "$out/crash-loop-bare-restart.txt" 2>&1; then bare="started (exit 0)"; else bare="refused (exit $?): $(tr '\n' ' ' < "$out/crash-loop-bare-restart.txt")"; fi
sleep 3
note "ANSWER: a bare 'systemctl restart' while the limit stands: $bare; then ActiveState=$(show ActiveState), Result=$(show Result)"
if [ "$(show ActiveState)" != active ] && [ "$(show ActiveState)" != activating ]; then
    systemctl reset-failed "$unit"
    note "after reset-failed: NRestarts=$(show NRestarts), ActiveState=$(show ActiveState), Result=$(show Result)"
    systemctl start "$unit"
fi
wait_until 120 started_past "$seen" || fail "after the limit a hand start did not start the app within 120 s"
wait_until 60 waits_past "$w1" || fail "the hand start after the crash loop did not wait"
pass "after the limit a hand start works (bare restart: ${bare%% (*}), and it waits: the start before it was killed"

# D12's question: does a hand restart reset NRestarts? One crash first, so the count is above zero.
r_before="$(show NRestarts)"; seen="$(started)"
crash "$(show MainPID)"
wait_until 60 started_past "$seen" || fail "the crash after the hand start was not restarted within 60 s"
r_crash="$(show NRestarts)"
[ "$r_crash" -gt "$r_before" ] || fail "NRestarts did not rise on a crash ($r_before -> $r_crash)"
seen="$(started)"; w2="$(waits)"
systemctl restart "$unit"
wait_until 120 started_past "$seen" || fail "a hand 'systemctl restart' did not start the app within 120 s"
sleep 10
r_hand="$(show NRestarts)"
note "ANSWER: NRestarts $r_before after a hand start, $r_crash after a crash, $r_hand after a hand 'systemctl restart'"
[ "$(waits)" = "$w2" ] || fail "the start after a hand restart waited: an orderly quit was read as a crash"
pass "a hand 'systemctl restart' works and is an orderly quit: the start after it does not wait"

# ---- 3. the owner's pause survives a crash ---------------------------------------------------------------
systemctl stop "$unit"
set_owners_word false
systemctl reset-failed "$unit"
seen="$(started)"; n3="$(noticed)"; w3="$(waits)"
systemctl start "$unit"
wait_until 120 started_past "$seen" || fail "the paused home did not start within 120 s"
sleep 5
seen="$(started)"
crash "$(show MainPID)"
wait_until 60 started_past "$seen" || fail "the paused home was not restarted after its crash within 60 s"
wait_until 60 noticed_past "$n3" || fail "the start after the paused home's crash did not say the last start did not close properly"
sleep 5
[ "$(works_on_its_own)" = 0 ] || fail "after a crash the paused AI reads ai_works_on_its_own=$(works_on_its_own)"
[ "$(waits)" = "$w3" ] || fail "a paused AI waited after a crash: there is nothing to wait for"
pass "the owner's pause survives a crash: still paused, the crash said, no wait"
systemctl stop "$unit"
systemctl reset-failed "$unit"
read_db "SELECT at, text FROM activity WHERE text LIKE 'The last time TradeAgent started%' OR text LIKE 'The AI waits until%' ORDER BY id DESC LIMIT 4;" \
    | tee -a "$summary"
