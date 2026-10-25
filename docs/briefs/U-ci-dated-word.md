# U-ci-dated-word — the linux-host CI step takes every dated word from the tool's own clock (seat A, 2026-10-25; the orchestrator's ruling, BOARD Decisions 00:4x)

**Source:** CI's `linux-host` job fails between 22:00 and 24:00Z (23:00–24:00Z after the DST change). The step's `TZ=Europe/Brussels date -d tomorrow` gives UTC's date on the runner, while `tools/linux-host.sh`'s `word_ok` (Python zoneinfo, the env file's zone) gives Brussels' date. Seen: scorecards-a 40135261354 ("stop ran on tomorrow's word", 22:21:24Z), train B12 40133410607 (exit 2 at 22:01:30Z: the throwaway word refused), repair-position-home 40133431188.
**Model/effort:** sonnet/high (CI/tooling only). No rung. Due before 2026-10-25 23:00Z; rides seat A's next train.

## Protections touched
- The owner's dated word (install and stop refuse without TODAY's word in the host's zone) is UNCHANGED in `tools/linux-host.sh`. Only the CI step's way of producing test words changes. The refusals of an old, yesterday's and tomorrow's word stay asserted.
- No host name; nothing touches the owner's machines (CI runners only).

## Items (one commit each)
1. **One clock.** In `.github/workflows/build.yml`'s linux-host step (~:511–537), every date the step builds (yesterday, today, tomorrow) comes from the tool's own clock: `python3 -I` with zoneinfo in the env file's zone (`TA_LINUX_ZONE`, default Europe/Brussels), the same computation `word_ok` runs. Factor it into one helper in the step, or a `tools/linux-host.sh` subcommand that prints the dated day — pick the smaller change and say why. Each date is computed right before the call that uses it.
2. **Recompute across the sleep.** The throwaway word used by `stop` after `sleep 90` (~:533–537) is recomputed right before `stop`, so a midnight inside the sleep cannot refuse it.
3. **Evidence names the cause.** The step writes `TZ=Europe/Brussels date`, `date -u` and the Python zone date side by side into the evidence folder (`$ev/clocks.txt`) at the start and right before `stop`.
Red-first where it can be shown: a local run of the step's date logic with a faked clock (e.g. `faketime` if present, or by calling the helper with an injected "now") at 22:30Z showing the old pair disagreeing and the new one agreeing; quote it. If no honest local reproduction exists, say so and rely on CI.

## Gate
BUILDER-PROMPT-v2's gate, adapted: no .NET change expected (build still 0 warnings); `bash -n tools/linux-host.sh`; `actionlint` if available; CI via ci-dispatch.sh (all jobs green); names.sh 0 removed. Keep `fleet/bin/bookkeeping-paths.txt` and build.yml's paths-ignore untouched unless you change both together.

## Report
Tip: CODE `59d7b0d6` (CI-tested); report commit follows it, docs only.
- build/.NET: no .NET change, not run locally (CI's build/test jobs green). `bash -n tools/linux-host.sh`: ok. `actionlint build.yml`: only a pre-existing SC2012 info at the step on :228, none in the linux-host step. tools/linux-host.sh UNCHANGED (word_ok as strict as before).
- CI run 40139569440 (59d7b0d6), all 13 jobs green incl. linux-host (34 min), which STARTED 2026-10-24T22:47Z — inside the 22:00-24:00Z bug window, and passed (the old step failed in that window on three earlier runs).
- names.sh main u-ci-dated-word: removed: 0 (added 0; [Fact]/[Theory] base 3159 tip 3159).
- Item 1 done (one commit, items 1-3 touch one block): step helper `dayof N` = `python3 -I` zoneinfo in the env file's TA_LINUX_ZONE (default Europe/Brussels), `date + timedelta(days=N)` on the zone's CALENDAR date, never ±24 h instants; chosen over a linux-host.sh subcommand because it adds no tool surface. Each date is computed right before its call (yesterday, tomorrow, install word).
- Item 2 done: the stop word is `dayof 0` computed after `sleep 90`, right before `stop`.
- Item 3 done: `$ev/clocks.txt` gets `TZ=Europe/Brussels date`, `date -u` and zoneinfo today at start, before stop, and on a refusal-test failure.
- RED-first (injected now; faketime/GNU date absent on this Mac, so the old `date -d` could not be run itself): at 2026-10-25T00:46 Europe/Brussels (the 25-hour DST-end day) the old logic, instant +24 h, gives tomorrow 2026-10-25 (equals today, seat A's second cause); the new calendar-day helper gives yesterday 2026-10-24, tomorrow 2026-10-26. At 2026-10-24 22:30Z the zone's today is 2026-10-25 while UTC's is 10-24 (first cause). The helper run live from the step text agreed with Brussels' date (`$S/helper.sh`).
- Review (Codex gpt-6.1-sol): `fleet/tmp/A-org/ci-dated-word/U-ci-dated-word-review.md` — "No defects found".
- Tests box: NOT RUN — not a .NET change.
- NOT verified: the DST-end instant itself on the real runner (only the injected-now computation); a run just after midnight Brussels.
