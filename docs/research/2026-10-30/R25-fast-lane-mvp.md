# R25 — The fast lane and the MVP (2026-10-30)

**The owner, 2026-10-30 ~18:1x CET, verbatim:** "hey let's reframe a bit, we've been doing good work on the tradeagent project. but in comparison to the
end vision we are going way too slowly, this is because of my last directives and because we are way way too careful. here's what i'd like you to do.
since you are the smartest agent out there, i'd like you to go your way to build full speed towards the end vision adn MVP. for you now the orchestration
standards and such are a mere suggestion. i trust you to run as top level orchestrator and make the most of it with the tokens we have at our disposition.
you can always ping me if in need and even though you need to be canonically grounded in the vision you must delegate expertly to keep your context lean
and long-running. the only thing i'm going to do myself and don't want you using your tokens on is the head-start tools, skills and skills i'll build that
myself and hand it to you later."

This record defines the MVP the build now runs at, the gaps three read-only surveys found on main 997365c4, and the decisions the orchestrator took on
the owner's behalf (`orchestrator-decides`, 2026-10-04) — each his to overrule. `docs/PRINCIPLES.md` stays the product definition and `docs/VISION.md`
the end state; nothing here weakens a protection in `CLAUDE.md`. The operating rules for the build are the fleet's `FAST-LANE.md` (outside the repo).
Surveys: the fleet's `tmp/MVP-2026-10-30/{org-loop,windows-path,backlog-health}.md`.

## 1. Where it stood (main 997365c4)
- The organisation's kernel is built and green in CI with puppet seats: chart, positions, envelopes, wakes, assignments, packets, verbs, the board, the
  chief, scorecards, the surface. **No real model has ever taken a turn on a chief, head or executor seat.**
- The legacy pair (operations + research) proved author → backtest → iterate on a real model (attempts 2–3); **verdict → paper → fills has never run
  on any seat** (attempt 3 asked no verdict).
- The Linux host holds a recorded cell proof (2026-10-25) and the owner's sign-in; Windows cannot record one, so the organisation cannot run there.
- The last published release is v0.1.2 (2026-09-06), 2,418 commits behind main; CI builds no ATAS adapter.

## 2. The MVP
**MVP-L (the Linux host — the first proof).** From a fresh home on the owner's server: onboarding (paper only), appoint and activate a chief, start the AI.
Then, with no further owner input, the organisation charters at least one department or team; its head hires executors who activate inside the gate;
executors author and backtest candidates in their cells; verdicts are asked; an eligible version is allocated to paper by app policy and runs forward
through the deterministic runner; fills, P&L and costs come back to its author; the organisation keeps, kills or iterates — for **72 hours unattended**
inside its AI budget, surviving a service restart, every step and cost visible on the app's pages, and the owner able to talk to his chief.
**MVP-W (the product).** The same on a Windows 11 laptop, installed from a published release, with AppContainer cells and no terminal. Mihael is told
only once MVP-W holds (`mihael-not-to-be-bothered`).
Real money stays closed in both: the board's live path exists in code, no live venue is connected.

## 3. The gaps (ranked; S/M/L)
1. Real model on a broker/cell seat unproven; `BrokerSeat.Model` a constant (BrokerSeat.cs:40) — the M-org1 sitting (S).
2. The chief's first turn is a 24 h ceiling wake — `U-chief-wake-once` (S).
3. The chief and heads cannot request their next wake (AgentReach.cs:229-241) (S).
4. Every hire stays dormant until an owner press (OrgStore.Restructure.cs:378-383); activation wakes nobody (M).
5. Org positions get no role instructions (Canon.cs:149-154) (M).
6. The chief cannot spend a verdict (GatewayPipeServer.cs:2963-2966) (S).
7. Dataset and holdout are Settings presses; a campaign holds 3 verdicts / 200 trials and nothing renews it (M).
8. The Linux sign-in code clips (OnboardingView.CodeWell) (S).
9. The owner's words never reach the chief; his chat is refused while a chief is active (MissionEventStore.cs:741-749, PositionActivation.cs:283-288) (S).
10. Agents cannot list or stop their paper runs (GrantedWorkerTools.cs:135-142) (S).
11. No end-to-end puppet test of the whole loop (M).
12. No official socket for the owner's head-start kit (skills and tools) (M).
13. Windows: no cell provider, no proof (M–L, two or three units); no release reaches the laptop (S–M).
14. The recurring CodexSurfaceProbeTests timing red (S).

## 4. Decided by the orchestrator on the owner's behalf (2026-10-30)
- **D1 The fast lane** replaces the seat/train/record ceremony; the protections stay (FAST-LANE.md "What stays").
- **D2 Windows egress** keeps his "Web (443) for every executor": a firewall rule on the cell container SID, placed by one fixed elevated helper (one
  UAC "Yes"); declined → that host's cells run at egress `none`, said in words; the proof records what was measured.
- **D3 Hires below the chief activate themselves** inside the gate (proven cell, envelope, the AI day-total) and wake both the hire and its hirer. The
  owner keeps a narrowing veto (deactivate, two presses); the board's powers stand (R24). The chief's appointment and activation stay as ruled 2026-10-25.
- **D4 The owner's words reach the chief:** while a chief is active, his chat and inbox route to the chief's queue, as data (INBOX-001); the legacy pair
  stays asleep while a chief is active, as today.
- **D5 The chief and heads request their next wake,** bounded by the board's ceiling and the day-total.
- **D6 The chief may spend a verdict** on a version authored in its subtree; the campaign's budget is unchanged.
- **D7 Campaigns renew only on data no prior verdict or research could read** (fresh forward data); never a spent holdout reused; dataset and holdout
  set up in onboarding with defaults.
- **D8 gpt-6-luna is the default for every org seat** (≈ $0.11 a turn measured, list price, his plan); a position's model becomes a choice among catalog
  entries that pass the classic-tools check; gpt-6.1-sol is the fallback if luna is refused (his 2026-10-25 words).
- **D9 Org positions list and stop their own subtree's paper deployments** (paper only; orders stay operations').
- **D10 The head-start socket:** a read-only kit folder (skills + tools) the owner's kit is installed into, visible in every seat home and cell, named in
  the canon. The kit itself is his to build.

## 5. Order
Wave 1: `U-chief-wake-once` · `U-ci-fast-fixes` (gaps 8, 14) · `U-cell-windows` (gap 13, the provider and proof) · `U-org-canon` (gap 5).
Wave 2: `U-org-autonomy` (gaps 3, 4, 6, 9, 10; D3–D6, D9) · `U-evidence-campaign` (gap 7; D7) · `U-seat-models` (D8) · `U-kit-socket` (gap 12; D10) ·
`U-loop-e2e` (gap 11).
Sittings: M-org1 #1 as soon as wave 1's first two land (the first real luna turn on the chief); M-org1 #2 — the 72-hour run — once wave 2 has landed.
Wave 3: the Windows release (a CI-built installer with the app's own paper venue first; the ATAS-enabled build when the ATAS box answers) and the
Windows sitting; then what the 72-hour run teaches.

*Written 2026-10-30 by the orchestrator. Establishes direction and decisions only: it marks no feature implemented, no test passed and no boundary
enforced.*
