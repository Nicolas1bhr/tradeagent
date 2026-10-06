# U-dataset-version-once — a re-collection deletes and replaces the raw files every earlier dataset of its pair rests on, so one offline press rejects them all for good; dataset files become write-once, a version is allocated where it is written, and a file unreadable for a moment is not rejected
**Protects:** the evidence protection (`CLAUDE.md`: evidence recorded in a home stays true about what ran) and the ledger's reproducibility claim (`S:256-261`). Runs bind `dataset_id` + `dataset_sha256`
(`Database.cs:683-684`), promotions the holdout's id + sha (`:933-934`), campaigns its id (`:824`); `BarFeed.Open` re-hashes before serving (`BarFeed.cs:126-130`). An overwrite is caught, not silent — but
the bytes are gone and the REJECTED is permanent (`S:379-382`, `:485-491`). From seat P's survey of the windows-latest red at `c6a283e0` (run 37421444199). Fresh builder, seat P; **no rung**.
**Facts (SOURCE at `a562283a`; P1/P3/P4 MEASURED by a throwaway probe in a scratch copy at `a562283a`, macOS; `S`/`M`/`C` = `src/TradeAgent.Core/Db/DatasetStore.cs`, `src/TradeAgent.Provisioning/{MarketDataService,CandleSourceClient}.cs`).**
- The red (`DataLicenceTests.cs:168`, "v2"/"v1"; the log holds only the assertion). `M.Rebuild` returns a version not `NextVersion`'s only on its REJECTED return (`M:131-134`); `NextVersion` (`S:287-292`) runs on
  the one connection (`Database.cs:11`, gate `:17`, `Pooling = false` `:28`) and counts the row `Newest` just found, so ≥ v2: `Checked` rejected the fresh v1 — DEDUCED. Which file — INFERRED: one home per test
  process (`tests/Shared/TestEnv.cs:40-47`; `Paths.cs:9`, `:68`); `DataLicenceTests` (`:599`) and `DatasetLedgerTests` (`:20`) both collect `BTCUSDT` into `binance/BTCUSDT/1m/` (`BinanceArchive.cs:121-125`),
  each in its own ledger (`TestEnv.cs:65`) naming `v1`, and the latter flips a raw byte (`:94-95`) and appends to `v1.csv` (`:154`); `CandleSourceTests.cs:26-32` records this trap, paid once. P1 (two ledgers,
  one home, one pair, 2.1 s apart): `samePath=True sameRawPath=True`, raw sha `81e11d3d…` became `6f742e6d…`; A's `Rebuild` → `version=v1 state=REJECTED`, "…no longer matches the hash recorded for it", persisted.
- REACHABLE in the product: `FetchPeriodAsync` deletes the raw file at its per-month path before asking anything (`C:66-69`), again on a failed download (`C:112`, `:121`), and replaces it (`Downloader.cs:250-251`);
  every dataset of the pair records that path and hash (`S:331-342`). P4, one press with the vendor unreachable: "None of the 12 periods asked for arrived…", `rawFilesStillOnDisk=0/12`, the earlier dataset →
  REJECTED "the raw archive file for 2025-09 is no longer on disk", persisted. An online press opens the same hole month by month under any `Checked` (an agent's backtest, `BarFeed.cs:126`); a re-published month rejects it.
- Unreadable for a moment = rejected for good, in false words: `Sha256Hex.OfFile` returns null on ANY IOException (`Sha256Hex.cs:46-55`), `FirstMismatch` reads null as "no longer on disk" (`S:407-409`, `:415-417`),
  `Checked` persists it (`S:384-393`). P3: `v1.csv` held with `FileShare.None` → REJECTED "…no longer on disk…"; released → `state=REJECTED bytesStillMatch=True`.
- Not reused in one home TODAY, but unenforced: COUNT+1 in a read (`S:287-292`) before normalising (`M:104`, `:136`), recorded in a later transaction (`M:247`, `:171`); no UNIQUE on `dataset` (`Database.cs:490-514`);
  `FileMode.Create` (`KlineNormaliser.cs:345`). What holds: no `DELETE FROM dataset` in `src`; one `_collecting`-guarded caller (`SettingsView.cs:560-572`; page built once, `MainWindow.cs:470`); one process
  per home (`AppHost.cs:550-555`); `Rebuild` has no caller in `src`.
Read first: `CLAUDE.md` (evidence; "No backward compatibility while we build"); `docs/HOW-WE-BUILD.md`; `S:246-293`, `:375-423`, `:485-493`; `M:82-174`, `:217-248`; `C:57-145`; `Downloader.cs:133-252`;
`KlineNormaliser.cs:152-215`, `:335-364`; `DatasetLedgerTests.cs`; `DataLicenceTests.cs:50-71`, `:101-171`; `CandleSourceTests.cs:24-33`.
Must NOT: delete, truncate, replace or move a file a dataset row records, raw or normalised — not on a failed download, a cancel or a re-press; persist REJECTED unless a file is absent or its bytes
differ; weaken a hash comparison, the permanence of those two, or `Rebuild`'s refusal; change the normalised bytes (`CandleSourceTests`' pinned `6a5958f4…` stays green); migrate, rename or re-hash an older
home's rows or files (they keep their paths, never touched again; no shim, no rung); hide a race with a retry, sleep, platform skip or serialising `[Collection]`; wire `Rebuild` or give the agent a writer.
Items, one commit each, one-sentence messages:
1. **One pair per test (the red):** every test that collects through `MarketDataService` — `DataLicenceTests`, `DatasetLedgerTests`, `CandleSourceTests` — takes a pair of its own per test from a helper beside
   `TestEnv.NewDb` (the home, so `Paths.Data`, is the process's), valid for `BinanceArchive.IsPair` (`:45-46`) and `DataCandleSource.RequireSymbol`; `CandleSourceTests.cs:26-31`'s note moves to it.
2. **Raw evidence is write-once:** `C` downloads into a staging folder under the raw folder (the only place it deletes), hashes, and places the file at the vendor's name if free — reused if the file there has that
   hash, else at that name with the first 16 hex of its SHA-256 before the extension (same rule; other bytes there refuse the period in a sentence naming the file). Nothing placed is deleted or replaced.
3. **The version is allocated where it is written; a normalised file is never replaced:** one `DatasetStore` writer, in one `db.Write`, takes `v{max+1}` over the ledger's (pair, interval) labels read as numbers,
   skipping any n whose `v{n}.csv` exists (an orphan of a crashed write: never replaced or deleted), moves the staged file there with `overwrite: false`, and inserts; `Record` refuses a (pair, interval, version)
   the ledger holds, in words. `NextVersion` goes; `M:104-111`, `M:136-171` normalise to a staged name and call the writer. Fixtures recording two rows under one label (`DataLicenceTests.cs:58-59`,
   `VenueCatalogTests.cs:299-300`, any other the suite names) get distinct labels, each named in the report.
4. **Unreadable now is not rejected:** `FirstMismatch` tells absence and other bytes (persisted, as today) from any other failed read, which `Checked` answers REJECTED for THIS read with "…could not be read just
   now (<the exception's message>); nothing was recorded, and it is checked again on the next read", without `Reject`.
Proof, RED before, quoted, in `DatasetLedgerTests` unless named: (i) collect, vendor unreachable, collect again → the first ACCEPTED, its 12 raw files byte-identical, `Rebuild` → v2 (red: P4); (ii) a month
re-published with other bytes, collect again → the first ACCEPTED and untouched, the second on the hashed name (red: REJECTED); (iii) P1's shape → neither ledger writes the other's files, A's `Rebuild`
ACCEPTED (red: P1); (iv) an unrecorded `v2.csv` present → `Rebuild` records v3, `v2.csv` byte-identical, the next collect v4 (red: overwritten); (v) `v1.csv` held with `FileShare.None` → not served, the row
ACCEPTED; released → ACCEPTED, `Rebuild` → v2 (red: P3). `DataLicenceTests` (g) green 3×. ONE mutant, quoted: staging dropped — the fetch deletes and downloads at the vendor's name, as `C:66-69` does today → (i) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed; touched classes 3×; branch CI on
all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
