#!/usr/bin/env bash
# THE TIMING CATEGORY'S ONE RETRY, AND WHAT IT RE-RUNS (U-red-says-why): `build.yml`'s "Test the timing category".
#
#   timing-retry.sh trx <name>                  the trx path attempt 1 must have written for EVERY test project that
#                                               runs Timing tests, one per line, derived from the repo (see EVERY PROJECT)
#   timing-retry.sh filter <trx>...             the VSTest filter for the second attempt, on stdout: the Timing tests
#                                               the first attempt's trx files list as FAILED, and nothing else. Exit 1,
#                                               nothing on stdout and an ::error on stderr, when they name no failed
#                                               test at all — or when ANY trx cannot be trusted (see FAIL-CLOSED)
#   timing-retry.sh verify <trx>... -- <trx>... after the second attempt: its trx files (after the `--`) must list
#                                               EXACTLY the tests attempt 1's trx files (before it) listed as failed,
#                                               each one Passed. Exit 1, and an ::error, otherwise
#
# PER TEST, NOT PER CATEGORY. The retry used to re-run the whole category, so one red re-ran every timing test and
# a test that was GREEN on the first attempt could go red on the second and fail a run it had passed — Windows run
# 37524459410's button row did exactly that, behind a FaultTests press test's red. "Red twice is red" is a fact
# about one test; this makes the second attempt ask it of the tests that were red once, and only of them.
#
# A THEORY'S ROWS: THE FILTER IS THE METHOD, THE VERDICT IS THE ROW. A failed result is joined, by its testId, to its
# definition's TestMethod element, whose className and name are the class and the METHOD — so a failed row re-runs every
# row of that method, matched as FullyQualifiedName=<class>.<method> (xUnit gives every row the method's name). Exact,
# never ~: a substring would re-run a method whose name merely contains another's. But a method is not a row: `verify`
# compares the full testName of the result (data row included, `Ns.C.M(x: 1)`), so a failed row counts as re-run only
# when that very row is in the retry's trx, Passed; another row of the same method passing does not stand in for it
# (the second review of this unit, finding 2).
#
# EVERY PROJECT (the second review of this unit, finding 1). The first attempt runs the whole solution, so a crashed or
# aborted test project writes no trx at all, and a glob over the trx files that exist cannot see it: another project's
# failed test is then retried, passes, and the step goes green over a project nobody judged. So the list of trx files
# is NEVER the files present: `trx <name>` derives it from the repo — every test project TradeAgent.sln lists under
# tests\ whose sources declare a Timing trait (a line that begins with `[` and names "Timing") — and the step passes
# exactly that list to `filter` and `verify`, where a missing, empty or unreadable one fails the step (FAIL-CLOSED below).
# A project whose trait this does not recognise is a project the step does not require a trx from: declare the trait
# as an attribute line, as every Timing test does today.
#
# NOTHING NAMED, NOTHING RE-RUN. A first attempt that failed without a failed result in its trx — a crash, an abort,
# a host that died, no trx at all — is not a timing red that a second attempt can judge, and re-running the whole
# category for it is the scope this replaced. It exits 1 and says so; the step fails on it.
#
# FAIL-CLOSED (the review of this unit, fixes 1-3): THE FILTER IS BUILT ONLY FROM EVIDENCE IT HAS READ IN FULL, and a
# green second attempt means something only if it re-ran every test the first one failed. So the script refuses —
# exit 1, nothing selected — when any trx it is given is missing, empty, unreadable or cut short (no closing
# </TestRun>, no ResultSummary); when a non-passing result's testId has no definition, so it could not be named;
# and when a name carries anything the filter cannot say exactly (an XML character reference other than the five
# named entities, a control character, a name that is empty or begins or ends in a space). A failed test it cannot
# name is a failed test the retry would not re-run, and a step that goes green past it hides a red. THE GUARD THAT
# CLOSES THE CLASS IS `verify`: whatever the filter selected, the retry's own trx must list exactly attempt 1's
# failed tests, each Passed — a filter that selected fewer, more or other tests than were red fails the step.
#
# PARSING. Newlines are flattened before splitting on '<', so every line starts at a tag and a test's captured
# output, which the trx writer escapes, can never start one: printed text that looks like an attribute is not read.
# Attribute values are XML-decoded (&lt; &gt; &amp; &quot; &apos;) in one left-to-right pass before they are used.
set -euo pipefail

usage() { echo "usage: timing-retry.sh trx <name> | timing-retry.sh filter <trx>... | timing-retry.sh verify <attempt-1 trx>... -- <retry trx>..." >&2; exit 2; }
err() { echo "::error title=The timing retry cannot be trusted::$*" >&2; }

# parse <strict 0|1> <trx>: one "<outcome><TAB><class>.<method><TAB><testName>" line per result, on stdout; nonzero (and an ::error on
# stderr) when the file cannot be trusted. strict=1 (the retry) also requires a definition for a PASSED result, since
# the retry's set is compared as a whole; strict=0 (attempt 1) needs one only for a result that did not pass.
parse() {
  local strict=$1 trx=$2 out
  if [ ! -f "$trx" ] || [ ! -r "$trx" ] || [ ! -s "$trx" ]; then
    err "the trx '$trx' is missing, empty or unreadable, so what ran there is not known. Nothing was selected; read the step log above."
    return 1
  fi
  out=$(tr '\r\n' '  ' < "$trx" | tr '<' '\n' | awk -v strict="$strict" -v file="$trx" '
    function bad(msg) { printf "::error title=The timing retry cannot be trusted::%s in %s. Nothing was selected; read the step log above.\n", msg, file > "/dev/stderr"; failed_parse = 1 }
    function attr(line, key,   m) {
      if (match(line, " " key "=\"[^\"]*\"")) { m = substr(line, RSTART + length(key) + 3, RLENGTH - length(key) - 4); return m }
      return ""
    }
    # One left-to-right pass: the five named entities and nothing else. A character reference or an unknown entity
    # leaves the name unsaid exactly, so it is refused rather than guessed (returns "\001" for "refused").
    function dec(s,   out, i) {
      out = ""
      while ((i = index(s, "&")) > 0) {
        out = out substr(s, 1, i - 1); s = substr(s, i)
        if      (substr(s, 1, 4) == "&lt;")   { out = out "<";  s = substr(s, 5) }
        else if (substr(s, 1, 4) == "&gt;")   { out = out ">";  s = substr(s, 5) }
        else if (substr(s, 1, 5) == "&amp;")  { out = out "&";  s = substr(s, 6) }
        else if (substr(s, 1, 6) == "&quot;") { out = out "\""; s = substr(s, 7) }
        else if (substr(s, 1, 6) == "&apos;") { out = out sprintf("%c", 39); s = substr(s, 7) }
        else return "\001"
      }
      return out s
    }
    function sayable(s) { return s != "" && s != "\001" && s !~ /[[:cntrl:]]/ && s !~ /^[[:space:]]/ && s !~ /[[:space:]]$/ }
    /^TestRun[[:space:]]/ { started = 1 }
    /^\/TestRun>/ { ended = 1 }
    /^ResultSummary[[:space:]]/ { summary = 1 }
    /^UnitTest[[:space:]]/ { id = attr($0, "id"); next }
    /^TestMethod[[:space:]]/ && id != "" {
      c = dec(attr($0, "className")); n = dec(attr($0, "name"))
      cls[id] = c; mth[id] = n; id = ""; next
    }
    /^UnitTestResult[[:space:]]/ { nres++; rid[nres] = attr($0, "testId"); rout[nres] = attr($0, "outcome"); rname[nres] = attr($0, "testName") }
    END {
      if (!started || !ended || !summary) bad("the trx is cut short (no TestRun start, closing </TestRun> or ResultSummary)")
      for (i = 1; i <= nres; i++) {
        pass = (rout[i] == "Passed"); skip = (rout[i] == "NotExecuted")
        if (rout[i] == "") { bad("a result with no outcome (testId " rid[i] ")"); continue }
        if (rid[i] == "" || !(rid[i] in mth)) {
          if (!pass && !skip) bad("a " rout[i] " result (testId " rid[i] ") has no test definition, so it cannot be named")
          else if (strict) bad("a " rout[i] " result (testId " rid[i] ") has no test definition")
          continue
        }
        if (!sayable(cls[rid[i]]) || !sayable(mth[rid[i]])) {
          if (!pass && !skip) bad("the test " rid[i] " has a class or method name the filter cannot say exactly")
          else if (strict) bad("the test " rid[i] " has a class or method name the filter cannot say exactly")
          continue
        }
        # The row identity: the full testName of the result, as the writer escaped it (both attempts come from the one writer,
        # so the raw text compares). A failed result, or any result of the retry, without a usable one is refused.
        if (rname[i] == "" || rname[i] ~ /[[:cntrl:]]/) {
          if (!pass && !skip) bad("the failed test " rid[i] " has a testName the retry cannot be checked against")
          else if (strict) bad("the result " rid[i] " has a testName the retry cannot be checked against")
          continue
        }
        printf "%s\t%s.%s\t%s\n", rout[i], cls[rid[i]], mth[rid[i]], rname[i]
      }
      exit failed_parse ? 1 : 0
    }
  ') || return 1
  [ -z "$out" ] || printf '%s\n' "$out"
}

# all_of <strict> <trx>...: the parse of every file, concatenated; the first untrustworthy file fails the lot.
all_of() {
  local strict=$1 trx out
  shift
  for trx in "$@"; do
    out=$(parse "$strict" "$trx") || return 1
    [ -z "$out" ] || printf '%s\n' "$out"
  done
}

# failed_methods <trx>...: the sorted, distinct "<class>.<method>" of every result that did not pass (not NotExecuted).
failed_methods() {
  local all
  all=$(all_of 0 "$@") || return 1
  printf '%s\n' "$all" | awk -F'\t' 'NF == 3 && $1 != "Passed" && $1 != "NotExecuted" { print $2 }' | sort -u
}

# failed_rows <trx>...: the sorted, distinct full testName (data row included) of every result that did not pass.
failed_rows() {
  local all
  all=$(all_of 0 "$@") || return 1
  printf '%s\n' "$all" | awk -F'\t' 'NF == 3 && $1 != "Passed" && $1 != "NotExecuted" { print $3 }' | sort -u
}

# timing_trx <name>: tests/<project>/TestResults/<name> for every test project the solution lists that declares a Timing
# test. Nothing found is an error: a repo that names no Timing project has nothing for a retry to be judged against.
timing_trx() {
  local name=$1 csproj dir found=0
  [ -f TradeAgent.sln ] || { err "TradeAgent.sln is not here, so the test projects the Timing step runs cannot be listed."; return 1; }
  while IFS= read -r csproj; do
    dir=$(dirname "$csproj")
    [ -d "$dir" ] || { err "the solution lists '$csproj', which does not exist, so the test projects the Timing step runs cannot be listed."; return 1; }
    if grep -rqE --include='*.cs' --exclude-dir=bin --exclude-dir=obj '^[[:space:]]*\[.*"Timing"' "$dir"; then
      printf '%s/TestResults/%s\n' "$dir" "$name"; found=1
    fi
  done < <(tr -s '\\/' '//' < TradeAgent.sln | sed -n 's|^Project(.*= "[^"]*", "\(tests/[^"]*\.csproj\)".*|\1|p' | sort -u)
  [ $found -eq 1 ] || { err "no test project in TradeAgent.sln declares a Timing test, so there is no trx to require."; return 1; }
}

[ $# -ge 1 ] || usage
mode=$1
shift
case "$mode" in
  trx)
    [ $# -eq 1 ] || usage
    timing_trx "$1" || exit 1
    ;;
  filter)
    [ $# -ge 1 ] || usage
    names=$(failed_methods "$@") || exit 1
    if [ -z "$names" ]; then
      echo "::error title=The timing category failed and named no failed test::its first attempt failed, but no trx it wrote lists a failed test (a crash, an abort, or no trx at all), so there is nothing for a second attempt to judge. Nothing was re-run; read the step log above." >&2
      exit 1
    fi
    # The filter syntax's own operators, escaped, so a name is always read as a name. (XML is already decoded.)
    filter=$(printf '%s\n' "$names" | sed -e 's/[\\()&|=!~]/\\&/g' -e 's/^/FullyQualifiedName=/' | paste -sd '|' -)
    echo "Category=Timing&($filter)"
    ;;
  verify)
    first=(); second=(); seen=0
    for a in "$@"; do
      if [ "$a" = -- ] && [ $seen -eq 0 ]; then seen=1; elif [ $seen -eq 0 ]; then first+=("$a"); else second+=("$a"); fi
    done
    [ ${#first[@]} -ge 1 ] && [ ${#second[@]} -ge 1 ] || usage
    expected=$(failed_methods "${first[@]}") || exit 1
    expected_rows=$(failed_rows "${first[@]}") || exit 1
    if [ -z "$expected" ]; then
      echo "::error title=The timing retry has nothing to verify::attempt 1's trx files name no failed test, so no retry can have been the right one." >&2
      exit 1
    fi
    ran=$(all_of 1 "${second[@]}") || exit 1
    # Every retried result Passed (a Theory's rows each), and the set of methods re-run equals the set that failed.
    notpassed=$(printf '%s\n' "$ran" | awk -F'\t' 'NF == 3 && $1 != "Passed" { print $1 ": " $3 }' | sort -u)
    reran=$(printf '%s\n' "$ran" | awk -F'\t' 'NF == 3 { print $2 }' | sort -u)
    passed_rows=$(printf '%s\n' "$ran" | awk -F'\t' 'NF == 3 && $1 == "Passed" { print $3 }' | sort -u)
    if [ -n "$notpassed" ]; then
      echo "::error title=A retried timing test did not pass::${notpassed//$'\n'/; }" >&2
      exit 1
    fi
    if [ "$reran" != "$expected" ]; then
      missing=$(comm -23 <(printf '%s\n' "$expected") <(printf '%s\n' "$reran") | paste -sd ',' -)
      extra=$(comm -13 <(printf '%s\n' "$expected") <(printf '%s\n' "$reran") | paste -sd ',' -)
      echo "::error title=The timing retry did not re-run exactly the tests that failed::failed on attempt 1 and not re-run: [${missing}]; re-run though attempt 1 did not fail them: [${extra}]. The step is red." >&2
      exit 1
    fi
    # Every row that failed on attempt 1 is itself in the retry, Passed: the other rows of a method cannot stand in for it.
    unrun=$(comm -23 <(printf '%s\n' "$expected_rows") <(printf '%s\n' "$passed_rows"))
    if [ -n "$unrun" ]; then
      echo "::error title=A test row that failed on attempt 1 is not Passed in the retry::${unrun//$'\n'/; }. The step is red." >&2
      exit 1
    fi
    echo "The retry re-ran exactly the $(printf '%s\n' "$expected" | wc -l | tr -d ' ') test method(s) that failed on attempt 1, and each passed."
    ;;
  *) usage ;;
esac
