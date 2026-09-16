# U-grant-liveness — a launch grant is re-verified on every frame that reaches the role gate, and a grant that ends closes the connections that hold it
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (operator authority in-process only; the agent-facing pipe grants nothing), `docs/REVIEW-2026-09-16.md` finding 5 with its
probe `C1`, `docs/CONTRACTS.md` (`U-containment`, `U-api-worker`, `U-turn-commit`: known by launch on the pipe; a turn ends in one transaction), then
`src/TradeAgent.Gateway/GatewayPipeServer.cs:658` (`GrantRefusal` inside the `hello` arm), `:702` (the captured `grant` handed to `Handle`; `ctx.MayPlaceOrders` the only
thing between a frame and the wire), `src/TradeAgent.Security/AgentGrants.cs:118-130` (`Revoke`, `EndTurn`, the 60 s `Grace` documented for a call in flight), the
probe test on branch `review-probes-c` @ `58aa4fa` — your RED test: bring it over, quote it red on your base, make it green. Branch `u-grant-liveness`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-grant-liveness`, from `main`. **Money path** (authority): a test RED before the guard and ONE mutant, quoted, over
`RecordingConnector`. No schema. No box, no money. One item; a light leg.

**Why.** A grant is verified once, at `hello`, and the role it proved is used for every later frame on that connection: an EXPIRED, REVOKED or turn-ended grant keeps
placing orders for as long as the caller holds the socket, while a NEW connection with the same grant is refused `IPC_UNAUTHENTICATED`. Revocation that does not reach a
live connection is not revocation.

1. **Liveness at every frame, and an ending that reaches the socket.** Every frame that reaches the role gate re-verifies the presented grant (`GrantRefusal` already does
   the work; the refusal is the same code a fresh connection gets), AND `Revoke`/`EndTurn` close the live connections holding that token so a caller cannot even try; the
   60 s `Grace` covers a call already in flight, never a frame that arrives after the grant ended — say so where `Grace` is documented. RED: `C1` (expired-by-an-hour,
   revoked, and `Dispose()`d all still place). Mutant (the per-frame check kept but `Revoke` not closing connections): the revoked arm places one more order inside the
   grace. Second mutant (the check applied only to `buy`): a `close-all` on an ended grant still goes out — assert the wire.
Not this unit: the pipe ACL and token (`U-…-auth`, landed); the turn budget; the harness worker's key.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched classes 3×; a `Timing`
red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the RED and mutants quoted,
what you did NOT do.

## Report

Tip `174fa5b` (this report commits on top), branch `u-grant-liveness`, rebased onto `main` at `c1ac6cd`. The one item is done.

**Gate**, Release, on the rebased tip. `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)`, `0 Error(s)`. `Failed: 0, Passed: 1145, Skipped: 0, Total: 1145` (UnitTests); `Failed: 0, Passed: 350, Skipped: 0, Total: 350` (FaultTests); `Failed: 0, Passed: 670, Skipped: 1, Total: 671` (IntegrationTests). `GrantLivenessTests` 3× → `Failed: 0, Passed: 2` each, 189/195/196 ms; no `Timing` red anywhere, nothing re-run, nothing loosened. `[Fact]`/`[Theory]` under `tests/`: 1798 on `main`, 1800 on HEAD; the sorted test-name diff removes nothing and adds only this unit's two.

**RED, on the base.** Probe `C1` brought over as `An_ended_grant_places_no_further_order_on_the_connection_it_authenticated`, its logging turned into assertions and a fourth ending added. All four red at the wire: `[Expired] before=SENT after=SENT orders after=1 new connection=IPC_UNAUTHENTICATED`; `[Revoked]` identical; `[DisposedAndLapsed]` identical; `[DisposedInsideTheGrace] before=SENT after=SENT orders after=1 new connection=ACCEPTED`. And `An_expired_grant_sends_no_close_all_on_the_connection_it_authenticated`: `close-all after, on that connection: SENT`, `mutating calls at the wire after: 1`.

**Mutant 1** — `Grants.Ended += OnGrantEnded` taken out of `Start`, the per-frame check kept: `Failed: 1, Passed: 1`, `[DisposedInsideTheGrace] 1 order(s) reached the broker AFTER the turn ended`, while the other three arms now read `after=IPC_UNAUTHENTICATED orders after=0`. That is the finding's second half exactly: `Revoke` takes the token out of the register, so only the ending that deliberately keeps it there — the turn ending, inside the 60 s grace — needs the socket closed to be an ending at all.

**Mutant 2** — the per-frame check applied only to `Ops.Buy`: `Failed: 1, Passed: 1`, `the gateway served a close-all on a connection whose launch grant had expired an hour earlier`, `mutating calls at the wire after: 1` (the close left this process as an offsetting placement, which is why the assertion counts every mutating call and not `Closes`).

**Choices**, all written into `docs/CONTRACTS.md` under `U-grant-liveness`. The frame is re-verified against the token the connection PROVED at hello, never against `req.Grant`, or a peer could swap in another live grant per frame. An ended grant is refused outright with the code and words a fresh connection gets, and the connection closes — not downgraded to roleless, so reads go too. The peer-image rule is not re-run per frame: the process behind an accepted pipe cannot change, and re-running would hash the `trade` image off disk in the path of every order. `AgentGrants.Ended` fires for every way a grant stops being live, outside the register's lock, possibly twice for one token, and a subscriber that throws does not stop the next; the server subscribes in `Start` (`Grants` is an `init` property, unset in the constructor) and unsubscribes in `DisposeAsync`, because `Shared` outlives every server. Natural expiry is enforced on the frame rather than by a timer. `Revoke` raises `Ended` whether or not this register held the token.

**The `Grace` stays**, documented where it is declared: a call already in flight, and a `trade` launched a moment before the turn ended. A connection inside a call is marked, answers that frame, then closes — cutting it would report a failure for an order that may already be at the broker. `An_ended_grant_places_no_further_order…` asserts in the same breath that a FRESH connection is still ACCEPTED inside the grace, so the closure cannot be mistaken for permission to delete the grace.

**NOT done.** No schema rung, no Windows box, no order of any kind placed anywhere. Nothing touched of the pipe ACL and token, the turn budget, or the harness worker's key. The in-process worker path (`CallAsync`) is unchanged — it presents no grant and has none to end. No new permission reaches the agent-facing pipe: the only thing this unit adds to it is a refusal.
