#!/usr/bin/env bash
# THE ONE READER OF .github/ci/test-shards.txt (U-ci-shards, U-ci-shards-3): how the windows test job is split.
#
#   shard-filter.sh shards             the shard numbers, "1 2 ... N" — the workflow makes one windows job of each
#   shard-filter.sh filter <k>         shard k's test filter, to AND with the step's category filter: every class
#                                      whose full name ROUTES to k (below)
#   shard-filter.sh check <k> <names>  after shard k ran: <names> is the class names its tests ran under, one per
#                                      line ('-' reads stdin). An ::error and exit 1 for a class that does not route
#                                      to k, for a class name this routing cannot model, or for a shard that ran no
#                                      test at all; a ::warning for a prefix routed to k that no class ran under
#   shard-filter.sh spread <names>     after every shard ran: <names> is the class names of all of them. How many
#                                      classes each shard holds, and a ::warning when the last shard's catch-all
#                                      (classes no prefix matches) holds more than a tenth of them
#
# ROUTING BY PREFIX. A line "<shard> <prefix>" sends every class whose full name STARTS WITH the prefix to that shard,
# and the LONGEST matching prefix wins, so "3 TradeAgent.Tests.Unit.O" and "4 TradeAgent.Tests.Unit.Or" send
# OrgLedgerTests to 4 and OwnerPagesTests to 3. A class no prefix matches runs in the LAST shard, so a new class runs
# somewhere by construction — and, being named, usually where its first letters already send its neighbours, which
# keeps the shards balanced as classes are added without a hand edit. Names are compared ignoring case, as VSTest does.
#
# WHY THE FILTER IS EXACT, AND WHAT KEEPS IT SO. A shard's filter is, for each of its prefixes P,
# FullyQualifiedName~P minus the longer prefixes under P routed elsewhere, and for the last shard also "no prefix at
# all". ~ is a SUBSTRING match on Namespace.Class.Method, so it equals "the class name starts with P" only while the
# test namespaces are flat: every prefix is TradeAgent.Tests.<Segment>.<letters> (validated below), and every class
# that runs must be TradeAgent.Tests.<Segment>.<Class>, nested types after "+" (C# names hold no "."). Then P, which
# holds three dots, can only occur at the start of the name. `check` REFUSES a class of any other shape, so a
# namespace that breaks the flatness is a red on the run that adds it, never a test run twice or nowhere. The filter
# is a few hundred chars per prefix group, not one term per class: the old negative list grew by ~133 chars per listed
# class toward Windows' 32 767-char command line, and this one grows by ~45 chars per prefix line, twice for a top-level
# one (the last shard's catch-all names each): ~4 000 chars for the last shard on ~100 lines, while the top-level
# prefixes stay one letter. A filter over 8 000 chars is a ::warning, over 24 000 an ::error.
#
# A bad file is REFUSED — exit 1, nothing on stdout, every problem named — in every mode, before anything is printed.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
file="${SHARD_FILE:-$here/test-shards.txt}"
mode="${1:-}"
k=""
names=""

usage() { echo "usage: shard-filter.sh shards | filter <shard> | check <shard> <names-file|-> | spread <names-file|->" >&2; exit 2; }
case "$mode" in
  shards) [ $# -eq 1 ] || usage ;;
  filter) [ $# -eq 2 ] || usage; k="$2" ;;
  check) [ $# -eq 3 ] || usage; k="$2"; names="$3" ;;
  spread) [ $# -eq 2 ] || usage; names="$2" ;;
  *) usage ;;
esac
[ -f "$file" ] || { echo "::error::$file is missing" >&2; exit 1; }
if [ -n "$names" ] && [ "$names" != - ] && [ ! -f "$names" ]; then echo "::error::$names is missing" >&2; exit 1; fi

set -- "$file"
[ -n "$names" ] && set -- "$file" "$names"

awk -v mode="$mode" -v want="$k" -v list="$file" '
  function fail(msg) { printf "::error title=.github/ci/test-shards.txt::%s\n", msg > "/dev/stderr"; bad = 1 }
  # The prefix a class routes by: the longest line that starts it, or 0 — the last shard catch-all.
  function route(n,   i, best) {
    best = 0
    for (i = 1; i <= m; i++)
      if (index(n, low[i]) == 1 && (!best || length(low[i]) > length(low[best]))) best = i
    return best
  }
  function shardof(i) { return i ? shard[i] : count }
  function term(i,   j, jj, t, n, keep) {
    # Every prefix under i routed elsewhere, except one under another such prefix (that one already excludes it).
    t = "FullyQualifiedName~" pre[i]; n = 0
    for (j = 1; j <= m; j++) {
      if (j == i || shard[j] == shard[i] || index(low[j], low[i]) != 1) continue
      keep = 1
      for (jj = 1; jj <= m; jj++)
        if (jj != j && jj != i && shard[jj] != shard[i] && index(low[jj], low[i]) == 1 && length(low[jj]) < length(low[j]) && index(low[j], low[jj]) == 1) { keep = 0; break }
      if (keep) { t = t "&FullyQualifiedName!~" pre[j]; n++ }
    }
    return n ? "(" t ")" : t
  }

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
    if (NF != 2 || $1 !~ /^[0-9]+$/) { fail("line " FNR ": not \"<shard> <prefix>\": " $0); next }
    if ($2 !~ /^TradeAgent\.Tests\.[A-Za-z]+\.[A-Za-z0-9_]*$/) { fail("line " FNR ": not a prefix of the form TradeAgent.Tests.<Segment>.<letters>: " $2); next }
    key = tolower($2)
    if (key in seen) { fail("line " FNR ": " $2 " is listed twice (first on line " seen[key] ")"); next }
    seen[key] = FNR; m++; shard[m] = $1 + 0; pre[m] = $2; low[m] = key; at[m] = FNR
    next
  }

  # Runs once the file is read, before the first name: a bad file prints nothing, whatever the mode.
  function validate(   i, used) {
    if (validated) return
    validated = 1
    if (!count) fail("no \"shards <n>\" line")
    for (i = 1; i <= m; i++) {
      if (count && (shard[i] < 1 || shard[i] > count)) fail("line " at[i] ": shard " shard[i] ": the shards are 1 to " count)
      used[shard[i]]++
    }
    for (i = 1; i < count; i++) if (!used[i]) fail("shard " i " has no prefix, so it would run nothing")
    if ((mode == "filter" || mode == "check") && (want !~ /^[0-9]+$/ || want + 0 < 1 || want + 0 > count)) fail("there is no shard \"" want "\": the shards are 1 to " count)
    if (bad) exit 1
    want += 0
  }
  FILENAME != list && !validated { validate() }

  # THE NAMES (check and spread): each class that ran, routed.
  {
    sub(/\r$/, ""); if ($0 == "") next
    ran++
    n = tolower($0); r = route(n); s = shardof(r)
    if (mode == "spread") { held[s]++; if (!r) loose++; next }
    if (n !~ /^tradeagent\.tests\.[a-z]+\.[a-z_][a-z0-9_]*(\+[a-z_][a-z0-9_]*)*$/) {
      printf "::error title=A class this routing cannot model::%s ran in shard %d. The shard filters match prefixes as substrings, which is exact only for a class named TradeAgent.Tests.<Segment>.<Class> (nested types after +); a class named otherwise may run in two shards. Move it into a flat test namespace, or change .github/ci/shard-filter.sh to model it.\n", $0, want
      wrong++; next
    }
    # A prefix of this shard is in use when a class that ran here starts with it, routed by it or by a longer one of this shard.
    if (s == want) { for (i = 1; i <= m; i++) if (shard[i] == want && index(n, low[i]) == 1) hit[i] = 1; next }
    printf "::error title=A class ran in a shard it does not route to::%s ran in shard %d but routes to shard %d (%s), so it may run in more than one shard.\n", $0, want, s, (r ? "line " at[r] ", " pre[r] : "no prefix matches it: the last shard")
    wrong++
  }

  END {
    validate()
    if (bad) exit 1
    if (mode == "shards") {
      for (i = 1; i <= count; i++) printf "%s%d", (i > 1 ? " " : ""), i
      print ""
    } else if (mode == "filter") {
      out = ""
      for (i = 1; i <= m; i++) if (shard[i] == want) out = out (out == "" ? "" : "|") term(i)
      if (want == count) {
        loose = ""
        for (i = 1; i <= m; i++) {
          top = 1
          for (j = 1; j <= m; j++) if (j != i && index(low[i], low[j]) == 1) { top = 0; break }
          if (top) loose = loose (loose == "" ? "" : "&") "FullyQualifiedName!~" pre[i]
        }
        if (loose == "") loose = "FullyQualifiedName!=__no_test_is_named_this__"
        out = out (out == "" ? loose : "|(" loose ")")
      }
      if (length(out) > 24000) { printf "::error title=.github/ci/test-shards.txt::shard %d'"'"'s filter is %d chars, too near the 32 767-char Windows command line: route by shorter or fewer prefixes\n", want, length(out) > "/dev/stderr"; exit 1 }
      if (length(out) > 8000) printf "::warning title=A long shard filter::shard %d'"'"'s filter is %d chars; the Windows command line ends at 32 767. Route by shorter or fewer prefixes.\n", want, length(out) > "/dev/stderr"
      print out
    } else if (mode == "check") {
      if (!ran) {
        printf "::error title=This shard ran no test::shard %d ran no test at all, so its filter matched nothing or no result was written. Every shard runs a share of the suite: read the filter and the test step above.\n", want
        exit 1
      }
      listed = 0; stale = 0
      for (i = 1; i <= m; i++) {
        if (shard[i] != want) continue
        listed++
        if (!hit[i]) {
          stale++
          printf "::warning title=A prefix in .github/ci/test-shards.txt matched no class::%s (line %d, shard %d) matched no class that ran there. A class it no longer starts goes by a shorter prefix or to shard %d, the last; remove or change the line.\n", pre[i], at[i], want, count
        }
      }
      print "shard " want ": " ran " classes ran; " listed " prefixes, " listed - stale " matched a class, " stale " none; " wrong + 0 " classes that do not belong here"
      if (wrong) exit 1
    } else {
      for (i = 1; i <= count; i++) printf "shard %d: %d classes\n", i, held[i]
      printf "%d classes, %d of them matched by no prefix (in shard %d, the last)\n", ran, loose, count
      if (loose * 10 > ran)
        printf "::warning title=The last windows shard holds the unrouted classes::%d of %d classes match no prefix in .github/ci/test-shards.txt and all run in shard %d, the last. Route them by prefix before that shard becomes the longest job of every run.\n", loose, ran, count
    }
  }
' "$@"
