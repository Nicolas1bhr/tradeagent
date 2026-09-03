# U14 — ADVERSARIAL-VERIFY BRIEF · round 4 (targeted: items 1–6) on the builder's final sha

**Tier T1.** You are leg [2]: an independent Opus verifier. **Break it, do not confirm it.** Default to "fails" when
uncertain. The previous verifier (whose round 3 PASSED with three real processes × 80 claims → 238 durable / 0 lost /
0 phantom) is gone; its harness was lost with the scratchpad — rebuild the harness from the record's description.
Leg [3] (Codex) runs concurrently on the same sha in another worktree.

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md`.
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md` (rule 1 is the whole point of this unit).
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14.md` — the unit record incl.
   the builder's "Round 4 — item 6" section.
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u14-coid-witness-rewrite.md`.

## Where you work

- Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u14-verify-r4`, detached at the sha
  named in your dispatch message. First command: `git checkout -b u14-verify-r4-probes` so your probe commits stay
  reachable. Work ONLY there.
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`. No `timeout`; tool calls cap at 10 min;
  full suite at most twice, output to a file.
- The Windows box is OFFLINE: Windows sharing violations, the two `AtasStrategyAdapter.cs` hunks and the load-dependent
  `.tmp` assertion cannot be exercised here — list them under NOT verified.

## Targets (round 4 is targeted — these, then stop)

1. **One owner per witness (item 1).** Rebuild the multi-process harness: three real processes claiming concurrently on
   one witness directory. Expected now: exactly one owner writes; the others are REFUSED (`Submitting` false, `Trouble`
   "another writer owns this witness"), nothing lost, nothing phantom, no merge. A CAS miss is a refusal. Numbers.
2. **Protocol 3 (item 2).** A v2 bridge hello is refused by the app by the established mechanism; a v3 accepted; the
   `witness_failure` field reaches `AtasHealth.BridgeRow` DEGRADED naming the file.
3. **Sidecar never drops a safety event (item 3).** Under quota pressure a safety event is still written (rotation), while
   warnings/markers are rationed. Both directions.
4. **Readers never write (item 4).** A concurrent reader cannot adopt, quarantine, or mark a good rewrite unresolved;
   adoption/quarantine happen only under the owner's lock at startup; the writer re-reads the sidecar before RESOLVED.
5. **Probe treats RESOLVED as historical (item 5).**
6. **Item 6:** superset by MEMBERSHIP (same count, different members → not adopted); the `Identified` asymmetry pinned;
   "refused without the lock" pinned on both paths; an anchorless candidate reads as a flagged zero.

## Method

- Red-first refutation per target: write the probe/test that PASSES if the defect exists; run; quote.
- Mutants for every guard behind targets 1, 3, 4 and 6 (commit first; `cp` restore; `touch`): a guard whose test does not
  bite is a MED finding even when the behaviour is correct today. Rerun the builder's own mutants for item 6 — do not
  trust the table, reproduce it.
- Severity: HIGH = an order can reach the wire without a durable witness record, or a record can be lost/phantomed;
  MED = a guard without a biting test or a measured miss; LOW = wording/docs. ≥2 findings with one root cause → the
  CLASS (§9.10).

## Record (checkpoint as you go)

Write `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14-verify-r4.md` (MAIN
worktree path; no git there). Sha under test · per target: refutation executed (command + output tail) · mutants table ·
findings ranked with `file:line`, risk, exact fix expectation · NOT verified list · "What I did NOT do" · final line
`VERDICT: PASS | PASS WITH LOW | FAIL — nH/nM/nL`. Update after every target. Banned words: should work, looks correct,
probably, I believe, minor, trivial, static-verified. Do not fix anything. Do not push. Report the verdict line, the
findings one line each, and the record path.
