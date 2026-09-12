# The strategy language, v1

A strategy is a **data file with an expression grammar**, not a program in a programming language: it
declares constants, indicators and ordered rules, and the app parses it into a typed, frozen
`StrategyProgram` (`src/TradeAgent.Core/Strategy/`) identified by hash. Statements, functions, loops,
recursion, imports, clocks, randomness, files, network, model calls, a second instrument, shorting,
leverage and pyramiding are not switched off — the AST has no node for them (`StrategyAst.cs`) and the
grammar has no word for them. Parsing is **total**: every text yields a program or a refusal naming
the line, and nothing throws.

## A program

One declaration per line; `#` starts a comment; blank lines are ignored. Declaration ORDER does not
matter except among rules. Keywords and names are read case-insensitively and canonicalised to lower
case; an instrument symbol is upper-cased.

```
declaration := "instrument" SYMBOL | "timezone" ZONE      # one instrument, required; zone default UTC
             | "const" NAME "=" (NUMBER | "true" | "false") | "indicator" NAME "=" indicator
             | "size" ("fixed" | "capital_fraction" | "risk_fraction") value      # required
             | "stop" ("fixed" value | "percent" value | "atr" value value)
             | "target" ("fixed" value | "percent" value) | "max_hold_bars" value
             | "weekdays" DAY ("," DAY)* | "session_exit" CLOCK    # mon tue wed thu fri sat sun
             | "entry_window" CLOCK "-" CLOCK | "opening_range" CLOCK "-" CLOCK   # windows: up to 4
             | ("exit" | "entry") "when" expr             # exits first, at least one entry
indicator   := ("sma" | "ema" | "rsi" | "highest" | "lowest") "(" SERIES "," value ")"
             | "atr" "(" value ")" | ("opening_range_high" | "opening_range_low") "(" ")"
value       := NUMBER | NAME     # a declared number constant
SERIES      := "open" | "high" | "low" | "close" | "volume"
CLOCK       := HH ":" MM         # 24-hour, in the declared zone
```

Rules are evaluated **in declared order, every exit before every entry**; an exit written below an
entry is refused rather than reordered. A program is **long or flat**: `entry` buys, `exit` flattens,
there is one position, and no rule takes a side.

## Conditions

```
expr    := or
or      := and ("or" and)*                 and     := not ("and" not)*
not     := "not" not | cmp                 cmp     := sum (CMPOP sum)?
sum     := product (("+" | "-") product)*  product := unary (("*" | "/") unary)*
unary   := "-" unary | primary             CMPOP   := "<" | "<=" | ">" | ">=" | "==" | "!="
primary := NUMBER | "true" | "false" | "(" expr ")" | cross | ref
cross   := ("crosses_above" | "crosses_below") "(" expr "," expr ")"
ref     := (SERIES | NAME) ("[" INT "]")?  # close, close[1], fastma[2]
```

Two types, `number` and `boolean`: arithmetic and comparison take numbers, `and`/`or`/`not` take
booleans, and a rule condition must be a boolean. `crosses_above(a, b)` is true on the bar where `a`
is above `b` and was at or below it on the bar before; `crosses_below` is the mirror. `x[k]` is the
value `k` closed bars ago and `x[0]` is `x`; a constant has no history.

## Indicators — semantics and initialisation

Every series is over **closed** bars only, in the order the dataset publishes them; a missing bar is
missing and nothing is filled in. Prices are decimals, exactly as the vendor published them.

| Indicator | Value | Initialisation | Bars needed |
|---|---|---|---|
| `sma(s, p)` | mean of the last `p` values of `s` | none | `p` |
| `ema(s, p)` | `v*a + prev*(1-a)`, `a = 2/(p+1)` | `prev` seeded with the `sma` of the first `p` values | `p` |
| `rsi(s, p)` | `100 - 100/(1+avgGain/avgLoss)`, Wilder's smoothing | the first `p` changes' mean gain and loss; a zero average loss reads as 100 | `p+1` |
| `atr(p)` | Wilder's mean of `max(high-low, abs(high-prevClose), abs(low-prevClose))` | the mean of the first `p` true ranges | `p+1` |
| `highest(s, p)` / `lowest(s, p)` | largest / smallest of the last `p` values | none | `p` |
| `opening_range_high()` / `opening_range_low()` | highest high / lowest low of the bars closing inside the `opening_range` interval | resets each session; undefined until that interval's first close | `1` |

`atr` takes no series, because a true range is over high, low and the previous close; an indicator
reads a bar series, never another indicator. These semantics are versioned in `StrategyVersions`
(`language`, `indicators`, `calendar`), and moving one re-identifies every program.

## Limits — `StrategyLimits`, one place

8192 source bytes · 200 lines · 240 characters a line · 32 characters a name · 32 constants ·
16 indicators · 20 rules · 200 expression nodes · 8 levels of nesting · history depth 20 · 500 bars
of period and of warm-up · 4 entry windows · 10000 holding bars · fixed quantity 1000000 · sizing
fraction 1 (above one is leverage) · 100 percent and 100 ATR multiples. Zones a program may name:
`UTC`, `America/New_York`, `America/Chicago`, `Europe/London`, `Europe/Berlin`, `Asia/Tokyo` — data
rather than an OS lookup, so a program means the same thing on every machine that hashes it.

## Refusals

A refusal reads `line 7: <reason>`, or `program: <reason>` when what is wrong is the ABSENCE of a
declaration — no instrument, no size, no entry rule, nothing that can ever close the position. A
period at or below zero, an undeclared name, a type mismatch, a second `size`, risk sizing with no
stop to measure risk against, and every limit above are refusals rather than warnings.

## The three day-one programs

`docs/COUNCIL.md` requires the language to express these three on day one. They are the fixtures under
`tests/TradeAgent.UnitTests/Strategies/`, byte for byte, each pinned to a `StrategyId`.

`ma-crossover.strategy`
```
# A moving-average crossover with fixed sizing and a stop.
instrument BTCUSDT
timezone UTC
const fast = 20
const slow = 50
indicator fastma = sma(close, fast)
indicator slowma = sma(close, slow)
size fixed 1
stop percent 1.5
exit when crosses_below(fastma, slowma)
entry when crosses_above(fastma, slowma)
```

`opening-range-breakout.strategy`
```
# An opening-range breakout with ATR risk sizing and a time stop.
instrument BTCUSDT
timezone America/New_York
const atrperiod = 14
const atrmultiple = 2
const riskfraction = 0.01
indicator rangehigh = opening_range_high()
indicator rangelow = opening_range_low()
indicator truerange = atr(atrperiod)
size risk_fraction riskfraction
stop atr atrmultiple atrperiod
max_hold_bars 120
weekdays mon,tue,wed,thu,fri
opening_range 09:30-10:00
entry_window 10:00-15:30
session_exit 15:55
exit when low < rangelow
entry when close > rangehigh
```

`rsi-mean-reversion.strategy`
```
# An RSI mean reversion with a profit exit and a maximum holding time.
instrument BTCUSDT
timezone UTC
const period = 14
const oversold = 30
const recovered = 55
indicator momentum = rsi(close, period)
size capital_fraction 0.25
stop percent 2
target percent 1
max_hold_bars 60
exit when momentum > recovered
entry when momentum < oversold
```
