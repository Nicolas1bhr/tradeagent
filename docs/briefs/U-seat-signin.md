# U-seat-signin — the owner signs the app's seat home in from the dashboard, any time, with no terminal: browser sign-in, device code or a pasted key
**Arrow closed:** part 2 of `U-seat-home` (`docs/ORCHESTRATION.md` § 2.7: one app-owned seat home and ONE sign-in, "in the app; on Linux the device code
shown in the app's own window, with expiry and retry"); ORGANISATION § 15 row `U-seat-signin`. **A protection — CRED-001 and the no-terminal rule:
red-first and ONE watched mutant.** Seat A; opus/high (credentials). **No rung.** Dispatched on a `main` that carries `U-seat-home`.
**Today (main `b211fbb3`, re-checked by seat A after U-seat-home landed — `053b9ec2`, record `133759b5`):** the app signs in headless already — `AuthArgs`
(`RuntimeManifest.cs:325`), `AuthStateArgs` (:326), `StdinArgs` (:174), the browser URL by `AuthUrlPattern` (:407), the device-code shape (RUN 0.160.1: a link and a
one-time code "expires in 15 minutes"; Linux signs in with `login --device-auth`); `CliAgentRuntime.BeginAuthenticationAsync` (:491), `SignInWithApiKeyAsync`
(:634-643), `GetAuthenticationStateAsync` (state at :750) — all now under the seat home (U-seat-home's `Apply`). But the only caller is onboarding
(`OnboardingView.cs`, the only `src/TradeAgent.App` file naming `BeginAuthenticationAsync`: the step's state check `:417-418`, the sign-in UI `:780-850` — the pasted key `:791`, `BeginAuthenticationAsync` `:815`, the device code `:817-850`): an owner who already onboarded meets U-seat-home's refusal
`SignedOut` ("TradeAgent now keeps its own AI sign-in, separate from your own Codex. Sign in again." — `RuntimeManifest.cs:679`) with NO button behind it.
The fleet's observed-run audit reads `~/.codex/sessions` (`fleet/charters/M-milestones.md:36`), which no seat writes any more.
**Observable result:** whenever the seat home holds no credential (`login status` under it exits ≠ 0), the dashboard shows ONE "AI sign-in" card —
not only onboarding — offering the browser sign-in, the device code (link + code + its expiry, a Retry when it lapses) and a pasted API key, each run
under the seat home with `CreateNoWindow`, never a console; the card disappears when status turns 0. The Doctor page has a row for the seat home
(present, signed in or not, profile hash, pinned version). Nothing reads or copies the owner's `~/.codex`.
Read first: `CLAUDE.md` (no terminal; two presses for what removes permission; the dashboard tree is built once and updated in place; Theme.cs only);
U-seat-home's record in `BUILD-STATUS.md` (grep -n 'U-seat-home —'); the survey §§ 3-4; `OnboardingView.cs:410-420, 770-860`; the dashboard's card pattern
(`grep -rn 'class .*Card' src/TradeAgent.App`); `CliAgentRuntime.cs:491-605` (BeginAuthenticationAsync), `:624-745` (SignInWithApiKeyAsync), `:746-766` (GetAuthenticationStateAsync); `src/TradeAgent.Diagnostics/Doctor.cs` (its row pattern).
Items, one commit each, one-sentence messages:
1. **A seat-home sign-in state the app polls cheaply:** `SeatSignIn.State()` = `login status` under the seat home (the same `Apply` env as every
   codex process), cached with the existing refresh cadence; never on the UI thread; a failure to run status reads as "unknown", shown as such.
2. **The dashboard card** (built once, updated in place — never rebuilt on refresh): visible iff the state is "signed out" or "unknown"; three
   actions reusing `BeginAuthenticationAsync` / the device-code path / `SignInWithApiKeyAsync`; the browser URL opened by the app; the device code
   shown with its expiry and a Retry; a pasted key goes to stdin, is never logged, never shown back, and the field is cleared after use.
3. **Device code as data:** the device-code arguments and its URL/code/expiry patterns live in `runtimes.json`'s codex entry (vendor commands are
   data); a pattern that does not match the output refuses in words naming the runtime version, never guesses.
4. **Doctor row "AI seat home":** path, signed in or not, the pinned version and the runtime-profile hash `U-seat-home` records — no credential
   content, no account id.
5. **Sign out:** a "Sign out of TradeAgent's AI" action on the card's signed-in state — it removes permission, so TWO presses (CLAUDE.md) — running
   `logout` under the seat home only.
6. **The observed-run audit path** moves to `<seat home>/codex/sessions`: update `fleet`-facing docs the repo owns (grep the repo for `.codex/sessions`);
   the fleet charter line is seat A's to fix at landing (outside the repo).
Red-first tests (fake CLI + fake status, the suite's pattern; quote each red at base):
(a) `An_onboarded_owner_with_a_signed_out_seat_home_sees_the_sign_in_card` (red: only onboarding checks today);
(b) `Every_sign_in_path_runs_under_the_seat_home_never_the_owners_home`; (c) `A_pasted_key_reaches_stdin_and_is_never_logged_or_kept`;
(d) `The_device_code_shows_its_expiry_and_retry_and_an_unmatched_output_refuses_naming_the_version`;
(e) `Sign_out_needs_two_presses`; (f) `The_card_is_updated_in_place_not_rebuilt_on_refresh`.
**The watched mutant:** let the sign-in path fall back to the inherited environment (skip the seat-home `Apply`) ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** `tools/mac-run.sh` + `tools/mac-shot.sh` on a dev home whose seat home is empty: a screenshot path of the card, and of it gone
after a FAKE api-key sign-in into a throwaway seat home (a fake key, never a real one; no model call). The tests box runs the suite if `ready`.
Out of scope: the headless Linux host's presentation of the device code beyond the app's own window (`U-linux-host`); OpenCode's home; any seat class.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.
**Seat A at dispatch (main `c8004ffc`, pointers re-checked — OnboardingView's and CliAgentRuntime's ranges corrected):** Codex commands for evidence only with a throwaway `CODEX_HOME`+`HOME` and the mock provider (`fleet/tmp/A-org/seat-home/mock/`); a FAKE key only; the local Colima VM is OFF-LIMITS.
