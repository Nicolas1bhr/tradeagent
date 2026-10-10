# U-reconcile-packet — Research is woken once for a new tape item it owns and a director before a boundary's deadline, and every turn's Situation says what changed, what the role believes and what is wanted
**Protects:** operator authority in-process (`CLAUDE.md`: no op, verb, arg, pipe-writable setting or agent-written file creates a subscription or a wake); held-back months and tape text as data (no wake
inside a holdout window, no item text, no held figure in any line); the owner's money (a tape wake coalesced, one unconsumed per role; a deadline wake keyed once per boundary and director); measurement vs claim
(a belief shown as the role's own claim, never as a measurement); honesty; the canon (one word, versioned, bytes quoted). VISION § 6.7 (the packet disposable, built from state).
Seat B; heavy (the agent boundary, holdout, the canon: opus); **no rung**. Part 2 of 2 of survey S-reconcile-wakes's draft (seat B split it, J1). **Depends on** `U-reconcile-wakes` (its objectives are this
packet's "what is wanted") and `U-research-ledger` (rung 31; its `List` is "what the role believes"; canon v2) — both land first.
**Facts (SOURCE at `main` `2fd1338e`, src identical at `dab2768e`; survey `fleet/tmp/B-survey-U-reconcile-wakes.md`; ◆ = `U-research-ledger`'s, ◇ = seat A's units — re-check every pointer at dispatch).**
- The tape raises nothing today (`Provisioning/TapeCollector.cs:264-276`; `TapeAppend`, `Core/Data/Tape.cs:158`); holdouts are by source time (`Data/TapeHoldout.cs:9-20`; `Pipe` `:114-118`); programs declare
  their tape inputs (`Features/FeatureSpec.cs:48`, `:146-148`) ◇.
- A boundary's deadline wakes nobody (`MissionLoop.cs:2219-2229`, `:2248-2263`) while canon v1 says the directors are "woken once" for it (`Canon.cs:252-253`, hashed ◆); `next.json` cannot reach a deadline more
  than 30 minutes away (`MissionLoop.cs:111`).
- The Situation (`MissionLoop.cs:658-752`) carries owner messages, deliveries, wake causes, boundaries and account lines — no belief and no objective (R21 row `:49`).
Read first: `CLAUDE.md`; VISION § 6.7; R21 §§ 2-3; `docs/briefs/U-research-ledger.md` and its landing record; `U-reconcile-wakes`' record; `MissionLoop.cs:500-760`, `:2200-2420`; `TapeCollector.cs`;
`TapeHoldout.cs`; `Canon.cs:240-260`, `:440-520`; ◆ `Core/Db/ResearchLedger.cs`.
Must NOT: add a pipe op, verb, arg, pipe-writable setting, table, rung or migration, or an agent-declared watch (survey J2: not until a role's record shows an owned event it missed); give `TapeCollector` a
database; quote a tape item's text, name one inside a holdout window or put a held figure in any line; drop the owner's words from first place, the attempt line or any state line; touch the canon beyond the one
word below; write `ledger_` tables; touch `OrgStore`, positions or assignments (`U-org-*`).
Items, one commit each, one-sentence messages:
1. **Owned observations.** A `tape` wake for Research when an announcement item, or an input an active deployment of its version declares, is stored new and outside every window `TapeHoldout.Pipe` refuses (a
   window with no end included): one unconsumed per role, its id from the fetch, its payload source/series/subject/count only, raised through a delegate. A `deadline` wake per unassessed boundary and director,
   keyed `deadline:<boundary>:<role>`, due a quarter of its window before `deadline_at`, settled without a turn once assessed. Canon v3: "both directors are woken for it" (−5 B, survey J4), after v2, both seats'
   hashes in one commit, each pair's bytes before and after quoted.
2. **The packet.** "Why you are awake" heads **what changed** (one line per observation with its entity, ≤ 50, `BatchLimit`), then **what you believe** (◆ `List(role, status: open)`, ≤ 10 entries and 2 KiB,
   each marked as the role's claim), then **what is wanted** (`U-reconcile-wakes`' open instances, boundaries, Guidance); owner words first, state lines and the attempt line unchanged; Continue rewritten; the
   guide's "Every turn has a cause" says it; each pair's Situation size before and after quoted.
Red-first tests (each quoted red at base): (a) `A_new_announcement_wakes_research_once_and_names_no_text`; (b) `A_tape_item_inside_a_holdout_window_wakes_nobody` (a window with no end included);
(c) `An_unassessed_boundary_wakes_its_director_before_its_deadline_and_an_assessed_one_does_not`; (d) `The_packet_says_what_changed_what_the_role_believes_and_what_is_wanted_under_the_owners_words`;
(e) `Nothing_an_agent_writes_becomes_a_subscription`; `U-canon`'s binding, version and size tests green, the quiet-review tests unedited. Mutants, red and quoted: the tape wake not coalesced ⇒ (a); the holdout
check skipped ⇒ (b); a belief line without its claim mark ⇒ (d).
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three platforms; tests box `ready` once or
NOT RUN; names vs `main` 0 removed (both set sizes). The report names "no rung", the canon version with both hashes and each pair's bytes. `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
