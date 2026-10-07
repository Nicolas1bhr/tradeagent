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
**For attempt 3 — the procedure as it now stands (seat M, 2026-10-07).** GO `1828a188` (pinned on Law 10 before `U-tape-read`; landing CI ✓ ×3 + package,
37536627562). Debug bundle (say so) from the DETACHED worktree `m0-run` by `tools/mac-bundle.sh`'s steps, its binaries' sha256/16 recorded, `caffeinate` on the
app's pid, the lid open (closed, the Mac sleeps); launched on `TRADEAGENT_HOME=~/Projects/ai-trading-software-for-mihael-worktrees/observed-run-home`. The home
(read only, 2026-10-07): schema 26, which `1828a188` MIGRATES to 30 at the first start (`Database.Migrate` keeps rungs 27–30; it refuses only a newer home), so it
is used; `state/gateway.lock` is stale (pid 5957 gone; nothing holds a home file); PAPER-1; dataset 1 (binance-spot BTCUSDT 1m, holdout from 2026-06-01;
research-only from rung 30, paper unchanged); campaign 1, 0 verdicts; the envelope (BTCUSDT, 0.05, 5000, one deployment, to 2026-10-08T15:47Z); BTCUSDT allowed;
loss limits 0 (off); gpt-5.6-luna; the AI PAUSED. No campaign, download, allowlist or envelope press is owed. The start checks BTCUSDT against Binance ("Check now",
one press, only if it failed); campaign 1's first verdict pins binance-spot costs (0.1 % fee, 0.02 % assumed slippage, the checked step) in its charge; paper fills
pay them; `bars 1h` runs on paper; the paper book is spot, never short. The tape records from the launch, no stop press: Binance context, OKX EEA announcements,
GDELT news (first start ≤ 96 files, ≈ 420 MB on the wire; ≤ 25 MB a day kept); no role reads it. Presses, each recorded: "Start the AI"; gpt-6-luna on each seat
(0.16 USD reserved; refused → one press a seat to gpt-5.6-luna, 0.324); "Let the AI work on its own" (two); NEVER "Close all positions" (two closes measured ES −2;
until `U-press-close-once`); then a detached dead-man quits the app at the window's end unless paused. Verify first: `U-resume-agent` by ONE restart between turns
while working; `U-vendor-limit` only if met (since 2026-10-05 codex's logs write its limit sentence with ’, the manifest `'`; codex is now 0.160.1). Pause between
turns at a churn, the vendor's limit (the end) or the window's end. Bounds: ≤ 6 h in one local day (~16:15 → ~22:15), the 5 USD cap, the owner's plan (his Codex
Desktop shares it), the envelope; evidence into `fleet/records/`. Unsandboxed CLI: no verdict here is protected evidence.
