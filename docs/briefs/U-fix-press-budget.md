# U-fix-press-budget — on hosted Windows the owner's "Cancel all working orders" press cancelled nothing; find the cause, and if the press's own bookkeeping can spend its two seconds, charge the owner's presses for platform time only
**Protects:** the owner's emergency presses on the money path (gateway): Cancel all working orders and Close all positions must reach the platform when the platform
answers. A press that refuses before the wire because TradeAgent's OWN disk was slow leaves orders working or a book open that the owner asked to remove. That is the
dangerous direction, and it is the class `U-fix-loss-reopen` fixed for the app's own flatten (`BUILD-STATUS.md` 2026-10-04, judgement 1). **A FRESH FIXER, seat P,
FIRST in its queue** (the orchestrator's ruling, 2026-10-06: brief it on the first sighting). No rung.
**Evidence (RUN, `gh run view`), windows-latest only, ubuntu and macos green, on a branch with no src/tests change:** `r-containment` `74025d51`, run 37391256380, job
112036608839: `PressIdShapeTests.The_operator_cancel_all_names_its_legs_without_the_brokers_order_id` (`tests/TradeAgent.FaultTests/PressIdShapeTests.cs:105`) failed
[46 s]: "Expected: 2 / Actual: 0" at `:123`, `Assert.Equal(2, legs.Count)` — two working orders, and the press wrote no cancel leg. The Fault suite took 31 m 54 s
on that runner. First sighting; main's own windows runs on the same src were green (e.g. `0c510689`, run 37388890179).
**Facts to start from (SOURCE at `69589886`; facts, not a diagnosis; `TG` = `src/TradeAgent.Gateway/TradingGateway.cs`):** `OperatorCancelAllAsync` (`TG:7694`)
opens `RiskReducingScope.Begin(Connector.EmergencyBudget)` (`:7709`; the Fake's budget is 2 s, `Connectors.Fake/FakeConnector.cs:54`), then `RequireAccountId`
(`:7713`) and the write-ahead press row (`:7723`), and only then reads the orders (`:7729`). A throw there is caught as "TradeAgent could not read your working orders,
so nothing was cancelled." (`:7740`) — no leg. The Fake refuses any call made after the deadline, "the operation deadline had already passed and nothing was sent"
(`HonourTheOperationDeadline`, `FakeConnector.cs:144-156`); ATAS clips at it (`Connectors.Atas/AtasConnector.cs:1237`, `:1747`). `OperatorCloseAllAsync` (`:7955`)
has the same shape (`:7970`, `:7974`, positions read `:7978`). `RiskReducingScope.BeginExcludingTheStore` (`ConnectorSdk/RiskReducingScope.cs:146`, `Core/Db/StoreTime.cs`)
refunds the app's own store time; its doc (`:130-135`) keeps owner presses on `Begin` on purpose — "two seconds" as a promise about the whole operation.
**The orchestrator's ruling, on the owner's behalf:** if the measurement confirms that the press's own commits spent the budget, that promise is superseded. The owner's
presses are charged for platform time only, because a press that refuses itself on a slow disk fails the emergency it exists for. If the measurement shows
something else, fix that instead and say why.
**Reproduce on CI** (`fleet/bin/ci-dispatch.sh`; read the windows job — green on this Mac proves nothing about Windows). Diagnostic runs may narrow `.github/workflows/build.yml`
on your branch (windows only, the press classes looped) and print each step's remaining budget, branch only, removed before the proving run, each named in the report.
The Windows boxes are NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; the `U-fix-loss-reopen` record (`BUILD-STATUS.md`, 2026-10-04) and its `LossFlattenOwedTests.cs` (the slow-store
fixture, `:151-173`); `PressIdShapeTests.cs`, `EmergencyPressTests.cs` (whole); `RiskReducingScope.cs`; `TG` around the lines above.
Must NOT: lengthen the 2 s budget, or let a press wait past it on a stalled PLATFORM (`EmergencyPressTests` guards that bound and stays green, unchanged); send a leg the
press did not capture; change the presses' two-press confirmation, their refusal while another press is open, or how a leg the budget cannot reach is flagged; add a
retry or a sleep, raise a timeout, skip on Windows, weaken an assert, or move a test into `Timing` without measured numbers argued at the test.
Items, one commit each, one-sentence messages:
1. **Name the cause with evidence from Windows runs:** per step of the press in the failing test, the budget left (after `Begin`, after `RequireAccountId`, after the press
   row, at the read) and where the 46 s went; a CONTROL (the deadline gone at the first platform call) shown to reproduce "Expected: 2 / Actual: 0" exactly.
2. **Fix it where it is wrong.** If confirmed: both owner presses (`:7709`, `:7970`) open `BeginExcludingTheStore`, and its doc, CONTRACTS and the user guide (wherever they
   promise the press's two seconds) say that the budget bounds the platform's time. Name the agent's risk-reducing pipe scope (`GatewayPipeServer.cs:1206`) as covered or left,
   with the reason; the data-loss exit (`:9064`, `:9156`) is `U-flatten-confirm`'s. Otherwise: the real cause, fixed product- or harness-side, with the reason.
Proof: RED before item 2, quoted — a new slow-store test per press, the store held by a second writer past the 2 s budget (as `LossFlattenOwedTests.cs:151-173`): the
cancel-all writes 2 legs and both orders end CANCELLED; the close-all leaves the book flat. Green after, plus `EmergencyPressTests`, `PressIdShapeTests`,
`PressInFlightTests`, `PressAtomicityTests`, `UnknownCloseTests` and `DispatchRecoveryTests` unchanged. ONE mutant, quoted: the cancel-all back on `Begin` → its
slow-store test red, no leg.
Done: the cause quoted; the full workflow green on all three platforms on the final tip (run id); any narrowed stress run quoted with its count; the gate per `docs/HOW-WE-BUILD.md`
and `docs/FLEET.md` "The builder pass" (rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed; touched classes 3×; tests box or NOT RUN;
names 0 removed, both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
