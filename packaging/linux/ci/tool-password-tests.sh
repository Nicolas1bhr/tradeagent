#!/usr/bin/env bash
# THE WINDOW'S PASSWORD, ON THE FLEET'S SIDE (U-window-door): tools/linux-host.sh against a fake ssh and scp that record every
# argument, every byte of stdin and the environment each call was given, so that what reaches "the host" can be read back.
# Run by CI's package-linux job (and by hand on a Mac or in Git Bash, with python3 on PATH for the dated word). Asserts:
#   - the password reaches the host on stdin only: in exactly one call's stdin, the root run of the install (or window-password)
#     script, as its one line; in no argument of any ssh or scp, and in no environment handed to them;
#   - it is never printed: not in the tool's stdout or stderr;
#   - it is never left on disk on the fleet's machine: not in any file under the test's HOME or TMPDIR;
#   - it is never generated: with no dialog to ask on and no seam, the tool refuses in words and sends nothing to stage;
#   - the test seam says so on the output whenever the password came from it;
#   - a host whose window already has a password is not asked for one, and its install gets an empty stdin;
#   - a password of the wrong shape is refused before anything is sent, and the refusal does not repeat it.
# The host's own side (the password piped into TigerVNC's tool, nothing written in plaintext, a real VNC login accepting it) is
# packaging/linux/ci/door.sh's, in the linux-host job.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../../.." && pwd)"
tool="${TOOL:-$repo/tools/linux-host.sh}"   # TOOL: another copy, for a red-first or mutant run
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fails=0
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; fails=$((fails + 1)); }

mkdir -p "$work/bin" "$work/home/.tradeagent" "$work/tmp" "$work/dist" "$work/fake"
cat > "$work/bin/ssh" << 'SSH'
#!/usr/bin/env bash
# the fake host: each call numbered; its arguments (NUL-separated), its environment and its stdin kept under $FAKE
n=$(( $(cat "$FAKE/n" 2> /dev/null || echo 0) + 1 )); echo "$n" > "$FAKE/n"
printf '%s\0' "$@" > "$FAKE/argv.$n"
env > "$FAKE/env.$n"
cmd="${!#}"
cat > "$FAKE/stdin.$n"
case "$cmd" in
    *'mktemp -d'*) echo /tmp/fake-stage ;;
    *'.vnc/vncauth'*has-one*) if [ "${FAKE_HAS_PASSWORD:-no}" = yes ]; then echo has-one; else echo has-none; fi ;;
    *tape-health*) echo 'tape|fake' ;;
esac
exit 0
SSH
cat > "$work/bin/scp" << 'SCP'
#!/usr/bin/env bash
n=$(( $(cat "$FAKE/n" 2> /dev/null || echo 0) + 1 )); echo "$n" > "$FAKE/n"
printf '%s\0' "$@" > "$FAKE/argv.$n"
env > "$FAKE/env.$n"
exit 0
SCP
# The seam: its password is read from a file outside the HOME and TMPDIR the tool is given (the scan for it on disk reads
# those two), and named by its path, so no environment holds the password itself.
cat > "$work/bin/seam" << 'SEAM'
#!/usr/bin/env bash
[ -z "${SEAM_CALLED:-}" ] || : > "$SEAM_CALLED"
head -n 1 "$SEAM_FILE"
SEAM
# The platform as the tool reads it: Linux, a seat with no screen to ask the owner on (and on a Mac or in Git Bash, where these
# tests may run by hand, no real dialog ever opens).
printf '#!/bin/sh\necho Linux\n' > "$work/bin/uname"
chmod +x "$work/bin/ssh" "$work/bin/scp" "$work/bin/seam" "$work/bin/uname"
: > "$work/key"
printf 'TA_LINUX_HOST=fake.invalid\nTA_LINUX_USER=admin\nTA_LINUX_KEY=%s\nTA_LINUX_ADDRESS=127.0.0.1\nTA_LINUX_ZONE=Europe/Brussels\n' \
    "$work/key" > "$work/home/.tradeagent/linux.env"
tgz="$work/dist/TradeAgent-0.0.1-linux-x64.tar.gz"
echo "not a real tarball" > "$tgz"
(cd "$work/dist" && sha256sum "$(basename "$tgz")" > SHA256SUMS)
today="$(python3 -I -c 'import datetime, zoneinfo; print(datetime.datetime.now(zoneinfo.ZoneInfo("Europe/Brussels")).date().isoformat())')"
word="$today the tool's own tests"

# run <name> <args...>: the tool with the fake host, a HOME and TMPDIR of its own; its output in $work/<name>.out, exit in $rc
run() {
    local name="$1"
    shift
    rm -rf "$work/fake" && mkdir -p "$work/fake"
    rc=0
    env HOME="$work/home" TMPDIR="$work/tmp" PATH="$work/bin:$PATH" FAKE="$work/fake" "$@" > "$work/$name.out" 2>&1 || rc=$?
}
# calls_matching <text>: the numbers of the fake calls whose arguments hold <text>
calls_matching() {
    local f
    for f in "$work"/fake/argv.*; do
        if tr '\0' '\n' < "$f" | grep -qF -- "$1"; then echo "${f##*.}"; fi
    done
}
# found <text> <paths...>: whether any file under the paths holds the text
found() { local t="$1"; shift; grep -rqaF -- "$t" "$@" 2> /dev/null; }

pw='Zq7#wP2x'
printf '%s\n' "$pw" > "$work/seam-secret"
export SEAM_FILE="$work/seam-secret"

# ---- 1. a first install, the password from the seam -----------------------------------------------------------------
run first-install TA_WINDOW_PASSWORD_FROM="$work/bin/seam" FAKE_HAS_PASSWORD=no "$tool" install "$tgz" "$word"
if [ "$rc" = 0 ]; then pass "a first install with the seam's password ran (exit 0)"; else fail "a first install exited $rc: $(tail -n 5 "$work/first-install.out")"; fi
holders="$({ grep -laF -- "$pw" "$work"/fake/stdin.* 2> /dev/null || true; } | sed 's/.*\.//' | sort -n | tr '\n' ' ')"
runner="$(calls_matching 'linux-host-install.sh' | while read -r n; do if tr '\0' '\n' < "$work/fake/argv.$n" | grep -q 'sudo -n bash'; then echo "$n"; fi; done)"
if [ -n "$runner" ] && [ "$holders" = "$runner " ] && [ "$(cat "$work/fake/stdin.$runner")" = "$pw" ]; then
    pass "stdin only: the password is in one call's stdin, the root run of the install script (call $runner), as its one line"
else
    fail "stdin only: the password is in the stdin of calls '$holders'; the root run of the install script is call '$runner'"
fi
if found "$pw" "$work"/fake/argv.*; then fail "the password is in an argument of ssh or scp"; else pass "no argument of any ssh or scp holds the password"; fi
if found "$pw" "$work"/fake/env.*; then fail "the password is in the environment handed to ssh or scp"; else pass "no environment handed to ssh or scp holds it"; fi
if grep -qaF -- "$pw" "$work/first-install.out"; then fail "never printed: the tool's output holds the password"; else pass "never printed: the tool's output does not hold it"; fi
if found "$pw" "$work/home" "$work/tmp"; then fail "never on disk: a file under the fleet machine's HOME or TMPDIR holds it: $(grep -rlaF -- "$pw" "$work/home" "$work/tmp")"; else pass "never on disk: no file under the fleet machine's HOME or TMPDIR holds it"; fi
if grep -q "did NOT come from the owner's dialog" "$work/first-install.out"; then pass "the output says the password came from the test seam"; else fail "the output does not say the password came from the seam"; fi
if grep -q "the window listens on loopback (127.0.0.1): only an administrator's ssh tunnel reaches it" "$work/first-install.out"; then
    pass "install says what a loopback address means for the owner"
else
    fail "install does not say what the address in force means"
fi

# ---- 2. no screen to ask on, no seam: refused, nothing staged ------------------------------------------------------
run no-screen env -u TA_WINDOW_PASSWORD_FROM FAKE_HAS_PASSWORD=no "$tool" install "$tgz" "$word"
if [ "$rc" != 0 ]; then pass "never generated: with no dialog and no seam the install refuses (exit $rc)"; else fail "never generated: with no dialog and no seam the install ran"; fi
if grep -q "no screen to ask the owner on" "$work/no-screen.out"; then pass "the refusal says there is no screen to ask the owner on"; else fail "the refusal does not say why: $(tail -n 3 "$work/no-screen.out")"; fi
if [ "$(cat "$work/fake/n")" = 1 ] && [ -n "$(calls_matching 'has-one')" ]; then
    pass "nothing was staged or run: the one call was the read of whether the window has a password"
else
    fail "a refused install still reached the host: $(for f in "$work"/fake/argv.*; do tr '\0' ' ' < "$f"; echo; done)"
fi

# ---- 3. the window already has a password: nobody is asked ---------------------------------------------------------
run has-one TA_WINDOW_PASSWORD_FROM="$work/bin/seam" SEAM_CALLED="$work/seam-called" FAKE_HAS_PASSWORD=yes "$tool" install "$tgz" "$word"
runner="$(calls_matching 'linux-host-install.sh' | while read -r n; do if tr '\0' '\n' < "$work/fake/argv.$n" | grep -q 'sudo -n bash'; then echo "$n"; fi; done)"
if [ "$rc" = 0 ] && [ ! -e "$work/seam-called" ] && [ -n "$runner" ] && [ ! -s "$work/fake/stdin.$runner" ]; then
    pass "a window that has a password: the install asks nobody and its root run gets an empty stdin"
else
    fail "a window that has a password: exit $rc, seam called: $([ -e "$work/seam-called" ] && echo yes || echo no), root run '$runner'"
fi

# ---- 4. a password of the wrong shape: refused before anything is sent, not repeated --------------------------------
for bad in 'Qx9' 'Qx9 Wq7z' 'Qx9Wq7zLL'; do
    printf '%s\n' "$bad" > "$work/seam-bad"
    run bad-shape env SEAM_FILE="$work/seam-bad" TA_WINDOW_PASSWORD_FROM="$work/bin/seam" FAKE_HAS_PASSWORD=no "$tool" install "$tgz" "$word"
    if [ "$rc" != 0 ] && [ -z "$(calls_matching 'mktemp -d')" ] && ! grep -qaF -- "$bad" "$work/bad-shape.out" && grep -q '6 to 8' "$work/bad-shape.out"; then
        pass "a password of ${#bad} characters: refused before anything was staged, in words, without repeating it"
    else
        fail "a password of ${#bad} characters: exit $rc, staged: $(calls_matching 'mktemp -d'), output: $(tail -n 2 "$work/bad-shape.out")"
    fi
done

# ---- 5. window-password: the same path, on the owner's dated word ----------------------------------------------------
run wp-no-word TA_WINDOW_PASSWORD_FROM="$work/bin/seam" "$tool" window-password "yes"
if [ "$rc" != 0 ] && [ ! -e "$work/fake/n" ] && grep -q "dated word" "$work/wp-no-word.out"; then
    pass "window-password without a dated word: refused before any call to the host"
else
    fail "window-password without a dated word: exit $rc, calls: $(cat "$work/fake/n" 2> /dev/null || echo 0)"
fi
run wp-no-screen env -u TA_WINDOW_PASSWORD_FROM "$tool" window-password "$word"
if [ "$rc" != 0 ] && [ ! -e "$work/fake/n" ] && grep -q "no screen to ask the owner on" "$work/wp-no-screen.out"; then
    pass "window-password with no dialog and no seam: refused in words before any call to the host"
else
    fail "window-password with no dialog and no seam: exit $rc, calls: $(cat "$work/fake/n" 2> /dev/null || echo 0)"
fi
run wp TA_WINDOW_PASSWORD_FROM="$work/bin/seam" "$tool" window-password "$word"
holders="$({ grep -laF -- "$pw" "$work"/fake/stdin.* 2> /dev/null || true; } | sed 's/.*\.//' | sort -n | tr '\n' ' ')"
runner="$(calls_matching 'linux-host-window-password.sh' | while read -r n; do if tr '\0' '\n' < "$work/fake/argv.$n" | grep -q 'sudo -n bash'; then echo "$n"; fi; done)"
if [ "$rc" = 0 ] && [ -n "$runner" ] && [ "$holders" = "$runner " ] && [ "$(cat "$work/fake/stdin.$runner")" = "$pw" ]; then
    pass "window-password: the password is in one call's stdin, the root run of its script (call $runner), as its one line"
else
    fail "window-password: exit $rc; the password is in the stdin of calls '$holders'; the root run is call '$runner'"
fi
if found "$pw" "$work"/fake/argv.* "$work"/fake/env.* "$work/wp.out" "$work/home" "$work/tmp"; then
    fail "window-password: the password is in an argument, an environment, the output or a file on the fleet's machine"
else
    pass "window-password: no argument, environment, output line or file on the fleet's machine holds it"
fi

echo
if [ "$fails" = 0 ]; then echo "tool-password-tests: all passed"; else echo "tool-password-tests: $fails FAILED"; exit 1; fi
