# U-canon — each role's AGENTS.md becomes a short deliberate canon with its reach generated per runtime; a test refuses a verb, tool or path the runtime cannot reach; each attempt records its canon
**Protects:** honesty (`CLAUDE.md`; R20 § 1: the harness is told of a shell, the internet and a wake file it lacks) and VISION § 6.7's canon — deliberate, short, versioned, changed only by a change that says so,
its capability section generated from what the app grants that seat and runtime (R21 § 3.2) — inside PRINCIPLES "Autonomy is the default inside authority": it states authority and the boundary, never a method.
Heavy; seat B; no rung; the order rules it moves are money safety, moved verbatim. **Depends on `U-quiet-review`** (merge `7471908e`, record `8184265f`) **and `U-memory-kept`** (merge `3b5b08d3`, record `550301df`), both landed; before `U-research-ledger`, whose verbs come with the canon's next version.
**Facts (SOURCE at `550301df`, read and not run; survey S-canon at `eb906b17`, re-pointed by seat B after both lights landed).**
- AGENTS.md = `WorkspaceBuilder.Instructions` (`:201-641`) + `RoleSection` (`:658-721`) + `SimulatorParagraph` (`:171-191`), written for every role at each AI start (`:93-94`, `:103-105`
  ← `AgentSupervisor.cs:88` ← `AppHost.cs:1406`), one text whatever the runtime (`WorkspaceContext` `:14-16`); the harness sends that file as its system message (`ApiConversation.cs:348-352`, `:469-480`).
  After both lights, default settings (real renders, summed): operations 31,543 B / 32,757 B with the simulator paragraph — 11 B under codex 0.160.1's `project_doc_max_bytes = 32768` (cut NOT VERIFIED); research 31,459 / 32,673; settings move it by tens of bytes; the role section is last.
- Untrue today. To the harness: shell, internet, packages (`:204-206`), `../inbox` (`:209-210`, `:522-527`; `ToolPaths.cs:52`), `.tradeagent/next.json` (`:253-270`, `U-quiet-review`'s idle sentence included),
  writing `strategies/`, `data/`, `scripts/` (`:297`, `:339-345`, `:363`, `:541`, `:581-584`; it writes `out/`, `trading/` only, `GrantedWorkerTools.cs:74`), "nothing stops you" (`:714-717`). To every CLI seat:
  "personal Windows laptop", "modest laptop" (`:204`, `:509`; RECORD `BUILD-STATUS.md:8785`, attempt 3 on this Mac). To Research: the order rules (`:470-505`) while its mutating ops are refused
  (`GatewayPipeServer.cs:1181-1191`, `Council.cs:63`; comment `:644-655` stale). `:587` against `:684`, `:714` on writing `in/`.
- The reach's sources, referenced and never copied: `GatewaySchema.Ops()` (`:76-524`), `Ops.Mutating` (`Protocol.cs:172-174`), `CouncilRoles.MayPlaceOrders`; `GrantedWorkerTools.cs:49-54`, `:74`, `:116-129`;
  `Containment.Sandbox()` (`AgentRuntime/Containment.cs:43-45`), PATH (`WorkspaceBuilder.cs:140-152`); `SubDirs` (`:32-33`), `CouncilRoles.InDir/OutDir`; `MissionLoop.cs:2419` (`next.json`, a literal), `:108`,
  `:111`; `CouncilRelay.cs:587-626`; role → runtime `AppHost.cs:202-216`. Unrecorded: the canon an attempt ran under (`input_hash` is the prompt's, `TurnMeter.cs:821`; `context`, `:281-293`, has none).
- Pinned, re-pointed and never deleted: `MissionInstructionsTests` (16), `CouncilRoleTests:70`, `CoreTests:214-258`, `Loss{Reopen,DayClosed,Flatten,Budget}SurfacesTests` (`:211`, `:225`, `:158`, `:291`),
  `DataOpsTests:114`, `DailyReportTests:182`, `DeclaredBarsGrammarTests:162`, `MaterialAppOriginTests:214`, `ResearchLibraryTests:162`, Fault `EmptyAllowlistTests:86`, `ApiWorkerTests:65`, `WorkerToolTests:389`.
Read first: `CLAUDE.md`; PRINCIPLES' two autonomy paragraphs; VISION § 6.7; `WorkspaceBuilder.cs` whole; `GrantedWorkerTools.cs:1-130`; `ApiConversation.cs:340-360`, `:465-480`; `TurnMeter.cs:185-300`, `:780-830`.
Must NOT: change a grant, op, tool, path rule, wake, cap or relay rule (the canon describes the reach, it grants nothing); drop, soften or paraphrase a money-safety or boundary sentence; rename AGENTS.md or
move it out of the role's home (vendor CLIs read it by name; `OnboardingView.cs:447-448`); add a setting or toggle selecting an older canon (the comparison is two builds, `CLAUDE.md`); add a rung; copy a grant
list; name a ledger, objective or packet that has not landed, or an arg `GatewaySchema` lacks (`backtest`'s `parent`: seat A's `U-backtest-parent`); read the canon or the guide into a Situation.
Items, one commit each, one-sentence messages:
1. **The reach.** `AgentReach.For(role, runtime class)` — CLI or harness — gives host, surface, read and write sets and whether each is a wall, wake, hand-back kinds with patterns and caps, verbs or tools
   (Research without `Ops.Mutating`) and session cadence, all from the sources above; the `next.json` path becomes one constant the loop and the reach share.
2. **AGENTS.md is the canon; GUIDE.md holds the rest.** Kept verbatim: mission (`:212-240`, `:291-294`), authority (limits `:390-402`, the consequential boundary `:608-637`, a human once `:594-606`; methods, tools
   and hypotheses are the agent's), evidence (`:226-227`, `:299-304`, `:344-355`, `:379-380`, `:703-704`, plus § 6.7's observed, claim and unknown in two sentences), the boundary (`:465-469`, `:532-539`; `:470-505`
   only where the role may place orders, else one line that it may not), the role part; then `## What you can reach` from item 1 and one line naming GUIDE.md, which takes the rest verbatim, app-owned and
   rewritten every start, opening "these are defaults; your own methods are yours, in your own files". Both render per pair through slots that throw on a capability the pair lacks; the harness's system
   text is the canon rendered for the harness at the same start, read by `Mission()`, never the CLI's file.
3. **The canon changes only with its version, and stays small.** `Canon.Version` plus a test-held SHA-256 ledger per (version, seat) over the template, slots unfilled; versions only grow; AGENTS.md ≤ 16 KiB.
4. **Each attempt records its canon.** The attempt's `context` JSON gains `canon_version` and `canon_sha256` of what the turn received (the CLI's AGENTS.md as found at launch, whether it is the app's own —
   `AppFileManifest.Wrote` — and whether an `AGENTS.override.md` lay beside it; the harness's system text).
Red-first tests (each quoted red at base): (a) `The_harness_is_never_told_of_a_shell_a_wake_file_or_the_inbox`; (b) `Every_verb_tool_and_path_the_canon_names_is_reachable_by_its_role_and_runtime` — O·CLI,
R·CLI, R·harness: each backticked `trade` form or op name a verb of the pair, each tool one of its tools, each path in its read set, each write-slot path in its write set (red at base: R·CLI `cancel-all`;
R·harness); (c) `The_reach_is_read_from_what_enforces_it`; (d) `A_canon_change_without_a_new_version_is_refused`; (e) `The_canon_fits_in_half_the_vendors_read_limit`; (f) `An_attempt_records_the_canon_it_ran_under`
(an edited AGENTS.md reads not the app's); (g) `The_research_director_is_told_it_may_not_place_orders`. Mutants to watch red and quote: the harness rendered with the CLI reach ⇒ (a), (b); the template edited
with its version unchanged ⇒ (d).
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; three suites 0 failed; touched test classes 3×; branch CI on all
three platforms; tests box or NOT RUN with `ready`'s answer; names vs `main` 0 removed (both set sizes printed); the report names the canon version, per-seat hashes, each pair's rendered bytes and the `main` sha it last rebased on (the old canon's build); `## Report`
≤ 20 lines appended here. No push to `main`, no merge.
