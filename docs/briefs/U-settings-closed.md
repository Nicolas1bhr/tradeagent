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

Branch `u-settings-closed` off `main` at `9cc3fb4`. Items are committed in the order 2 → 1 → 3,
because item 1's "an allowlist that allows NOTHING" is item 2's change.

**Item 2 — an empty allowlist allows nothing.** RED, review probe P1 lifted verbatim onto `main`'s
source and run (it PASSES, which is the defect): `AiTradingStopped : False (owner set it TRUE)`,
`InstrumentAllowlist : []   (owner set it MES)`, `InstrumentAllowed(ES): True`, `health row : UNKNOWN
''`, `activity lines : 0`. GREEN: `InstrumentAllowed` no longer begins `Count == 0 ||`; Release
`--no-incremental` 0 warnings, suite 219 + 222 + 570 = 1011, 0 failed.
