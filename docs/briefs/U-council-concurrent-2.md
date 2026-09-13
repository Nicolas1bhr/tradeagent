# U-council-concurrent-2 — the consequential boundary: two sealed assessments, one bounded challenge, one disposition applied by code
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:59-65` (verbatim: both directors submit an assessment BEFORE either sees the other's;
one bounded challenge; one disposition; a deadline with a predetermined default; code applies the policy so neither director can veto an
eligible deployment forever; two assessments are two turns; deduplicate boundary events by entity and revision), `:30-31` (rule 7), `:222`
(the retirement-candidate event reuses the same shape), then `Db/PublicationStore.cs` (`recipients`, `classification`, `Commit`, `IdOf`,
`delivery` committed vs delivered), `CouncilRelay.cs:365-385` (`Deliver`), `MissionEventStore.cs:14-43` and `MissionEventIds.ForRole`,
`DailyReports.cs:486-533` (the owner-message deadline shape, `overdue` at `:503`), `DailyReport.cs:200-206`, `CampaignStore.cs:291-297,320-337`
(`TrialRefusal` reads, `RegisterTrial` writes — two transactions) and `:401-431` (`ChargeVerdict`, one transaction), `Promotions.Standing` and the
verdict flow from `U-referee-2`. Branch `u-council-concurrent-2`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-council-
concurrent-2`, rebased onto `main` AFTER `U-referee-2` and `U-council-concurrent-1` land. Money-path grade: every guard ships with a test RED
before it and ONE mutant, quoted. No box, no ATAS, no money, no order. **Schema 16.**

**Why.** `COUNCIL.md:59-65` is the one paragraph of the doctrine with no product code at all: `grep -rn "assessment\|challenge" src` is empty,
`mission_event` has no boundary kind, and the report's "Decisions" are owner messages and warn lines. `U-referee-2` defers the directors'
sealed assessments here by name, and `:222` needs the same shape for retirement. `U-referee-1` left one race: the trial refusal is read in one
transaction and the trial registered in another, so two roles can exceed a campaign's trial budget by one.

1. **A boundary event, deduplicated by entity and revision:** `boundary_event` (kind — `promotion`, `retirement`; entity id; revision;
   opened_at; deadline; the predetermined default; disposition; disposed_at, by) keyed by (kind, entity, revision) — a repeat writes nothing
   and buys no turn (the `MissionEventIds.ForRole` shape); opening one wakes both directors with one paid turn each. RED: two raises of one
   proposal buy two turns each. Mutant (the key taken from the attempt): a restart manufactures senior spend.
2. **Two sealed assessments:** each director's `assessment` publication is committed but its `delivery` to the peer is WITHHELD until both
   exist, then both released together by `Deliver`; a second assessment from the same director for the same boundary is refused. RED: the
   first assessment reaches the second director's `in/` before they have written theirs. Mutant (released when one exists): the same.
3. **One bounded challenge, then one disposition:** exactly one `challenge` publication per boundary, capped like a report, from either
   director after both assessments are delivered; a second is refused and unpaid. RED: a second challenge accepted and paid. Mutant (the cap
   dropped): an over-length challenge published.
4. **The deadline's default is applied by CODE:** at the deadline, or when the challenge window closes, the app writes the disposition itself
   from the policy's default (`Promotions.Standing` for a promotion boundary), never from a director; the Situation shows each director their
   open boundaries and deadlines; section 8's decisions list every disposition with its evidence, owner and deadline. RED: the deadline
   passes and the boundary stays open for ever. Mutant (the comparison inverted): a boundary disposed the instant it opens.
5. **The trial budget charged in one transaction:** `TrialRefusal` and `RegisterTrial` become one `Database.Write` (or the charge precedes the
   run, as `ChargeVerdict` does). RED (the `Barrier(2)` idiom on the last trial): `Expected: 200 / Actual: 201`. Mutant (the check outside the
   write): the same.

Not this unit: the allocator and evolution (`COUNCIL.md:272`); retirement beyond reusing the tables; promotion itself and `TryAuthorizeExecution`;
the boundary on a screen beyond the Situation and the report; a third role.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, what you did NOT do.
