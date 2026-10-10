#!/usr/bin/env bash
# THE ONE READER OF .github/ci/test-shards.txt (U-ci-shards): how the windows test job is split.
#
#   shard-filter.sh shards             the shard numbers, "1 2 ... N" — the workflow makes one windows job of each
#   shard-filter.sh filter <k>         shard k's test filter, to AND with the step's category filter: the classes
#                                      listed for k, or for the LAST shard the COMPLEMENT — every test of a class
#                                      listed nowhere, so a new class runs there by construction
#   shard-filter.sh check <k> <names>  a ::warning for each class listed for k that no name in <names> belongs to
#                                      (class or test names, one per line; '-' reads stdin): a stale line
#
# EXACTLY ONCE. A listed class C is matched as FullyQualifiedName~C. or FullyQualifiedName~C+ — its own tests and
# those of its nested types, never a class whose name merely starts with C — and the complement negates every
# one of those terms. So a test belongs to the shard of the one line naming its class, or to the last shard.
# VSTest compares names ignoring case, so this script does too: a class listed twice in any spelling, or listed
# inside another listed class (C+Inner beside C), would match two shards, and the file is REFUSED — exit 1,
# nothing on stdout, every problem named — in every mode, before anything is printed.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
file="$here/test-shards.txt"
mode="${1:-}"
k="${2:-}"
names="${3:-}"

usage() { echo "usage: shard-filter.sh shards | filter <shard> | check <shard> <names-file|->" >&2; exit 2; }
case "$mode" in
  shards) [ $# -eq 1 ] || usage ;;
  filter) [ $# -eq 2 ] || usage ;;
  check) [ $# -eq 3 ] || usage ;;
  *) usage ;;
esac
[ -f "$file" ] || { echo "::error::$file is missing" >&2; exit 1; }
if [ "$mode" = check ] && [ "$names" != - ] && [ ! -f "$names" ]; then echo "::error::$names is missing" >&2; exit 1; fi

set -- "$file"
[ "$mode" = check ] && set -- "$file" "$names"

awk -v mode="$mode" -v want="$k" -v list="$file" '
  function fail(msg) { printf "::error title=.github/ci/test-shards.txt::%s\n", msg > "/dev/stderr"; bad = 1 }

  # THE FILE, every line validated before anything is printed.
  FILENAME == list {
    sub(/\r$/, ""); sub(/#.*/, "")
    if (NF == 0) next
    if ($1 == "shards") {
      if (NF != 2 || $2 !~ /^[0-9]+$/ || $2 + 0 < 2) fail("line " FNR ": \"shards\" takes one number, 2 or more")
      else if (count) fail("line " FNR ": a second \"shards\" line")
      else count = $2 + 0
      next
    }
    if (NF != 2 || $1 !~ /^[0-9]+$/) { fail("line " FNR ": not \"<shard> <class>\": " $0); next }
    if ($2 !~ /^[A-Za-z_][A-Za-z0-9_]*([.+][A-Za-z_][A-Za-z0-9_]*)+$/) { fail("line " FNR ": not a fully qualified class name: " $2); next }
    key = tolower($2)
    if (key in seen) { fail("line " FNR ": " $2 " is listed twice (first on line " seen[key] ")"); next }
    seen[key] = FNR; m++; shard[m] = $1 + 0; cls[m] = $2; low[m] = key; at[m] = FNR
    next
  }

  # Runs once the file is read, before the first name: a bad file prints nothing, whatever the mode.
  function validate(   i, j, used) {
    if (validated) return
    validated = 1
    if (!count) fail("no \"shards <n>\" line")
    for (i = 1; i <= m; i++) {
      if (count && (shard[i] < 1 || shard[i] >= count))
        fail("line " at[i] ": shard " shard[i] ": the listed shards are 1 to " count - 1 ", and shard " count ", the last, is the complement and lists nothing")
      used[shard[i]]++
      for (j = 1; j <= m; j++)
        if (i != j && (index(low[j], low[i] ".") == 1 || index(low[j], low[i] "+") == 1))
          fail("line " at[j] ": " cls[j] " is inside " cls[i] " (line " at[i] "), so its tests would match both lines")
    }
    for (i = 1; i < count; i++) if (!used[i]) fail("shard " i " lists no class")
    if (mode != "shards" && (want !~ /^[0-9]+$/ || want + 0 < 1 || want + 0 > count)) fail("there is no shard \"" want "\": the shards are 1 to " count)
    if (bad) exit 1
    want += 0
  }
  FILENAME != list && !validated { validate() }

  # THE NAMES (check only): which listed classes of the wanted shard ran a test.
  {
    sub(/\r$/, ""); n = tolower($0)
    for (i = 1; i <= m; i++)
      if (shard[i] == want && (n == low[i] || index(n, low[i] ".") == 1 || index(n, low[i] "+") == 1)) hit[i] = 1
  }

  END {
    if (bad) exit 1
    validate()
    if (mode == "shards") {
      for (i = 1; i <= count; i++) printf "%s%d", (i > 1 ? " " : ""), i
      print ""
    } else if (mode == "filter") {
      out = ""
      for (i = 1; i <= m; i++) {
        if (want < count && shard[i] == want)
          out = out (out == "" ? "" : "|") "FullyQualifiedName~" cls[i] ".|FullyQualifiedName~" cls[i] "+"
        if (want == count)
          out = out (out == "" ? "" : "&") "FullyQualifiedName!~" cls[i] ".&FullyQualifiedName!~" cls[i] "+"
      }
      print out
    } else {
      listed = 0; stale = 0
      for (i = 1; i <= m; i++) {
        if (shard[i] != want) continue
        listed++
        if (!hit[i]) {
          stale++
          printf "::warning title=A class listed in .github/ci/test-shards.txt ran no test::%s is listed for shard %d on line %d and no test of it ran there. Rename or remove the line; a class listed nowhere runs in shard %d, the last.\n", cls[i], want, at[i], count
        }
      }
      if (want == count) print "shard " want " is the complement and lists no class: nothing to check"
      else print "shard " want ": " listed " listed classes, " listed - stale " ran a test, " stale " ran none"
    }
  }
' "$@"
