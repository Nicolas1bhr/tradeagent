# U-org-principals — a third position is a row, not a constant: its own home, conversation, attempts and keys, never folded into the chair
**Arrow closed:** fixed roles → positions, first half: identity and homes (`docs/ORGANISATION.md` §§ 2, 15; R12 § 5 O2; R18 § 4 split it; the second half is
`U-org-rights`). **Depends on `U-org-ledger`** (landed `33017817`); **dispatch after M0 attempt 3 (`cc7a0974`, the run it waited for) and after seat B's three layers** (factory-plan Law 10: the
truth run must not wait for this, so M0 attempt 4 does not; R21, R22). **Once `U-decision-port` has landed (§ 15 puts it first), `perception` (`AppPrincipals.Perception`) stays a meter role that is
no position:** its launches reach `AiAttemptStore.Begin` directly, never `TurnMeter`, so item 1's active-position check sits on the position paths and never in `Begin`; `Fence` keeps quarantining it by
exact role, `Refuse` and `ComposeRecovery` keep naming it, `Totals`' role-less counts keep skipping app principals and count every position; its test (e) stays green, name kept (R22). No schema change.
**Today (SOURCE at `1275aff`, R12/R18-checked, NOT runtime-verified):** 121 `CouncilRoles.` lines in 29 src files, 635 in 50 test files; `CouncilRoles.Or`
folds an UNKNOWN role into the chair (`Core/Council.cs:53`) and keys open attempts and loop counters (`TurnMeter.cs:806, 1124`; `MissionLoop.cs:1245-1263, 2277`;
`MissionEventStore.cs:192`; `CouncilRelay.cs:500`); `AiAttemptStore.cs:299` and `DailyReports.cs:881` use it for wording only; `HomeDir` sends an unknown role
to the chair's folder (`Council.cs:80`; `Paths.RoleHome("x")` = `workspace/agent`, `Paths.cs:50`), so its CLI would resume the chair's codex session
(`RuntimeManifest.cs:572-575`). **Observable result:** a third position seeded by a TEST with raw SQL through `Database.Cmd` (`Database.cs:95`, as
`DispatchRecoveryTests.cs:294` writes rows) gets its own home, conversation, open-attempt slot and loop counters keyed by its exact id; an id that is not an
active position is refused or quarantined with a reason, never folded into the chair; the legacy pair's every test name and outcome is unchanged.
Read first: `CLAUDE.md`; `docs/ORGANISATION.md` §§ 2, 13–14; R12 § 1 seams 1–6 and § 5 O2; R18 § 1.3; `Council.cs` (whole); `OrgStore` as `U-org-ledger` landed it.
Items, one commit each, one-sentence messages:
1. `CouncilRoles.All`, `Default` and `Or` stay as the legacy pair — history (a NULL role on an old row is the chair's), the two-senior boundary ceremony
   (`BoundaryStore.cs:423, 602, 622, 818`, unchanged) and the default share (`Trading.cs:314`) until `U-org-envelopes`; every LIVE key (`TurnMeter`'s open slot,
   the loop's counters, `MissionEventStore.For`, `CouncilRelay.Fence`) takes the exact position id; an id that is not an active position is refused or
   quarantined with a reason.
2. Homes: `CouncilRoles.HomeDir` stays static and total — `operations` → `agent`, `research` → `research`, any other id → `org/<id>` (the id checked as
   `[a-z0-9-]+`; never a path read from a row, which a CLI seat could write); `org_position.home_dir` records the same value. Builders and walkers take the
   active positions: `WorkspaceBuilder` (`:50, 76, 91, 103-105`; `BuildAll` gains an optional positions argument, default the legacy pair), `AgentSupervisor`
   (`:78, 140`, the only `BuildAll` callers), `MaterialScanner.RolePaths` (`:99-102`, from its own `Database`), `CouncilRelay.Reconcile` (`:250`) and `AppHost`
   (`:263-264`); `Paths.EnsureAllVerbose` (inside `StartAsync`, `AppHost.cs:512`) is left as it is. Whether an id is ACTIVE is checked where a `Database` is held.
3. Compatibility: `HomeDir`, `Paths.RoleHome`, `BuildAll` and every constructor stay source-compatible (optional arguments only — `new MaterialScanner(` has 50
   call sites, `new MissionLoop(` 52, `new TurnMeter(` 17); a test that still breaks is rewritten in place, name kept.
Red-first tests: (a) `A_third_position_runs_in_its_own_home_not_the_chairs` (quoted red at base: `workspace/agent`); (b)
`A_third_positions_open_attempt_is_keyed_by_its_own_id` (red: the chair's slot, `TurnMeter.cs:806, 1124`); (e) `An_unknown_principal_is_refused_not_folded_into_the_chair`.
Mutants to watch red and quote (R12): `Or` back in `TurnMeter`'s key ⇒ (b) red; the `HomeDir` fallback to `agent` restored ⇒ (a) red.
Conflicts (R18 § 3, re-check at dispatch): `U-tape-read` (W5, same wave) shares `WorkspaceBuilder.cs` (instructions against `:50-105`) — L, rebase;
`U-evidence-identity` (landed by then) shares `GatewayPipeServer.VerdictFor` only with `U-org-rights`, not with this unit.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
