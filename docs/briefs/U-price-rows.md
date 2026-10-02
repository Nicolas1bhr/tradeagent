# U-price-rows — the GPT-6 models the organisation's seats need are priced from the vendor's own page, dated, not at the dearest row
**Protects:** spend accounting — a reservation is a ceiling only if it is the vendor's price, and an unpriced model must not be charged so high that the cap
stops all work (`CLAUDE.md`; `docs/ORGANISATION.md` § 10; R15 § 4.4; R18 § 1.1). **Today (SOURCE at `1275aff`, R15/R18-checked, NOT runtime-verified):**
`ListPrices.ReadOn = "2026-09-06"` (`ListPrices.cs:44`); the OpenAI table (`:79-99`) and `CodexModels` (`:115-119`) have no `gpt-6-luna`, `gpt-6-sol` or
`gpt-6.1-sol`; `TurnMeter.Reservation` (`TurnMeter.cs:881-897`) asks `CostCatalog.Reserve` for the role's model, finds no row and takes the runtime's DEAREST
row by output then input (`RuntimeManifest.cs:1091-1101`, `Highest` `:963-971`) — on `codex` `gpt-6-astra`: 1.2 M × 12.50 + 20 k × 50 = $16.00; on opencode and
the harness `gpt-5.5-pro`: $39.60 — both over the $5 cap (`Trading.cs:841-842`), although a GPT-6 Luna turn costs ≈ $0.006 (R15 § 2.2, § 3).
**Observable result:** a turn on `gpt-6-luna`, `gpt-6-sol`, `gpt-6.1-sol` (and any other model the vendor's model page lists for Codex on dispatch day) reserves
and charges at its own published rates; every figure is read from the page, never estimated from a neighbour; a model the page does not price stays absent.
**No schema change.** NOT claimed: that a harness turn on a GPT-6 model can call tools — that is `U-harness-responses` (R15 § 2.1).
Read first: `CLAUDE.md`; `ListPrices.cs` (whole — its summary is the contract); `RuntimeManifest.cs:929-971, 1006-1027, 1061-1107, 1161-1166`; `TurnMeter.cs:881-897`;
`Trading.cs:689-691`; tests `TurnMeterTests.cs:531-600`, `RequestedModelPriceTests.cs:37,40`, `HarnessCatalogTests.cs:77-92`, `MissionCostSurfacesTests.cs:374-406`.
Items, one commit each, one-sentence messages:
1. On dispatch day read `https://developers.openai.com/api/docs/pricing` and `https://learn.chatgpt.com/docs/models` (the URLs `ListPrices` names, `:47`, `:50`)
   and quote in the report every row taken. R15 § 2.1 read on 2026-10-02 (per M tokens, input / cached / cache-write / output): GPT-6 Luna 0.10 / 0.01 / 0.125 /
   0.50; GPT-6.1 Sol 2.00 / 0.10 / 2.50 / 10.00; GPT-6 Sol 2.00 / 0.20 / 2.50 / 10.00 — the page wins over this brief. A row carries the STANDARD price; a
   promotional price appears only in a comment with its end date (an under-charge is the direction that walks past the cap, `:68-70`).
2. Add the rows to the OpenAI table and the ids the model page lists to `CodexModels`; keep deprecated rows that still bill; the harness's catalogue still
   equals opencode's (`HarnessCatalogTests.cs:77-92`).
3. `ReadOn` stamps EVERY row (`:164`): move it only after re-reading every existing row that day; quote old → new for any figure that moved, and rewrite the
   figures tests pin (`RequestedModelPriceTests.cs:37,40`; the cache-write dictionary `TurnMeterTests.cs:576-582` gains each new row's figure) in place, names kept.
4. `docs/USER-GUIDE.md` (`:475`, `:1087`) and `docs/RESEARCH-REQUIRED.md` § D (`:209`) carry the new day and rows (`MissionCostSurfacesTests.cs:374-406` assert it).
5. Say in the report, with the figure, that adding cheaper rows leaves the dearest-row estimate for an UNIDENTIFIED turn unchanged.
Red-first tests: (a) `A_gpt_6_luna_turn_reserves_at_its_own_rate_not_the_dearest_row` — on `codex`: `CostCatalog.Reserve(TurnAllowance.Default, "codex",
requestedModel: "gpt-6-luna")` is 16.00 labelled `PricedAtHighestListPrice` at base and 0.16 after; the class sits in `[Collection(VendorOverrideFiles.Name)]`
like `ShippedListPriceTests` (`TurnMeterTests.cs:531`) — written FIRST and quoted red at base; (b) `Every_built_in_row_carries_the_read_on_day` (a GUARD,
green on base). Mutant to watch red and quote: delete the `gpt-6-luna` row ⇒ (a) red.
Light leg (W1): it shares only `USER-GUIDE.md`/`RESEARCH-REQUIRED.md` with `U-cost-model` and `U-key-host-pin` (other sections; rebase). Builder gates on
this Mac are serialised in W1 (three builders).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
