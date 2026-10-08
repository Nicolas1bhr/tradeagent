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
             | "bars" BARS                               # the bar the rules are asked on; default 1m
             | "timeframe" DURATION | "data_freshness" DURATION | "max_decision_age" DURATION
             | "const" NAME "=" (NUMBER | "true" | "false") | "indicator" NAME "=" indicator
             | "feature" NAME "=" SPEC                  # SPEC: one JSON object, the rest of the line; up to 8
             | "size" ("fixed" | "capital_fraction") value  # required: this size line or the next one
             | "size" "risk_fraction" value ("max_capital_fraction" value)?   # the cap: see Sizing below
             | "stop" ("fixed" value | "percent" value | "atr" value value)
             | "target" ("fixed" value | "percent" value) | "max_hold_bars" value
             | "weekdays" DAY ("," DAY)* | "session_exit" CLOCK    # mon tue wed thu fri sat sun
             | "entry_window" CLOCK "-" CLOCK | "opening_range" CLOCK "-" CLOCK   # windows: up to 4
             | ("exit" | "entry") "when" expr             # exits first, at least one entry
indicator   := ("sma" | "ema" | "rsi" | "highest" | "lowest") "(" SERIES "," value ")"
             | "atr" "(" value ")" | ("opening_range_high" | "opening_range_low") "(" ")"
value       := NUMBER | NAME     # a declared number constant;  CLOCK := HH ":" MM, 24-hour
DURATION    := INT ("s" | "m" | "h" | "d")   # `30s`, `5m`, `2h`, `1d`; no default unit, no fractions
BARS        := "1m" | "5m" | "15m" | "30m" | "1h" | "4h" | "1d"   # spelled like a DURATION; `60m` is `1h`
SERIES      := "open" | "high" | "low" | "close" | "volume"
```

The three execution bounds are **all three or none**: a `timeframe` and a `data_freshness` with no
`max_decision_age` beside them read like an execution gate and are not one, because nothing there
refuses a late order. They are what the dispatcher checks again when an intent reaches execution
(`docs/COUNCIL.md`:96-97), and they are in the canonical form — a changed bound is a different id.

Rules are evaluated **in declared order, every exit before every entry**; an exit written below an entry
is refused, not reordered. A program is **long or flat**: one position, and no rule takes a side.

## Bars — what a program is evaluated on

`bars 1h` declares the bar the rules are asked on: one of `1m`, `5m`, `15m`, `30m`, `1h`, `4h`, `1d`. A
program that declares none is asked on **every closed minute**, which is what every program written before
`bars` existed is asked on; `bars 1m` is that same program with the same id. `bars` is a declaration only as
the first word of a line, never a reserved name — `const bars = 20` still means what it meant.

**Why it exists.** A program that decides every minute trades as often as the minute lets it, and every
trade pays a fee and the spread: one model-written program, deciding every minute, traded 321 times in a
quarter, and 62% of its loss was fees. A program on hourly bars decides once an hour, and the same 500-bar
lookback reaches three weeks back instead of eight hours. Minute-scale turnover dies on costs; declare the
bar your edge lives on.

**How a bar is built.** From the closed one-minute bars, on a fixed grid: `[k·d, (k+1)·d)` counted in UTC,
so the 14:00 hour is the same hour everywhere — and for `1d`, from midnight to midnight **in the program's
`timezone`**, so a daily bar on a daylight-change day is 23 or 25 hours long. The open is its first minute's
open, the high and low its minutes' extremes, the close its last minute's close, the volume their sum —
decimals, as published. It **closes when its last minute has closed**, and the rules are asked at that close
and never before. A window with no minute at all is **no bar**: a gap, never filled. A window with some
minutes missing is a **partial bar**, built from the minutes it has, and every minute it is short is counted
as missing, exactly as a missing minute always was.

**What counts declared bars**: indicator periods, warm-up, history (`close[3]` is three declared bars back),
`max_hold_bars`, every bar limit below — 500 hourly bars is about 21 days — and a backtest's run cap, so a
year of hourly bars is one run. **What stays on the minute**: a decision taken at a declared bar's close
fills at the **next minute's open**; a stop and a target fire on each minute's own range, with the
conservative ordering applied minute by minute — an hour whose range touched both levels is a target if a
minute inside it reached the target first and alone; and `max_hold_bars` is taken at the close of the
declared bar that reaches it. **Time filters read the declared bar's open time**, so on hourly bars an
`entry_window` or `opening_range` must contain an hour's open (`10:00`) to admit that hour: declare them on
the bar's boundaries.

**`bars` and `timeframe` are independent.** `timeframe` is one of the three execution bounds above — a
statement, hashed into the id, recorded on a verdict and carried to execution — and it resamples nothing.
`bars` is what the rules are evaluated on. A program on `bars 1h` will normally state `timeframe 1h` beside
it; nothing refuses one whose two differ, because a stored program's `timeframe` never meant the bar it was
evaluated on.

**On paper, the same two clocks.** The paper runner asks the rules once per closed declared bar, on the bar
it builds from the minutes the same way, and sends what they decide at that bar's close. Everything that
protects a position stays on the minute: the stop and the target go to the venue in the pass after the
minute the entry filled on, the other one is cancelled in the pass after the minute one of them filled on,
and `max_hold_bars` — counted in declared bars — closes the position at the close of the declared bar that
reaches it. A run's first bar is the partial bar it saw from its start, its missing minutes counted as
missing.

## Features — a program reads the tape

`feature funding = {"kind":"latest","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":180}`
declares a FEATURE: a value TradeAgent computes from its market-context tape, which a rule then reads like an
indicator — `entry when funding < -0.0003`, `exit when funding[1] > 0`. `feature` is a declaration only as the
first word of a line, never a reserved name, so a stored `const feature = 2` still means what it meant.

**The author writes the spec; the app computes the value and states the id.** Everything after the first `=` is one
JSON object, read by the same parser `docs/CONTRACTS.md` "Features" describes: a `kind` (`latest`, `change`, `mean`,
`min`, `max`, `pct-rank`), the `input` (`source`, `series`, `subject`, `field` — one of this build's Binance USDⓈ-M
rows, one of its six symbols), `latency_s`, `max_age_s`, and the keys its kind needs. Every key is required, no
other is accepted, and a spec the parser refuses is refused on its line with the parser's own words. A `#` starts
a comment here as on every line, so a spec holding one is cut there and refused as the JSON it then is not. A
feature line may be up to 512 characters; every other line is still held to 240. A program declares at most 8.

**As it had arrived, at the close.** A feature is read at the CLOSE of each bar the rules are asked on — the
instant the decision is taken — from only the readings TradeAgent had FIRST SEEN by that close minus `latency_s`.
A reading stamped before the close that arrived after it moves no decision at that close; it can move the next
one. `funding[1]` is the value at the previous evaluated bar's close. On `bars 1d` in a zone the close is that
zone's midnight, 23 or 25 hours after the last on a daylight-change day.

**Absent is no decision.** A value can be absent — nothing fresh within `max_age_s`, too few readings in a window,
a field that is not a plain decimal, a reading the tape's screen withheld. An absent value is UNDEFINED, exactly
as an indicator still warming up is: the event decides nothing and is counted, and the scheduled exits —
`session_exit`, `max_hold_bars` — and the stop and the target still act. Nothing is filled in and nothing is
carried forward. Bars before a feature's clean-history start (when its readings first became first-hand) are
evaluated, not skipped; a backtest says how many there were.

**Identity.** The canonical form names each feature by its spec's own hash — `feature funding=<64 hex>` — and a
rule's reference as `$funding`, so the program's id names every input it reads and the semantics they are
computed under (`features=1`). Two spellings of one spec are one id; any fact of the spec changed is another
feature and another program. A program that declares no feature has no such line: every program written before
features existed keeps its text and its id.

**Where a feature program runs.** `trade backtest` and TradeAgent's verdict read the features from the tape under
the same rules as the bars, and refuse to run at all where no tape is open — never as though every value were
absent. The tape is held back over every dataset's holdout window as the bars are, so a backtest whose features
would read it there is REFUSED in words before a bar is read — and a feature is read at the LAST bar's close too, so
a backtest's `to` must be more than one of the program's bars before a `holdout_from`. Where a holdout window lies
before the run, the search for a feature's clean-history start reaches back no further than that window's close,
and the backtest says so. **The paper runner does not run one yet**: a program uses only declarations every reader of it
implements, so a deployment of a program that reads a feature is ended before its first bar, in words, until a
later update values features on paper. And every tape source is research-only today, so no capital may stand on a
version that reads a feature.

## Required declarations

Every declaration a program uses constrains its orders, its risk or how it is evaluated, so every one is
REQUIRED: a reader that does not implement one refuses the program in words rather than running the rest of it.
The backtest and the verdict implement every declaration this page lists; the paper runner every one but
`feature`. `max_capital_fraction` is one of them although it is written on the size line: a reader that applied a
risk fraction without its cap would send an entry larger than the program says one may ever be. Comments are the
one optional part — kept byte for byte in the source, outside the id, and required by nobody. A declaration that
states the default (`timezone UTC`, `bars 1m`, all seven weekdays) requires nothing.

## Conditions

```
expr    := or
or      := and ("or" and)*                 and     := not ("and" not)*
not     := "not" not | cmp                 cmp     := sum (CMPOP sum)?
sum     := product (("+" | "-") product)*  product := unary (("*" | "/") unary)*
unary   := "-" unary | primary             CMPOP   := "<" | "<=" | ">" | ">=" | "==" | "!="
primary := NUMBER | "true" | "false" | "(" expr ")" | cross | ref
cross   := ("crosses_above" | "crosses_below") "(" expr "," expr ")"
ref     := (SERIES | NAME) ("[" INT "]")?  # close, close[1], fastma[2], funding[1]
```

Two types, `number` and `boolean`: arithmetic and comparison take numbers, `and`/`or`/`not` take
booleans, a rule condition must be a boolean. `crosses_above(a, b)` is true where `a` is above `b` and
was at or below it on the bar before; `x[k]` is `k` closed bars ago; a constant has no history. A feature
is a number, read at a bar's close; `funding[k]` is its value at the close `k` evaluated bars ago.

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

8192 source bytes · 200 lines · 240 characters a line (512 for a `feature` line) · 32 characters a name ·
32 constants · 16 indicators · 8 features · 20 rules · 200 expression nodes · 8 levels of nesting · history depth 20 · 500 bars of
period and of warm-up · 4 entry windows · 10000 holding bars · fixed quantity 1000000 · sizing fraction
1, the cap's too (above one is leverage) · 100 percent and 100 ATR multiples · every execution bound at least 1
second and at most one week. Every count of bars is in the program's declared bars: 500 bars of lookback
is about eight hours of minutes and about 21 days of hours. Zones a program may name: `UTC`,
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
  opening range before its session's interval, a feature with no value at the close — the event is NOT
  evaluated and is counted. `and` and
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
  `capital_fraction` is `capital * f / close`, `risk_fraction` is `equity * f / (close - stop)`. Capital
  is what the caller supplies: in a backtest the cash the run has left, on paper the allocation's ceiling.
  A size is **not** rounded to an instrument increment — a dataset carries none — so rounding happens
  downstream, always DOWN; and every limit downstream — a backtest's cash, the gateway's per-order value,
  an allocation's ceiling — REFUSES an entry WHOLE: nothing downstream makes an order smaller to fit. A
  stop that is not below the close has no risk distance and is a fault. An entry that sizes to nothing is
  counted, not a fault: an account with no capital cannot act.
- **A risk size can ask for more than everything, and `max_capital_fraction` caps it.** With `stop
  percent p` the notional is the constant `100f/p` of equity — a `capital_fraction` in disguise, and an
  `f` a margin below `p/100` never overshoots. With `stop atr` or `stop fixed` it is `f * close /
  distance` of equity, without bound as the stop nears the price: `risk_fraction 0.01` over a stop 0.2 %
  below the close asks for five times the equity. No fraction keeps such a signal funded and no capital
  does either — the size grows with the capital — so it is a no-trade in a backtest and refused whole on
  paper. `size risk_fraction 0.01 max_capital_fraction 0.95` sizes the SMALLER of the risk size and
  `capital * 0.95 / close` — what `capital_fraction` reads — so a tight stop sizes to the cap instead of
  to no trade, and a cap only ever makes a size smaller. Only a risk fraction takes one (a capital fraction
  is its own cap, a fixed quantity a constant), above 0 and at most 1, a number or a constant. The cap
  applies at the signal's close, so fees, slippage and the next open come on top of it: under the venue
  cost model's 0.1 % fee and 0.02 % slippage, `max_capital_fraction 1` cannot pay for its own fill unless
  the next open is about 0.12 % below the close — 0.95 leaves room.
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
The canonical form states every bound, present or not, with ONE exception: the `bars` line is written only
when a program declares a bar that is not one minute — so every program written before `bars` existed keeps
its canonical text and its id, and an hourly program can never share an id with its minute twin. A `feature`
line is written only for a declared feature, by its spec's hash, for the same reason, and the size line's
`max_capital_fraction:<c>` only for a declared cap — `size risk_fraction:0.01 max_capital_fraction:0.95`.

## The three day-one programs

`docs/COUNCIL.md` requires the language to express these three on day one. They are the files the app
ships — `src/TradeAgent.AgentRuntime/Strategies/`, written into every role's home as
`strategies/examples/` on every start — byte for byte, each pinned to a `StrategyId`. That is the one
copy: the parser tests read those same bytes, so the programs a role is given and the programs the
tests prove are one set of files. Run the first of them with
`trade backtest --strategy strategies/examples/ma-crossover.strategy --dataset <id>`.

`ma-crossover.strategy`
```
# A moving-average crossover with fixed sizing and a stop.
instrument BTCUSDT
timezone UTC
timeframe 1m
data_freshness 2m
max_decision_age 60s
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
timeframe 1m
data_freshness 2m
max_decision_age 30s
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
timeframe 1m
data_freshness 5m
max_decision_age 5m
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

## Running one — the backtest

`trade backtest --strategy strategies/x.strategy --dataset 3` runs a program over the history the app
holds and records it. The app parses the file (a refusal names the line), streams the dataset's own
hashed bars, and computes every figure from its own trace: `docs/CONTRACTS.md` has the whole contract.
The four numbers a run DECLARES — fees and slippage as fractions, the quantity increment a size is
rounded down to, the capital it starts with — are part of the run's identity, because every figure
depends on them.

The rules that decide a result, stated once here: a signal from a bar's close fills at the **next bar's
open** plus adverse slippage, with a fee on every fill; a stop or a target fires **intrabar** at its own
price, and a bar that touched both counts as the **stop**; a bar that opened through the stop fills at
that open; `max_hold_bars` is taken at the close of the bar that reaches it, by the backtest's own
protection, before the evaluator is asked anything on that bar — so the evaluator emits nothing for it.
A size that rounds down to nothing is no trade, with the reason. On a program that declares `bars`, the
decision and `max_hold_bars` are the declared bar's and everything else here is the minute's: the fill at
the next MINUTE's open, the stop and the target on each minute's range — the "Bars" section above. The
dataset still serves minutes; the run builds the declared bars from them, and a run that ends inside one
closes it as a partial bar. A program that reads a feature is handed each feature's value at each evaluated
bar's close, as it had arrived; the values read are part of the run's identity, and the run ends with one
`Feature` line per feature — its id, its clean-history start, the bars evaluated before it, the bars it had no
value at, and the worst evidence class of what it read — which the answer's `features` repeats.

**What a run cannot prove.** It is computed over bars, and bars establish no actual fill, no queue
position and no intrabar ordering. A run is a reason to test something and never a record of a trade.
