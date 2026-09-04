# U14 — ADVERSARIAL-VERIFY BRIEF · round 7 (+7b, targeted) at the sha named in the dispatch

Same verifier (context intact). Sha under test = `f8a724c` + round 7 (4 commits, `bb5f53c`) + round 7b (the
keep-until-compatible-hello rule). Builder's claims: Mac **438 green** (75/111/252) at `bb5f53c`; V1 in both halves
(mismatch returns false AND `Drop` keeps `_incompatible` when our own refusal closed the connection; your MD1 RED
8/23; the repaired bridge connects); V2 gated on the whole per-writer set (MV2/MV2d RED; MV2b and MV2c each survive
ALONE because each blocks the other — the pair pins the boundary); V3 pinned as an invariant (quarantine-then-never-save
RED); a real flake fixed (`Redial` — a bridge dialling while the single pipe instance recycles); no box this round.
Round 7b (manager's rule): a refused mismatch survives ANY later disconnect until a COMPATIBLE hello, as
`_unauthenticated` is kept.

Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u14-verify-r7`, detached at the
sha; first command `git checkout -b u14-verify-r7-probes`; cherry-pick from `u14-verify-r6-probes` as useful. Work ONLY
there; the box is not yours.

## Targets (then stop)

1. **V1 both ways, on your own probes:** v2 hello → dropped within the read-loop turn, row keeps version + reinstall;
   the fixed v3 bridge connects next (your 146 ms control) and is accepted; a v2 redial is refused again; MD1 RED; the
   `Redial` fixture — does it hide a real failure (a bridge that must redial N times) or model one?
2. **Round 7b:** v2 refused → an unrelated later disconnect → row still says reinstall; a compatible v3 hello clears it;
   nothing else clears it. Is there any path by which a refused row is cleared by something other than a compatible
   hello (restart of the connector, `SwitchConnectorAsync`, a health probe)?
3. **V2's pair-pinned boundary:** reproduce MV2b/MV2c surviving alone and MV2d RED; decide whether a single test can
   pin each half (a MED if a real defect can hide in the gap between them).
4. **V3 invariant** and the F18 guard: still defensive, still true.
5. **438 green once**; your R3 per-writer harness and the F17 variants still hold at this sha.

Record `records/U14-verify-r7.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. NOT
verified by name (Windows entirely; ATAS teardown callback). Do not fix; do not push; full suite at most twice.
