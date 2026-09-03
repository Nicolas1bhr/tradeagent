# U2c-1 — BUILD BRIEF · round 4 (class A: derive the rule correctly · class B: one-shot + pause + human)

**Tier T1** (dispatch recovery and the emergency controls — the code that decides whether trading may resume over an
order whose outcome is not known, and what a "close all" press actually sends). **Legs:** you are leg [1] (builder). A
fresh Opus adversarial verifier [2] and Codex [3] run on your final sha in their own worktrees. **Rounds until no
HIGH/MED** (§9.1); this is round 4 of a unit whose round 3 drew 13 HIGH from Codex — the manager decided the round-4
content (below); do not re-open those decisions, implement them, and report where a decision does not survive contact
with the code.

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` (mandatory read-gate).
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md` (the four safety rules; two-press; operator
   authority in-process only).
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2c1.md` — the unit record,
   including "Codex round 3 (the open list)" F1–F14 and "Round-4 brief (decided)". Original records lost; branch = truth.
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u2c1-dispatch-recovery.md`.
5. `docs/CONTRACTS.md` in your worktree (the written rule for the unconfirmed set lives there or must).

## Where you work

- Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2c1-build`, branch
  `u2c1-dispatch-recovery` @ `1e10660` (28 commits on the OLD U2b tip `cb2ce2f`, which is not on `main`).
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`. No `timeout` binary; tool calls cap at
  10 min; the full suite ≈ 2–3 min — output to a file, read the tail. The Windows box is OFFLINE.

## Step 0 — rebase onto the `main` of the moment, then prove the baseline

```
git rebase --onto main cb2ce2f u2c1-dispatch-recovery
```

The manager dry-ran this on 2026-09-03 against `main` `7c94cfe`: clean, 21 commits survive (the 7 old U2b commits drop
out because `main` carries their rewritten form). **If U2a has been integrated by the time you run it, expect a content
conflict in `src/TradeAgent.Gateway/GatewayTypes.cs`** (U2a sealed `AgentContext`, minted `op-…` ids, added the
61-char budget; U2c-1 added record/press types). Resolve it keeping BOTH sides' behaviour; never drop a U2a guard to make
it compile. Then `dotnet build TradeAgent.sln` + FULL suite; write the counts into the record before touching round 4.
Any red after the rebase is your first defect: diagnose, do not skip.

## Round 4, class A — the rule stays; derive it correctly

*A request leaves the unconfirmed set only on positive, definite, stable evidence about its own target; anything else is
inconclusive and keeps trading paused.* Implement each as a red-first test that FAILS today, then the fix:

- (a) **Connector identity (F1):** reconciliation uses only the connector whose id the record carries. A record placed on
  A while B is connected is inconclusive with the reason "placed on A; connected to B" — an empty book on B settles
  nothing.
- (b) **Non-definite never clear (F2):** a non-definite target state is never "clear", with or without a captured set.
- (c) **`Adopt` never treats UNKNOWN as resolved (F3).**
- (d) **"Held still" is not a verdict (F4):** a cancel/modify whose target is unchanged after grace stays inconclusive
  until a definite state — target terminal, broker refusal, or the owner's card.
- (e) **Latch on the definite settle path (F5):** any persist failure after the wire → latch (round 2 covered only the
  indefinite path).
- (f) **Modify verdict (F6/F7/F8):** returned order id == target id (and symbol/account); then price ∈
  {round-down(request), round-up(request)} on the tick grid AND ≠ the pre-modify price when the request differs;
  quantity only when the SDK contract states what `OrderInfo.Quantity` means — document it in
  `src/TradeAgent.ConnectorSdk/Contracts.cs`; otherwise inconclusive.

## Round 4, class B — SIMPLIFY the emergency controls: one-shot + pause + human (F9–F14)

- A press writes per-target write-ahead records, sends the wire calls, and from that moment trading is paused: the
  press's records count as unconfirmed work (WORKING closes included) until the owner resolves them through the card,
  which shows the outcome per target and the current position.
- A second press while unresolved is refused: "close-all sent at HH:MM; resolve it first". No retry-with-same-nonce path;
  no in-memory `OperatorPress` to reconstruct at restart — the durable records ARE the press.
- Cancel-all is sent as per-order cancels of the captured set; no account-wide sweep on the wire.
- Close-all re-reads the position immediately before the wire call and refuses (fresh two-press) if it differs from the
  captured one; the wire call keeps ATAS's own close-position (side chosen by ATAS — the sign-convention decision stands).
- Completion and outcome read the ACCOUNT stored on the records, never the current account.
- Delete the code the simplification retires (the reconstruct-at-restart path, the same-nonce retry, the sweep call);
  a simplification that leaves the old path reachable is not one.

## Round 4, class C — inherited from U2a's Codex review (HIGH, owner moved here because the files are yours)

- **C1 (U2a Codex F5 + F11 class): intent must survive the layer that transforms operations.** Agent `close` and every
  `close-all` leg call `PlaceAsync`, so the connector sees `Place` and the risk-reducing fast path (2 s emergency gate,
  U2a round 4) never applies. Carry the risk-reducing intent through `ITradingConnector` for close legs (a `Close`
  intent, not an offsetting `Place`), so the connector classifies it correctly; single `close` included. Acceptance:
  `trade close ES` through the real `GatewayPipeServer` against a bridge that answers the position lookup then stalls
  → completes near `EmergencyGateWait` with the emergency wording. U2a's builder fixes the prerequisite-read half in the
  pipe server and connector; you own the gateway half.
- **C2 (U2a Codex F6 + F8): replaying a sweep must not repeat effects.** A replayed `cancel-all`/`close-all` with the same
  outer request id currently mints a fresh nonce and re-sweeps the CURRENT book (cancels orders created after the
  original; liquidates newly opened positions). The class-B press records are the mechanism: the composite (outer request
  id → captured child plan → per-child results) is persisted BEFORE effects, for the agent's sweep as for the operator's
  press; a replay of a known outer id returns the stored outcome and sends nothing. Acceptance: sweep order A as
  `sweep-1`, lose the reply, create order B, repeat `sweep-1` → B stays working and the original result is returned.
  Idempotency by request id applies to every mutating op, not only `Place`.

## Proof obligations

- Every item above: RED quoted before the fix, GREEN after, and a mutant that reverts the guard watched to bite (commit
  before mutating; restore from a `cp` copy, never `git checkout --`; `touch` after restore). Mutation found vacuous
  tests in four units last session; assume yours has some until the table says otherwise.
- Both directions on every guard: the wrong evidence is refused AND the right evidence still settles (a definite
  CANCELLED from the right connector still clears; a correctly applied modify still reads applied; a normal press with
  a stable position still goes to the wire).
- Regression set: the round 1–3 tests must stay green unchanged; if the lost verifier probes (`PROBE_A/A2/C/E/H/K`) do not
  exist in the tree, say so — the verifier will rebuild them from the record.
- R3 adjacent sweep: every reader of the unconfirmed-work query (Dashboard, Doctor, GatewayHost, `status`,
  authorization, and the updater's provider once U2d lands) enumerated and confirmed.
- Gates: targeted per item; `dotnet build TradeAgent.sln` + FULL suite at the end, counts pasted.

## Ownership (R2)

Yours: `src/TradeAgent.Gateway/**` (except the pipe server and `AgentContext`, which U2a owns — touch them only to
resolve the rebase), `src/TradeAgent.Core/Db/Stores.cs`, `Errors.cs`, `src/TradeAgent.ConnectorSdk/Contracts.cs` (the
quantity sentence), `src/TradeAgent.App/DashboardView.cs` (the card), `GatewayHost`, the Doctor, `docs/CONTRACTS.md`,
tests. NOT yours: the updater (U2d), `CoidWitness*` (U14), `AppHost.cs` beyond wiring you can name in one line — if an
item needs more, STOP that item and report.

## Rules

No `Co-Authored-By` trailers; one-sentence commit messages. Commit after every item. Checkpoint AS YOU GO by appending
`## Round 4 (build record, <date>)` to `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2c1.md`
(MAIN worktree path; no git there — the manager commits it), with the mutants table growing item by item. Honesty
contract (§6); banned words: should work, looks correct, probably, I believe, minor, trivial, static-verified, basically.
§9.9: answer for class A whether a test class could catch "concluded on non-definite evidence" generically. §9.10: if
two findings share a root cause, fix the class. Do not push, merge, or touch other worktrees.

## Report back

Tip sha; rebase result (conflicts resolved, in which files); baseline counts; per item RED → GREEN → mutant bit in one
line each; what the simplification deleted; the R3 sweep; suite counts; "What I did NOT do".
