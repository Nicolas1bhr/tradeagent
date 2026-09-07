# U-wakes — persisted events replace the immediate re-turn; idleness with a reason is healthy; the owner's words survive
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rule 7, "Never-stopping is a scheduler", the `U-wakes` line, the
round-4 lines on the owner's message), then `docs/RESUME-HERE.md` step 6. Branch `u-wakes`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-wakes`, rebased onto `main` first. Every guard ships with a test that
was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money.

**Why.** `AskedForDelay()` (`MissionLoop.cs:590-606`) returns `Zero` when the AI wrote no `next.json`, so the loop re-turns at
once and only the cost cap stops it; the AI is never told `next.json` exists, while `WorkspaceBuilder.cs:172-174` says that
having nothing to do means "you have not looked hard enough". A message typed while it works is queued in memory only
(`AgentSession.cs:273-283`): a restart loses it and its receipt. Rule 7: justified idleness launches no inference.

1. **`mission_event`, schema 7** (the `ai_attempt` pattern, `Database.cs:290-345`, `Versioning.cs`): `id` (deterministic per
   source — `owner:<seq>`, `inbox:<scan-at>`, `fill:<execution_id>`, `order:<request_id>:<state>`, `renewal:<local-day>`,
   `self:<attempt-id>`, `review:<yyyy-MM-ddTHH:mm>`), `kind`, `created_at`, `due_at`, `payload` (small JSON), `consumed_at`,
   `consumed_by` (attempt id), `disposition`; UNIQUE on `id`, a second raise of the same id is a no-op; app-written only, no
   verb, no pipe op. Raised by: the owner's message (item 3); a scan with `Added > 0`; a fill recorded; a request reaching a
   terminal state (`OnOrderChanged`); the midnight renewal; the AI's `next.json` as a `self` event due at `after_seconds`
   (capped at `MaxDelay`, the file still read and deleted); a review tick every `MissionReviewMinutes` (a setting, default 30,
   0 = off, on the Safety page; lowering it spends more, so it asks twice).
2. **The loop wakes on events, never on nothing.** `TurnAsync` runs only when an unconsumed event is due, marking the events it
   takes `consumed_by` the attempt id BEFORE the process starts (one transaction with `AiAttemptStore.Begin`); the Situation
   names the events that caused the wake (the owner's words still FIRST); with nothing due the loop waits for the earliest
   `due_at` or an in-process raise; `BusyRetry` and backoff unchanged; the card's Waiting state says what it waits for. RED
   (the property in `docs/COUNCIL.md`): no eligible unconsumed event → ticks, a fresh loop over the same database and a replay
   of every consumed event launch ZERO turns (`Sent.Count == 0`); mutant: consumption not committed → a restart re-runs it.
3. **The owner's message, recorded.** `SendAsync` while the AI works writes an `owner` event (text, `received_at`) and shows the
   receipt line as now; on restart the queue is the table, not memory; after the turn that consumed it the event's
   `disposition` is `answered` (the turn ended with a reply) or `failed` (re-raised once, the failure in the payload). RED: a
   message typed while working, then a new host over the same database → the next Situation carries the words (today it is
   lost); mutant: the event written without the text.
4. **The idle language.** `WorkspaceBuilder.cs:170-174` rewritten: turns are caused by named events; a turn with nothing new
   ENDS at once and costs little; idleness with its reason stated is healthy; `next.json` `{"after_seconds": N}` asks for the
   next wake when a job needs one (cap 30 min) — the AI is told the file exists. `MissionInstructionsTests` pins the new
   sentences and the ABSENCE of "you have not looked hard enough". RED: the old sentence present. Mutant: `next.json` unnamed.
5. **The quiescence barrier, pinned:** one test proves an owner-chat turn during the post-turn scan makes its sighting
   `InboxUnattested` (both share `AgentPresence.Shared`); mutant: the chat session on its own presence attests falsely → RED.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, the gate counts pasted, one line per item with its RED and mutant, what you did NOT do.
