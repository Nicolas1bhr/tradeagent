# U-org-verbs-a — a head re-scopes a mandate it decides into a linked successor, an executor proposes a finding beyond its mandate to its head, and every capable position reads its own place on the chart with `org-show`
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.9 (:316 heads' `rescope`; :320 executors' `propose`; :314 every position's `org show`), § 2.2 (rows never updated; failures
remembered), § 2.5 (a deviation or off-path finding always reaches the head); ORGANISATION § 4 (:170-174 latitude, :178-181 retry then re-scope); § 15 O6 (:427, part a of 5);
seat B handoff § 4 ("MUST carry mandate-rescope + the executor's propose"). Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` (main `25bae9bc`) — re-grep every pointer.
**Protections — ORG-001 (the RECORDED seat assurance, never headship), AUTH-001 (no op reaches operator authority; none joins `Ops.Mutating`), ORDER-001 (no
mandate to `operations`), measurement vs claim (the proposal is a claim, its refs app-checked), BUDGET-001 (a successor never launders the old mandate's spend):
red-first and ONE watched mutant.** Seat B; **opus/high**. **No rung** (events, evidence JSON, one wake kind as text).
**Depends:** O5a (`2ed34aaf`, rung 40) and O5b (`dd813fb8`, rung 41) LANDED; **`U-org-packets` first** (§ 15 :460; it owns the position's packet view — `org-show`
reuses its reader, never a second one; both touch `MissionEventStore`'s wake table and `RoleObjectives`).
**Rulings carried:** § 2.9 at `bdf66e1b`/`920238d3` — every verb named apart from the pipe's ops, ONE `OrgVerbCaller` = recorded assurance ≥
`CapabilityPolicy.Minimum(DecisionVerbs)`, served on `BrokerDoor.OrganisationVerbs` behind `VerbGate`; O3 — a new wake kind is mapped in `WakePriority` quoting
the § 10 words, never defaulted; CLAUDE.md — no compatibility, refuse never guess. **Applied pending ORCHESTRATOR (survey Q9):** rescope = successor, propose =
event + `proposal` wake.
**Today:** mandate rows refuse UPDATE (`Core/Db/Database.cs:2496-2499`); event kinds have no `rescoped`/`proposed` (`Core/Db/OrgStore.Mandates.cs:93-129`); `Cancel`
is the decider-only template (`:367-377`); `AssignRefusal` validates terms (`:279-316`); `RefsRefusal` checks refs (`OrgStore.Decisions.cs:182-196`); caller gate
`GatewayPipeServer.Mandates.cs:24-48`; ops `Core/Protocol.cs:206-256`, dispatch `GatewayPipeServer.cs:1379-1394`, op table `:363`, schema `GatewaySchema.cs:596`, CLI
`TradeCli/Program.cs:360-430`; wake kinds `MissionEventStore.cs:159-173`, table `:212`; disjointness guard families `OrgAssignmentTests.cs:331` (no `Org` family yet).
**Observable result:** a puppet head re-scopes a mandate whose holder failed twice: the old one reads `rescoped` with the new id, the new one is issued with the old
id, the holder is woken once for the new; a puppet executor proposes a finding with two refs and its head is woken once; each reads `org-show`; the legacy pair
on their unconfined seats are refused all three in the gate's words.
Items, one commit each, one-sentence messages:
1. **`mandate-rescope`** (`OrgStore.Rescope`, one write): decider only (the row decides), the mandate open; the new terms pass `AssignRefusal` (same holder; a
   different holder is cancel + assign); old gets `rescoped` (joins `MandateEventKind.Closing`) with evidence `[new]`, new gets `issued` with evidence `[old]`;
   old wakes withdrawn, ONE `mandate` wake for the new; the old mandate's spend and failures stay its own (`mandate-show` names the predecessor and its cost).
   Check: `Rescope_closes_the_old_and_issues_a_linked_successor_in_one_write`; `A_launch_under_a_rescoped_mandate_is_refused`; non-decider refused, nothing written.
2. **`mandate-propose`**: the HOLDER of an OPEN mandate; claim ≤ `MandateLimits.MaxClaimChars`, refs through `RefsRefusal`; a `proposed` event (non-closing),
   ONE `proposal` wake to the decider (new kind, classed with `escalation` in `WakePriority`, quoting § 10), one open proposal per mandate (the decider's next
   accept/reject/rescope/cancel closes it); `RoleObjectives`: a head owns its unread proposals. Check: `A_proposal_wakes_the_decider_once_and_carries_no_authority`;
   second proposal refused; not-holder refused.
3. **`org-show`** (`OrgVerbCaller(ctx, Capability.Reads)`): the caller's unit, its chain to the root (the owner as "the owner", never the watcher or audit line),
   its open mandates held and decided, and each chain unit's resolved AI envelope with today's spent + reserved (O4's readers) — built on packets' reader.
   Check: `Org_show_shows_only_the_callers_own_view`; an app principal and a dormant position refused.
4. **Names and door:** `MandateRescope`, `MandatePropose`, `OrgShow` join `Ops.Organisation`; the guard's families gain `Org`; schema + CLI words; the door serves
   them unchanged. Check: the disjointness guard green; `BrokerDoorTests` lists the three behind `VerbGate`.
5. **One puppet act** (`PuppetLife`; `trade mandate rescope …`, `trade mandate propose …`, `trade org show`) on a Mediated puppet manifest, plus the legacy refusal.
Red-first (quote each red at base): items 1–3's checks, and GUARDS `OrgAssignmentTests.The_legacy_pair_on_unconfined_seats_hold_no_mandate_verb_research_headship_included` (:212) and `BrokerDoorTests.An_org_verb_on_the_door_is_refused_to_an_attempt_recorded_unconfined` (:390) extended to the three.
**The watched mutant:** `Rescope` skips the decider check ⇒ the holder re-scopes its own mandate ⇒ item 1's non-decider test red. Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.2, 2.5, 2.9; ORGANISATION §§ 2, 4; the survey §§ 1–2; O5a/O5b records; `OrgStore.Mandates.cs`, `GatewayPipeServer.Mandates.cs`.
**NOT in scope:** fences on cancel/rescope (part b); charter/hire/close (c, d); envelopes beyond AI (e); helpers; any packet field (packets owns it).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
