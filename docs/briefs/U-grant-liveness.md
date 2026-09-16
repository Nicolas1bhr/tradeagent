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
