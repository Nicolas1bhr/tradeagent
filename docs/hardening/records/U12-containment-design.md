# U12 — agent process containment: design (2026-09-02) and its design challenge — SUMMARY

The 349-line design document and the 247-line Codex challenge were lost with the session scratchpad. This is the
manager's reconstruction of both from their reports. Nothing here is built.

## The problem

The AI CLI (Codex/OpenCode, Node-based) runs as the same Windows user as TradeAgent, unsandboxed (Codex is launched with
`--dangerously-bypass-approvals-and-sandbox`). Proven or established from source: it can read `state\ipc.token` and
`state\bridge.auth`; write `state\tradeagent.db` (clearing a reconciliation flag unpauses live trading — executed probe),
rewrite `bridge.auth` and squat the bridge pipe during an app restart to reach the ATAS adapter directly; drive
TradeAgent's own Approve button through UI Automation (nothing distinguishes a human click from a UIA invoke at the same
integrity level — this SATISFIES LIVE_CONFIRM rather than weakening it); a detached child survives "Stop the AI". The
money boundary today is persuasion (AGENTS.md) plus checks a same-user process can walk around. Threat table: 14 assets,
"what stops them today" honestly NOTHING for 8 (settings row, `needs_reconciliation`, `bridge.auth`, `runtimes.json`,
`bin\trade.exe`, the installed image, the Run key, the Approve button).

## The designer's recommendation

1. **Job Object now** (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, no `BREAKAWAY_OK`; nested jobs on Win 8+ mean a vendor CLI's
   own job nests rather than escapes) — free, no UX cost. (= unit U13.)
2. **Feasibility spike on the box**, then **launch the CLI at LOW integrity level** with three labels: Low on
   `workspace\` + a private TEMP + vendor dirs (`CODEX_HOME` documented; OpenCode's data dir relabelled in place);
   Medium+NoReadUp on `state\bridge.auth`; Low on the gateway pipe (so `trade` keeps working) while the bridge pipe stays
   Medium and becomes unopenable. Everything TradeAgent creates is unlabelled = Medium and default policy is NoWriteUp, so
   the DB, `runtimes.json`, `bin\`, `tools\`, the installed image and `HKCU\…\Run` become unwritable with no code change.
   UIPI: `SendInput` "permitted to inject input only into applications at an equal or lesser integrity level"; UI
   Automation is not an exception (a UIA client reaching higher-privilege UI must be signed with `uiAccess=true` and
   installed in a secure location). Cost: `.NET` cannot launch with a token, so `AgentSession.RunTurnAsync` moves to
   `CreateProcessAsUserW` + `CreatePipe` stdio — the path whose failure mode is "the chat hangs forever". A plaintext IPC
   token copy in a Low-readable file was proposed (DPAPI at Low IL NOT VERIFIED).
3. Vendor sandbox flags last (only meaningful once `runtimes.json` is unwritable).
Rejected: AppContainer as plan A (Codex sign-in needs inbound loopback on :1455 — `CheckNetIsolation LoopbackExempt` is
"development only"), a separate local user (admin to create, stored password, wrong desktop session), Windows Sandbox/VM.
Spike: nine runnable steps with Medium positive controls; step 5 (five boundary assertions both directions) decides
shippability; the UIA/SendInput crux must be asserted by EFFECT (UIPI blocks silently, no error).

## The design challenge (Codex, §4.6): DESIGN VERDICT UNSOUND — 5 of 6 sections; §6 SOUND WITH CHANGES

Required changes, as reported: cover every vendor process with one launcher/job; create pipe labels atomically; remove
the client `CreateNewInstance`; make the bridge pipe NoReadUp+NoWriteUp; put `bridge.auth` in an inheriting NRNW directory
and verify after every rewrite; **eliminate the globally readable token copy**; test every Avalonia/message input path
(not only `SendInput`); make physical-input provenance mandatory or use a private desktop; **evaluate AppContainer first**
or combine Low IL with a genuinely restricted token and a sandbox SID; identify gateway clients by job/container identity
rather than a shared plaintext secret; add reparse-safe Medium/Low crossings, immutable Medium-owned instructions and inbox
staging, migration/relabel verification, broker-escape tests, job resource limits; give every spike step an independent
effect-based positive control and test the production launch paths; move file/pipe lifecycle and Medium-on-Low confused-
deputy behaviour to the top of the uncertainty list; test the final launch API under a standard account
(`CreateProcessAsUser` privileges). §6: keep Job Object first but universal; spike AppContainer alongside Low; fix the known
file/pipe defects first; **the decisive security test is that a non-physical approval-state transition is impossible.**

## Next

A revised design (the designer's agent is gone with the process; re-brief from this summary and the challenge list),
then a second challenge, then decision 6 to Nicolas with the trade-offs written out. U13 (Job Object) can proceed
independently now.
