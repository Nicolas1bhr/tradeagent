# U-watcher-b-fix — caller text off the owner's watcher page, and citations that fail closed (seat A, 2026-10-24; from the Codex second pass on U-org-watcher-b)

**Source:** `fleet/tmp/A-org/codex2-2026-10-23/U-org-watcher-b-review.md` (Codex `gpt-6.1-sol`, second pass of U-org-watcher-b, landed 4b2af1e7 / branch tip 19979487). Seat A triaged two findings real and in scope here; the third (LedgerAuthor / `Backtests.RoleOf` refusals unrecorded, GatewayPipeServer.cs:1457) is a CARD, not this unit.
**Model/effort:** opus/high (the agent boundary: agent-supplied text reaching the owner's private line; evidence integrity). No rung.

## Protections touched, and how each is kept
- INBOX-001 / the watcher's brief: no agent-supplied string reaches the owner's page. The page shows measured values the app produced, never text a caller sent.
- EVID-001 / the honesty rule: a citation is shown as verified only when its verification RAN and passed; a failed read is shown as NOT VERIFIED, never as resolving.
- The watcher stays invisible to the organisation (ORGANISATION § 9) — nothing here adds a reachable surface.

## Items (one commit each; each red-first with its settling check quoted RED then green; ONE watched mutant per guard)
1. **Caller text never reaches measured findings** — `src/TradeAgent.App/OversightView.cs:97` renders measured values verbatim, including `RefusalsDetector`'s `session`, copied from the request. Fix at the source (the detector records an app-made value or a digest/length, never the caller's string) or at the render (only app-typed fields render). Settling check: a wrong-version hello carrying a sentinel instruction in `session`, sweep, assert the page's findings exclude the sentinel. Sweep the other detectors for the same pattern and fix any found the same way (name each in the report).
2. **Citation verification fails closed** — `src/TradeAgent.App/OversightView.cs:395` substitutes `_ => true` on any database-read exception. A failed verification must render the citation as NOT VERIFIED (words, Theme colours). Settling check: a severe finding citing a missing log row with `OrgRecords` forced to throw; assert the banner does not show it as a verified bracketed citation.

## Gate
BUILDER-PROMPT-v2's gate. Report ≤ 20 lines appended here. Not in scope: the ledger-add refusal card; any new detector.
