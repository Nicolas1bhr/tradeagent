#!/usr/bin/env bash
# A LATE ADDRESS AT BOOT (U-window-door; D10 F3), run by CI's linux-host job as root, last, on the home prove.sh seeded. On the
# owner's server the window is to move from loopback to his private network's address (the tailnet's), and that address can
# appear only after the display's first start at boot. D10 inferred the result: Xvnc's bind fails, the display's start job
# fails, tradeagent.service's start job (Requires=, After=) fails with it, the display's Restart=always brings Xvnc back once
# the address exists, and nothing ever starts the app again. This shows it on a real systemd, and that the kit does not do it:
#   - the kit installed with --address 192.0.2.10 (TEST-NET-1, added to this runner's loopback interface);
#   - A BOOT, as far as these two units can tell: both stopped, the address removed, the app's start queued as boot queues it
#     (its Requires= pulls the display into the same transaction), the address added LATE_SECONDS later;
#   - asserted: within the display's wait (the kit's bound) plus a margin, Xvnc listens on 192.0.2.10, both units are active,
#     the app started (its "TradeAgent started" row) and it records a tape row again.
# Then THE CONTROL, when the kit's display unit carries the wait: the same boot with the wait taken out of the installed unit,
# which must leave the app down (the red the wait prevents). If that red does not reproduce, the output says so, plainly.
set -uo pipefail

account="${ACCOUNT:-tradeagent}"
home="${HOME_DIR:-/var/lib/tradeagent}"
out="${OUT:?OUT: where the evidence goes}"
tarball="${TARBALL:?TARBALL: the artifact the app runs}"
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
late="${LATE_SECONDS:-20}"
late_address=192.0.2.10
port=$((5900 + ${DISPLAY_NUMBER:-1}))
db="$home/.local/share/TradeAgent/state/tradeagent.db"
tape="$home/.local/share/TradeAgent/state/tape.db"
display_unit=/etc/systemd/system/tradeagent-display.service

mkdir -p "$out"
summary="$out/late-address.txt"
: > "$summary"
fails=0
note() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$summary"; }
pass() { note "PASS  $*"; }
fail() { note "FAIL  $*"; echo "::error title=late-address::$*"; fails=$((fails + 1)); }
now() { date +%s; }
read_db() { sudo -u "$account" sqlite3 -readonly -batch -noheader -cmd '.timeout 5000' "$1" "$2" 2> /dev/null || echo 0; }
started() { read_db "$db" "SELECT count(*) FROM activity WHERE text = 'TradeAgent started';"; }
tape_max() { read_db "$tape" "SELECT coalesce(max(id), 0) FROM tape_fetch;"; }
listening() { ss -H -ltn "src $late_address:$port" | grep -q .; }
wait_until() {
    local limit="$1" t0
    shift
    t0="$(now)"
    until "$@"; do
        [ $(( $(now) - t0 )) -lt "$limit" ] || return 1
        sleep 1
    done
}
# the display's own bound on its wait for the address, as the installed unit runs it (0 when it carries no wait)
bound() { sed -n 's|^ExecStartPre=.*display-prepare address [^ ]* \([0-9]*\)$|\1|p' "$display_unit" | head -n 1; }
journal() { journalctl -u tradeagent.service -u tradeagent-display.service --no-pager -o short-iso --since "@$1" > "$out/late-address-journal-$2.txt" 2>&1; }

# boot <label> <expect: up|down>: the boot described above; the verdict is the app's, LATE + the bound + 120 s after the queueing (the first tape row can take a minute)
boot() {
    local label="$1" want="$2" t0 s0 m0 limit b
    systemctl stop tradeagent.service tradeagent-display.service
    ip addr del "$late_address/32" dev lo 2> /dev/null || true
    systemctl reset-failed tradeagent.service tradeagent-display.service 2> /dev/null || true
    s0="$(started)" m0="$(tape_max)" t0="$(now)"
    systemctl start --no-block tradeagent.service
    sleep "$late"
    ip addr add "$late_address/32" dev lo
    note "$label: the app's start queued at $(date -u -d "@$t0" +%T), the address added ${late}s later"
    b="$(bound)"
    limit=$(( ${b:-0} + 120 ))
    app_up() { systemctl is-active --quiet tradeagent.service && [ "$(started)" -gt "$s0" ] && [ "$(tape_max)" -gt "$m0" ]; }
    if wait_until "$limit" app_up; then
        note "$label: the app is up $(( $(now) - t0 ))s after its start was queued (display $(systemctl is-active tradeagent-display.service), Xvnc on $late_address:$port: $(listening && echo yes || echo no))"
        journal "$t0" "$label"
        [ "$want" = up ]
    else
        note "$label: after ${limit}s the app is $(systemctl is-active tradeagent.service) (result: $(systemctl show -p Result --value tradeagent.service)), the display $(systemctl is-active tradeagent-display.service), Xvnc on $late_address:$port: $(listening && echo yes || echo no)"
        journal "$t0" "$label"
        [ "$want" = down ]
    fi
}

# ---- the kit, on the late address -----------------------------------------------------------------------------------
ip addr add "$late_address/32" dev lo 2> /dev/null || true
"$repo/packaging/linux/install.sh" --tarball "$tarball" --address "$late_address" --zone Europe/Brussels < /dev/null > "$out/late-address-install.txt" 2>&1 \
    || { fail "the kit did not install on $late_address: $(tail -n 5 "$out/late-address-install.txt")"; exit 1; }
if wait_until 30 listening; then pass "the kit's display listens on $late_address:$port"; else fail "the display does not listen on $late_address:$port after the install"; fi
note "the display's wait for its address: $(if [ -n "$(bound)" ]; then echo "at most $(bound) s"; else echo none; fi) ($(grep -c 'display-prepare address' "$display_unit") line in the installed unit)"

# ---- the boot, as the kit ships ---------------------------------------------------------------------------------------
if boot "as-shipped" up && listening; then
    pass "a late address at boot: the display waited for it, then the app started and recorded a tape row"
else
    fail "a late address at boot leaves the app down: the address came ${late}s after the start, and tradeagent.service never came back"
fi

# ---- the control: the same boot without the wait --------------------------------------------------------------------
if [ -n "$(bound)" ]; then
    cp "$display_unit" "$out/late-address-display-unit-as-shipped.txt"
    sed -i '/display-prepare address/d' "$display_unit"
    systemctl daemon-reload
    if boot "control-without-the-wait" down; then
        pass "the control: without the wait the same boot leaves the app down (the red the wait prevents)"
    else
        note "NOTE  the control's red did not reproduce: without the wait the app came up too, so this row proves the wait is harmless, not that it is needed"
    fi
    cp "$out/late-address-display-unit-as-shipped.txt" "$display_unit"
    systemctl daemon-reload
fi
systemctl stop tradeagent.service
if [ "$fails" = 0 ]; then note "late-address: all passed"; else note "late-address: $fails FAILED"; exit 1; fi
