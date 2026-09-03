# U14 — ADVERSARIAL-VERIFY BRIEF · round 5 (targeted on the bounce) at `6a40fa7`

You are the same verifier as round 4 (context intact, §9.3). Sha under test **`6a40fa7`** = `e22eec6` + 14 commits
(the round-5 bounce: `briefs/U14-r5-bounce.md`). Builder's record: `records/U14.md` "## Round 5"; it claims 417 green
(75/111/231) on the Mac AND on the Windows box, the four adapter hunks compiled on the box against real ATAS
(`5 Warning(s) 0 Error(s)`), one RED + one biting mutant per finding, and TWO survived mutants declared unreachable:
MF4b (`_loaded` ordering once `Parse` validates) and MV9 (`Trouble`'s `EnsureRecovered` after the class fix).

Worktree: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u14-verify-r5`, detached at
`6a40fa7`. First command: `git checkout -b u14-verify-r5-probes`; cherry-pick what you need from
`u14-verify-r4-probes` (your round-4 probes) if they still apply. Work ONLY there. Toolchain as before; box OFFLINE
for you (the builder's Windows run is a claim you read, not repeat).

## Targets (then stop)

1. **Your two HIGH classes from Codex, as now implemented:** the lifetime lease (F1) — B refused with A alive and no
   overlapping call; A killed as a REAL process → B acquires; your MV2 mutant now RED; what happens to the lease on
   `StopBridge` (the builder says the runtime release is NOT done — decide whether that is a finding and at what severity).
   The legal-transition rule (F3) — Codex's X/Y/Z-at-cap case, your swap-under-cap case, and the both-directions half (a
   legitimate one-trim rewrite at cap still adopted).
2. **F8: a temp is never a new claim.** The rewritten test at the old line 1800; a refused submission's temp after a
   restart; an `Identified` temp for a committed claim still adds its broker id. Name the F8 residual (rename throwing
   after the replace landed) and check the direction the builder states.
3. **F4/F13 anchors:** `records:[null, A]` → unreadable, write refused, original bytes intact; corrupt committed bytes +
   a temp with the corrupt fingerprint → no adoption. Then attack the two survived mutants: is MF4b really unreachable
   (construct the path or prove it closed), and does MV9 have an observable effect anywhere (`Describe()` call order
   included)?
4. **The class fix "degraded = unresolved SAFETY lines":** RESOLVED written as a safety event survives quota; a
   quarantine warning no longer degrades; a lost claim still does. Both directions.
5. **F9 partial:** the builder fixed events from a v2 peer but "refuted the disconnect with a code reason" and flagged an
   adjacent "unproved hello" peer — read both, run the check, and rule (finding or refuted, with output).
6. **Re-run the round-4 harness on `6a40fa7`:** three real processes × concurrent claims → durable / lost / phantom /
   who refused whom (lock vs CAS — V6). Numbers.

## Method and record

Red-first refutations, mutants (commit first, `cp` restore, `touch`), both directions. Record:
`/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14-verify-r5.md` (MAIN worktree
path; no git there) — checkpoint per target; final line `VERDICT: PASS | PASS WITH LOW | FAIL — nH/nM/nL`. NOT verified
list by name (Windows, ATAS runtime refusal of F5, the box run). Banned words as before. Do not fix. Do not push. Full
suite at most twice. Report the verdict line, findings one line each, and the record path.
