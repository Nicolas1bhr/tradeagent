# Backlog accumulated during the U2a/U14 hardening rounds (for U6 = UX + small items, U9 = build hygiene)

Items discovered while hardening other units; none is in scope for the unit that found it. Each names its source
record so the brief writer can quote the measurement.

## U6 (product defects / UX) — in addition to the 16 defects in `records/U4-windows-eyes.md`

- **Shutdown with an order in flight can wait up to ≈265 s at shipped values** (U2a round 9: drain = max(5 × worst
  ordinary path, emergency budget + worst path) + settle). The app must SAY so while it waits ("waiting for an order to
  settle — do not force-close") and must never look hung; an idle shutdown stays fast. Source: `records/U2a.md` "Round 9",
  manager ruling in `records/manager-log-2026-09-03b.md`.
- **The emergency answer at 2 s reads outcome-first** ("'cancel-all' is NOT confirmed — check your positions and orders
  in ATAS", then the connection detail) — the Dashboard/CLI rendering of that sentence and of the five per-leg outcome
  words (confirmed / rejected / not-sent / sent-not-confirmed / sent-still-working) has not been seen on Windows.
  Source: `records/U2a.md` rounds 7–9.
- **`BridgeRow` precedence strings** (U14 round 8 V4: the newest observation wins — auth failure after a protocol
  refusal, silent new peer, etc.) are measured as pure functions only; the rendered row on the Dashboard is NOT verified.
  Source: `records/U14.md` "Round 8".
- **The status row must never outlive ATAS**: a refused-peer sentence while ATAS is closed should read "waiting for
  ATAS to start" (U14 V4 second instance). Verify on the box with ATAS closed.
- The CLI's replay wording is now per-op (only Buy/Sell honour same-id replay until U2c-1 lands) — re-check the AGENTS.md
  and USER-GUIDE sentences after U2c-1 widens it. Source: `records/U2a.md` round 7 PRIOR 8.

## U9 (build hygiene / release)

- **`TreatWarningsAsErrors`**: an incremental build reported "0 warnings" over a new CS8619 (U2a round 8). Every gate is
  now `--no-incremental`; turning warnings into errors is the mechanism that would catch the class — DEFERRED because the
  box's release build of the bridge showed 5 warnings in the adapter compile (U14 round 5) that would go red. Examine
  those five at the v0.1.2 build, fix or suppress with a reason each, then enable the property.
- The test suite's integration project now runs ≈6 min (the 10 s liveness grace is waited out by the phase probes) —
  a slow-test category is the lever if CI time becomes a problem; never shorter deadlines. Source: U2a round 7 ruling.

## For the v0.1.2 box session (not a unit — the release step)

- Redeploy the bridge DLL at protocol 3 (U14) and read `proto=3` from the app; the old v2 DLL must be REFUSED with the
  reinstall sentence (a live check of the round-7b/8 behaviour).
- Which ATAS teardown callback fires (`OnStopping` vs `OnDispose`) — observe once with the real bridge; until then the
  lease-release path is verified only by the extracted `AdapterTeardown` tests.
- A deliberate 64-char and 65-char client-order-id probe against ATAS (U2a): the generated ids are ≈23 chars and prove
  only the charset.
- Mutant B4 (the Windows no-buffer pipe stall) has been run by nobody — run it once on the box.
- The five keyboard minutes for the Inbox picker COPY-path test (decision 5).

## LOW batch carried out of U2a at integration (test quality, no product change)

- `ConnectorSendDeadlineTests.cs:848` (the round-11 flake rewrite) captures `connectedAtTheVerdict` before the delayed
  liveness judge runs and disposes before grace expiry, so a regression in `PeerAnsweredSince` is not observed by that
  test (Codex r11 LOW). The 12-phase liveness probes on `u2a-verify-r9-probes` do observe it — lift one into the suite.
- `AtasConnector._pending` leaks an entry when a caller cancels an emergency (U2a round-11 builder, flagged not fixed;
  the rounds 10+11 verifier measures it).
