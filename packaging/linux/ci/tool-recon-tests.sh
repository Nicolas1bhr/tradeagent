#!/usr/bin/env bash
# RECON READS FAIL2BAN AND WRITES NOTHING (U-window-door; D10 F7): tools/linux-host.sh recon with a fake ssh that runs recon's own
# remote script here, as the host would, with SSH_CONNECTION naming the address the tool "comes from", a sudo that only drops
# its -n, and a fake fail2ban-client that answers as fail2ban 1.0 does and records every subcommand it is asked. Run by CI's
# package-linux job. Asserted, for each case: the one line the fleet reads to the owner, that recon's earlier lines are still
# there ahead of it, and that fail2ban was asked only `status` and `get`.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../../.." && pwd)"
tool="${TOOL:-$repo/tools/linux-host.sh}"   # TOOL: another copy, for a red-first or mutant run
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fails=0
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; fails=$((fails + 1)); }

mkdir -p "$work/bin" "$work/f2b" "$work/home/.tradeagent"
cat > "$work/bin/ssh" << 'SSH'
#!/usr/bin/env bash
cmd="${!#}"
case "$cmd" in
    'bash -s') SSH_CONNECTION="$FAKE_FROM 50022 192.0.2.1 22" exec bash -s ;;
    *) echo "the fake host runs only recon here: $cmd" >&2; exit 1 ;;
esac
SSH
cat > "$work/bin/sudo" << 'SUDO'
#!/usr/bin/env bash
[ "${1:-}" = -n ] && shift
exec "$@"
SUDO
cat > "$work/f2b/fail2ban-client" << 'F2B'
#!/usr/bin/env bash
echo "$*" >> "$F2B_LOG"
case "$*" in
    'status sshd')
        if [ "$F2B_JAIL" = off ]; then echo "Sorry but the jail 'sshd' does not exist"; exit 255; fi
        printf 'Status for the jail: sshd\n|- Filter\n|  |- Currently failed:\t0\n|  `- Total failed:\t5\n`- Actions\n   |- Currently banned:\t1\n   |- Total banned:\t1\n   `- Banned IP list:\t%s\n' "$F2B_BANNED" ;;
    'get sshd ignoreip')
        if [ -z "$F2B_IGNORE" ]; then echo "No IP address/network is ignored"; else
            echo "These IP addresses/networks are ignored:"
            for n in $F2B_IGNORE; do echo "|- $n"; done
        fi ;;
    *) exit 1 ;;
esac
F2B
chmod +x "$work/bin/ssh" "$work/bin/sudo" "$work/f2b/fail2ban-client"
: > "$work/key"
printf 'TA_LINUX_HOST=fake.invalid\nTA_LINUX_USER=admin\nTA_LINUX_KEY=%s\n' "$work/key" > "$work/home/.tradeagent/linux.env"

# recon_case <name> <with fail2ban: yes|no> [VAR=value ...]: recon's output in $work/<name>.out
recon_case() {
    local name="$1" with="$2" path="$work/bin:$PATH"
    shift 2
    [ "$with" = yes ] && path="$work/bin:$work/f2b:$PATH"
    : > "$work/f2b.log"
    env HOME="$work/home" PATH="$path" F2B_LOG="$work/f2b.log" FAKE_FROM=198.51.100.7 F2B_JAIL=on F2B_BANNED='' F2B_IGNORE='127.0.0.0/8 ::1' \
        "$@" "$tool" recon > "$work/$name.out" 2>&1 || true
}
# expect <name> <line>: recon said exactly this, after its earlier lines, and fail2ban was asked only status and get
expect() {
    local name="$1" line="$2"
    if ! grep -qxF -- "$line" "$work/$name.out"; then fail "$name: recon does not say '$line': $(grep 'fail2ban' "$work/$name.out" | tail -n 3)"; return; fi
    if ! grep -q '== sqlite3 present' "$work/$name.out"; then fail "$name: recon's earlier lines are missing"; return; fi
    if [ "$(grep -n '== sqlite3 present' "$work/$name.out" | cut -d: -f1)" -gt "$(grep -nxF -- "$line" "$work/$name.out" | cut -d: -f1)" ]; then fail "$name: the fail2ban line is not at recon's end"; return; fi
    if grep -qvE '^(status sshd|get sshd ignoreip)$' "$work/f2b.log"; then fail "$name: fail2ban was asked more than status and get: $(tr '\n' ';' < "$work/f2b.log")"; return; fi
    pass "$name: '$line'"
}

recon_case absent no
expect absent "fail2ban: not installed here, so no jail can lock this machine out"

recon_case jail-off yes F2B_JAIL=off
expect jail-off "fail2ban: installed; its sshd jail is not running (Sorry but the jail 'sshd' does not exist)"

recon_case not-exempt yes F2B_BANNED='198.51.100.7'
expect not-exempt "fail2ban: the sshd jail is on, 1 address(es) banned now (198.51.100.7)"
expect not-exempt "fail2ban: 198.51.100.7, the address this tool comes from, is NOT exempt: a failed login from this machine can lock the fleet out of your server, the tool that opens your window included"

recon_case exempt-by-range yes F2B_IGNORE='127.0.0.1/8 ::1 198.51.100.0/24'
expect exempt-by-range "fail2ban: 198.51.100.7, the address this tool comes from, is exempt from the sshd jail"

recon_case exempt-v6 yes FAKE_FROM=fd7a:115c:a1e0::1 F2B_IGNORE='127.0.0.1/8 fd7a:115c:a1e0::/48'
expect exempt-v6 "fail2ban: fd7a:115c:a1e0::1, the address this tool comes from, is exempt from the sshd jail"

recon_case nothing-ignored yes F2B_IGNORE=''
expect nothing-ignored "fail2ban: 198.51.100.7, the address this tool comes from, is NOT exempt: a failed login from this machine can lock the fleet out of your server, the tool that opens your window included"

echo
if [ "$fails" = 0 ]; then echo "tool-recon-tests: all passed"; else echo "tool-recon-tests: $fails FAILED"; exit 1; fi
