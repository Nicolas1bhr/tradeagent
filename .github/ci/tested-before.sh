#!/usr/bin/env bash
# THE DEDUPE LOOKUP (U-ci-dedupe): has a run of this workflow already proven the tree this push is about to build?
#
#   tested-before.sh [<commit>]      <commit> defaults to HEAD; prints, on stdout, exactly one of
#                                       skip=true  tested_by=<run id>  tested_sha=<sha>
#                                       skip=false
#
# skip=true ONLY when a COMPLETED run of .github/workflows/build.yml, started by workflow_dispatch, first attempt, with
# conclusion success and EVERY one of its jobs success, has a head commit whose tree equals <commit>'s outside the
# on.push.paths-ignore lines of <commit>'s own build.yml (the fleet's bookkeeping-paths.txt, which land.sh uses for the
# same equality; land.sh refuses every carry when the two differ, so this reads the mirror and needs no copy of its own).
# EVERYTHING ELSE IS skip=false: a lookup that fails, times out, finds no filter, cannot fetch a candidate or sees a
# job that is not success RUNS the build. It only ever answers "tested", never "probably fine".
# Needs: git, gh (GH_TOKEN, actions: read), jq. Bounded: the TB_RUNS newest successful dispatch runs (default 50).
# Locally: run it in a clone with gh authenticated, give it the main commit to ask about.
set -u -o pipefail
TARGET=${1:-HEAD}
WF=.github/workflows/build.yml
N=${TB_RUNS:-50}
REPO=${GITHUB_REPOSITORY:-}; [ -n "$REPO" ] || REPO='{owner}/{repo}'
note() { if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then printf '%s\n' "$*" >> "$GITHUB_STEP_SUMMARY"; else printf '%s\n' "$*" >&2; fi; }

# on.push.paths-ignore of the target's build.yml, one pattern per line (the reader land.sh uses for its drift guard).
filter() { git show "$TARGET:$WF" 2>/dev/null | awk '
  function ind(s) { match(s, /^[ \t]*/); return RLENGTH }
  function clean(x) { gsub(/\r/, "", x); sub(/[ \t]+#.*$/, "", x); gsub(/^[ \t]+|[ \t]+$/, "", x); gsub(/^["\047]|["\047]$/, "", x); return x }
  { line = $0; sub(/\r$/, "", line) }
  line ~ /^[ \t]*(#|$)/ { next }
  { n = ind(line) }
  n == 0 { inon = (line ~ /^["\047]?on["\047]?:/); inpush = 0; inpi = 0; next }
  !inon { next }
  inpi && line ~ /^[ \t]*-/ { x = line; sub(/^[ \t]*-[ \t]*/, "", x); print clean(x); next }
  { inpi = 0 }
  inpush && n <= pushind { inpush = 0 }
  !inpush && line ~ /^[ \t]+push:/ { inpush = 1; pushind = n; next }
  inpush && line ~ /^[ \t]*paths-ignore:/ { inpi = 1; next }
' | grep .; }

# Prints "<run id> <head sha>" of the first run that tested the target's tree; nothing otherwise.
find_run() {
  local target pats p id sha rc
  local -a ex=()
  target=$(git rev-parse --verify -q "$TARGET^{commit}") || { note "tested-before: cannot resolve $TARGET — running"; return 0; }
  pats=$(filter) || pats=""
  [ -n "$pats" ] || { note "tested-before: no on.push.paths-ignore found in $WF — running"; return 0; }
  while IFS= read -r p; do ex+=(":(exclude,glob)$p"); done <<< "$pats"
  local runs
  runs=$(gh api "repos/$REPO/actions/workflows/build.yml/runs?event=workflow_dispatch&status=success&per_page=$N" 2>/dev/null \
    | jq -r '.workflow_runs[] | select(.path == ".github/workflows/build.yml" and .event == "workflow_dispatch" and .status == "completed" and .conclusion == "success" and .run_attempt == 1) | "\(.id) \(.head_sha)"') \
    || { note "tested-before: run lookup failed — running"; return 0; }
  while read -r id sha; do
    [ -n "$id" ] && [ -n "$sha" ] || continue
    git cat-file -e "$sha^{commit}" 2>/dev/null || git fetch -q --no-tags --depth=1 origin "$sha" 2>/dev/null || continue
    git cat-file -e "$sha^{commit}" 2>/dev/null || continue
    git diff --quiet "$sha" "$target" -- . "${ex[@]}" 2>/dev/null; rc=$?
    [ "$rc" -eq 0 ] || continue
    # Equal tree: now every job of that run must be success (a skipped, cancelled or missing job is not).
    gh api "repos/$REPO/actions/runs/$id/jobs?per_page=100" 2>/dev/null \
      | jq -e '(.jobs | length) >= 1 and .total_count == (.jobs | length) and all(.jobs[]; .conclusion == "success")' > /dev/null 2>&1 || continue
    note "tested-before: this push's tree equals the tree of ${sha:0:10}, which workflow_dispatch run $id built with every job success; they differ only in the bookkeeping paths of $WF on.push.paths-ignore:"
    note "$(printf '%s\n' "$pats" | sed 's/^/  /')"
    printf '%s %s\n' "$id" "$sha"; return 0
  done <<< "$runs"
  note "tested-before: no successful workflow_dispatch run (newest $N) built an equal tree — running"
}

hit=$(find_run) || hit=""
if [ -n "$hit" ]; then
  set -- $hit
  echo "skip=true"; echo "tested_by=$1"; echo "tested_sha=$2"
else
  echo "skip=false"
fi
