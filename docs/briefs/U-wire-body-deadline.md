# U-wire-body-deadline — a perception answer whose 200 headers arrive at once and whose body then stalls ends UNANSWERED "timeout" at the wire's own deadline, its status kept, pinned by a test
**Protects:** the owner's money and the evidence of what a paid call came to (`CLAUDE.md`; a Jev call is reserved before it is sent and must settle — a body read that never ends holds the reservation, the
flying register and the caller forever); honesty (the attempt's record says UNANSWERED `timeout` with `HttpStatus` 200, never ANSWERED, never FAILED). Seat B; light (**tests only**: sonnet); **no rung**.
Owed by `U-decision-card` (record `fleet/records/U-decision-card.md`): the body-read deadline exists in code and no test fails without it.
**Facts (SOURCE at `main` `023413fa`; re-check by `grep -n`).**
- `src/TradeAgent.AgentRuntime/TypeSafeWire.cs` `SendAsync` (:386-494): one linked deadline `deadline.CancelAfter(_http.Timeout)` (:401) over the whole exchange; the send uses
  `HttpCompletionOption.ResponseHeadersRead` (:407), so HttpClient's own timeout stops at the headers and only `deadline` bounds `LoadIntoBufferAsync` / `ReadAsStringAsync` (:451-452);
  a cancellation there not asked for by the caller is `Lost("timeout", status)` (:458-461).
- `tests/TradeAgent.UnitTests/DecisionPortTests.cs`: the `Wire(...)` helper (:131-134, `timeout:`), the never-answered case (:354-370: headers never sent ⇒ "timeout", status null) — the HEADERS case is
  pinned, the BODY case is not. `tests/TradeAgent.UnitTests/FakeProvider.cs` is the local HttpListener host (`answers: false` holds a request unanswered).
Must NOT: touch any file under `src/`; edit, weaken or rename an existing test; raise a timeout or add a retry; reach any vendor (`SuiteReachesNoVendorTests` stays green).
Items, one commit each, one-sentence messages:
1. **The stalling host.** `FakeProvider` gains a mode that writes status 200, a JSON content type and the headers (flushed), then a first part of a valid body, and then holds the rest until the test ends
   (or is disposed) — nothing in it hangs past disposal.
2. **The test** in `DecisionPortTests`: `A_200_whose_body_stalls_ends_unanswered_timeout_at_the_wires_deadline` — wire timeout 2 s; `DecideAsync` with no caller cancellation; assert UNANSWERED, `ErrorClass` "timeout",
   `HttpStatus` 200, the attempt ENDED with the reservation as its cost, not held as flying, the tape record UNANSWERED; and that it returned within a bound the test sets itself (e.g. 20 s via
   `Task.WhenAny`), so the mutant FAILS rather than hangs the suite.
RED-first: write the test against a temporarily removed `deadline.CancelAfter(_http.Timeout);` (:401) — the mutant, in the working tree only, never committed — and quote it red (the self-set bound
tripping); restore and quote green. That mutant is the one watched.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; `DecisionPortTests` 3× through `suite.sh`; the full suite on branch CI, all three platforms;
tests box `ready` once or NOT RUN; names vs `main` 0 removed (both set sizes). `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
