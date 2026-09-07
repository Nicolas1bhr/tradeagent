# U-archive-win — the archive test that read "not published" on windows-latest after a thirty-minute Unit suite
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph, "The fresh-fixer rule"), `CLAUDE.md` (rule 3 on `IAtasAdapter`: a
timeout is never a definite answer — the same rule holds for a download), the `## 2026-09-08 — U-data-binance landed` section
of `BUILD-STATUS.md`. Branch `u-archive-win`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-archive-win`,
rebased onto `main` first. No assertion loosened; product code only where the product is wrong; no box, no ATAS, no money.

**The red.** CI run 34167309186 at `a22939d` (the `U-data-binance` merge), windows-latest only, ubuntu and macos green:
`TradeAgent.Tests.Unit.BinanceArchiveTests.A_month_with_no_sidecar_at_all_is_n…` → `Expected: ChecksumNotPublished /
Actual: NotPublished`, and that Unit run took **30 m 48 s** for 539 tests where the other runners take about a minute —
the shape of ONE request hanging until `Downloader.Http`'s 30-minute timeout (`Downloader.cs:89`) and the client then
classifying the outcome as "not published". Every archive test runs against the loopback `HttpListener` in
`tests/TradeAgent.UnitTests/FakeArchive.cs` (the `DownloadPartBindingTests.cs:35-90` pattern, which IS green on Windows).

1. **Measure before touching anything:** a draft PR with the test instrumented on all three runners — a mark per request the
   fake server receives (path, method, the answer it sent) and per call the client makes (URL, elapsed, status or
   exception), so the report says which request hung, on which side, for how long. Quote the marks per runner. Look at:
   a listener prefix of `127.0.0.1` versus a client resolving `localhost` to `::1` first on Windows; a handler that
   serves one request and stops; a `HEAD` the Windows listener answers without closing; the `.CHECKSUM` path.
2. **Then fix the right thing, once.** If the harness: fix `FakeArchive.cs` so it answers every request the client makes,
   on every platform, and keep every assertion byte-identical. If the product: a client that turns a timeout or an
   exception into `NotPublished` is reading a guess as an answer — it must surface a distinct outcome (`Unreachable`, the
   exception's words) that the Settings section and the ledger record as such, never as "the vendor has no such month";
   RED-first test and one watched mutant, and that is the headline of the report. Both may be true; fix both.
3. **The thirty minutes cannot recur:** whichever side hung, a test that talks to a loopback server gets a client timeout of
   seconds, not the production thirty minutes (an injectable timeout on the client, defaulting to production's value), so
   a future harness fault fails in seconds with its reason rather than eating the runner's budget. RED: the hang reproduced
   under the fake (a handler that never answers) takes longer than 10 s → after the fix it fails in under 10 s with the reason.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Unit project 3× and the three test projects
in Release to a file → 0 failed; test names vs `main` → nothing removed; the draft PR's three runners green at the tip with
the Windows Unit suite back under two minutes (quote the durations). Commit per item, one sentence, no trailers. Append
`## Report` (≤20 lines): tip sha, the gate counts pasted, the marks per runner, what was fixed on which side, the RED and
mutant if the product changed, the run ids and durations, what you did NOT do. Close the draft PR after the report.
