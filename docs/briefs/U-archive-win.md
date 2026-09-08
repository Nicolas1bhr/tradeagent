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


## Report

**Gated tip `110a77d`**, this report on top. Five commits, rebased twice (both times docs-only on `main`'s side, no conflict); `git diff --stat 1e2dc97 HEAD -- src tests` EMPTY across the second rebase. Written by a fresh fixer verifying a killed fixer's branch; every number below was re-measured here.
**MEASURED, and the marks settle it — the harness was at fault, on Windows only** (probe run 34169530998, since deleted). windows-latest: `srv got HEAD …-2026-08.zip` → `srv answering 200 zip, 268 bytes` → `153 ms srv THREW ProtocolViolationException: Bytes to be written to the stream exceed the Content-Length bytes size specified.`, no `closed` after it, and `cli THREW TaskCanceledException` at 15008 / 15001 / 15010 ms (the probe's own 15 s bound; 30 minutes in the suite). ubuntu-latest and macos-latest, same request: `srv answering 200 zip, 268 bytes` → `write returned` → `closed`, `cli 200 OK` in 29 ms and 8 ms. http.sys allows no entity body on a HEAD; the other two let it through, so nobody saw the harness sending one.
**Fixed on BOTH sides, as the brief allowed.** Harness (`FakeArchive.cs`): a HEAD is answered with headers only, and `Close()` moved out of the `try` into a `finally`. Product (`BinanceArchiveClient`, `Downloader`, `BinanceDataService`): a download nobody could complete is `Unreachable` with the reason in words — only a 404 is the vendor saying the month does not exist — and the ledger's missing-months list and the owner's sentence (`SettingsView.cs:297` shows `result.Summary`) name it as unreachable instead of unpublished.
**RED of the product change, reproduced here** by reverting the classification to `main`'s `status is OK ? ChecksumNotPublished : NotPublished`: `dotnet test …UnitTests --filter FullyQualifiedName~BinanceArchiveTests` → `Failed: 3, Passed: 7`, each `Assert.Equal() Failure: Values differ / Expected: Unreachable / Actual: NotPublished` — the never-answering server twice and the 503 once.
**ONE MUTANT, watched:** `Downloader.StatusAsync`'s `catch (OperationCanceledException)` returning `new UrlStatus(HttpStatusCode.NotFound, …)` instead of `null` — a silence dressed as a 404 → `Failed: 2, Passed: 8`, `Expected: Unreachable / Actual: NotPublished`. Restored; worktree clean after each.
**The harness fix reproduced on this Mac** (throwaway probe, outside the worktree): `main`'s shape (body written on a HEAD, `Close()` inside the `try`) → macOS accepts it, `srv write returned`/`closed`, `cli 200 OK after 0.0 s` — **the trigger is http.sys's alone and is NOT reproducible here**; that same throw injected at the same point with `Close()` inside the `try` → `cli THREW TaskCanceledException after 5.0 s` on a 5 s leash, the hang; the branch's shape (no body on a HEAD, `Close()` in a `finally`) → `srv closed`, `cli 200 OK after 0.0 s` even though the handler threw.
**Item 3, measured end to end:** `Downloader.Http.Timeout` = `00:30:00`, `BinanceArchiveClient.DefaultRequestTimeout` = `00:30:00`, `new BinanceArchiveClient(url).RequestTimeout` = `00:30:00`, injected 2 s honoured (`00:00:02`). A fake that accepts and never answers: **4.0 s**, `Unreachable`, `BTCUSDT-1m-2026-08.zip could not be asked about: it did not answer within 2 seconds`, 2 requests accepted. One test joins `Timing` (`A_server_that_never_answers_costs_seconds…`), argued at the test: 4 s floor of timer, 10 s ceiling, 2.4x.
**Gate, Release, at `1e2dc97` and re-run after the last rebase at `110a77d`:** `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)`, `0 Error(s)`, 17 project outputs, exit 0 (both tips). Unit 3× → `Failed: 0, Passed: 562, Skipped: 0, Total: 562` each of three, at both tips. Three projects to files → Unit 562 + Fault 277 + Integration `Failed: 0, Passed: 621, Skipped: 1, Total: 622` = 1460 passed, 0 failed, 1 skipped; files deleted. Names vs `main` → 1194 → 1197, **0 removed**, 3 added (the three new archive tests); Fact/Theory 1197 → 1200. Secret scan of the whole diff → clean. No trailers.
**CI, draft PR #15, run `34172964688` at `8676ba7`: all four green** — `test (ubuntu-latest)` pass 11m26s, `test (macos-latest)` pass 15m11s, `test (windows-latest)` pass 17m40s, `package` pass 4m10s. **Windows Unit assembly 1 m 40 s for 561 tests** (plus 4 s for the 1 timing test), against **30 m 48 s for 539** at `a22939d` — the thirty minutes are gone. That tree differs from this tip only by `SweepRequestIdTests.cs`, which came from `main`; this branch's own five files are byte-identical to the green sha. PR #15 closed after this report.
**NOT VERIFIED / NOT done:** the http.sys trigger itself on a Windows box — read off the runner's marks, not reproduced locally, and no box was used; Fault and Integration were NOT re-run after the last rebase (docs-only on `main`, `src` and `tests` byte-identical to the gated tree). No ATAS, no money, no real download, no vendor reached. No assertion loosened, none renamed, none removed.
