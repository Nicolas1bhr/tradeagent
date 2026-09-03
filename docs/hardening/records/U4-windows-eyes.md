# U4 — Windows eyes: the setup journey, the override card, the refusal sentence (2026-09-02/03) — D3

Reconstructed 2026-09-03 from the machine leg's two reports. The 400-line command log and screenshots `U4-10..71` were
lost with the session scratchpad. Done in a SCRATCH home (`C:\ta\scratch-home-u4`, deleted afterwards) with the
installed 0.1.1 exe; the real home's `tradeagent.db` mtime was `2026-08-31T13:54:50.94Z` (98,304 B) before and after.

## Setup journey — walked end to end, first time on Windows

Rail says STEP n OF 16. **Only 8 of 16 screens are ever shown** (1 Welcome, 3 Choose your AI assistant, 6 Choose your
trading platform, 9 Connecting to ATAS, 11 Choose your account, 14 Creating the AI's workspace, 15 Starting the AI,
16 Setup complete); 2, 4, 5, 7, 8, 10, 12, 13 verified themselves and flashed past inside the 2 s poll (codex.cmd on
PATH, `codex login status` authenticated off the user-level `%USERPROFILE%\.codex\auth.json`, ATAS installed, bridge DLL
present, prices and trading access fine). Nothing asked for a terminal, an administrator, or a credential. **RESUME
WORKS**: closed at 9/16, relaunched, came back on 9/16 with nothing re-walked. Deviation: the scratch home minted its own
bridge secret so the real bridge inside ATAS could not authenticate; the leg copied the installation's `state\bridge.auth`
(read-only on the source) to proceed. After setup: `mode PAPER · ai_trading_stopped false · live_activated false ·
execution_available true · connector atas · connector_is_paper TRUE · account CRYPTO5EB41 · Agent runtime READY "OpenAI
Codex CLI 0.147.0"`.

## The override card — through a genuinely ambiguous order (nothing seeded)

Scratch app set to LIVE_CONFIRM, live activated (two presses); `trade buy BTCUSDT 1 --limit 38675` (bid 77350) →
`APPROVAL_REQUIRED`; the bridge strategy stopped in ATAS (legend `[Stopped]`, health "installed, but the strategy is not
started on a chart"); approved in the Dashboard (two presses) → **UNKNOWN + flagged + Execution capability PAUSED + the
card**. Bridge restarted → does **not** self-resolve (8 samples over 70 s, `RECONCILING` throughout — correct: ATAS cannot
prove order history). Resolved through the card (note + two presses) → record `CANCELLED`, flag cleared, `open 0 /
unrec 0`, execution READY, "All systems ready". Book verified clean afterwards with TradeAgent closed: `probe atas` →
`ORDERS IN LIVE BOOK : 0`, `orders=0 strategyorders=0 mytrades=0 position=0`, `client_order_id_attempts=0` (nothing ever
reached ATAS). Note: `probe atas` rewrote the real home's `bridge.auth` `server_image` to its own path; relaunching the
installed app put it back.

## The bridge-refusal sentence — rendered

Honest route: with the scratch home's own secret, the real bridge's challenge fails. Rendered on the Dashboard and Checks
rows: *"the ATAS bridge did not authenticate — the peer on the bridge pipe could not prove it holds this installation's
bridge secret (…\state\bridge.auth)"*. The longer `PresentedNoProof` sentence was NOT reached (needs a peer that speaks
the protocol but holds no secret).

## Defects found → unit U6 (page, text, what is wrong)

1. **Onboarding 9/16 "Connecting to ATAS" never surfaces the refusal.** It shows five instructions and "Waiting for ATAS
   to connect." while `_host.Connector.StatusDetail` holds the sentence above; a real user redoes the five steps forever.
   Highest-value fix.
2. Top bar clips the sentence mid-word, no wrap, no ellipsis.
3. System health prints the identical ~180-char sentence twice ("ATAS bridge" and "Trading connection"), pushing Account
   / Market data / Execution capability off the bottom of the window.
4. Health-row stutter: row "ATAS bridge", detail "the ATAS bridge did not authenticate…".
5. Activity: "Trading mode set to LIVE_CONFIRM" — a raw enum on the "plain-language record" page (every other surface
   says "Real, ask me first").
6. Activity: "AI order refused: The AI is asking permission to place an order…" — it was PARKED, not refused; the
   Dashboard says "asking permission" at the same moment. Contradictory.
7. Activity: "You confirmed order u4-card-001 as CANCELLED" — raw enum; the button said "no such order exists".
8. Programmer plural twice: "1 earlier request(s) are unconfirmed"; "1 request(s) unconfirmed".
9. Override card "Last check" goes stale: still "the ATAS bridge is not connected" while the health column shows
   `ATAS bridge — connected · bridge 8.0.14, protocol 2`.
10. Checks page: "what to do: See the activity history for what happened." on eight consecutive rows, including three
    "not checked yet" rows and the bridge row whose real repair (stale `bridge.auth` → reinstall the add-on) is named
    nowhere.
11. Dashboard health "Agent process — paused — stopped": doubled state word.
12. `docs/USER-GUIDE.md` "Setting it up" lists 12 screens; the rail says 16; the guide's "Installing the ATAS add-on" is
    the app's "Installing the ATAS bridge" (jargon the guide avoids).
13. `docs/USER-GUIDE.md` "What is not finished" says "Trading through ATAS does not work yet. The piece that actually
    sends an order into ATAS has not been written." — false since 2026-08-31.
14. 1/16 Welcome presumes ATAS ("…your ATAS trading platform") three screens before the platform choice, whose
    recommended answer is the practice simulator.
15. 15/16 promises "…and take you to the main screen", then a 16th screen promises it again.
16. Back skips a real decision: on 11/16 and 14/16 the only Back is "Back to 'Choose your trading platform'"; "Choose
    your account" is a genuine choice but not a Back target (`IsDecision` omits `ACCOUNT_SELECTED`).

**Possible accessibility defect (not asserted):** UIA `Invoke` throws on this app's buttons (`patterns:Invoke`,
`offscreen:false`, empty `BoundingRectangle`) and synthesized keystrokes vanish in a focused Avalonia `TextBox`;
`setvalue` (ValuePattern) and screen-coordinate clicks work. If a screen reader sees what the harness saw, no button is
operable by assistive tech. Worth its own check.

## NOT VERIFIED

The eight auto-passed screens (a clean machine is the only way); the Inbox drop/picker COPY path (needs a person at the
keyboard); the `PresentedNoProof` sentence; ATAS behaviour with a broker (none attached).
