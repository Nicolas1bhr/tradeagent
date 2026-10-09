#!/usr/bin/env bash
# Builds TradeAgent for a Linux host: the app SELF-CONTAINED, with the single-file `trade` and
# `tradeagent-gateway` beside it, verified before anything is packaged, then one tar.gz and a
# SHA256SUMS that names it. The Linux half of packaging/build.ps1, and held to the same rules.
#
#   packaging/build-linux.sh                        linux-x64, into artifacts/linux
#   packaging/build-linux.sh --runtime linux-arm64  the same for an ARM host, on demand
#   packaging/build-linux.sh --output <dir>
#
# Runs on Linux and on the dev Mac (bash 3.2, BSD tools): `dotnet` has to be on PATH.
#
# Notes that matter:
#   - SELF-CONTAINED, like the Windows build: the host is never asked to install a .NET runtime.
#   - No ATAS bridge: it is loaded into ATAS, which runs on Windows only. This artifact cannot trade
#     through ATAS and says so in its manifest; the paper connector is what a Linux host runs.
#   - Every step that can fail is checked. A stage missing a file, a file too small to be the real
#     thing, or a binary built for another processor exits non-zero, names the file and packages
#     nothing. It never prints "Done" over an incomplete stage.
#   - The tarball holds one folder, TradeAgent-<version>-<runtime>/, which packaging/linux/install.sh
#     unpacks into /opt/tradeagent/<version>.
set -euo pipefail

runtime="linux-x64"
configuration="Release"
output="artifacts/linux"

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) runtime="${2:?--runtime needs a value}"; shift 2 ;;
        --configuration) configuration="${2:?--configuration needs a value}"; shift 2 ;;
        --output) output="${2:?--output needs a value}"; shift 2 ;;
        *) echo "build-linux.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

case "$runtime" in
    linux-x64) elf_machine="3e" ;;     # EM_X86_64
    linux-arm64) elf_machine="b7" ;;   # EM_AARCH64
    *) echo "build-linux.sh: '$runtime' is not a runtime this script builds (linux-x64, linux-arm64)" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

# The version is declared once, in Directory.Build.props, exactly as build.ps1 reads it.
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props | head -1 | tr -d '[:space:]')"
case "$version" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) echo "build-linux.sh: Directory.Build.props names no <Version> this script can read (got '$version')" >&2; exit 1 ;;
esac

case "$output" in
    ""|/|.|..) echo "build-linux.sh: '$output' cannot be the output folder: it is emptied first" >&2; exit 2 ;;
esac

name="TradeAgent-$version-$runtime"
stage="$output/$name"
tarball="$output/$name.tar.gz"

rm -rf "$output"
mkdir -p "$stage"

say() { printf '== %s ==\n' "$*"; }

# ---------------------------------------------------------------------------------------------
# Compile and stage
# ---------------------------------------------------------------------------------------------

say "publish desktop app (self-contained, $runtime)"
# This one publish also brings in Gateway, Connectors, AgentRuntime, Provisioning, Diagnostics and
# Security, because the app references them. They are verified below by name.
dotnet publish src/TradeAgent.App/TradeAgent.App.csproj -c "$configuration" -r "$runtime" \
    --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o "$stage" --nologo

say "publish trade CLI (self-contained, single file)"
cli_temp="$output/cli"
dotnet publish src/TradeAgent.TradeCli/TradeAgent.TradeCli.csproj -c "$configuration" -r "$runtime" \
    --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o "$cli_temp" --nologo
cp -f "$cli_temp/trade" "$stage/trade"
# Removed once copied: a leftover intermediate would otherwise be packaged as if it were shipping.
rm -rf "$cli_temp"

say "publish headless gateway (diagnostics and support)"
gw_temp="$output/gateway"
dotnet publish src/TradeAgent.GatewayHost/TradeAgent.GatewayHost.csproj -c "$configuration" -r "$runtime" \
    --self-contained true -p:PublishSingleFile=true -o "$gw_temp" --nologo
cp -f "$gw_temp/tradeagent-gateway" "$stage/tradeagent-gateway"
rm -rf "$gw_temp"

# ---------------------------------------------------------------------------------------------
# Verify the stage before anything is packaged around it (build.ps1's check, in bash)
#
# Each artifact is named individually so a failure says which piece is missing. The sizes are
# floors, not expectations: they catch a stub, a wrapper or a framework-dependent publish standing
# in for the real thing. The natives are the ones the window cannot open without on Linux.
# ---------------------------------------------------------------------------------------------

say "verify the staged build"

# path | floor in bytes | must run | what it is
expected="
TradeAgent|51200|yes|the desktop application
trade|1048576|yes|the CLI the AI calls; self-contained single file
tradeagent-gateway|1048576|yes|the headless gateway used by diagnostics
TradeAgent.Provisioning.dll|4096|no|installs the AI tool per-user, with no terminal
TradeAgent.AgentRuntime.dll|4096|no|runs the AI and hosts the in-app conversation
TradeAgent.Gateway.dll|4096|no|the execution authority: risk, idempotency, reconciliation
libSkiaSharp.so|1048576|no|draws the window (Avalonia's renderer)
libHarfBuzzSharp.so|262144|no|shapes the window's text
libe_sqlite3.so|262144|no|every ledger and the tape
"

size_of() { wc -c < "$1" | tr -d '[:space:]'; }

problems=""
key_files=""
while IFS='|' read -r path floor runs what; do
    [ -n "$path" ] || continue
    full="$stage/$path"
    if [ ! -f "$full" ]; then
        problems="$problems
   MISSING     $path  -  $what"
        continue
    fi
    len="$(size_of "$full")"
    key_files="$key_files
      $(printf '%-34s %s bytes' "$path" "$len")"
    if [ "$len" -lt "$floor" ]; then
        problems="$problems
   TOO SMALL   $path is $len bytes, expected at least $floor  -  $what"
    fi
    if [ "$runs" = yes ] && [ ! -x "$full" ]; then
        problems="$problems
   NOT RUNNABLE $path has no execute bit  -  $what"
    fi
    case "$path" in
        *.dll) ;;
        *)
            # Read the ELF header rather than trusting the switch: bytes 0-3 are the magic and
            # bytes 18-19 the machine, little-endian. A binary for another processor is a host
            # that cannot start the app, found here instead of on the owner's machine.
            magic="$(od -An -tx1 -N4 "$full" | tr -d ' \n')"
            machine="$(od -An -tx1 -j18 -N1 "$full" | tr -d ' \n')"
            if [ "$magic" != "7f454c46" ]; then
                problems="$problems
   NOT ELF     $path is not a Linux executable or library  -  $what"
            elif [ "$machine" != "$elf_machine" ]; then
                problems="$problems
   WRONG CPU   $path is built for ELF machine 0x$machine, not $runtime (0x$elf_machine)  -  $what"
            fi
            ;;
    esac
done <<EOF
$expected
EOF

stage_files="$(find "$stage" -type f | wc -l | tr -d '[:space:]')"
stage_bytes="$(find "$stage" -type f -exec wc -c {} + | awk '$2 != "total" { s += $1 } END { print s + 0 }')"
if [ "$stage_bytes" -lt 31457280 ]; then
    problems="$problems
   TOO SMALL   the whole stage is $stage_bytes bytes. A self-contained $runtime publish is far larger
               than that, so this was most likely published framework-dependent, which would make
               the host install a .NET runtime by hand."
fi

if [ -n "$problems" ]; then
    printf '%s\n\n' "$problems" >&2
    echo "build-linux.sh: the staged build in $stage is incomplete (problems listed above). Nothing was packaged." >&2
    exit 1
fi
echo "   $stage_files files, $stage_bytes bytes - every expected artifact present"

# ---------------------------------------------------------------------------------------------
# Package and checksum
# ---------------------------------------------------------------------------------------------

say "package"
# No extended attributes and no AppleDouble files: a tarball made on the dev Mac otherwise carries the
# Mac's own file metadata, which tar on the host warns about and nothing there reads.
COPYFILE_DISABLE=1 tar --no-xattrs -C "$output" -czf "$tarball" "$name"
[ -s "$tarball" ] || { echo "build-linux.sh: tar exited 0 but $tarball is empty or missing" >&2; exit 1; }

sha256() {
    if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

say "checksums"
# Exactly the file that ships, named as the release asset is, so "the artifact tested" can be proven
# later with `sha256sum -c SHA256SUMS` beside it — which is what install.sh does before it unpacks.
printf '%s  %s\n' "$(sha256 "$tarball")" "$(basename "$tarball")" > "$output/SHA256SUMS"

# ---------------------------------------------------------------------------------------------
# Manifest: what this artifact actually is, measured, not assumed
# ---------------------------------------------------------------------------------------------

echo ""
say "what this build actually contains"
echo "   version           $version"
echo "   configuration     $configuration  /  $runtime"
echo "   staged files      $stage_files files, $stage_bytes bytes"
echo "   ATAS bridge       ABSENT - ATAS runs on Windows only; this build trades on paper"
echo "   key files:$key_files"
echo "   tarball           $tarball  ($(size_of "$tarball") bytes)"
echo ""
cat "$output/SHA256SUMS"
echo ""
echo "Done. Artifacts in $output"
