#!/bin/bash
# Ship the working tree to C:\ta\repo on the Windows machine, minus build output.
#
# COPYFILE_DISABLE=1 is load-bearing: without it macOS tar writes AppleDouble "._*" companions for
# extended attributes, and csc rejects every one of them with "is a binary file instead of a text
# file". That failure looks like a corrupted checkout, not like a tar flag.
set -euo pipefail
[ -f "$HOME/.tradeagent/win.env" ] && source "$HOME/.tradeagent/win.env"
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

# The delete used to run with -EA 0 and say nothing about what it could not remove. That is not a
# harmless nicety: Windows refuses to delete a RUNNING executable but happily deletes the loose
# files beside it, so a push against a machine running the UI agent out of the repo removed that
# agent's runtimeconfig.json, left its locked .exe in place, and reported success. Nothing failed
# until the next reboot, hours later, when the agent could no longer start (0x80008083) and the
# machine came up unattended with no way to drive its own desktop.
#
# The agent now runs from C:\ta\agent\bin (see win-agent.sh), so this can no longer hit it — but a
# delete that silently leaves files behind is the wrong default whatever is holding them. Report the
# survivors instead of hiding them: leftovers mean the unpack is landing on top of something.
"$HERE/win-ps.sh" <<'PS'
$ErrorActionPreference = 'Stop'
$targets = 'C:\ta\repo\src', 'C:\ta\repo\tests', 'C:\ta\repo\packaging', 'C:\ta\repo\tools'
foreach ($t in $targets) {
  if (Test-Path $t) { Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue }
}
$left = @($targets | Where-Object { Test-Path $_ } | ForEach-Object {
  $n = @(Get-ChildItem $_ -Recurse -File -EA SilentlyContinue).Count
  "  LEFT BEHIND: $_ ($n file(s) still there - something holds them open)"
})
if ($left) { $left; "  a push that cannot fully clear the tree can leave STALE files that look built" }
New-Item -ItemType Directory -Force -Path C:\ta\repo | Out-Null
tar -xzf C:\ta\src.tgz -C C:\ta\repo
"unpacked: " + @(Get-ChildItem C:\ta\repo -Recurse -File).Count + " files"
PS
