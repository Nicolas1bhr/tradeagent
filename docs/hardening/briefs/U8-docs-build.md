# U8 + D8 — BUILD BRIEF · the deployment and monitoring documents, and the user guide told the truth

**Tier T3 (mechanical/docs) with one T2 obligation:** every sentence that describes behaviour must be traced to code or
to a record — a user guide that says "trading through ATAS does not work" when it does is the failure this unit exists
to remove. **Legs:** you are the builder; the manager reviews against the sources you cite; no Codex (skip logged: docs).
**Rounds: cap 1** plus the manager's correction pass.

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` §6 (honesty contract) and §0.
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md` — "The user never sees a terminal. Not once."
   applies to the USER GUIDE too: no command line in any sentence addressed to the owner.
3. `docs/hardening/PROGRAM.md` (D5–D8), `docs/hardening/HANDOFF-2026-09-03.md` §2, §5 (the decisions as resolved) and §7
   (the docs backlog), `docs/hardening/records/U3-update-proof.md` and `U4-windows-eyes.md` (what was actually seen on
   Windows), `records/U2d.md` "Docs to change at integration", `records/U14.md` (protocol 3 and the fail-closed
   consequence of a missing bridge directory).
4. The current `docs/USER-GUIDE.md`, `docs/RESUME-HERE.md`, `BUILD-STATUS.md`, `tools/README.md`, `packaging/`.

## Where you work

Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u8-docs`, branch `u8-docs` cut from
`main` by the manager. Docs only: you may create `docs/DEPLOYMENT.md` and `docs/MONITORING-PHASE.md` and edit
`docs/USER-GUIDE.md`. You may NOT edit code, tests, `BUILD-STATUS.md` or `RESUME-HERE.md` (the manager refreshes those
at v0.1.2). Toolchain if you need to count screens or read strings: `export PATH="$HOME/.dotnet:$PATH"`; the box is
reachable but NOT yours to touch — cite `records/U4-windows-eyes.md` instead of re-walking the app.

## Deliverables

**1. `docs/USER-GUIDE.md` corrections (D8).** Each correction cites its source in your record, not in the guide:
- "trading through ATAS does not work" → false since 2026-08-31; describe what works (orders through the ATAS bridge on
  the connected account, LIVE_CONFIRM parking, the two-press controls) in the owner's words.
- The setup journey: the guide lists 12 screens, the app shows 16 (`U4-windows-eyes.md`). Count them from the app's
  setup code (`grep -rn` for the step/page definitions), list them by their on-screen titles, and say which ones can be
  resumed.
- "Installing the ATAS add-on" → the app calls it the bridge; use the app's word everywhere.
- The update paragraph: add the failure branch (if the checksum is missing or does not match, nothing is installed and
  the reason appears on the strip and in Settings) and the hard-stop sentence (TradeAgent will not replace itself while
  an order's outcome is unconfirmed, and says so) — from `records/U2d.md`; mark both as "from the next release
  (0.1.2)" if the guide is versioned, since U2d is not on `main` yet.
- The first-install SmartScreen sentence (decision 2: signing deferred): what the owner sees, which button to press, and
  that this is expected for an unsigned first release. No workaround instructions that involve a terminal or a policy
  change.
- A "what you will be asked to do" paragraph: the only burden is clicking Yes on a Windows prompt.

**2. `docs/DEPLOYMENT.md` (D6, half).** Audience: Nicolas installing on a machine he can sit at (the test box now,
Mihael's laptop later). Preconditions (Windows 11, ATAS installed and signed in, an account visible in ATAS — simulated
is fine); the release download and the SmartScreen prompt; the setup journey pointer; the bridge deployment (the DLL and
the `proto=3` reading after v0.1.2); how to confirm the install is healthy from inside the app; how Tailscale + SSH is
set up on a machine Nicolas wants to monitor (decision 4: channel A); a "first hour" checklist; the rollback (reinstall
the previous release — say what state survives: the database, the home). Everything stated must be something a record
shows was done at least once, or be marked "not yet walked".

**3. `docs/MONITORING-PHASE.md` (D6, other half).** The protocol for the first weeks: what Nicolas checks, how often, and
when to stop it. Built on what exists: `trade status` over SSH, the support zip, the on-machine logs (name them, say
which are append-only and which rotate — from the code), the dashboard's health rows. Define: daily check (five items,
each with the reading that means "fine" and the reading that means "stop"), weekly check, the stop rule (any UNKNOWN
order older than N minutes without a resolved card; a bridge refused; an update refused; kill switch found on), and
how to stop (the kill switch, then closing positions in ATAS by hand). The outbound-alert variant is a one-paragraph
"not built; why; what it would need". Mark every reading you could not verify against the code as NOT verified.

## Rules

- Every behavioural sentence in the three documents maps to a line in your record: `sentence → file:line or record §`.
  A sentence you cannot source is removed or marked "not yet walked". Banned words: should work, looks correct, probably,
  I believe, minor, trivial, static-verified, basically.
- Owner-facing text: short sentences, the app's own words for its screens and buttons, no command lines, no paths.
- Commit on `u8-docs` per document; one-sentence messages; no `Co-Authored-By` trailers. Do not push, merge, or touch
  other worktrees.
- Checkpoint AS YOU GO: write `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U8-docs.md`
  (MAIN worktree path; no git there) with the source map and "What I did NOT do".

## Report back

Tip sha; the three files with line counts; the number of sourced sentences vs "not yet walked" markers; anything the
guide currently claims that you could NOT confirm either way; "What I did NOT do".
