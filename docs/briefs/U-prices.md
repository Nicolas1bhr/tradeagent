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

## Report

Tip `de87316`, with this report committed on top of it. Items 1–2 were the PREVIOUS builder's; I re-read their sources and ran their tests. Everything
below is the quoted output of a command I ran, except this: NOT VERIFIED — no box, no UI photograph, nothing run on a screen, and nobody has seen the two new boxes.
**Prices re-read today, never remembered** (`curl` + parse of the rendered tables): <https://developers.openai.com/api/docs/pricing> on **2026-09-06**, standard
tier / short context — astra 10/1/**12.50**/50 · sol 4/.4/**5**/20 · terra 2/.2/**2.50**/12 · luna .2/.02/**.25**/1.2 · 5.3-codex 1.75/.175/–/14, and the other
thirteen rows (5.5 … 5-pro) figure by figure; Codex's ids from <https://learn.chatgpt.com/docs/models>, same day; `gpt-5.3-codex-spark` still has NO row (0 hits
in the HTML). All eighteen committed in/cached/out figures MATCH. **One thing did not:** the page publishes a cache-writes column for the four Daybreak rows,
DEARER than input, and the file carried none under a comment saying OpenAI does not bill them — 10.00 charged where the page says 12.50. `5151372`: RED
`Expected: 12.50 / Actual: null` (7/8) → GREEN 8/8 → mutant (drop `CacheWritePerMillion = row.Write`) → the same RED.
**Item 3 `5dfffcb`:** all eight uncommitted files KEPT — they are the item, they build and they hold; one fix, the owner-`Charge` doc was a second `<summary>`
orphaning the list-price overload's. RED (the rule replaced by a one-press save, since unchanged code has no such control to compile against) `Expected: 0 /
Actual: 1`, Failed 2 / Passed 21 → GREEN 27/27 → mutant `||`→`&&` → RED, 1 failed. **A hole I then closed — `de87316`, my judgement, revert that commit alone
to undo:** the boxes open on the rate in force, 0 where nothing is shipped, so ONE press on an untouched pair priced every turn at nothing and retired the cap
while it still read as in force; `OwnerPrice.From` refuses a zero as it refused a half pair. RED `Actual: OwnerPrice { InputPerMillion = 0, … }` → GREEN 43/43 →
mutant (input half only) → RED.
**Item 4 `b517aa7`:** `costs.json` still wins where it speaks (their `The_owners_file_wins_for_a_model…` passes); the guide names both pages, the date, the
estimate, the backwards press and the refused zero, and `RESEARCH-REQUIRED.md` gains section D — a row per source read, recording the long-context and
Codex-fast-mode tiers as deliberately not taken. Two tests pin the guide to `ListPrices`' constants: RED `Sub-string not found` 2 of 2 → GREEN 31/31 → mutant
(`ReadOn`→`2026-11-01`, guide untouched) → both RED.
**Gate.** Release `--no-incremental`: **0 Warning(s), 0 Error(s)**, 17 projects. Touched classes 3×: 68/68 each. Suite one project at a time, no other test host
alive: Unit **424** + Fault **261** + Integration **615** (1 skipped) = **1300 passed, 0 failed**. Names vs `main` (`89dccc6`): **0 removed, 32 added**.
