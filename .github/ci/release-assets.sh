#!/usr/bin/env bash
# THE ASSETS OF A RELEASE, CHECKED AND NAMED ONCE (U-windows-release). Run by .github/workflows/release.yml between the two package
# jobs and the draft; runnable anywhere with bash and sha256sum (or shasum).
#
#   release-assets.sh <version> <windows-dir> <linux-dir> <out-dir>
#
# <windows-dir> is packaging/build.ps1's artifact: the installer of a build WITHOUT the ATAS adapter (CI has no ATAS), named
# TradeAgent-Setup-x64-no-atas.exe, and the SHA256SUMS.txt build.ps1 wrote beside it. <linux-dir> is packaging/build-linux.sh's:
# TradeAgent-<version>-linux-x64.tar.gz and its SHA256SUMS. Each file is checked against the manifest its own build wrote — a file
# that is not the one its build hashed is refused, and so is a missing one, a with-ATAS installer, or a kit of another version.
#
# <out-dir> receives the two files and ONE manifest naming both, under the two names their readers look for: SHA256SUMS.txt (the
# in-app updater, src/TradeAgent.Provisioning/UpdateService.cs) and SHA256SUMS (packaging/linux/install.sh). One line per file,
# "<sha256>  <bare name>". The manifest is checked again against the files in <out-dir> before this exits 0.
set -euo pipefail

refuse() { echo "release-assets: $*" >&2; exit 1; }
[ $# -eq 4 ] || refuse "usage: release-assets.sh <version> <windows-dir> <linux-dir> <out-dir>"
version="$1"; win="$2"; lin="$3"; out="$4"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || refuse "'$version' is not a release version (major.minor.patch)"

sha256() { if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

installer="TradeAgent-Setup-x64-no-atas.exe"
kit="TradeAgent-$version-linux-x64.tar.gz"

# A file is the one its own build's manifest names: exactly one line ending in its bare name (a path before it allowed — build.ps1
# writes artifacts/<name>), and that line's hash is the file's.
check() {
  local dir="$1" manifest="$2" name="$3" line expected actual
  [ -f "$dir/$name" ] || refuse "$name is not in $dir"
  [ -f "$dir/$manifest" ] || refuse "$manifest is not in $dir, so $name cannot be checked"
  line="$(tr -d '\r' < "$dir/$manifest" | grep -E "^[0-9a-fA-F]{64} [ *]?(.*/)?${name//./\\.}$" || true)"
  [ "$(printf '%s\n' "$line" | grep -c .)" = 1 ] || refuse "$dir/$manifest does not name $name exactly once"
  expected="$(printf '%s' "${line%% *}" | tr 'A-F' 'a-f')"
  actual="$(sha256 "$dir/$name")"
  [ "$actual" = "$expected" ] || refuse "$name is not the file its build hashed (sha256 $actual, $manifest says $expected)"
  printf '%s' "$actual"
}

if ls "$win"/TradeAgent-Setup-x64.exe > /dev/null 2>&1; then
  refuse "$win holds TradeAgent-Setup-x64.exe, the name of a build WITH the ATAS adapter; a CI release never carries one"
fi
win_sha="$(check "$win" SHA256SUMS.txt "$installer")"
kit_sha="$(check "$lin" SHA256SUMS "$kit")"

mkdir -p "$out"
cp "$win/$installer" "$lin/$kit" "$out/"
printf '%s  %s\n%s  %s\n' "$win_sha" "$installer" "$kit_sha" "$kit" > "$out/SHA256SUMS.txt"
cp "$out/SHA256SUMS.txt" "$out/SHA256SUMS"

# And once more, over what will be uploaded.
check "$out" SHA256SUMS.txt "$installer" > /dev/null
check "$out" SHA256SUMS "$kit" > /dev/null
echo "release-assets: v$version — $installer and $kit checked against their builds' manifests; SHA256SUMS.txt and SHA256SUMS written:"
cat "$out/SHA256SUMS.txt"
