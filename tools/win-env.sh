# Sourced, not run, by every tools/win-*.sh: which Windows machine, and how to get in.
#
# There is more than one machine now, and the failure that matters is acting on the wrong one — a
# full suite left running on the ATAS box, or a probe looking for ATAS on a box that has none. So the
# choice is a name, and a name with no file behind it is refused rather than quietly answered by the
# default machine:
#
#   TA_WIN_BOX unset    ~/.tradeagent/win.env           the ATAS box: the original, and the default
#   TA_WIN_BOX=<name>   ~/.tradeagent/win-<name>.env    any other, e.g. TA_WIN_BOX=tests
#
# Each file sets TA_WIN_HOST, TA_WIN_USER, optionally TA_WIN_NAME (for `tailscale ping`), and one way in:
#
#   TA_WIN_KEY=<private key path>   preferred: what sits on this Mac is a key that opens one account
#                                   on one machine and is revoked by deleting one line over there
#   TA_WIN_PASSWORD=<password>      the ATAS box's original setup; needs sshpass
#
# Callers use win_ssh / win_scp and never build ssh options themselves, so the two auth paths cannot
# drift apart script by script — they had already been copied six times.
if [ -n "${TA_WIN_BOX:-}" ]; then
  _ta_env="$HOME/.tradeagent/win-$TA_WIN_BOX.env"
  if [ ! -f "$_ta_env" ]; then
    echo "TA_WIN_BOX=$TA_WIN_BOX, but $_ta_env does not exist." >&2
    echo "Refusing rather than falling back to the default machine. See tools/README.md." >&2
    exit 2
  fi
  source "$_ta_env"
elif [ -f "$HOME/.tradeagent/win.env" ]; then
  source "$HOME/.tradeagent/win.env"
fi
: "${TA_WIN_HOST:?set TA_WIN_HOST, or create ~/.tradeagent/win.env (tools/README.md)}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

_TA_SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 -o LogLevel=ERROR)
if [ -n "${TA_WIN_KEY:-}" ]; then
  # BatchMode: a refused key must fail now, not wait on a password prompt nobody will answer.
  _TA_SSH_OPTS+=(-i "$TA_WIN_KEY" -o IdentitiesOnly=yes -o BatchMode=yes)
elif [ -n "${TA_WIN_PASSWORD:-}" ]; then
  _TA_SSH_OPTS+=(-o PreferredAuthentications=password -o PubkeyAuthentication=no
                 -o NumberOfPasswordPrompts=1)
fi

_ta_with_auth() {
  if [ -z "${TA_WIN_KEY:-}" ] && [ -n "${TA_WIN_PASSWORD:-}" ]; then
    SSHPASS="$TA_WIN_PASSWORD" sshpass -e "$@"
  else
    "$@"
  fi
}

win_ssh() { _ta_with_auth ssh "${_TA_SSH_OPTS[@]}" "$TA_WIN_USER@$TA_WIN_HOST" "$@"; }
# win_scp <local> <remote path>   and   win_scp --get <remote path> <local>
win_scp() {
  if [ "$1" = "--get" ]; then
    _ta_with_auth scp "${_TA_SSH_OPTS[@]}" "$TA_WIN_USER@$TA_WIN_HOST:$2" "$3"
  else
    _ta_with_auth scp "${_TA_SSH_OPTS[@]}" "$1" "$TA_WIN_USER@$TA_WIN_HOST:$2"
  fi
}
