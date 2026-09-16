# U-scope-identity — a loss-line scope is (connector, account, symbol), carried on every record and read off the row, never off the shape of a key
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/REVIEW-2026-09-16.md` findings 1, 2 and 3 with their quoted probes (`P1`, `P1b`, `P1c`, `P3`, `P3b`, `C2`) and UNVERIFIED 2,
`docs/CONTRACTS.md` (`U-flatten-1` → `U-flatten-3`, `U-reopen-1/-2`: the account-only breach key is stated there as an inherited limit — this unit removes it), then
`src/TradeAgent.Gateway/LossBreach.cs:144-152,179-261` (`SymbolKey`, `ScopeOf` splitting on `:`, `LossBreachRecord`), `TradingGateway.cs:783-794,820` (`LedgerPnl` over
`_fills.Since()`), `:1958` (`OpenClosures`), `:2026` (`LatestBreach`), `:3110` (`ClosureHistory`), `:3433` (`PriorBreaches`), `:6299` (`FlattenForBreachAsync`: `account.Id ==
breach.Account` and nothing else), `Pnl.cs:112,124` (the book keyed by SYMBOL alone), `Core/Db/FillStore.cs:16-30`, `Database.cs` (`fill`, the rung ladder), the probe tests
on branch `review-probes-c` @ `58aa4fa` — they are your RED tests: bring them over (cherry-pick or copy into your unit's test classes, renamed), quote each one red on
your base, then make it green. Branch `u-scope-identity`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-scope-identity`, from `main`. **Money path**
(the loss gate, the flatten): a test RED before every guard and ONE mutant, quoted, over `RecordingConnector`. **Schema 22** (the fill's connector column). No box, no money.

**Why.** Three HIGH findings with one root cause: the loss line does not know which scope it is about. A venue-qualified symbol (`ES:H6`, `BINANCE:BTCUSDT`) or an account
id with a colon makes a closure every reader skips, so the closed scope trades; the fill ledger is scoped to nothing, so one account's profit keeps another trading past
its budget and one account's loss closes an account that never traded; a PAPER breach flattens the LIVE book after a platform switch because the record carries no
connector and no mode. One structural fix, not three patches.

1. **The scope is on the row.** `LossBreachRecord` (and every loss-line record: flatten, receipt, hold, release, valuation episode and exit) carries `Connector`, `Mode`,
   `Account`, `Symbol`, `Day` as fields; every reader — `ScopeOf`, `OpenClosures`, `LatestBreach`, `ClosureHistory`, `PriorBreaches`, `SymbolsClosedToday`, the strike count —
   takes the scope from the row after a prefix scan, never from `Split(':')`; the key's own delimiter is one no venue puts in an instrument or account name, and a name
   that contains it is refused when the key is MINTED (in words), never silently at read. RED: `P1`/`P1b`/`P1c` red on your base. Mutant (`ScopeOf` back to the split): `P1` red.
2. **A breach is bound to its platform and mode.** `FlattenForBreachAsync` refuses when the record's connector OR mode differs from the gateway it runs on — the sentence
   the account check already uses — and the killed-flatten sweep never re-runs a breach recorded on another connector. RED: `C2` (1 cancel and 1 close reach the live
   platform). Mutant (the mode check dropped): a PAPER breach on the same connector id in LIVE mode flattens.
3. **The fill ledger is scoped.** `fill` gains `connector` at schema 22 (the rung-16 rollback fixture undoing it); `LedgerPnl` reads the operating `(connector, account)`
   pair; `Pnl.Compute` keys its average-cost book by `(account, symbol)`; `trade pnl`, the Performance card and section 4 print the operating pair's figure and name it.
   A row with no connector (written before this unit) belongs to no pair: reported as "unattributed fills" beside the figure, never netted into it — the owner's to
   overrule in `CONTRACTS.md`. RED: `P3` (B's day closed by A's loss) and `P3b` (A's profit hides B's 1250 loss against 1000). Mutant (the book keyed by symbol): `P3b` red.
Not this unit: the sweep's second attempt after an exit throws (UNVERIFIED 1); the bridge; a per-symbol valuation denial.
Gate and report as `U-flatten-1`; every probe you brought over named with its new name and its red quoted.
