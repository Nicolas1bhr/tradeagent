# U2d — updater fail-closed

Branch `u2d-updater-fail-closed` @ **c519966** (base 3931c10, 12 commits; messages in `commits-u2d-updater-fail-closed.md`).
Tier 2 with a Tier-1 consequence (the updater replaces the program holding the owner's open orders). Reconstructed
2026-09-03; the 603-line build record and 1125-line verify record were lost. Suite at tip: **373 green**, App builds.

## Why it exists (283d942 adversarial F14/F15, Codex F13)

The trust chain is checksum-only and unsigned, and every way of losing the checksum degraded silently to installing an
unverified 90 MB executable: BOM, TAB, renamed asset, truncated hash, empty/absent manifest all made
`ChecksumManifest.Find` return null, which `Downloader` treated as "no check". The installer asset was chosen by position.
The "no install while an order is unconfirmed" hard stop existed only on the banner button; the Settings button bypassed
it. `UpdateTests.cs:295-306` pinned "checksumless installation succeeds".

## What the branch does

**Round 1 (ddb6e74).** Missing/unfetchable/non-matching manifest → refused before any download with a readable reason;
`Downloader.DownloadVerifiedAsync` refuses a null/blank hash (the tolerant `DownloadAsync` stays for the ATAS/Node
installers); more than one installer-pattern asset → refused as ambiguous via `ReleaseFeed.Parse(..., out problem)`;
`InstallAsync` re-hashes the file immediately before `Launch`; the hard stop lives in `InstallAsync` behind a fail-closed
`Func<int>? UnconfirmedWork` (null/throw = unknown work = refuse), wired at the composition root (`AppHost.cs`).
**Round 2 (3a33571).** Hard stop re-checked before `Launch` (ADDED, not moved — moving it killed three tests);
`TradingGateway.InstallInProgress` latch consulted first in `TryAuthorizeExecution` (`UPDATE_INSTALL_IN_PROGRESS`; operator
not exempt; stays up after a successful Launch); banner renders refusals only and expires them; conflicting duplicate
manifest entries refuse; 64 KiB / 2000-line manifest cap; `UPDATE_INTEGRITY_FAILED` code; post-Launch logging never
changes control flow; `CanBeVerified` up front on both surfaces; `IsPlainFileName`; negative count fails closed.
**Round 3 (c519966).** The manifest FETCH capped at read time (`ReadLimitedAsync` reads at most 64 KiB + 1;
`ResponseHeadersRead`; declared Content-Length refused unopened; 30 s fetch timeout; linked CTS) and the latch no longer
spans the fetch — it goes up after the manifest is resolved, immediately before the download; the wiring moved into a
testable seam `Diagnostics/UpdateGatewayCoupling.Attach(gateway, updates)` run against a real gateway in tests (the
previous source-text gate was defeated by commenting the line out); Unicode-category refusal of invisible/bidi
characters in asset names; `Dismissed` clears on a changed reason; no checks after Launch; the "true return ⇒ this
process exits" invariant documented where `InstallAsync` is declared.

## Verification history

| Round | Opus verifier | Codex |
|---|---|---|
| 1 | FAIL 0H/3M/6L — guard sampled once before a 90 MB download; banner renders any Failed over the offer; hard stop excludes DISPATCHING | 2H/4M/2L — provider must count every wire-touched record; re-check before launch; duplicate hashes; uncapped manifest; wrong error code; post-Launch logging; banner hides refusals |
| 2 | FAIL 0H/2M/5L — manifest fetch uncapped + latch up during the fetch; source-text gate defeated by a comment | — |
| 3 | FAIL 0H/2M/4L — a throwing `Activity` sink on the REFUSAL path replaces the owner's reason; round 3's fetch fix is revertible with a green build (no test drives `TryGetSmallTextAsync` through `UpdateSources.GitHub`) | — |

Verifier positives worth keeping: the fetch primitive is byte-exact (65536 read, 65537 refused having pulled exactly
65537) against a real `HttpListener`; latch span verified `releaseQuery=False, manifestFetch=False, download=True,
rehash=True, LAUNCH=True`; the latch cannot be stranded (`Process.Start` null throws before `_launched`; a dying Setup
still ends in `desktop.Shutdown`); `UpdateGatewayCoupling` in `TradeAgent.Diagnostics` is correct layering (misnomer noted).

## Round 4 (briefed, NOT started — the leg was killed at its first step)

1. Wrap the refusal-path `Activity` sink like the success path (mutant ADV3-I keeps 373 green); test: a throwing sink on
   a refusal still returns the refusal reason.
2. A real test through `UpdateSources.GitHub` → `TryGetSmallTextAsync` using a per-request-dispatch `HttpListener`:
   declared-oversized refused unopened; chunked beyond the cap refused at maxBytes+1; a stalling body cut at the leash;
   a healthy manifest resolves; then the three reverts (unbounded `TryGetStringAsync`, no leash, no Content-Length check)
   quoted RED.
3. LOW batch: document/pin the astral-character refusal and the Surrogate/PrivateUse/Unassigned categories; let caller
   cancellation propagate as cancellation; guard `ReadLimitedAsync(_, int.MaxValue)` overflow; keep the one-line
   `Attach` source assertion with its disclosed limit; rename `UpdateGatewayCoupling` for what it does.
4. **Item 10, after U2c-1 merges and this branch rebases onto main:** the provider counts every record whose wire may
   have been touched (DISPATCHING, UNKNOWN, RECONCILING, or flagged) using U2c-1's store query; the doc-comment at
   `UpdateService.cs:255-265` then becomes true; test with a live unflagged DISPATCHING row → refused.

## Docs to change at integration (not this unit's files)

`docs/USER-GUIDE.md:57-60` (the failure branch: if the checksum is missing or does not match, nothing is installed and
the reason appears on the strip and in Settings), `:68-74` (a fourth thing worth knowing: TradeAgent will not replace
itself while an order's outcome is unconfirmed, and says so); `BUILD-STATUS.md` updater section (refuses when it cannot
resolve the manifest; re-checks the file before Setup; the hard stop lives in `InstallAsync`). Not verified anywhere:
`UpdateSources.Install` (Windows-only, executed by no test); any UI rendering; the release JSON fetch is still uncapped
(named, not fixed).
