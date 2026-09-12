# The strategy language, v1

A strategy is a **data file with an expression grammar**, not a program in a programming language: it
declares constants, indicators and ordered rules, and the app parses it into a typed, frozen
`StrategyProgram` (`src/TradeAgent.Core/Strategy/`) identified by hash. Statements, functions, loops,
recursion, imports, clocks, randomness, files, network, model calls, a second instrument, shorting,
leverage and pyramiding are not switched off — the AST has no node for them (`StrategyAst.cs`). Parsing
is **total**: every text yields a program or a refusal naming the line, and nothing throws.

## A program

One declaration per line; `#` starts a comment; blank lines are ignored. Declaration ORDER does not
matter except among rules. Keywords and names are read case-insensitively; a symbol is upper-cased.

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
value       := NUMBER | NAME     # a declared number constant;  CLOCK := HH ":" MM, 24-hour
SERIES      := "open" | "high" | "low" | "close" | "volume"
```

Rules are evaluated **in declared order, every exit before every entry**; an exit written below an entry
is refused, not reordered. A program is **long or flat**: one position, and no rule takes a side.

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
booleans, a rule condition must be a boolean. `crosses_above(a, b)` is true where `a` is above `b` and
was at or below it on the bar before; `x[k]` is `k` closed bars ago; a constant has no history.

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
| `opening_range_high()` / `opening_range_low()` | highest high / lowest low of the bars whose open time is inside the `opening_range` interval | resets each session; undefined until that interval's first close | `1` |

`atr` takes no series (a true range is over high, low and the previous close), and an indicator reads a
bar series, never another indicator. These semantics are versioned in `StrategyVersions`; moving a
version re-identifies every program.

**The arithmetic, spelled out** (`StrategyIndicators.cs`), because "Wilder's smoothing" and "`v*a +
prev*(1-a)`" each have more than one implementation and they do not produce the same digits:

- Every value is a **decimal**, never a float. Addition and subtraction are exact, so a running sum
  cannot drift from the window it stands for; division rounds at the type's own ~29 significant
  digits and nothing else rounds anywhere.
- `ema`: `a = 2/(p+1)` is computed ONCE, and the other factor is `1 - a` over that same `a` — so the
  two weights sum to one even where `a` does not terminate (`p=20` is 0.0952380952380952380952380952).
  The multiply-add form above is normative; `prev + a*(v - prev)` is the same algebra and not the
  same digits.
- `rsi` and `atr` smooth as **`avg = (avg*(p-1) + x)/p`** after their seeds. `rsi`'s seed is the mean
  gain and the mean loss of the first `p` CHANGES (hence `p+1` bars); `atr`'s is the mean of the
  first `p` true ranges (hence `p+1` bars, the first bar having no previous close). A zero average
  loss reads 100, which is also what a price that never moves reads.
- `highest` / `lowest` are exact over the window and cost one operation on an ordinary bar, or `p`
  on the bar the extreme expires from the window.
- The **opening-range accumulator's window is a wall-clock interval, not a lookback**: a bar belongs
  to it when its OPEN time is inside the declared `opening_range` interval in the program's zone
  (half-open, `from` included and `to` excluded). It resets at the start of each session and is
  undefined until that session's interval has had a bar, so a session whose opening range has no
  bars leaves the program unable to act rather than acting on yesterday's range. Nothing is carried
  over. **Warm-up** is the maximum "bars needed" over every DECLARED
indicator, every history reference (`close[3]` is 4), every crossing (one more than its deeper side) and
the stop's ATR period; at least 1. It is stated on the frozen program, hashed into its id, and a bar
before it is refused rather than answered from a half-filled window.

## Limits — `StrategyLimits`, one place

8192 source bytes · 200 lines · 240 characters a line · 32 characters a name · 32 constants ·
16 indicators · 20 rules · 200 expression nodes · 8 levels of nesting · history depth 20 · 500 bars of
period and of warm-up · 4 entry windows · 10000 holding bars · fixed quantity 1000000 · sizing fraction
1 (above one is leverage) · 100 percent and 100 ATR multiples. Zones a program may name: `UTC`,
`America/New_York`, `America/Chicago`, `Europe/London`, `Europe/Berlin`, `Asia/Tokyo` — data rather than
an OS lookup, so a program means the same thing on every machine that hashes it.

## Evaluation — one closed bar at a time

`StrategyEvaluator.Step(state, bar, account)` is the whole interface: the bar that has just closed, and
the account state the CALLER supplies (capital, equity, position, average fill price, pending-order
state, bars since entry). None of those is in the expression grammar — a rule cannot say `equity` — and
the evaluator never invents one. It emits **intents** and places nothing.

- **Warm-up first.** Before `WarmUpBars` closed bars the event is consumed, the indicators are fed and
  no rule is evaluated. A gap advances no lookback, so warm-up counts BARS and not minutes.
- **Order.** Every exit before every entry; the scheduled `session_exit` and `max_hold_bars` before
  the declared exit rules; declared rules in their written order; **at most one intent per event**.
- **No same-event reversal.** An exit that fires ENDS the event, even though the position is now flat
  and an entry rule may be true on the same bar.
- **No duplicate signal while one is pending.** The caller's pending-order state is the authority; the
  intent the evaluator emitted is held over the next event as well, so a caller that has not yet
  reported cannot be handed the same signal twice. It applies to exits, not only entries.
- **Executable only afterwards.** An intent carries the bar it came from and `NotBefore`, that bar's
  close: the earliest instant it may be acted on.
- **Undefined is not false.** If a value a rule reads is undefined once the program is warm — an
  opening range before its session's interval — the event is NOT evaluated and is counted. `and` and
  `or` are three-valued: a definitely-false side makes `and` false whatever the other side is.
- **Time filters are read from the bar's OPEN time** in the program's zone, which is the only timestamp
  the dataset carries. `weekdays` and `entry_window` gate ENTRIES only — a program that could not exit
  outside its window would be a trap. `session_exit` fires at or after its wall clock. A session is
  one local DAY.
- **The calendar** (`StrategyCalendar`, `StrategyVersions.CalendarVersion`) is a rule table, not a
  database: standard and daylight offsets for the six zones, the United States' rule (second Sunday in
  March, first Sunday in November, local) and the European Union's (last Sunday in March and October,
  01:00 UTC), applied to every year. Instants become wall clocks and never the other way round, so the
  spring hour that does not exist admits no bar and the autumn hour that happens twice admits both.
  There is no holiday table, and a southern-hemisphere zone would need a version bump.
- **Sizing** is the declared rule over the supplied account state: `fixed` is the quantity,
  `capital_fraction` is `capital * f / close`, `risk_fraction` is `equity * f / (close - stop)`. It is
  **not** rounded to an instrument increment — a dataset carries none — so that rounding and the
  gateway's limits happen downstream. A stop that is not below the close has no risk distance and is a
  fault. An entry that sizes to nothing is counted, not a fault: an account with no capital cannot act.
- **A fault is a value**, `EvaluationOutcome.Faulted` with a reason, and the run halts with no intent:
  a bar out of order or off the interval grid, a division by zero, arithmetic that overflowed, an event
  over the operation budget, state over its size limit, or a run the caller stopped. Nothing throws out
  of the evaluator — the program was written by a cheap model, and a crash it could cause would be the
  app's.

## Refusals

A refusal reads `line 7: <reason>`, or `program: <reason>` when what is wrong is the ABSENCE of a
declaration — no instrument, no size, no entry rule, no way to ever close the position. Every limit
above, a period at or below zero, an undeclared name, a type mismatch and risk sizing with no stop to
measure risk against are refusals, not warnings.

## One meaning, one id

Identity is `Sha256Hex.Of(canonical + "\n" + parameters + "\n" + manifest)`: the typed canonical form
(comments, spacing, case and declaration order gone; rule order and the computed warm-up kept), the
constants sorted by name, `StrategyVersions`. The source is retained. `docs/CONTRACTS.md` has the form.

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
