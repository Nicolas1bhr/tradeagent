#!/usr/bin/env bash
# THE STRUCTURAL PRE-CHECK (U-ci-precheck): the millisecond reds, caught on the machine in a few minutes before a full CI run spends
# 60-125 job-minutes to find them. Seven of the ten deterministic branch reds of 2026-11-06..13 were such tests: a guard over src/, an
# "exposes one write", a "no pipe op writes", a contract text, a schema rung. They fail the same way on every platform and need no runner.
#
#   tools/precheck.sh [<worktree>]     builds <worktree> (default: the checkout this script is in) once, Release, and runs exactly the
#                                      classes tools/structural-tests.txt lists
#
# Exit 0: every listed class ran at least one test here and none failed. Exit 1: a red, each failing test named, or a listed class that
# ran no test (a filter that matches nothing exits 0 in dotnet test, so the trx is read, not the exit code alone). Exit 2: the worktree,
# the list or the build is wrong. It replaces nothing: CI still runs everything, on all three platforms.
#
# Portable on purpose: bash 3.2 (macOS's /bin/bash), Linux bash and Git Bash on Windows, so no mapfile, no associative arrays, and paths
# handed to dotnet through cygpath where Git Bash has it.
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
wt="${1:-$(dirname "$here")}"
if [ ! -f "$wt/TradeAgent.sln" ]; then echo "precheck: FAIL $wt holds no TradeAgent.sln" >&2; exit 2; fi
wt=$(cd "$wt" && pwd)
list="$wt/tools/structural-tests.txt"
if [ ! -f "$list" ]; then echo "precheck: FAIL $list is missing" >&2; exit 2; fi
if [ -d "$HOME/.dotnet" ]; then PATH="$HOME/.dotnet:$PATH"; export PATH; fi

# A path as dotnet reads it: native on macOS and Linux, mixed (C:/...) under Git Bash.
native() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }

# THE LIST: one class per line, "<full class name> <why it is structural>"; '#' starts a comment line. CRLF tolerated.
bad=$(tr -d '\r' < "$list" | awk '
  /^[[:space:]]*(#|$)/ { next }
  $1 !~ /^TradeAgent\.Tests\.(Unit|Integration|Fault)\.[A-Za-z_][A-Za-z0-9_]*$/ { print "  line " NR ": not a test class name: " $1; next }
  NF < 2 { print "  line " NR ": " $1 " says no why" }')
if [ -n "$bad" ]; then echo "precheck: FAIL $list:"; printf '%s\n' "$bad"; exit 2; fi
classes=$(tr -d '\r' < "$list" | awk '/^[[:space:]]*(#|$)/ { next } { print $1 }')
if [ -z "$classes" ]; then echo "precheck: FAIL $list lists no class"; exit 2; fi
count=$(printf '%s\n' "$classes" | grep -c .)
filter=$(printf '%s\n' "$classes" | awk '{ printf "%sFullyQualifiedName~%s.", (NR > 1 ? "|" : ""), $1 }')

start=$SECONDS
echo "precheck: building $wt (Release)"
build_log=$(mktemp)
if ! dotnet build "$(native "$wt/TradeAgent.sln")" -c Release -nologo > "$build_log" 2>&1; then
  grep -E ' error |error [A-Z]+[0-9]+' "$build_log" | sort -u | head -n 20
  tail -n 3 "$build_log"; rm -f "$build_log"
  echo "precheck: FAIL the build — nothing was tested"; exit 2
fi
rm -f "$build_log"

results=$(mktemp -d)
echo "precheck: running $count structural classes"
dotnet test "$(native "$wt/TradeAgent.sln")" -c Release --no-build --nologo --filter "$filter" \
  --logger "trx;LogFilePrefix=precheck" --results-directory "$(native "$results")" > "$results/test.log" 2>&1
code=$?

# EVERY RESULT, from the trx files: "<outcome> <test name>", one per line (one trx per test assembly).
outcomes=$(cat "$results"/*.trx 2> /dev/null | tr '\n' ' ' | grep -o '<UnitTestResult [^>]*>' |
  sed -n 's/.*testName="\([^"]*\)".*outcome="\([A-Za-z]*\)".*/\2 \1/p')

red=0
for c in $classes; do
  ran=$(printf '%s\n' "$outcomes" | awk -v p="$c." 'index($2, p) == 1' )
  passed=$(printf '%s\n' "$ran" | grep -c '^Passed ')
  failed=$(printf '%s\n' "$ran" | grep -c '^Failed ')
  if [ "$failed" -gt 0 ]; then
    red=1; echo "  FAIL $c: $failed failed"
    printf '%s\n' "$ran" | awk '$1 == "Failed" { print "       " $2 }'
  elif [ "$passed" -eq 0 ]; then
    red=1; echo "  FAIL $c: ran no test here (a misspelt or moved class, or one this platform skips whole)"
  else
    echo "  ok   $c: $passed passed"
  fi
done
if [ "$code" -ne 0 ] && [ "$red" -eq 0 ]; then
  red=1; echo "  FAIL dotnet test exited $code with no failed result in the trx:"; tail -n 15 "$results/test.log"
fi
if [ "$red" -ne 0 ]; then
  grep -E '^[[:space:]]+(Error Message|Assert|Expected|Actual)' "$results/test.log" | head -n 40
  echo "precheck: RED after $((SECONDS - start)) s — fix it before a CI run (the log: $results/test.log)"; exit 1
fi
rm -rf "$results"
echo "precheck: GREEN, $count classes in $((SECONDS - start)) s"
