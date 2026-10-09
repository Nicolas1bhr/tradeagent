#!/usr/bin/env bash
# TradeAgent on a Linux host, unattended: the build fleet's kit (U-linux-host item 4).
#
# RUN AS ROOT, OVER SSH, BY THE FLEET, ON THE OWNER'S WORD ONLY. Never by the owner — he never sees a
# terminal; his part is his Mac's Screen Sharing, a ChatGPT setting and a code typed on his phone — and
# never by a build unit on his machines. Nothing in this repository names a host, an address or a
# password: they are the arguments.
#
#   install.sh --tarball <dir>/TradeAgent-<version>-linux-x64.tar.gz   (SHA256SUMS beside it)
#              --address <the host's address on the owner's private network>
#              --zone <the owner's IANA zone, e.g. Europe/Brussels>
#              [--vnc-pass-file <file holding the VNC password, 6 to 8 characters>]
#              [--account tradeagent] [--home /var/lib/tradeagent] [--display 1] [--geometry 1280x800]
#              [--no-start]
#
# IDEMPOTENT. A second run with the same arguments changes nothing and restarts nothing. A run with a new
# tarball installs it beside the old one, moves the `current` link and restarts the app, which quits the
# way the app always quits. The VNC password file is needed on the first run only; a later run without
# it keeps the one already set.
#
# WHAT IT TOUCHES, AND NOTHING ELSE:
#   - packages: the libraries the window loads (Avalonia.X11 12.1.1 and libSkiaSharp.so), one font, and
#     TigerVNC's X server, its password tool and xauth. Installed when missing; nothing is ever upgraded.
#   - an account of its own with no login shell and a 0700 home, holding the app's data, its TMPDIR,
#     the X cookie and the VNC password file. The app's credentials are 0600 inside it from their first
#     byte (OwnerOnlyFile) — which keeps them from every OTHER account, and not from the agent: the
#     agent runs as this same account (EDGE-FACTORY § 6.11, containment, not claimed here).
#   - the app under /opt/tradeagent/<version>-<sha>, root-owned, with a `current` link, and two system
#     units, root-owned: the app's own account cannot change what systemd starts or what it runs.
#     Updates arrive this way, by deploy, on the owner's word; the app does not install them itself.
#   - the only listener is Xvnc, on --address, port 5900 + display, VNC password authentication.
set -euo pipefail

kit="$(cd "$(dirname "$0")" && pwd)"
root_dir=/opt/tradeagent

tarball="" address="" zone="" vnc_pass_file=""
account=tradeagent home=/var/lib/tradeagent display=1 geometry=1280x800 start=yes

refuse() { echo "install.sh: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --tarball) tarball="${2:?--tarball needs a file}"; shift 2 ;;
        --address) address="${2:?--address needs an address}"; shift 2 ;;
        --zone) zone="${2:?--zone needs an IANA zone}"; shift 2 ;;
        --vnc-pass-file) vnc_pass_file="${2:?--vnc-pass-file needs a file}"; shift 2 ;;
        --account) account="${2:?--account needs a name}"; shift 2 ;;
        --home) home="${2:?--home needs a folder}"; shift 2 ;;
        --display) display="${2:?--display needs a number}"; shift 2 ;;
        --geometry) geometry="${2:?--geometry needs WIDTHxHEIGHT}"; shift 2 ;;
        --no-start) start=no; shift ;;
        *) refuse "unknown argument '$1'" ;;
    esac
done

# ---------------------------------------------------------------------------------------------------
# Everything is checked before anything is changed
# ---------------------------------------------------------------------------------------------------

[ "$(id -u)" = 0 ] || refuse "run me as root: I create an account, write /opt/tradeagent and install system units"
command -v systemctl > /dev/null || refuse "this host has no systemd, and the app's restarts are systemd's"

[ -n "$tarball" ] || refuse "--tarball is required"
[ -f "$tarball" ] || refuse "no tarball at $tarball"
[ -n "$address" ] || refuse "--address is required: the host's address on the owner's private network"
[ -n "$zone" ] || refuse "--zone is required: the owner's zone, so his day and his spending cap turn at his midnight"
[ -f "/usr/share/zoneinfo/$zone" ] || refuse "'$zone' is not a zone this host knows (/usr/share/zoneinfo/$zone)"

case "$account" in [a-z_][a-z0-9_-]*) ;; *) refuse "'$account' is not an account name" ;; esac
case "$home" in /*) ;; *) refuse "--home must be an absolute path" ;; esac
case "$home" in /|/home|/root|/var|/var/lib|/opt|/tmp) refuse "--home cannot be $home: it becomes the account's own 0700 folder" ;; esac
case "$display" in [1-9]|[1-9][0-9]) ;; *) refuse "--display must be a number from 1 to 99" ;; esac
case "$geometry" in [1-9][0-9]*x[1-9][0-9]*) ;; *) refuse "--geometry must be WIDTHxHEIGHT, e.g. 1280x800" ;; esac
port=$((5900 + display))

# THE ADDRESS IS ONE THIS HOST HAS. Xvnc binds nothing else, so a typo would be a display nobody can
# reach — and an address meaning "every interface" would put the window's VNC port on every network
# this host is on, which is exactly what this kit exists not to do.
case "$address" in
    0.0.0.0|::|'[::]'|'*') refuse "--address $address means every interface; give the one address of the owner's private network" ;;
esac
host_addresses="$(ip -o addr show | awk '{ print $4 }' | cut -d/ -f1)"
printf '%s\n' "$host_addresses" | grep -Fx -- "$address" > /dev/null \
    || refuse "$address is not an address of this host (ip -o addr show): Xvnc could not listen on it"

# THE TARBALL IS THE ONE ITS SHA256SUMS NAMES, and it is for this host's processor.
sums="$(dirname "$tarball")/SHA256SUMS"
[ -f "$sums" ] || refuse "no SHA256SUMS beside $tarball: nothing to check it against, so it is not installed"
tarball_name="$(basename "$tarball")"
line="$(awk -v n="$tarball_name" '($2 == n || $2 == "*" n) && $1 ~ /^[0-9a-f]+$/ && length($1) == 64' "$sums")"
[ "$(printf '%s\n' "$line" | grep -c .)" = 1 ] || refuse "SHA256SUMS does not name $tarball_name exactly once"
sha="${line%% *}"
actual="$(sha256sum "$tarball" | cut -d' ' -f1)"
[ "$actual" = "$sha" ] || refuse "$tarball_name is not the file SHA256SUMS names (sha256 $actual, expected $sha)"

case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) refuse "TradeAgent is built for x86_64 and aarch64 Linux, not $(uname -m)" ;;
esac
# pipefail off for this one line: head stops reading after the first entry, and tar then dies of SIGPIPE.
top="$(set +o pipefail; tar -tzf "$tarball" | head -n 1 | cut -d/ -f1)"
case "$top" in
    TradeAgent-*-"$rid") version="${top#TradeAgent-}"; version="${version%-"$rid"}" ;;
    TradeAgent-*-linux-*) refuse "$tarball_name is built for ${top##*-}, and this host is $rid" ;;
    *) refuse "$tarball_name does not hold one TradeAgent-<version>-$rid folder (found '$top')" ;;
esac
case "$version" in [0-9]*.[0-9]*.[0-9]*) ;; *) refuse "'$version' is not a version" ;; esac

if [ -n "$vnc_pass_file" ]; then
    [ -r "$vnc_pass_file" ] || refuse "cannot read $vnc_pass_file"
    vnc_pass="$(head -n 1 "$vnc_pass_file")"
    # VNC's own authentication reads eight characters and ignores the rest, and TigerVNC refuses fewer
    # than six: a longer one would be a password the owner types that is not the one that counts.
    if [ "${#vnc_pass}" -lt 6 ] || [ "${#vnc_pass}" -gt 8 ]; then
        refuse "the VNC password must be 6 to 8 characters (VNC authentication reads eight)"
    fi
    unset vnc_pass
elif [ ! -s "$home/.vnc/vncauth" ]; then
    refuse "--vnc-pass-file is required on the first run: the window is reachable with a password or not at all"
fi

# ---------------------------------------------------------------------------------------------------
# Packages: installed when missing, never upgraded
# ---------------------------------------------------------------------------------------------------

packages=(libx11-6 libxext6 libxi6 libxrandr2 libxcursor1 libice6 libsm6 libfontconfig1 fonts-dejavu-core
          tigervnc-standalone-server tigervnc-tools xauth)
missing=()
for p in "${packages[@]}"; do
    # shellcheck disable=SC2016 # ${Status} is dpkg-query's own field name, not the shell's
    status="$(dpkg-query -W -f='${Status}' "$p" 2> /dev/null || true)"
    case "$status" in *"install ok installed"*) ;; *) missing+=("$p") ;; esac
done
if [ "${#missing[@]}" -gt 0 ]; then
    command -v apt-get > /dev/null || refuse "this host has no apt-get; install these first: ${missing[*]}"
    echo "== installing: ${missing[*]} =="
    DEBIAN_FRONTEND=noninteractive apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "${missing[@]}"
fi

xvnc="$(command -v Xvnc || command -v Xtigervnc || true)"
[ -n "$xvnc" ] || refuse "TigerVNC is installed but neither Xvnc nor Xtigervnc is on PATH"
vncpasswd="$(command -v tigervncpasswd || command -v vncpasswd || true)"
[ -n "$vncpasswd" ] || refuse "TigerVNC's password tool (tigervncpasswd) is not on PATH"
command -v xauth > /dev/null || refuse "xauth is not on PATH"

# ---------------------------------------------------------------------------------------------------
# The account and its 0700 home
# ---------------------------------------------------------------------------------------------------

if getent passwd "$account" > /dev/null; then
    existing_home="$(getent passwd "$account" | cut -d: -f6)"
    [ "$existing_home" = "$home" ] \
        || refuse "account $account exists with the home $existing_home, not $home: it is someone else's, or another install's"
else
    echo "== creating the account $account, home $home =="
    useradd --system --user-group --home-dir "$home" --no-create-home --shell /usr/sbin/nologin "$account"
fi
install -d -m 0700 -o "$account" -g "$account" "$home" "$home/tmp" "$home/.vnc"

changed_display=no
changed_app=no

if [ -n "$vnc_pass_file" ]; then
    new="$home/.vnc/vncauth.new"
    (umask 077 && head -n 1 "$vnc_pass_file" | "$vncpasswd" -f > "$new")
    chown "$account:$account" "$new"
    if cmp -s "$new" "$home/.vnc/vncauth"; then rm -f "$new"
    else mv -f "$new" "$home/.vnc/vncauth"; changed_display=yes
    fi
fi

# ---------------------------------------------------------------------------------------------------
# The app, root-owned, beside the versions before it
# ---------------------------------------------------------------------------------------------------

# The folder names the build as well as the version: while TradeAgent is being built every build is
# the same <Version>, and two different builds must not share a folder.
release="$version-${sha:0:12}"
install -d -m 0755 -o root -g root "$root_dir" "$root_dir/kit"

if [ ! -x "$root_dir/$release/TradeAgent" ]; then
    echo "== unpacking $tarball_name into $root_dir/$release =="
    work="$(mktemp -d "$root_dir/.unpack.XXXXXX")"
    trap 'rm -rf "$work"' EXIT
    tar -xzf "$tarball" -C "$work" --no-same-owner
    [ -x "$work/$top/TradeAgent" ] || refuse "$tarball_name unpacked without an executable TradeAgent"
    chown -R root:root "$work/$top"
    chmod -R u+rwX,go+rX,go-w "$work/$top"
    rm -rf "${root_dir:?}/$release"
    mv "$work/$top" "$root_dir/$release"
    touch "$root_dir/$release"   # its install time, which is what "the one before" is read from below
    rm -rf "$work"
    trap - EXIT
fi

if [ "$(readlink "$root_dir/current" 2> /dev/null || true)" != "$release" ]; then
    echo "== $root_dir/current -> $release =="
    ln -sfn "$release" "$root_dir/current.new"
    mv -Tf "$root_dir/current.new" "$root_dir/current"
    changed_app=yes
fi

# The version running and the one installed before it stay; anything older is removed. Each is ~300 MB.
previous="" previous_at=0
for d in "$root_dir"/*/; do
    b="$(basename "$d")"
    case "$b" in kit|"$release") continue ;; esac
    at="$(stat -c %Y "$d")"
    if [ "$at" -gt "$previous_at" ]; then previous="$b"; previous_at="$at"; fi
done
for d in "$root_dir"/*/; do
    b="$(basename "$d")"
    case "$b" in kit|"$release"|"$previous") ;; *) echo "== removing the old $b =="; rm -rf "${root_dir:?}/$b" ;; esac
done

install -m 0755 -o root -g root "$kit/display-prepare" "$root_dir/kit/display-prepare.new"
if cmp -s "$root_dir/kit/display-prepare.new" "$root_dir/kit/display-prepare"; then rm -f "$root_dir/kit/display-prepare.new"
else mv -f "$root_dir/kit/display-prepare.new" "$root_dir/kit/display-prepare"; changed_display=yes
fi

# ---------------------------------------------------------------------------------------------------
# The two units, root-owned
# ---------------------------------------------------------------------------------------------------

render() {
    sed -e "s|@ACCOUNT@|$account|g" -e "s|@HOME@|$home|g" -e "s|@DISPLAY@|$display|g" \
        -e "s|@GEOMETRY@|$geometry|g" -e "s|@ADDRESS@|$address|g" -e "s|@PORT@|$port|g" \
        -e "s|@ZONE@|$zone|g" -e "s|@XVNC@|$xvnc|g" -e "s|@KIT@|$root_dir/kit|g" -e "s|@ROOT@|$root_dir|g" "$1"
}

place_unit() {   # place_unit <template>: installs the rendered unit; exit 0 when it changed, 1 when not
    local name target new
    name="$(basename "$1")"
    target="/etc/systemd/system/$name"
    new="$target.new"
    render "$1" > "$new" || refuse "could not render $name"
    if grep -q '@[A-Z]*@' "$new"; then rm -f "$new"; refuse "$name kept a placeholder after rendering"; fi
    if ! chown root:root "$new" || ! chmod 0644 "$new"; then refuse "could not set the owner and mode of $new"; fi
    if cmp -s "$new" "$target"; then rm -f "$new"; return 1; fi
    mv -f "$new" "$target" || refuse "could not install $target"
}

if place_unit "$kit/tradeagent-display.service"; then changed_display=yes; fi
if place_unit "$kit/tradeagent.service"; then changed_app=yes; fi
systemctl daemon-reload
systemctl enable --quiet tradeagent-display.service tradeagent.service

# ---------------------------------------------------------------------------------------------------
# Start, or restart what changed
# ---------------------------------------------------------------------------------------------------

if [ "$start" = no ]; then
    echo "== installed $release; nothing started (--no-start) =="
    exit 0
fi

if [ "$changed_display" = yes ] && systemctl is-active --quiet tradeagent-display.service; then
    # Restarting the display restarts the app with it (Requires=), each quitting the way it quits.
    echo "== restarting the display, and the app with it =="
    systemctl restart tradeagent-display.service
elif [ "$changed_app" = yes ] && systemctl is-active --quiet tradeagent.service; then
    echo "== restarting the app on $release =="
    systemctl restart tradeagent.service
fi
systemctl start tradeagent-display.service tradeagent.service

systemctl is-active --quiet tradeagent-display.service || refuse "tradeagent-display is not running: journalctl -u tradeagent-display"
systemctl is-active --quiet tradeagent.service || refuse "tradeagent is not running: journalctl -u tradeagent"
echo "== TradeAgent $release is running on display :$display, reachable at $address:$port with the VNC password =="
