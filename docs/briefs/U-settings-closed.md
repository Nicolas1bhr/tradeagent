# U-settings-closed — an unreadable settings row disarms nothing and allows nothing

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (fail closed on every unreadable input; the kill switch
is the owner's; every colour and size comes from `Theme.cs`), then finding 5 in `docs/REVIEW-2026-09-05.md` with probe
P1 on branch `review-probes` (`tests/TradeAgent.FaultTests/ReviewProbes.cs` — lift it), and `docs/hardening/PROGRAM.md`
D4 (this was the old U2c-2's first item). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`; full suite 8–12 min in Release. No box. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-settings-closed`, new branch `u-settings-closed` from `main` —
after U-gates has landed (both touch `TradingGateway.cs`; rebase onto that `main`).

**The finding, executed (P1).** `LoadSettings` (`TradingGateway.cs:214-220`) catches every deserialization failure and
returns `new TradeAgentSettings()`. The owner's row said `ai_trading_stopped: true` and `instrument_allowlist: ["MES"]`;
the gateway built over a row this build cannot parse reads `AiTradingStopped: False`, `InstrumentAllowlist: []`, and
`RiskPolicy.InstrumentAllowed(ES): True` — everything allowed, the kill switch off, no log line, no health change.

1. **Unreadable settings are the most restrictive settings.** A row that does not parse yields `AiTradingStopped =
   true`, live not activated, an allowlist that allows NOTHING, every cap at its floor; plus an engineering-log line and
   a health row in the owner's words ("your settings could not be read; trading is stopped until you review them").
   RED first: lift P1 → expect stopped and nothing allowed; GREEN; mutant (defaults returned) → RED. An unknown mode
   value is U-gates' check; call it, do not duplicate it.
2. **An empty allowlist allows nothing.** `RiskPolicy.InstrumentAllowed` with an empty list refuses, and Settings says
   "no instrument is allowed until you add one" when the owner clears it. Both directions: a populated allowlist still
   allows exactly its members.
3. **The owner recovers in the app.** The health row leads to Settings, where the unreadable row shows as "could not be
   read" and saving rewrites it. No file path, no terminal, anywhere in the sentences.

Yours: `TradingGateway.cs` (`LoadSettings`, the health row), `RiskPolicy`, `SettingsView.cs` (the sentences),
`Theme.cs` only if a token is missing, `docs/USER-GUIDE.md` (one paragraph), tests. Not yours: the authorization and
dispatch path (U-gates), the press, the pipe server, the updater. Every item: RED quoted, GREEN, one mutant watched red
(commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED → GREEN → mutant; final counts;
what you did NOT do. Verified or NOT VERIFIED.

## Report

Branch `u-settings-closed` off `main` at `9cc3fb4`. Committed 2 → 1 → 3: item 1's "an allowlist that
allows NOTHING" is item 2's change. Two builders — the first was killed by a usage limit inside item 1.

**Item 2 — an empty allowlist allows nothing.** RED: probe P1 lifted verbatim onto `main` PASSES,
which is the defect — `AiTradingStopped: False` (owner set TRUE), `InstrumentAllowlist: []` (owner set
MES), `InstrumentAllowed(ES): True`, health `UNKNOWN ''`, 0 activity lines. GREEN: `InstrumentAllowed`
no longer begins `Count == 0 ||`. Mutant (that clause put back, run by the second builder because the
first recorded none): `EmptyAllowlistTests.An_owner_who_clears_the_allowlist_stops_every_order` and
`RiskPolicyTests.An_empty_allowlist_means_nothing_is_allowed` both red.

**Item 1 — an unreadable row is the most restrictive row.** RED, the new `UnreadableSettingsTests`
over `main`'s `LoadSettings`: 6 failed / 2 passed — `CouldNotBeRead: False`, `AiTradingStopped: False`
on a row whose text is `"ai_trading_stopped":true`, `Mode: PAPER` invented from a row saying
`LIVE_LOCKED`, caps back at 1/2/6. GREEN 12/12: `TradeAgentSettings.Unreadable()` — OBSERVE, stopped,
live off, no account, caps 0, allowlist [] — the owner's raw row kept as `settings_unreadable`, an
activity and an engineering line, health PAUSED in the constructor, and `PlaceAsync` denied with 0
orders at the broker. A second RED → GREEN inside the item: an EMPTY row (not an absent one) was read
as a fresh install — 3 failed, `MaxOrderQuantity: 1`; only a row that has never been written is one
now. Mutant (`catch` → `return new TradeAgentSettings()`): 6 of 12 red.
