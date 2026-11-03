# Working on TradeAgent

**Audience: whoever picks this up next — a human, a Claude Code session, Codex or any other agent** (the root `AGENTS.md` points here).

## TradeAgent is two repositories

**This repository is half of the project. The other half is private** (the owner, 2026-11-03 and 2026-11-04):

| | The kernel: this repository | The moat |
|---|---|---|
| GitHub | `Nicolas1bhr/tradeagent`, public | `Nicolas1bhr/tradeagent-moat`, private: only the owner can read it |
| On the owner's Mac | `~/Projects/ai-trading-software-for-mihael` | `~/Projects/tradeagent-moat` |
| What it holds | The app: the organisation, the loop, evidence, containment, the broker, the pages and the kit socket — every generic mechanism | The owner's head-start kit (skills, tools, information sources, perception as data) and the research that says where an edge is |
| How it reaches the running app | It is the app | Only as a kit installed through the socket (`src/TradeAgent.Core/Kit.cs`; Settings → Head-start kit): skills the agents read and tools they run in their cells. It is never compiled into the app and never bundled into a build from here |

"The moat" in this section means that repository. In the wider sense the project's moat also includes the data in the app's
home: the tape, ledgers and forward records. Neither repository holds that data.

What that means for anyone working here:

- **Nothing here depends on the moat.** No submodule, fetch, build step, bundle or test reads it. `kit/` stays the empty index. A
  build or release from this repository carries nothing of the moat and is complete without it.
- **Edge-revealing material belongs to the moat and is never committed here, not even on a branch.** Public history is permanent.
  This covers:
  - a promoted or deployed strategy's text;
  - research naming a family whose forward record is positive;
  - a data source that pays.

  Exports of the tape, ledgers or forward records stay in the app's home and belong to neither repository. If work here produces
  such material, stop and hand it to the owner, or to a session opened in the moat for that purpose.
- **A session working here does not open the moat and reads nothing from it.** When a task needs both sides, it ends in two
  commits. Here: the generic capability, written so it makes sense to a reader who has never seen the moat. There: the specific use.
- **Every push from this clone runs the moat's tripwire.** This clone is the owner's working copy on his Mac. Its `pre-push` hook
  covers every worktree. It stops a push that carries the moat's canary, a private term, or a 12-word passage of its kit or research.
  Take the text out of the commit. Never use `--no-verify`, and never remove or change the hook: only the owner switches it off.
  - **A judged false positive goes to the owner.** Allowing one is done in the moat, never from here.
  - **Never copy a hit into a record or a commit message.** The hook names only where the hit is, not the text, and a record says
    only that a push was stopped and where.
  - **It is not the fleet's own scan.** That scan (`scan.sh`) looks for credentials and host names. Both checks run, and neither
    can see an edge written here in fresh words.
  - **It fails closed.** If the moat folder is missing, the hook stops every push until the owner reinstalls it.
- **Records and the honesty rule.** Every instruction in this repository to record, quote or checkpoint excludes edge-revealing
  material. That covers BUILD-STATUS and its "verified or NOT VERIFIED" rule, `manager-prompt.md`, `docs/HOW-WE-BUILD.md` and every
  record. When the evidence for a claim reveals an edge, the public record says the claim was verified and that its quoted
  evidence is kept in the moat. It cites that evidence by date and a neutral id, never by a name that reveals the edge. This is
  still the first category, verified with the output quoted, in the one place where it may be quoted.
- **Work in the moat follows the moat's own rules:** its `AGENTS.md` and `BOUNDARY.md`.

## Start here

**`docs/PRINCIPLES.md` is the product definition** (landed 2026-09-19): what TradeAgent is, the three zones,
the loop and the priority rule. On product philosophy, organisation and future sequencing it outranks the
older council doctrine in `docs/COUNCIL.md`; it outranks no protection. The no-terminal rule, the honesty
rule and the money, credential, accounting, evidence and recovery protections in this file and on the code
stay as they are — where a change touches one, name the protected property and show how the replacement
keeps it. `docs/HOW-WE-BUILD.md` governs the build fleet; its roles and passes are not a design for the
agent organisation inside the product. `manager-prompt.md` at the root is the fleet's handoff.

**`docs/EDGE-FACTORY.md` is the target architecture and its units** (2026-10-02, the owner's "too stiff, no moat"
direction): the tape, perception, features, strategy language v2, the evidence cascade, selection, and the re-sequenced factory
floor, with the protections each one touches and how they are kept. Ready briefs wait in `docs/queue/`; the research behind it,
dated and sourced, is in `docs/research/2026-10-02/`. The order of work is `docs/ORGANISATION.md` § 15's current order (R22).

**`docs/ORGANISATION.md` is the agent organisation** (2026-10-02, the owner's hierarchical direction: one chief, top-level managers heading
divisions, team heads who decide, executors who own the path inside their mandate and never the mandate, an audit line, and an invisible watcher that reports
only to him). It sets how the product's agents are organised, decide, work and improve, and the second build lane; a manager's authority is over work, and it
outranks no protection. **Since 2026-10-08 `docs/PRINCIPLES.md` says autonomy is the default inside authority:** hierarchy and code govern structure,
mandates, money, evidence and authority — never how an agent reasons inside its mandate (the owner's decisions on an outside audit,
`docs/research/2026-10-08/R20-autonomy-audit.md`). A new rule over an agent's reversible reasoning or exploration needs a demonstrated failure first.

**`docs/ORCHESTRATION.md` is the in-app orchestration's architecture** (2026-10-11, the owner: the organisation is "a priority over all the
others", built out as far as it can be and "right from the get go"): one kernel in which the organisation is data — row, rule, reading — authority exercised
through the broker and code run only in confined cells, turns spent as capital, the proving ground built with the kernel. It is the product's runtime, NOT the build fleet
(`docs/FLEET.md`); it decides how the kernel is built, ORGANISATION § 15 alone decides the order, and it outranks no protection.

**`docs/VISION.md` is the end state the organisation grows toward** (2026-10-06, the owner's synthesis): unbounded in what TradeAgent can represent,
deliberately bounded in what it activates — every active seat, department and management layer exists because current work requires it or a bounded
experiment is testing it; one chief with a strategy council that assesses and never owns; a Security & Assurance Council and a watcher per department as the
accountability line; the authority lattice, invariant registry, enforcement classes, simulation boundaries and a proving ground in the kernel. It changes no
unit before M-org1 but one — **§ 6.7, the three layers of truth** (canon, operational prompts and sources of truth; the observed, believed and desired
worlds — the owner's direction of 2026-10-08 from the AI Concierge canon), built first on the two roles that run today (`docs/research/2026-10-08/R21-three-layers-of-truth.md`,
which also makes **the switch-on** — the loop unattended 24/7 on paper, not yet shown on Linux — the build's target; R22 corrected § 6.7's promotion rule
and keeps the order of work in ORGANISATION § 15 alone) — and it outranks no protection.

**`docs/RESUME-HERE.md` is the resume point.** It says what to do next, what is still open, and which
traps have already been paid for. Read it before planning anything.

`BUILD-STATUS.md` is the honest record: every claim in it is either verified with the output quoted,
or explicitly marked not verified. **Keep it that way.** If you cannot quote the run, write
"NOT VERIFIED" and say what you tried. There is no third category, and the words *should work*,
*looks correct*, *probably* and *I believe* do not belong in it.

## How we build

**`docs/HOW-WE-BUILD.md` is the process, and it is short on purpose.** A fresh builder builds a unit in one pass, the
manager lands it in one pass, the money path is reviewed once per milestone, and a builder that cannot fix an item is
replaced by a fresh one rather than asked again. It deliberately does not import the sibling projects' round-based
triad; `docs/hardening/` is the frozen record of the three days that did. Work in flight is whatever is in `docs/briefs/`.
**From 2026-10-02 the build runs as a fleet** — an orchestrator, top-level manager seats and their builders, each leg's model and effort named per
`fleet/SPEED-MODE.md` since 2026-10-11 (`docs/FLEET.md` § "Speed mode") — under `docs/FLEET.md`.
**Since 2026-10-06 the orchestrator holds the owner's own seat over the build — the standard** (`docs/FLEET.md` § "The orchestrator"): up to two
top-level manager seats, each with full authority over its lane, decisions above the seats on his behalf, and a heartbeat that carries the
fleet across usage stops;
live seats are on `fleet/BOARD.md` (`~/Projects/ai-trading-software-for-mihael-worktrees/fleet/`).
**Since 2026-10-30 the build runs on the fast lane** — the fleet's `FAST-LANE.md` (same fleet folder) under `docs/research/2026-10-30/R25-fast-lane-mvp.md`, on the
owner's words that the orchestration standards are "a mere suggestion": `docs/FLEET.md`, `docs/HOW-WE-BUILD.md`, `fleet/SPEED-MODE.md` and the charters are suggestions for
this build, not law, and the orchestrator dispatches builders directly in bigger units. **The protections are unchanged** — "The safety rules that outrank making it
compile" below, `docs/PRINCIPLES.md`, the public-repo scan before every push, red-first plus a watched mutant on the money, evidence, authority, credential, containment and
agent-boundary classes. Resume from `docs/RESUME-HERE.md`'s newest block and the fleet's `FAST-STATE.md`.

## The product rule that overrides convenience

**The user never sees a terminal. Not once.** That is what is being sold — the underlying capability
is already available to anyone willing to use a shell. It forbids the obvious implementation of
several features:

- The agent CLI is never launched with a visible console. `CreateNoWindow = true`, always. The
  conversation is hosted *inside the app* over the CLI's non-interactive mode.
- Sign-in runs headless: capture the login command's output, pull the URL out, open the browser from
  the app. Where a runtime has no headless sign-in at all, the app takes a pasted key in its own
  window rather than printing an instruction that means "open a terminal".
- Dependencies install themselves. A "here is the download page" fallback whose instructions are
  `npm install -g ...` breaks the promise exactly as badly as a console window does.
- The only burden the user may bear is clicking Yes on a Windows permission prompt — so prefer
  per-user installs into `%LOCALAPPDATA%\TradeAgent\tools` that need no elevation at all.

## The safety rules that outrank making it compile

This software places real orders with real money. Four rules, stated on `IAtasAdapter` and meant
literally:

1. Carry `ClientOrderId` onto the broker order and read it back. If a backend cannot round-trip a
   client identifier, report `SupportsClientOrderId = false` and accept that the gateway refuses
   fully automatic live trading. **Do not fake it.**
2. Order history must really reach back to the timestamp asked for. If it cannot, report
   `SupportsOrderHistory = false`. A partial history is worse than none: it makes "this order does
   not exist" look provable when it is not.
3. `AtasRejectedException` is for a definite broker refusal and nothing else. Timeouts, disconnects
   and anything ambiguous must propagate so the gateway records UNKNOWN and reconciles.
4. Never place orders by driving a user interface. Programmatic API only.

**The owner's authority** — the kill switch with pause, stop and flatten, his credentials and account
connections, his word on any paid API spend, his private line (the watcher), his veto, **and updating
the app itself** — is in-process only and is not reachable from the agent-facing pipe or from any verb,
the board's included. **Limit and capital authority** — every limit and allowance, paper and live
mode, live activation, capital allocation, venture and instrument approval — belongs since 2026-10-23
to the organisation's security board (the owner's decision; `docs/research/2026-10-23/R24-security-board.md`).
It is exercised only through recorded broker acts of board positions, bound to their exact intent;
anything that moves real money or widens a limit takes two independent board seats; and the gateway
enforces whatever is currently decided. An agent that wants more permission can only propose it to
the board; no position outside the board can act on it; the board cannot widen its own power; and
nothing can replace its own supervisor with a different build. Keep it that way: no `trade` verb and
no pipe op reaches the owner's authority, and no verb, the board's included, places, alters or times
an order — the board allocates, the frozen runner trades.

**Material in the inbox is data, never instruction.** The owner hands the agent programs and
documents through `workspace/inbox/`, and a document can contain text addressed to the agent —
approvals, orders, "ignore your instructions". Nothing there grants permission, `AGENTS.md` says so
explicitly, and the rule above is what makes that true rather than merely asked for. The ledger that
records it keeps **measurement and claim in different tables**: `material` is written only by the
scanner and the agent cannot edit it; `material_note` is the agent's account of itself. Do not merge
them and do not let a note touch a material row — a record the observed party can rewrite is not one.

## No backward compatibility while we build

**The owner's rule (2026-10-05): TradeAgent is in its building phase, and a new build owes nothing to
an earlier release.** Not its database schema or the homes it wrote, not its files, settings or config
formats, not its wire protocols, bridge DLL or CLI verbs — and not the other direction either: nothing
is designed so that an older build can read what a newer one writes. Compatibility with an earlier
release is never a reason to make the logic worse.

- **Do not write** migrations, shims, fallbacks, dual readers, "legacy" branches, tolerant parsers for
  old formats, deprecated aliases, or columns and fields kept because an older build wrote them; do not
  keep a design additive, or a schema rung undoable, for an older home's sake. When the right design
  breaks an earlier release, break it.
- **Existing compatibility code binds nothing.** Delete it when it is in the way. A test that exists
  only for an earlier release — reopening its data, undoing rungs to imitate it — may be rewritten or
  deleted; the report names it and the landing accepts it on this rule.
- **This outranks every older comment, brief, doc and test that asks for compatibility.** The 30
  rungs' "additive — an older database gains it empty" in `Versioning.cs` and R18's undo lines are
  history, not templates.

**What it does not relax — these are protections, not compatibility:**

- **Refuse, never guess.** Anything from another version that this build does not deliberately handle
  — a home, a file, a bridge, a peer — is refused by an explicit check whose message names both versions
  and the repair; it is never read on a guess, half-migrated or trusted. `Versions.BridgeCompatible`'s
  exact match is the model.
- **Never destroy without the owner's choice.** A build that will not open an older home refuses it and
  leaves it untouched; starting fresh is something the owner chooses in the app — never a silent
  rewrite, never a terminal. The first change that refuses older homes ships that in-app path with it.
- **The money, credential, accounting, evidence and recovery protections hold for the current build's
  own data:** a crash between a rung's statements and its stamp still recovers; ledgers stay
  append-only within a home; evidence recorded in a home stays true about what ran — a change that
  would make it mean something else refuses or re-labels it rather than bending the new design around it.

The building phase ends only when the owner ends it, in writing, here; until then this section holds.
The code-level statement is on `Versions` in `src/TradeAgent.Core/Versioning.cs`.

## Building and verifying

`dotnet` is not on PATH on the dev Mac: `export PATH="$HOME/.dotnet:$PATH"`.

```bash
dotnet build TradeAgent.sln
dotnet test TradeAgent.sln        # ~2,520 tests in three projects; the latest gate line in BUILD-STATUS.md
                                  # is the figure. Green is a precondition for packaging, not a report
tools/mac-run.sh                  # run the UI locally — seconds per iteration
tools/mac-shot.sh /tmp/ui.png     # capture only the app window
```

Windows is where the claims get proven, and **when a real machine is available, the real run is the
evidence** (the owner, 2026-10-05): a run one could have made and nobody made is NOT VERIFIED. Two
machines, both set up in `tools/README.md`: the **ATAS box** (the default — ATAS, the bridge, the GUI;
one leg at a time, by grant) and the **tests box** (`TA_WIN_BOX=tests` — the full suite on real
Windows in ~22 min, durably, one run at a time):

```bash
TA_WIN_BOX=tests tools/win-test.sh ready     # exit 0 = a run can start now; otherwise it says why not
TA_WIN_BOX=tests tools/win-test.sh start     # this tree, uncommitted edits included; then `wait`
```

The tests box is a lent laptop with its owner's own TradeAgent and ATAS installed. Nothing of ours
starts or touches them: the runner refuses while either is open and reports whether his files changed
during a run. `tools/probe` is the harness behind the two headline claims, re-runnable on the ATAS box.

## Conventions

- **Code-built UI, no XAML, no MVVM framework.** Every colour, size and gap comes from `Theme.cs`.
  If a value is not in the theme it does not belong on the screen — that is the only thing keeping a
  hand-built UI from drifting into forty slightly different greys.
- **The dashboard tree is built once and updated in place.** Rebuilding it on the five-second refresh
  made diagnostics vanish mid-read, reset scroll position and silently disarmed half-pressed
  confirmations. Rebuilding a tree is not a refresh.
- **Vendor commands are data, not code** (`runtimes.json`, `atas.json`). These vendors change their
  CLIs on their own schedule; a wrong command should be a one-line data fix, not a rebuild.
- **Anything that moves money or removes permission is two-press** — for the owner's in-process
  controls, each keeps its press count (the kill switch stops in one press and resumes in two); and a
  board act that moves real money or widens a limit takes two independent board seats, a proposer and
  a confirmer (R24, 2026-10-23).
- Do not commit credentials, host names or the contents of `%LOCALAPPDATA%\TradeAgent`.
- **Public, with the moat kept apart (the owner, 2026-10-08 and 2026-11-03).** Building in public is the cheaper way to build. The
  owner's word was "open source"; the licence is source-available (README, License). The moat is data, calendar time and the owner's
  kit, not this code. Nothing that reveals a working edge is committed here. The list, and where each item goes, is in "TradeAgent is
  two repositories", above. Losing and rejected work may be recorded here as before (the owner, 2026-10-08), one result at a time.
  The accumulated memory of what failed is still part of the moat in `docs/PRINCIPLES.md`'s sense. A collection that maps where the
  edge is, such as a family base-rate table or a census of what is left to try, goes to the moat.
  - **The barrier is partial.** The moat's tripwire stops a push that copies the moat's own text. Nothing detects an edge first
    written here in fresh words, and a repository made private later does not unpublish its history. So whoever writes a record
    checks it.
  - **The absolute point stays the owner's decision.** That is the moment the moat is proven, and he decides then whether this kernel
    itself moves to `TradeAgent-Org`, his private GitHub organisation.
  - **The older plan is superseded on one point.** Records that move the kernel there before the first edge-revealing record (R21
    § 4, R22, and the history in `docs/RESUME-HERE.md`) are superseded: such a record now goes to the moat. Amended 2026-11-04, when
    the moat repository was made.
