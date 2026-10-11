#!/usr/bin/env bash
# THE TIMING CATEGORY'S ONE RETRY, AND WHAT IT RE-RUNS (U-red-says-why): `build.yml`'s "Test the timing category".
#
#   timing-retry.sh filter <trx>...   the VSTest filter for the second attempt, on stdout: the Timing tests the
#                                     first attempt's trx files list as FAILED, and nothing else. Exit 1, nothing
#                                     on stdout and an ::error on stderr, when they name no failed test at all
#
# PER TEST, NOT PER CATEGORY. The retry used to re-run the whole category, so one red re-ran every timing test and
# a test that was GREEN on the first attempt could go red on the second and fail a run it had passed — Windows run
# 37524459410's button row did exactly that, behind a FaultTests press test's red. "Red twice is red" is a fact
# about one test; this makes the second attempt ask it of the tests that were red once, and only of them.
#
# A THEORY'S ROWS AT METHOD GRANULARITY. A failed result is joined, by its testId, to its definition's TestMethod
# element, whose className and name are the class and the METHOD — so a failed row re-runs every row of that method,
# matched as FullyQualifiedName=<class>.<method> (xUnit gives every row the method's name). Exact, never ~: a
# substring would re-run a method whose name merely contains another's.
#
# NOTHING NAMED, NOTHING RE-RUN. A first attempt that failed without a failed result in its trx — a crash, an abort,
# a host that died, no trx at all — is not a timing red that a second attempt can judge, and re-running the whole
# category for it is the scope this replaced. It exits 1 and says so; the step fails on it.
#
# PARSING. Newlines are flattened before splitting on '<', so every line starts at a tag and a test's captured
# output, which the trx writer escapes, can never start one: printed text that looks like an attribute is not read.
set -euo pipefail

usage() { echo "usage: timing-retry.sh filter <trx>..." >&2; exit 2; }
[ "${1:-}" = filter ] || usage
shift
[ $# -ge 1 ] || usage

names=$(for trx in "$@"; do
  [ -f "$trx" ] || continue
  tr '\r\n' '  ' < "$trx" | tr '<' '\n' | awk '
    function attr(line, key,   m) {
      if (match(line, " " key "=\"[^\"]*\"")) { m = substr(line, RSTART + length(key) + 3, RLENGTH - length(key) - 4); return m }
      return ""
    }
    /^UnitTest / { id = attr($0, "id"); next }
    /^TestMethod / && id != "" { method[id] = attr($0, "className") "." attr($0, "name"); id = ""; next }
    /^UnitTestResult / && attr($0, "outcome") == "Failed" { failed[attr($0, "testId")] = 1 }
    END { for (t in failed) if (t in method) print method[t] }
  '
done | sort -u)

if [ -z "$names" ]; then
  echo "::error title=The timing category failed and named no failed test::its first attempt failed, but no trx it wrote lists a failed test (a crash, an abort, or no trx at all), so there is nothing for a second attempt to judge. Nothing was re-run; read the step log above." >&2
  exit 1
fi

# The filter syntax's own operators, escaped, so a name is always read as a name.
filter=$(printf '%s\n' "$names" | sed -e 's/[\\()&|=!~]/\\&/g' -e 's/^/FullyQualifiedName=/' | paste -sd '|' -)
echo "Category=Timing&($filter)"
