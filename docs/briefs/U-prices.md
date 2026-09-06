# U-prices — the cost cap bites out of the box: list prices as dated data, an unknown priced high, the owner's override

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the owner never sees a terminal and never edits a
JSON file; vendor facts are data, not code), `docs/RESEARCH-REQUIRED.md`'s rule (check current official sources, not
memory), the `U-meter` section of `BUILD-STATUS.md`, `TurnMeter.cs`, `CostCatalog` and how `costs.json` is read through
`VendorFile` (as `runtimes.json` is: built-ins in code, an override file wins). `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/prices`,
branch `u-prices` from `main`. A fixer works `EmergencyPressTests.cs`; you touch no test of theirs.

**Why.** `U-meter` ships no prices, so every turn reads "unpriced" and the daily cap — half of "pay for yourself" —
is inert until someone writes a `costs.json`, which the owner will never do. The stream names no model, either.

1. **Built-in prices as dated data:** a built-in catalogue (the same shape as the `costs.json` override) per runtime id
   and model — input, cached input, output per million tokens — with `priced_at` (a date) and `source` (the vendor's
   pricing page URL), read from the vendor's CURRENT official page today and quoted in the report with the date. If a
   page cannot be read, that runtime's entry is absent and the report says NOT DONE for it; never a remembered number.
2. **An unknown is priced high, never zero:** a turn whose model the stream does not name is priced at the HIGHEST
   price in that runtime's catalogue and labelled "estimated at the highest list price — the AI did not say which
   model it used", so the cap always bites; the card, the Situation line and `trade status` carry the label. RED first
   (a turn with no model → unpriced, the cap never reached) → GREEN; mutant (the estimate → 0) → RED.
3. **The owner's override in the window,** on the Safety page beside the cap: the price per million tokens in and out
   as two numbers with the built-in shown as the default and its date; a HIGHER price saves at once, a LOWER one asks
   twice (it widens what the cap allows), the Safety page's rule. Saved in `Settings`, wins over the built-in, and the
   card says "priced by you". RED first (a lower price saved in one press) → GREEN; mutant → RED.
4. **The override file stays** for the engineer (`costs.json` beside `runtimes.json`), and the guide says what the
   numbers are and where they came from.

Yours: `TurnMeter.cs`/`CostCatalog`, the built-in catalogue, `Settings` in `Trading.cs`, the Safety page, the AI card's
cost line, the Situation line, `docs/USER-GUIDE.md`, `docs/RESEARCH-REQUIRED.md` (a row per source read), tests. Commit
per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file,
one project at a time, nothing else running; names vs `main` 0 removed. No box, no UI photograph: say so.

## Report — append here, commit it, ≤20 lines: tip sha; the prices and their sources with dates; per item RED→GREEN→
mutant quoted; gate counts; NOT done. Verified or NOT VERIFIED, nothing in between.
