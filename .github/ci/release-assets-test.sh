#!/usr/bin/env bash
# THE RELEASE-ASSETS CHECK, TESTED (U-windows-release). Run by release.yml before release-assets.sh touches the real artifacts, and
# runnable anywhere: release-assets-test.sh [path/to/release-assets.sh]. Each case builds its own fixture; exit 0 = every case held.
set -uo pipefail

script="${1:-$(dirname "$0")/release-assets.sh}"
v=0.2.0
fail=0
sha256() { if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

fixture() {   # a fresh pair of build outputs, as build.ps1 and build-linux.sh write them
  local d; d="$(mktemp -d)"
  mkdir -p "$d/win" "$d/lin"
  printf 'installer bytes' > "$d/win/TradeAgent-Setup-x64-no-atas.exe"
  printf '%s  artifacts/TradeAgent-Setup-x64-no-atas.exe\r\n' "$(sha256 "$d/win/TradeAgent-Setup-x64-no-atas.exe")" > "$d/win/SHA256SUMS.txt"
  printf 'kit bytes' > "$d/lin/TradeAgent-$v-linux-x64.tar.gz"
  printf '%s  TradeAgent-%s-linux-x64.tar.gz\n' "$(sha256 "$d/lin/TradeAgent-$v-linux-x64.tar.gz")" "$v" > "$d/lin/SHA256SUMS"
  printf '%s' "$d"
}

expect() {    # expect <case> <0|refused> <words or ''> -- <fixture dir>
  local name="$1" want="$2" words="$3" d="$5" said code
  said="$(bash "$script" "$v" "$d/win" "$d/lin" "$d/out" 2>&1)"; code=$?
  if [ "$want" = 0 ] && [ $code -ne 0 ]; then echo "FAIL $name: refused: $said"; fail=1; return; fi
  if [ "$want" = refused ] && [ $code -eq 0 ]; then echo "FAIL $name: exited 0, expected a refusal"; fail=1; return; fi
  if [ -n "$words" ] && ! grep -qF -- "$words" <<< "$said"; then echo "FAIL $name: said '$said', expected '$words'"; fail=1; return; fi
  echo "ok   $name"
}

d="$(fixture)"; expect "the two builds' files pass and one manifest names both" 0 "SHA256SUMS.txt and SHA256SUMS written" -- "$d"
if [ "$(grep -c . "$d/out/SHA256SUMS.txt" 2>/dev/null)" != 2 ] || ! cmp -s "$d/out/SHA256SUMS.txt" "$d/out/SHA256SUMS" \
   || ! grep -qE '^[0-9a-f]{64}  TradeAgent-Setup-x64-no-atas\.exe$' "$d/out/SHA256SUMS.txt"; then
  echo "FAIL the manifest: $(cat "$d/out/SHA256SUMS.txt" 2>/dev/null)"; fail=1
else echo "ok   the manifest has two bare-name lines and both copies are identical"; fi

d="$(fixture)"; printf 'X' >> "$d/win/TradeAgent-Setup-x64-no-atas.exe"
expect "an installer changed after its build hashed it is refused" refused "is not the file its build hashed" -- "$d"

d="$(fixture)"; printf 'X' >> "$d/lin/TradeAgent-$v-linux-x64.tar.gz"
expect "a kit changed after its build hashed it is refused" refused "is not the file its build hashed" -- "$d"

d="$(fixture)"; cp "$d/win/TradeAgent-Setup-x64-no-atas.exe" "$d/win/TradeAgent-Setup-x64.exe"
expect "a with-ATAS installer in a CI release is refused" refused "WITH the ATAS adapter" -- "$d"

d="$(fixture)"; rm "$d/win/SHA256SUMS.txt"
expect "an installer with no manifest is refused" refused "cannot be checked" -- "$d"

d="$(fixture)"; mv "$d/lin/TradeAgent-$v-linux-x64.tar.gz" "$d/lin/TradeAgent-0.1.9-linux-x64.tar.gz"
expect "a kit of another version is refused" refused "is not in" -- "$d"

exit $fail
