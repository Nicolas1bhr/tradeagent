# U-batch-2 — the second review's MED and LOW: a download is what it says it is, and the material ledger measures

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the inbox is data; measurement and claim in different
tables; the no-terminal rule), findings 4, 5 and 6 of `docs/REVIEW-2026-09-05b.md` with P4a–c, P5a and P5b quoted.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/batch-2`, branch `u-batch-2` from `main`. Other builders work
`TradingGateway.cs` and the App's Safety controls; you touch neither. Probes to lift are on branch `review-probes-b`
(`tests/TradeAgent.UnitTests/ReviewProbes2Unpack.cs`, `ReviewProbes2Ledger.cs`).

1. **A `.part` is bound to what it is a part of** (`Downloader.cs:62/70/105`). The resume file is named after the URL
   and the expected length and discarded when either differs; a checksum-less caller makes an explicit, recorded
   decision — a `DownloadUnverifiedAsync` (or a required `Unverified` argument) that writes one activity line the owner
   can read ("installed <thing> without a checksum: the vendor publishes none") instead of a silent skip. One structural
   fix for ATAS (`Prerequisites.cs:112-118`), Node (`NodeRuntime.cs:83-88`) and the runtime archives. RED first: lift
   P4c → GREEN (a stale part is never resumed into a different download); mutant (the binding deleted) → RED.
   The ATAS hash itself is Nicolas's decision, not this unit's: leave the call site able to take a pinned hash.
2. **`material` origin is a measurement** (`MaterialScanner.cs:25/67`, `Paths.cs:19`, `CliAgentRuntime.cs:309`). (a) The
   working directory handed to the runtime no longer contains the inbox — the agent's tree is a sibling of
   `workspace/inbox`, not its parent; `AGENTS.md` and the guide say where the owner drops things, unchanged for the
   owner. (b) `Inbox` origin is asserted only when the scanner can attest no agent process ran between the previous
   scan and the sighting (the runtime's process lifetime is known to the app); otherwise the row carries a new origin,
   `InboxUnattested`, and the Inbox view says so. RED first: lift P5a → GREEN (the agent's file is not `Inbox`); mutant
   (the attestation deleted) → RED. Both directions: a file the owner drops while no agent runs is still `Inbox`.
3. **A removed row is never un-removed** (`MaterialStore.cs:45/92`): a sighting after `removed_at` is a new version and a
   new row (or the sha is cleared so the next pass re-hashes) — the ledger never states a hash for bytes it did not
   hash. RED first: lift P5b → GREEN; mutant → RED. Two Codex claims of the same table, each RED first or refuted:
   F17 — `ByShaPrefix` (`MaterialStore.cs:106`) feeds an agent-supplied prefix unescaped to `LIKE … LIMIT 1`, so `%`, `_`
   or a one-character prefix attaches a note to an arbitrary row: refuse non-hex and ambiguous prefixes; F19 — hashing
   never revalidates `(size, mtime)` at hash time, so an equal-length, mtime-restored swap reuses the old row.

Yours: `Downloader.cs`, `Prerequisites.cs`, `NodeRuntime.cs` and the runtime-archive callers; `MaterialScanner.cs`,
`MaterialStore.cs`, `Paths.cs`, `CliAgentRuntime.cs`, `InboxView.cs`, `AGENTS.md`, `docs/USER-GUIDE.md`; tests. No
gateway, no Safety controls. Commit per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; each class 3×;
full suite once to a file (one suite at a time on this Mac; a `Timing` test that fails while another suite runs → that
class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; RED → GREEN → mutant per item with the outputs; gate counts;
what you did NOT do. Verified or NOT VERIFIED.

Code tip `53912fb`; this report is the commit above it. Rebased onto `main` (`896be63`) first — clean, 0 behind, no conflicts.
**Kept all seven uncommitted files, discarded nothing**, after reading and running them: `Integrity` with no null (`Pinned`/`Unverified`/`PinnedOr`), the URL+length part binding,
`AtasLayout.InstallerSha256` as the owner's pin, `DownloadPartBindingTests`. Two changes inside them: `Downloader.RecordDecision` is now wired above `Connector.ConnectAsync()` (a
backend that will not connect must not decide whether the owner is told), and the test vendor answers 416 to a past-the-end range — without that the mutant hung instead of failing.
**1 — a part file is bound to what it is a part of** (`154f085`). Mutant, the binding deleted: 3 of 5 RED — `Assert.Equal() Failure: Collections differ / Expected: [65,45,83,…] /
Actual: [83,84,65,76,69,…]` (that is `STALE`), `the download of ATASPlatform.exe was refused by the server (416)`, `Assert.EndsWith() … Expected end: "-44"`. GREEN 5/5 ×3. Resuming
still resumes; a checksum-less install writes one activity line carrying the vendor's reason; ATAS's hash stays the owner's decision as a null `installerSha256` in `atas.json`.
**2 — `Inbox` is a measurement** (`08a9e56`, `88c7e81`). The agent's home is `workspace/agent`, a sibling of `workspace/inbox` (unchanged for the owner; an older install's folders
are moved on the next start). Origin is `Inbox` only when `AgentPresence` attests no agent process was alive since the last COMPLETE pass, else `InboxUnattested`, and the Inbox
page and USER-GUIDE say so in the owner's words. Mutant, the attestation deleted: `Expected: InboxUnattested / Actual: Inbox` ×2 — P5a exactly. Second mutant, the window advanced
on a truncated pass: same message ×1. GREEN 6/6 ×3.
**3 — a removed row is never un-removed** (`8ca2ad0`). Schema 4 adds `material.version`: a sighting after `removed_at` is a new row with no hash and the old row keeps its own. F17 —
`ByShaPrefix` refuses non-hex, under 4 characters, and any prefix matching two distinct hashes. F19 — `(size, mtime)` re-read from the OPEN HANDLE before and after the bytes. Three
mutants, one per guard: `Expected: 2 / Actual: 1` rows; `Assert.Throws() Failure: No exception was thrown` ×2; `Expected: 0 / Actual: 1` hashed. GREEN 6/6 ×3.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` with bin/obj deleted → **0 Warning(s), 0 Error(s)**. Full suite in Release, one project at a time with no other test
host on the box, to a file: **Unit 253 + Fault 261 + Integration 587 = 1101, 0 failed**. Test names vs `main`: **0 removed, 17 added**.
NOT done: no Windows box, no real ATAS, no money, no UI run; touched neither `TradingGateway.cs` nor the Safety controls; did not pin an ATAS hash; left `DashboardView`'s "Open the
AI's folder" on `workspace/` (another builder owns that file); did not close the DDL's in-place blind spot (size AND mtime both preserved), which needs unconditional hashing;
ledger rows at the agent's old paths are re-recorded under `agent/` by the next scan, not migrated in the DB.
