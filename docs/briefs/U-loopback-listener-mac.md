# U-loopback-listener-mac — a loopback HttpListener fixture that dies on a port collision under parallel classes while the product is green
Read `docs/HOW-WE-BUILD.md` (step 6: a red thrown by a test's own setup is a fresh fixer on top of `main`; "The fresh-fixer rule"), `CLAUDE.md`, the
`## 2026-09-15 — U-decision-hook-race landed` section of `BUILD-STATUS.md` (the same class, fixed for a different race the same day) and `BUILD-STATUS.md:3738`
(`UpdateTrustTests`, `HttpListenerException: Address already in use`, an earlier sighting of this family), then
`tests/TradeAgent.UnitTests/DownloadPartBindingTests.cs:35-112` (`Vendor`: ONE `HttpListener` instance, `Port = 18000 + Random.Shared.Next(2000)` `:44`, `Start()` retried
up to 20 times ON THE SAME INSTANCE on `HttpListenerException` `:48`, `Dispose` `:112`), and every other loopback listener in `tests/` — `FakeProvider.cs`, `FakeArchive.cs`,
`UpdateTrustTests.cs`, `SuiteReachesNoVendorTests.cs` — each with its own port choice. Branch `u-loopback-listener-mac`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-loopback-listener-mac`, rebased onto `main` first (after `U-decision-hook-race`, which touches the same file).
Test-only unless the product is wrong; no assertion loosened; no test deleted; no box, no ATAS, no money.

**The red, one sighting of this shape, two of the family.** CI run 34967255830 at `5415a8f` — a DOCS-ONLY sha — macos-latest, step "Test everything outside the timing
category", Unit 1095/1096, the test itself 8 ms:
```
TradeAgent.Tests.Unit.DownloadPartBindingTests.A_stale_part_from_another_download_is_never_resumed_into_this_one
System.ObjectDisposedException : Cannot access a disposed object.
Object name: 'System.Net.HttpListener'.
```
ubuntu and windows green on the same sha; the same code green on macos at the sha before and the sha after. Eight milliseconds is a fixture that never served a
request: the listener was disposed before the download began.

1. **Judge it first:** establish from `System.Net.HttpListener`'s managed implementation (the one macOS and Linux run) what a failed `Start()` does to the instance — the
   suspected mechanism is that a bind failure (`Address already in use`, another class's listener on the same random port among 2000) disposes the listener, so the
   fixture's retry `:48` calls `Start()` on a disposed object and the constructor throws before any request. Reproduce on this Mac: two fixtures forced onto one port
   in a throwaway test, quote the exception; then the real shape — the Unit project's loopback classes in parallel, 20 runs, quoting any red. If it will not
   reproduce, say so with what you ran.
2. **Then fix once, for the family:** one loopback port allocator for the whole Unit project (a fresh `HttpListener` per attempt is the minimum; a suite-wide allocator
   that hands out distinct ports from one random base, or a bind-then-release probe on a `TcpListener` at port 0, is the structural fix) used by every listener fixture
   named above — name each one moved to it or left alone and why. No `[Collection]` serialisation unless nothing else is honest, argued in one sentence at the test.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; the Unit project 5× → 0 failed;
names vs `main` → nothing removed. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the mechanism quoted from the
runtime's behaviour, the reproduction quoted (or the honest failure to reproduce), every fixture moved or not, what you did NOT do.
