# U-puppet-isolation — a puppet life leaves nothing process-wide behind: AiAttemptLedgerTests' restart-and-midnight test passes beside PuppetLivesTests
**Arrow closed:** "never a flake" (docs/FLEET.md § Speed mode): a red the diff cannot reach is a defect once it repeats. Found by U-thinking-seats-a's adoption pass
(2026-10-17): `AiAttemptLedgerTests.A_turn_killed_before_its_usage_stays_charged_across_a_restart_and_midnight` (tests/TradeAgent.UnitTests/AiAttemptLedgerTests.cs:60)
fails at :84 (`session.Busy`) when run in one `dotnet test` with `PuppetLivesTests` (tests/TradeAgent.UnitTests/PuppetLivesTests.cs, `[Collection(VendorOverrideFiles.Name)]`);
reproduced twice on a clean main worktree; green alone. Seat A (U-puppet-runtime is its unit); **sonnet/high** (tests/rig; opus/high instead if the cause is product
code — then it is a protection unit: red-first + one watched mutant). **No rung.**
**Observable result:** both classes green in one run, in either order, 3× — and the shared state named: what a puppet life leaves behind (a static register, a
process-wide flag such as `PositionActivation.TestHost` or a gate register, a pipe or meter singleton, an environment variable, a file in a shared folder, a
background task outliving its host's dispose) and why `session.Busy` reads true in the other class.
Items: 1. **Reproduce and name the cause** (a filter running both classes; quote the failure and the state that crosses). 2. **Fix at the cause:** the puppet fixture
(or the product singleton it touches) restores or scopes that state per host — never a sleep, retry, order attribute or a collection that merely serialises them;
if the cause is PRODUCT state that two hosts in one process share, fix the product and say so (two app hosts in one process is also what the proving ground needs).
3. **A guard:** a test that runs a puppet life, disposes it, then asserts the shared state is back to its default (red before the fix, quoted).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; both classes + the guard 3× in one filter; branch CI on three platforms; the
Codex review with the CI wait; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.
