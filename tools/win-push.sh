#!/bin/bash
# Ship the working tree to C:\ta\repo on the Windows machine, minus build output.
#
# COPYFILE_DISABLE=1 is load-bearing: without it macOS tar writes AppleDouble "._*" companions for
# extended attributes, and csc rejects every one of them with "is a binary file instead of a text
# file". That failure looks like a corrupted checkout, not like a tar flag.
set -euo pipefail
: "${TA_WIN_HOST:?set TA_WIN_HOST}"
: "${TA_WIN_USER:?set TA_WIN_USER}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
cd "$ROOT"

TARBALL="$(mktemp -t ta-src).tgz"
COPYFILE_DISABLE=1 tar --exclude='.git' --exclude='bin' --exclude='obj' --exclude='artifacts' \
  -czf "$TARBALL" .
echo "packed $(du -h "$TARBALL" | cut -f1)"

if [ -n "${TA_WIN_PASSWORD:-}" ]; then
  SSHPASS="$TA_WIN_PASSWORD" sshpass -e scp -o StrictHostKeyChecking=accept-new -o LogLevel=ERROR \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    "$TARBALL" "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/src.tgz"
else
  scp -o StrictHostKeyChecking=accept-new -o LogLevel=ERROR \
    "$TARBALL" "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/src.tgz"
fi
rm -f "$TARBALL"

"$HERE/win-run.sh" 'powershell -NoProfile -Command "Remove-Item C:\ta\repo\src,C:\ta\repo\tests,C:\ta\repo\packaging,C:\ta\repo\tools -Recurse -Force -EA 0; New-Item -ItemType Directory -Force -Path C:\ta\repo | Out-Null; tar -xzf C:\ta\src.tgz -C C:\ta\repo; Write-Output (\"unpacked: \" + (Get-ChildItem C:\ta\repo -Recurse -File | Measure-Object).Count + \" files\")"'
