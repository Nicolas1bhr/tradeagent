# U14 — ROUND 5 BOUNCE · Codex round-4 review: 6 HIGH / 6 MED / 4 LOW (+ the verifier's list, appended below)

Raw output: `docs/hardening/records/codex-U14-r4.txt` (read every block in full; each carries an exact check).
Sha reviewed: `e22eec6`. **You stay the builder (§9.3); work in `u14-build` on `u14-coid-witness-rewrite`.** Findings are
INPUTS (§4.4): for each one, either (a) turn its "exact check" into a test, watch it RED, fix, GREEN, mutant; or
(b) refute it by RUNNING the check and quoting the output, one line of reason in the record. Silent dismissal is
forbidden. Two of the HIGHs are class findings — fix the class, not the instance (§9.10).

## Manager's direction on the design-level findings (decided; implement, and report where it does not survive the code)

- **F1 (HIGH, class: ownership lifecycle).** "One owner per witness" means a LIFETIME lease, not a per-call lock. Hold an
  exclusive handle on the lock file (`FileShare.None`) from the owner's construction/first write until disposal; the OS
  releases it on process death, so a crashed bridge does not strand the witness and a live second instance is refused on
  every call, not only when it overlaps one. Codex's check (A alive after `TA-A`; B submits `TA-B` with no overlap → B
  false, "another writer owns this witness") is the acceptance. Prove the both-directions half: after A is disposed or
  killed (a real process), B acquires. Say what happens on macOS vs Windows for the handle semantics; what you cannot
  prove on Windows goes under NOT verified.
- **F3 (HIGH, class: recovery validates shape, not a legal transition).** Replace the membership/trim exemption with the
  transition rule: a candidate is legal only if it preserves every committed record except at most the ONE oldest record
  removed by a trim at cap, adds at most what one rewrite adds, and contains no duplicate ids. Codex's check (A/B/C at
  cap 3; candidate X/Y/Z with correct predecessor → must NOT adopt, A/B/C retained) plus the builder's own swap-under-cap
  test both stay RED-then-GREEN.
- **F4 (HIGH).** `Parse` validates semantically (version, generation, non-null records and elements, no duplicate ids);
  `_loaded` is set only after a successful `Take`; a failed parse is UNREADABLE (flagged, `io:degraded`) and every write
  is refused while it stands. Codex's `records:[null, A]` check is the acceptance.
- **F8 (HIGH, class: a failed-submission temp is indistinguishable from a failed-acknowledgement temp).** Rule: **a temp
  is never adopted as a NEW claim.** Since round 2 the adapter refuses the order when `Submitting` returns false, so a
  temp that was never renamed is by contract a submission that did not happen; adopting it manufactures a phantom. A temp
  may only ADD acknowledgement information (`Identified`) to a claim that is already in the committed file. The test at
  `CoidWitnessTests.cs:1800` that expects a refused claim to reappear after restart pins the wrong behaviour — rewrite it
  to pin this rule and say so in the record. Edge to state explicitly: a rename that threw after the replace landed
  leaves a committed claim for an order the adapter refused — name it as the residual and its direction (a claim without
  an order, not an order without a claim).
- **F13 (HIGH).** An unparseable committed file is NOT an anchor: `DescendsFrom` requires BOTH the generation step and
  the predecessor fingerprint of a PARSED anchor; corrupt committed bytes permit no adoption and read as unreadable.
- **F5 (HIGH, money path, adapter).** `ClosePosition` (operator close-all) must call `_witness.Submitting` BEFORE
  `trading.ClosePosition` and refuse ("nothing was submitted") on false, like `Place`. This is a third adapter hunk that
  compiles only against ATAS. **The box is reachable now** (Tailscale up): read `tools/README.md` and use
  `tools/win-push.sh` / `tools/win-run.sh` to build the bridge project ON THE BOX against the real ATAS assemblies and
  paste the build output; do not deploy the DLL, do not touch the installed app or ATAS. If the tooling does not allow a
  build of your branch there, report exactly what blocked it and leave the hunks under NOT compiled (F16 stays open).
- **F2 (MED).** Readers never write — if an ordinary reader acquires ownership or mutates artifacts, that contradicts item 4;
  fix or refute with the executed check.
- **F7 (MED).** RESOLVED is a safety marker and is never rationed; quota exhaustion must not resurrect degraded after restart.
- **F9 (MED).** A protocol-v2 bridge is refused as a CONNECTION, not only for RPCs — no trusted events from a refused peer.
- **F6, F11, F14, F15 (tests).** A test that cannot fail is removed or made to bite; quote the mutant for each.
- **F10, F12 (probe/support package).** Fix or refute; LOW/MED batch.
- **F16 (MED).** Closed by the on-box build of F5 if it succeeds; otherwise stays open and named.

## Process

Commit per finding; no `Co-Authored-By`; commit before mutants, `cp` restore, `touch`. Append `## Round 5 (build record,
<date>)` to `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U14.md` AS YOU GO — a
table `finding · real/refuted · RED · GREEN · mutant · commit`. Targeted gates per finding; `dotnet build TradeAgent.sln` +
FULL suite once at the end with counts pasted. §9.9: for F8 and F13 answer whether a property test over
(committed, temp) shapes could catch the class. Report: tip sha; the table in one line per finding; suite counts;
on-box build result; "What I did NOT do".

## Verifier findings (appended by the manager when leg [2] reports)

_pending_
