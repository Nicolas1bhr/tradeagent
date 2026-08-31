# Decisions

Only decisions that affect code. No survey of alternatives not taken.

## Locked by the brief, honoured as written

- Windows 11 x64 target; current stable .NET (**10.0**, LTS); C# throughout.
- SQLite for local transactional state, via `Microsoft.Data.Sqlite`.
- Named-pipe IPC with authentication and explicit framing; no unauthenticated localhost port.
- `ITradingConnector` / `IAgentRuntime` / `trade` CLI / Trading Gateway as separate layers.
- ATAS is the first real connector; a fake connector precedes it and remains the test harness.
- `UNKNOWN` is a first-class execution state and never means failure.
- Live execution requires explicit activation; emergency controls are separate buttons.

## Decided here

**Avalonia 12 for the UI, built in code, no XAML and no MVVM framework.**
WPF and WinUI cannot build or run on the development or CI host, which would have made the UI the one
component nobody could compile until Windows day. Avalonia also publishes self-contained, so the user
never installs a runtime. Code-built controls because this UI is a dozen labels and a dozen buttons;
XAML plus a view-model layer would have cost more than it saved.

**Newline-delimited JSON over named pipes, 1 MiB frame cap.**
Explicit framing, trivially debuggable, and .NET implements named pipes over Unix domain sockets, so
the transport is testable on the build host. Authentication is a 32-byte random token in a
DPAPI-protected user-only file, presented in a mandatory `hello` frame and compared in constant time.
On Windows the pipe is additionally ACL'd to the current user's SID.

**Operator authority is never on the agent channel.**
The pipe carries reads and order operations. Mode changes, the kill switch, live activation and
approvals are in-process calls (or stdin on the headless host). An agent that decides it would like
more permission has nowhere to ask. Tested.

**Hand-written SQL, no ORM.**
On a low-spec laptop EF Core costs tens of MB of working set and hundreds of ms of startup for a
seven-table schema. Money is stored as `TEXT` and parsed as `decimal` with invariant culture, so no
value ever passes through a float.

**Client order id is derived from the request id (`TA-{requestId}`).**
One deterministic identifier links our record to the broker's, which is what makes reconciliation
possible without a lookup table that could itself be lost.

**Idempotency is resolved before authorisation and risk.**
A repeated request id exercises no new authority, so it must not be charged against the rate limit or
re-judged against limits that may have changed. It is a read that returns the original outcome. The
unique constraint on `request_id` remains the backstop for genuine concurrent races.

**Absence only means "never landed" under two conditions:** the backend can prove its own order
history, and a grace window has passed since dispatch. Otherwise the request stays unconfirmed and
trading stays paused — the safe direction to fail. A human can override, and that override is
recorded as theirs.

**Risk limits, taken from venture-agent's `policy.yaml` shape.**
The brief bounded *permission* (modes, activation) but not *size*. Five limits are enforced before
anything leaves the machine: order quantity, order value (opt-in), open positions, orders per minute,
instrument allowlist. Defaults are deliberately small. There is no command an agent can use to raise
them.

**The notional cap defaults to off.**
One ES future is a six-figure notional on a four-figure margin. Any naively chosen cap refuses every
legitimate futures order while teaching the user nothing, which is worse than no cap: it trains people
to disable safety features. `MaxOrderQuantity` is the limit that means something for leveraged
products. The cap stays available for instruments where face value is the real exposure.

**A fresh price is required for every order**, whether or not a value cap is set. An agent sizing a
market order from a stale quote is a failure mode worth closing permanently.

**Autonomous live trading is refused on connectors that cannot prove order state.**
No client-order-id round trip or no order history means post-disconnect state is unknowable. Rather
than trade and hope, `LIVE_AUTONOMOUS` is refused with an explanation, and confirm-each-order mode
remains available.

**Agent runtime and ATAS layout details are data, not code.**
Install commands, sign-in commands, success patterns, install paths and process names all live in
overridable JSON with a `Verified` flag. These vendors change their CLIs and installers on their own
schedule; a build that hard-codes today's flags becomes wrong silently. A wrong value is a one-line
fix in `%LOCALAPPDATA%\TradeAgent\runtimes.json`, and the Doctor tells the user when a value is
unverified rather than letting them discover it as a mysterious failure.

**The AI gets an inbox, and the workspace keeps a ledger of what is in it.**
The owner hands the agent programs, documents and data to experiment with. Three decisions follow,
and the third is the one that had to be made first:

- `inbox/` lives **inside the agent workspace**, not beside it. The agent is already broadly free in
  there, so the drop folder grants it nothing new; a folder outside would widen the blast radius,
  and the workspace boundary is the whole containment story.
- **Material is data, never instruction.** A document handed over may contain text addressed to the
  agent — approvals, orders, "ignore your instructions". Nothing in the inbox can change what the
  agent may do, and `AGENTS.md` says so in as many words. The architecture already made this
  survivable rather than fatal: operator authority is not on the agent channel, no command raises a
  risk limit, and `LIVE_CONFIRM` needs a human press per order. The inbox does not open an
  escalation path; it adds untrusted *content*, which is a different and much smaller problem.
- **Provenance is built first, because it cannot be added later.** A dropbox with no record is a
  dump within a fortnight, and the fortnight of files that arrived before the ledger existed can
  never be reconstructed — nobody can say afterwards what a binary was, when it came, or what was
  made from it. Everything else about the feature (preview, quotas, sandboxing, file types) is
  correctable at leisure. So the ledger shipped in the same commit as the folder.

**The ledger separates what was measured from what was claimed.**
Two tables, and they are not merged. `material` rows come from a directory listing and a SHA-256 the
software computed; the agent cannot write one or edit one. `material_note` rows are the agent's own
account of what it ran and what it derived from what, reaching the gateway over the same
authenticated pipe as everything else. An observation that the observed party can rewrite is not a
record — the split is what makes the history worth reading without having to assume the agent was
diligent, honest, or even still running. A row is a *file version*, not a path: replacing a file
leaves the old row stamped `removed_at` rather than overwriting it.

**The scanner is bounded, and the ledger is not allowed to become the dump.**
One `npm install` in the workspace is forty thousand files; tracking them would bury the dozen rows
anybody wants to read. Package and build directories are skipped by name, `scratch/` and `logs/` are
not tracked at all — the agent is told which folders are recorded, so "put it somewhere tracked if it
matters" is a rule it can follow. Identity is the (path, size, mtime) tuple a directory listing
already yields, and only a changed tuple is opened and hashed, a bounded number per pass. The known
cost is written down rather than hidden: content swapped with both size and mtime preserved reads as
the same version.

**The ATAS bridge is split in two.**
`BridgeServer` (transport, framing, heartbeat, reconnect, handshake, error classification) depends
only on `IAtasAdapter` and is tested on every platform against `LoopbackAtasAdapter`. Only
`AtasStrategyAdapter` touches ATAS. This shrinks the surface that cannot be verified without ATAS
from a whole subsystem to one file.

**The bridge dials out to TradeAgent, rather than listening.**
Its presence becomes an observable fact — a connection plus a heartbeat — so the setup wizard can
continue by itself once the user starts the strategy inside ATAS, instead of asking them to confirm
they did something the software can see for itself.

## Low-spec laptop budget

The target is a modest laptop, not a workstation. These choices exist to keep it usable:

- No EF Core, no Electron, no Node requirement, no local models.
- Workstation GC, non-concurrent; `InvariantGlobalization`; `TieredPGO`.
- One slow background loop (5 s health poll), reconciliation attempted only while something is
  unconfirmed, onboarding probes at 2 s — no busy loops anywhere.
- One agent process at a time, enforced by `AgentSupervisor`.
- Log tables capped and rotated, so a week-long agent run cannot fill the disk.
- `AGENTS.md` tells the agent explicitly not to run local models or leave services behind.
