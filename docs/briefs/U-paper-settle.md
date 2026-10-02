# U-paper-settle — one read catches a paper book up to the newest forward bar however far behind it starts, so its first order is priced rather than refused as stale
**Arrow closed:** a paper book that starts behind its forward ledger — `U-runner-forward`'s NOT-done line (`BUILD-STATUS.md`:7287-7289). **Protects:** the quote gates (a stale paper
price stays refused) and the fills (none lost, duplicated or moved off its bar). **Today (SOURCE at `8a51a16`, read by the survey leg; item 2 added by seat A from its finding; NOT
runtime-verified):** `SettleAsync` makes ONE `SinceAsync` per symbol per call (`PaperConnector.cs:463-476`, read `:471`), and every SDK call settles first (`:233-425`). The app's
`ForwardBarSource` makes one `ForwardBarStore.Since` at its default limit, `DatasetReader.MaxBars` = 10,000 (`ForwardBarSource.cs:45`; `ForwardBarStore.cs:227-245`), though `Since`
expects its caller to ask again (`:222-225`). A fresh book's watermark is `MinValue` (`PaperBook.cs:164-165`): its first settle takes the ledger's OLDEST 10,000 bars, N bars need
⌈N/10,000⌉ settles, and meanwhile the quote is that page's last close stamped at its close (`PaperConnector.cs:263-271`), stale by N − 10,000 minutes, so a market order is refused
`MARKET_DATA_UNAVAILABLE` "no price newer than 30s" (`TradingGateway.cs:2754-2760`). No fill is lost or duplicated (pages join on the open-time key; `PaperBook.cs:118,404`) and a
new market order never fills on a backlog bar (`PaperConnector.cs:509`), but a WORKING order whose bar is past the first page fills on that bar a read late. Reachable: the collector
runs on any platform, live bars on by default (`AppHost.cs:543-552`; `Trading.cs:376`), so a first switch to paper after a week of collecting starts a page behind (a connected book
follows the collector's 1,000-bar looks, `ForwardBars.cs:65`). `EveryBarSince` (`ForwardRuns.cs:568-581`) is private to the Gateway, which the paper side does not reference, and
`MemoryBarSource` answers everything (`IPaperBarSource.cs:85-95`): no paper test has met a page. **Observable result:** a fresh book over 10,005 stored minutes, read once, quotes
minute 10,005's close at its close and the gateway's first market order on it passes the 30 s gate; a WORKING order whose bar is past the first page fills on it in that read. **No
schema change.** **Cost, as the runner states its own:** memory O(one page), at most 10,000 bars held (≈ 590 B a bar, ≈ 6–7 MiB) whatever the backlog, where the runner's is O(age);
time O(backlog) in that one settle — per bar a read of every order (`PaperConnector.cs:506`), the watermark read and two meta upserts, each its own `synchronous=FULL` commit
(`PaperBook.cs:68,179-188`) — today's total work, in one read instead of ⌈N/10,000⌉. Every connector call waits on `_settling` meanwhile (`PaperConnector.cs:94,466`); the 5 s
`WorstCaseOperationPath` (`:121-125`, feeding `DispatchStrandedAfter`, `TradingGateway.cs:2496`) bounds neither this nor one page today.
Read first: `CLAUDE.md`; `PaperConnector.cs`; `PaperBook.cs:139-202`; `IPaperBarSource.cs`; `ForwardBarSource.cs`; `ForwardBarStore.cs:200-245`; `ForwardRuns.cs:547-581`;
`CONTRACTS.md:113-175,2970-2983`; `PaperBookTests.cs:66-111`; `PaperSettlementTests.cs:18-75`; `PaperGatewayTests.cs`.
Items, one commit each, one-sentence messages:
1. `SettleAsync` reads again from the watermark each page ended at until a read brings nothing past it — `EveryBarSince`'s pattern, keyed on the book's own watermark as a connector
   cannot know a source's page size — checking for cancellation before each read; nothing filtered, caught or held across calls; `Apply`, the stamp and the friction untouched; a later page
   that throws propagates with earlier bars settled; the docs of `IPaperBarSource` (`:16-20`) and `SettleAsync` (`:450-462`) say all this.
2. `PaperBook.MarkSettled` writes the watermark and the close in ONE transaction: today two commits (`PaperBook.cs:185-186`), so a kill between them quotes bar N−1's close stamped
   at bar N's close — a stale price that reads fresh — and a catch-up pays both commits per bar.
3. Measure one settle over 50,000 bars through the real `ForwardBarSource` on this Mac (a temporary test under `suite.sh`, not committed: time, MiB, swap state); `CONTRACTS.md` "The
   paper connector" gains the catch-up paragraph (one read, O(backlog) time, O(page) memory, the figure, the 5 s note); `USER-GUIDE.md:186-200` a line.
Red-first tests (paging doubles raise no `BarClosed`, so only reads settle): (a) `A_fresh_paper_book_over_more_than_one_page_of_forward_bars_prices_its_first_order_at
_the_newest_bar` (Integration, `PaperGatewayTests`: the real `ForwardBarSource`, 10,005 minutes in one `Append` as `Rig.Minutes`, health refreshed BEFORE seeding, one injected clock
5 s past minute 10,005's close, the DEFAULT 30 s `MaxQuoteAge` — `ForwardRunnerTests.cs:191` widens it, hiding this: a market `PlaceAsync` WORKING, the quote minute 10,005's; red on
base: `MARKET_DATA_UNAVAILABLE`, one settle stops at minute 10,000); in `PaperSettlementTests` (rig `:52-61` taking any source): (b)
`A_working_order_fills_once_on_its_own_bar_past_the_first_page_in_one_read` (eight bars, three a page, a limit first touched on bar 7: one read, one fill there at its level, a
second read still one; red on base); (c) `A_paged_catch_up_reads_until_nothing_is_past_the_watermark_and_no_further` (four reads, quote bar 8; red on base: one read, bar 3); (d)
`A_caught_up_book_reads_its_source_once_when_nothing_is_new` (guard, green on base); (e) `PaperBookTests.cs:75-111` stays green (a re-serving source ends the loop, no second fill);
(f) `A_failure_between_the_watermark_and_the_close_leaves_both_as_they_were` (a fault at the second write through an internal seam, or the reason none exists; red on base). All
traced by reading, NOT run: the main checkout's Release build predates `7ec88cc`. Mutants to watch red and quote: (i) the loop removed ⇒ (a) red; (ii) the stop keyed on a short page
(`bars.Count < DatasetReader.MaxBars`) instead of the watermark ⇒ (c) red; (iii) the two writes back in two commits ⇒ (f) red.
Seen, not in this unit: no other single-page read on a paper path (`GatewayPipeServer.cs:2753` refuses past the cap; `ForwardBarCollector.cs:297` reads what it stored).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
