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
