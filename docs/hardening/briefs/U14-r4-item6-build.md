# U14 — BUILD BRIEF · round 4, item 6 (the LOW batch) + suite figure

**Tier T1** (order path: the write-ahead record that answers "did this product submit this identifier"). **Legs:** you are
leg [1] (builder). Leg [2] (an independent Opus adversarial verifier) and leg [3] (Codex read-only) run AFTER you report,
on your final sha, each in its own worktree. **Round cap for this item: 2** — it is a LOW batch closing a PASS round.

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` (mandatory read-gate; R1–R6 and §6 bind you).
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md` (the four safety rules on `IAtasAdapter`).
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/PROGRAM.md` (record conventions).
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14.md` — the unit record. The
   original build/verify records were LOST with a wiped scratchpad; this reconstruction and the branch commits are the truth.
   The finding labels V20/V24/V26 in it come from the lost verify record; only the sentence next to them survives.
5. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u14-coid-witness-rewrite.md`.

Those docs live on `main`; your branch predates them, so read them at the absolute paths above.

## Where you work

- Worktree: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u14-build`, branch
  `u14-coid-witness-rewrite` @ `a8b3fb0` (base `3f1d8f2`, 25 commits). Work ONLY in this worktree. Never `cd` into the
  main checkout to edit code; never touch other worktrees.
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; `dotnet build TradeAgent.sln`;
  `dotnet test TradeAgent.sln`. The Mac has no `timeout` binary; each tool call is capped at 10 minutes, so run the full
  suite with output to a file and read the tail.
- The Windows box is OFFLINE for this leg. The two `AtasStrategyAdapter.cs` hunks compile only on the box — do not touch
  them and do not try to compile the bridge against ATAS.

## What to deliver (in this order)

**0. The missing number.** Run the full suite at `a8b3fb0` before changing anything and write the per-project counts
into the record (the round-4 items 1–5 figure was never captured). Also `dotnet build TradeAgent.sln`.

**1. Superset rule by MEMBERSHIP, not count.** Adoption/read-back currently compares record counts where it must compare
record sets: a candidate that has the same number of records but different members must NOT be adopted (a rival that
dropped one claim and added another looks like a superset by count). Red-first: write the test that adopts such a
candidate today, watch it RED, then fix. Keep the existing rule "a candidate may never shrink the record set".

**2. Pin the `Identified` asymmetry.** `Submitting` restores its pre-attempt snapshot on failure; `Identified`
deliberately does NOT (round 3). Write the test that pins both halves, and prove the pin bites: temporarily make
`Identified` restore its snapshot → the test must go RED → restore the code (from a copy, with `touch`).

**3. Pin the mandatory-lock behaviour.** One owner per witness (round 4 item 1): cannot acquire the lock → `Submitting`
returns false and `Trouble` reads "another writer owns this witness"; a CAS miss is a refusal. Tests must state
"refused without the lock" for both paths and bite under a mutant (e.g. make the lock failure fall through to a write).

**4. Keep "an anchorless candidate reads as a flagged zero".** Prove it is still true after items 1–3 (an existing test
must cover it; if none does, add it and show the mutant that would let an anchorless candidate be adopted bite).

**5. `docs/RESUME-HERE.md`, "Verifying what you inherited":** if that section exists on your branch, change the
expected bridge reading to `proto=3`. If the section does not exist on the branch, say so in the record and leave it.
If `docs/CONTRACTS.md` (on your branch) does not yet state the protocol-3 bump, add the one sentence; if round 4 already
did, quote the line.

**6. Final gate.** `dotnet build TradeAgent.sln` then the FULL suite; paste the per-project counts and the total into the
record. Any RED = not done.

## Ownership (R2 — do not expand or narrow silently)

You may edit: the CoidWitness implementation and its tests (locate with `grep -rl CoidWitness src tests`), the bridge
project's witness-related files, `docs/RESUME-HERE.md` (that one line), `docs/CONTRACTS.md` (that one sentence).
You may NOT edit: `src/TradeAgent.Gateway/**`, `AppHost.cs`, the updater, the App UI, `AtasStrategyAdapter.cs`. If
delivering an item requires a file outside this list, STOP that item and report; deliver the others.

## Working rules that cost real time last session

- Commit on the branch after every sub-item, BEFORE any mutant run. Restore mutated files from a copy (`cp`), never
  with `git checkout --` (three builders destroyed uncommitted work that way), and `touch` the restored file so the
  build does not skip it.
- No `Co-Authored-By` trailers in commit messages (owner's rule). Commit messages: one plain sentence saying what changed
  and why.
- Checkpoint as you go: append a section `## Round 4 — item 6 (build record, <date>)` to
  `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14.md` (the MAIN worktree
  file) and update it after EVERY sub-item — RED quote, GREEN, the mutant and its bite, the commit sha. Do not run git
  in the main worktree; the manager commits the record. Anything only in your context dies with the connection.
- Honesty contract (§6): every claim is "verified by running X → output" or "NOT verified: why". Banned words: should
  work, looks correct, probably, I believe, minor, trivial, static-verified, basically.
- §9.9: for item 1, answer in the record whether a test class or gate could catch "compared a count where a set was
  meant" next time. Answer only; do not build it.
- Do not push. Do not merge. Do not rebase.

## Report back (short — the record carries the detail)

Tip sha; suite counts before/after; each item: done / not done, with the RED and the mutant bite quoted in one line
each; the R3 adjacent sweep (every caller of the symbols you changed, confirmed green); "What I did NOT do".
