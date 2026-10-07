# U-vendor-limit-quote — the app holds at the owner's plan limit again: codex now writes its limit sentence with a typographic apostrophe
**Protects:** the AI's daily spend cap and the paid-turn trigger (`U-vendor-limit`, merge `c56540e`): a turn the vendor refused before any work is charged nothing, and no
turn launches on that runtime before the minute the vendor named. A PRODUCT defect on `main` today (the orchestrator's ruling, 2026-10-07): at the owner's plan limit the
app charges each refused turn its whole reservation and launches again. Light; one fresh builder, seat M; no rung; no money-path file. M0-scoped and urgent: it must be
LANDED by 15:45 CEST on 2026-10-07 (seat M cherry-picks its code commit onto M0's build), so report the moment branch CI is green on all three platforms.
**Facts (SOURCE at `ddf4b8b3`; these files are unchanged since `1828a188`).**
- `src/TradeAgent.AgentRuntime/RuntimeManifest.cs:596-605`: codex's `UsageLimit` — `Pattern = "You've hit your usage limit"` (ASCII U+0027), `RetryAtPattern`,
  `RetryAtFormats` ("h:mm tt" and four dated "MMM d'th', yyyy h:mm tt" forms). The capture comment `:577-595` records codex-cli 0.153.4 on 2026-10-01 and says the
  dated form "has NOT been seen in a stream".
- `:82-158` `UsageLimitPlan.Read`: `Regex.IsMatch(message, Pattern, RegexOptions.CultureInvariant, …)` — the pattern is a regex; no apostrophe is normalised.
- `src/TradeAgent.AgentRuntime/AgentSession.cs:699-715`: an `error` event's message goes to `UsageLimit.Read`; recognised → `state.Limit` (the turn ends on the vendor's
  words, held, charged nothing when before any work); not recognised → a System line and an ordinary failed turn (its reservation stands as its cost).
- `tests/TradeAgent.UnitTests/VendorLimitTests.cs:33-50`: the 2026-10-01 recording (`Stdout`, `VendorSaid`, ASCII); tests at `:133`, `:196`, `:254`, `:305`, `:332`,
  `:354`; `VendorLimitReadTests.cs` presses the typed reason.
- OBSERVED (seat M, 2026-10-07; `~/.codex/sessions` read for `usage_limit_exceeded` only, and the sentences below are the only text taken): the 3 events of 2026-10-01
  carry U+0027; the 8 events of 2026-10-05/06 (Codex Desktop sessions, codex-cli 0.159.2 and 0.160.1) carry U+2019. This Mac's CLI is now 0.160.1. Verbatim, C# escapes:
  "You’ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 8:49 PM."
  and the same sentence ending "try again at Oct 7th, 2026 1:10 AM." (the dated form, seen in a vendor sentence for the first time). NOT VERIFIED: that `codex exec --json`
  stdout's `error` event carries this text under 0.160.1 (the session log's `payload.error.message` does; on 2026-10-01 both carried U+0027).
Read first: `CLAUDE.md` ("Vendor commands are data, not code"; the honesty rule); `docs/HOW-WE-BUILD.md`; `docs/FLEET.md` "The builder pass"; the files above.
Must NOT: read `~/.codex` or run `codex` (the sentences above are the only vendor text this unit uses); widen the pattern beyond the apostrophe (no IgnoreCase, no bare
"usage limit": another error must never read as the limit); change the hold, the charge rule, the retry parse or the 2026-10-01 recording; add a rung; touch the app's UI.
Items, one commit each, one-sentence messages:
1. **The vendor's sentence in both spellings:** `Pattern` accepts U+2019 and U+0027 at that one place (e.g. `You['’]ve hit your usage limit`), as data in the
   manifest; the capture comment gains the dated reading above (what changed, when, where it was read — the sentence only). RED first (item 2) on the base, quoted.
2. **Tests on the observed sentences** (beside `VendorLimitTests`, its own class if cleaner): (i) the codex manifest's `UsageLimit.Read` recognises the U+2019 sentence,
   its retry the end of the minute 20:49 local on the event's own date, and the dated one the end of the minute 2026-10-07 01:10 local; (ii) the 2026-10-01 stream with
   only the apostrophe changed (DERIVED, labelled so in its doc comment) replayed through a real session as `Refusing` does: the last line is the U+2019 sentence, the
   turn is charged 0 with `context.refused`, and no launch comes before the named minute; (iii) GUARDS, green before and after: the ASCII recording is still recognised;
   an `error` whose message is not the limit sentence (e.g. "You’ve hit your rate limit.") is not a limit.
3. **One watched mutant:** the old ASCII-only pattern restored → item 2 (i) and (ii) red, quoted; restored byte-identical.
Proof: item 2's RED on the base and the mutant, quoted; the `VendorLimit*` classes 3× through `suite.sh`.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed;
touched classes 3×; `fleet/bin/ci-dispatch.sh <WT>` then `ci-wait.sh --run <id> 9` in foreground slices — every job green on all three platforms, quoted; tests box
(`TA_WIN_BOX=tests tools/win-test.sh ready`) or its NOT RUN line; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
**Code tip `b5d85ea4`** on base `30dd8b81` (main is now `18a7ab10`, touching neither file; `git merge-tree` clean). CI run 37604406951 at `b5d85ea4`: ubuntu-latest success
13 min, macos-latest success 16 min, **windows-latest failure 62 min** on ONE test this diff cannot reach (below); package skipped.
- **Item 1 `4761a61f`**: `RuntimeManifest.cs:613` `Pattern = "You['\u2019]ve hit your usage limit"` (a C# escape: the data is `You['’]ve…`; no IgnoreCase, nothing else
  widened); the capture comment `:597-611` gains the 2026-10-05/06 U+2019 reading, both observed sentences and the NOT VERIFIED exec-stdout line.
- **Item 2 `b5d85ea4`**: `VendorLimitQuoteTests.cs`, 7 tests: (i) both observed sentences via the shipped manifest (end of 20:49 CEST on the read's date; end of 2026-10-07 01:10
  CEST); (ii) the 2026-10-01 stream with only its apostrophe changed (DERIVED; checked: exactly 2 chars, ' to ’) through `VendorLimitTests.Refusing`: the U+2019 sentence last,
  charged 0 with `context.refused`, no launch before the named minute; (iii) two guards.
- **RED on the base** (`30dd8b81` + item 2): `Failed: 5, Passed: 2, Total: 7` — `:94`, `:108` "Assert.NotNull() Failure: Value is null"; `:157` Expected "You’ve hit your
  usage limit. Upgrade to P"··· Actual "OpenAI Codex CLI did not finish: Reading "···; `:179` "Expected: 0 Actual: 1.28"; `:240` "The collection contained 2 items". Guards green.
- **Item 3, mutant (no commit)**: `:613` back to `"You've hit your usage limit"` → `VendorLimit*` `Failed: 5, Passed: 16, Total: 21`, the same five reds; restored, `git diff` empty.
- **Gate** (via `suite.sh`): `--no-incremental` Release `0 Warning(s)` `0 Error(s)`, 19 projects; Unit `Failed: 0, Passed: 1433`; Fault `Failed: 0, Passed: 463`; `VendorLimit*`
  3× `Failed: 0, Passed: 21` each. **Names**: base 2255, tip 2262, 0 removed. **tests box: NOT RUN** — `ready` exit 1 (11:57, 12:00, 12:28, 13:13): "NO - the machine does not answer".
- **The Windows red**: `QuoteClockTests.A_bar_fed_feed_is_not_degraded_one_bar_after_its_close`, `QuoteClockTests.cs:309`, its simulator arm "simulator at 40 s : READY —"
  (Expected DEGRADED); the same job passed Unit 1432/1432 and Integration 641 (1 skipped). Not reachable from this diff (codex's pattern, a new UnitTests class). THIRD sighting:
  the same line failed on `main` in runs 37521857152 (`192a60a`) and 37600211089 (`1a987e1`); the ledger's reading (seat P): a test-rig clock split, folded into `U-test-hygiene-2`.
- **Deviations**: (ii) is three tests so each red shows alone; (iii) also refuses a non-sentence carrying "usage limit" (written for the guard, labelled so), so a bare "usage
  limit" widening goes red; the read instants in (i), 20:00 and 23:00 CEST on 2026-10-06, are the tests' choice (the brief carries no event instant).
- **NOT done**: `~/.codex` not read, `codex` not run; `codex exec --json` stdout carrying U+2019 under 0.160.1 stays NOT VERIFIED; hold, charge rule, retry parse and recording
  untouched; no rung; no UI; no rebase onto `18a7ab10`. This commit is docs-only; run 37604406951 covers the code tip.
