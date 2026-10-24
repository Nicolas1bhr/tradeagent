# U-exec-env-fix — a cell can neither stall the database nor rewrite the API seat's canon (seat A, 2026-10-24; from the Codex second pass on U-execution-environment)

**Source:** `fleet/tmp/A-org/codex2-2026-10-23/U-execution-environment-review.md` (Codex `gpt-6.1-sol`, second pass of U-execution-environment, branch tip 0ddbbe30, landed 05dfda7d). Both findings triaged REAL P1 by seat A.
**Model/effort:** opus/high (containment: a cell's reach into the app's state and into a seat's system instructions). No rung expected; if one is needed, the lowest free at landing.

## Protections touched, and how each is kept
- Containment (ORCHESTRATION § 2.8; the activation gate): what a cell writes is data the app checks, never a lever over the app's own process or another seat's instructions.
- INBOX-001: bytes a cell wrote never become instruction. The canon a seat receives is the app's, byte for byte.
- Measurement vs claim: `material` stays scanner-written; the fix does not let a cell's file shape a material row.

## Items (one commit each; each red-first with its settling check quoted RED then green; ONE watched mutant per guard)
1. **No unbounded hash under the database lock** — `src/TradeAgent.Core/MaterialScanner.cs:383`: `WeWroteIt` hashes every byte of a cell-replaceable reference inside `MaterialStore.Observe`'s database write lock, without cancellation. Hash OUTSIDE the lock, bounded (a size cap that refuses in words — an app-written reference has a known size/hash, so a different size is "not ours" without reading it), and cancellable. Settling check: a sparse 1-TiB reference, cancel; the scan and a concurrent database read both finish promptly (a bounded wait, not a raised timeout).
2. **The API turn sends the app's canon, never the home's file** — `src/TradeAgent.AgentRuntime/ApiConversation.cs:703`: a cell can rewrite `.tradeagent/harness/CANON.md`, and a later API turn sends those bytes as system instructions; the existing refusal checks only failed rebuilds. The API turn takes its system message from the app's canon source (or verifies the file against the app-recorded hash and refuses in words on a mismatch). Settling check: build a home successfully, overwrite its harness canon, switch the position to API, assert the captured system message is only the app's original canon (or the turn is refused in words).

## Gate
BUILDER-PROMPT-v2's gate. Report ≤ 20 lines appended here. Note: U-tls-denylist (in flight) also edits ApiConversation.cs (~:757); rebase on main before CI and resolve. Not in scope: the U-org-audit-a P2 cards.
