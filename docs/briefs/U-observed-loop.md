# U-observed-loop — the autonomous paper loop observed in the running app on this Mac with a real model, evidence quoted, nothing seeded
**Milestone claimed if it holds** (`manager-prompt.md` § 6 "Autonomous paper loop"): in the running app a real model authors a candidate, invokes `trade backtest`,
submits it with `trade verdict`, receives the verdict; a paper-eligible version is allocated to paper by app policy inside the owner's envelope, deployed, and run
forward on advancing bars; fills and costs reach the ledger and the report; a later agent turn reads them and records a keep/modify/kill/branch decision or a
justified wake. A rejected candidate that leads to another experiment is useful partial evidence. **Preconditions:** `U-deployment`, `U-runner`,
`U-language-in-home` landed; a Release build of `main`; `codex login status` exit 0 on this Mac (RUN 2026-09-20: 0). Run by the manager (screen control on
`dev.tradeagent.mac`), or by one leg given the same rules; the app's own UI and read-only verbs only — NO row is seeded, NO artifact handed between steps.
Procedure (buttons by their visible label; a two-press control's label changes when armed, re-read it): home `TRADEAGENT_HOME=$TMPDIR/tradeagent-dev`, empty →
onboarding replays. 1 `tools/mac-bundle.sh` (display awake). 2 Onboarding to the platform step: "Practice simulator" (paper is Settings-only); sign codex in if
asked. 3 Settings → Trading platform → "Use TradeAgent paper" (one press); select `PAPER-1`. 4 Settings → Market data: pair BTCUSDT, "Download 12 months"
(~25 MB, checksum-verified); live bars stay ON. 5 Settings → Private evaluation evidence: a cutoff inside the collected months, Research class, two presses →
the campaign opens (200 trials, 3 verdicts, PaperV1 pinned). 6 Dashboard → the paper envelope card: BTCUSDT, `PAPER-1`, ceilings, expiry, two presses.
7 Dashboard: the Research runtime row shows the CLI; pick the model; `AiDailyCostCap` non-zero (5 by default). 8 "Let the AI work on its own", two presses.
Then observe; intervene only to record. Stated bounds before the run: the observation window (≤ 6 h this session), the AI spend (the daily cap), the paper
envelope's ceilings; the run stops at the window's end or the cap, whichever first — exhaustion is a legitimate boundary, never reset.
Evidence, from a second shell with `TRADEAGENT_HOME` exported (roleless read-only caller; the app running): `trade status --json`, `trade data list`,
`trade pnl --json`, `trade report`; `tools/mac-shot.sh` at each state change; `sqlite3 state/tradeagent.db` READ ONLY over `ai_attempt`, `tool_call`,
`mission_event`, `publication`, `strategy_version`, `strategy_run`, `strategy_promotion`, `strategy_allocation`, `strategy_deployment`, `deployment_op`,
`execution_request`, `fill`, `forward_bar`, `paper_envelope`; the role's home (`trading/`, `out/`, `.tradeagent/next.json`) read, never written.
Record (`BUILD-STATUS.md`, ≤ 40 lines): build sha; runtime and model; data source, dataset id, cutoff, campaign id; every version hash the model froze, its
runs, its `trade verdict` answers (verdict + reason class); the allocation and deployment ids; ops, orders, fills with timestamps; costs from `ai_attempt`; the
model's own recorded decision text (quoted, labelled as the agent's claim); each manual press (the eight above, nothing else); wall-clock, cycles, interventions,
remaining gaps; what did NOT happen. Claims allowed: "forward paper observation under declared bar-fill assumptions", "a real model authored and submitted";
never executability, live readiness, realised profit, guaranteed protection, or "the system pays for itself". No favourable verdict or trade is forced.
If a step refuses in words, the refusal IS evidence: quote it, record the gap as the next unit, do not seed around it. Retire this brief with the record.
**For attempt 3 — the procedure as attempts 1–2 found it (records: `BUILD-STATUS.md`, 2026-09-20 → 2026-10-01).** Home: `~/Projects/ai-trading-software-for-mihael-
worktrees/observed-run-home` (outside `$TMPDIR`, which the OS empties after days); it already holds onboarding, PAPER-1, dataset 1 (11 months to 2026-08-31),
the holdout from 2026-06-01, campaign 1, the envelope (BTCUSDT, 0.05, 5000, until 2026-10-08), BTCUSDT allowed, gpt-5.6-luna, three versions, three runs,
and the AI PAUSED. Relaunch: `TRADEAGENT_HOME=<that path> tools/mac-bundle.sh`; "Start the AI" if the card reads stopped; "Let the AI work on its own" (two
presses). Corrections to steps 6–8: the envelope, the model, the daily cap and the Research runtime are on the SAFETY page, not the Dashboard; the default
gpt-5.6-sol reserves 6.4 USD a turn, more than the 5 USD cap ("the AI cannot start a turn at all") — luna (0.324) fits; model pills past the window's edge are
reached by keyboard (click the first, Tab ×N, Space; confirm from the note); a NINTH press is owed on a fresh home — Safety limits → "Instruments it may touch"
= BTCUSDT (empty by default; every order is refused without it); the envelope's quantity field SHOWS "0" while holding 0.05. Pause only between turns (a cut
turn is charged its reservation). The AI runs on the owner's ChatGPT Codex plan, which attempt 2 exhausted in ~2.5 h (mostly a self-wake loop, since fixed):
state that allowance as a bound and read `~/.codex/sessions/<date>/` for `usage_limit_exceeded`. On the first relaunch verify, before anything else,
`U-resume-agent` (a restart while working brings the next turn with no press) and `U-vendor-limit` (the card names the vendor's limit and time; a turn refused
before any work costs 0).
