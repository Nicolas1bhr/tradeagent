# R-containment — measure, on the owner's kind of Windows machine, which isolation route can confine an AI seat before any product unit commits to one
**Protects:** the Creative/Trusted boundary that is advisory today, and the owner's provider login every CLI seat can read (`CLAUDE.md`; `docs/ORGANISATION.md`
§§ 10, 14, 16 — the owner capped unconfined seats at 3, so M-org1's executors need CONFINED seats; EDGE-FACTORY § 6.11; R17 #2). **A probe-only spike on the
Windows box (W1's light leg, the only leg on the box):** no product code, no money path, nothing under `src/` or `tests/`, nothing installed machine-wide, NO
reboot and no feature enablement — Hyper-V and Windows Sandbox are disabled on the box and turning either on is the owner's call. **Today (SOURCE at
`c80f422`):** `Containment.Sandbox()` reports NONE on every platform (`docs/COUNCIL.md`, the `U-containment` note; `Containment.cs:43-45`); the codex CLI runs
as the owner's user with `--dangerously-bypass-approvals-and-sandbox` (`RuntimeManifest.cs:618`) sharing one `CODEX_HOME` (`:497-500`).
**Candidates** (the owner's factory plan § 9.2, DOC@2026-09-27 — re-read Microsoft's pages on dispatch day and quote their dates): **C1**
`Experimental_CreateProcessInSandbox` + AppContainer (experimental, Windows 11, no public header); **C2** a stable AppContainer / Win32 app isolation launch
(`CreateProcess` with `SECURITY_CAPABILITIES`, explicit filesystem grants); **C3** Hyper-V isolation — recorded as NOT RUN with the reason (needs a reboot).
**The matrix** (factory plan § 9.3; every cell RUN with the command and its saved output, or NOT RUN with the reason — never "should"): 1 workspace read/write;
2 read-only mount readable, not writable; 3 TradeAgent `state/` unreadable and unwritable (use a probe home, never the box's real one); 4 the user's secrets —
`%USERPROFILE%\.codex\auth.json`, `.ssh`, a browser profile folder — unreadable; 5 symlink, junction and hardlink escapes fail; 6 descendants (cmd,
PowerShell, node/python) stay inside; 7 persistence (a scheduled task, a Run key) fails; 8 opening or injecting a host process fails; 9 TradeAgent's named
pipe unreachable without a grant; 10 the brokered route with a live grant works (or NOT APPLICABLE, why); 11 a revoked or expired grant is refused; 12 public
HTTPS where granted; 13 loopback and LAN reachability recorded; 14 an npm or pip install inside, with no host-global change; 15 compile and run a generated
tool; 16 cancelling kills the descendants; 17 a restart revives no authority; 18 THE CREDENTIAL QUESTION — can the codex CLI run inside at all, how its
credential gets in, and whether code inside can read it (the factory plan's Law 2, topologies A–D); 19 cold and warm start, disk, latency, memory; 20 can the
app set it up with no terminal for the owner.
Deliverables: `docs/research/<dispatch-date>/R-containment-decision.md` in the factory plan's § 22 template — threat model, target build (`winver` output),
candidates, the matrix with an evidence file per cell, the decision for the NEXT slice only (`U-contain-seats`, `docs/ORGANISATION.md` § 15), what is NOT
claimed, the fallback, the revalidation trigger; and the probe harness under `tools/containment-probe/` (no credential in it; it reads `TA_WIN_*` through
`~/.tradeagent/win.env` like every other `tools/` script; if it is a .NET project, it is OUTSIDE `TradeAgent.sln` and builds with 0 warnings).
Box rules (`tools/README.md`, `docs/RESUME-HERE.md`): `tools/win-state.sh` first; never print or commit a host name, IP or user name (redact to `$TA_WIN_NAME`);
work under a fresh folder in `C:\ta` and a probe `TRADEAGENT_HOME`, never the box's real `%LOCALAPPDATA%\TradeAgent`, ATAS or the installed app; leave the box as
found, say what changed. The factory plan itself is outside the repo — its path is in `docs/RESUME-HERE.md` item 1; §§ 8–9 and 22 are the ones this leg needs.
Report ≤ 20 lines appended here: the decision in one sentence, the matrix's pass/fail counts per candidate, the cells NOT RUN and why, what the box looked like
before and after. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.

## Report
Fresh Opus, pass 1. Probe-only on the ATAS box (default box, `TA_WIN_BOX` unset); no src/tests/sln change.
Decision (one sentence): build `U-contain-seats` on **C2 — a stable AppContainer** (`CreateProcess` + `SECURITY_CAPABILITIES`), which on Windows 11 25H2 (10.0.26200.9457) confined a seat from `state/`, the owner's login/`.ssh`/browser, host processes, the gateway-ACL'd pipe, persistence and egress (granted only per capability), with descendants contained and clean cancellation; C1 stays benchmark-only, C3 deferred.
Deliverables: `docs/research/2026-10-06/R-containment-decision.md` (§22 template) + 20 per-cell evidence files + `matrix.json`/`run-meta.json`/raw `stdout-*`; harness `tools/containment-probe/` (standalone, outside the sln).
Pass/fail per candidate: **C2** 17 pass · 0 fail · 2 N/A (10 broker-unbuilt, 11 grant is app-layer) · 1 NOT RUN (14). **C1** availability only — both exports present + callable (rejects invalid spec, err 13); matrix NOT RUN. **C3** NOT RUN.
Cells NOT RUN & why: 14 (npm/pip no-host-mutation — box time bound); C1 matrix (valid FlatBuffer SandboxSpec schema is non-public — refuse, not guess); C3 (Hyper-V & Windows Sandbox Disabled; needs feature+reboot = owner's call). Credential answer (18): owner login OUTSIDE hidden (4a/18b, err 5); a credential INSIDE is readable (18a) → Topology D unsafe; use Topology A or a narrow token; codex does not start bare (18c).
Box before/after: `win-state.sh` "everything works" both times; ATAS installed/not-running/14 Strategies and TradeAgent intact, unchanged. Changed+undone: created & removed `C:\ta\containment-20261006`; created & deleted AppContainer profile `TA.RContain.Probe` (hr=0); winsta/desktop ACE revoked; cells 7 denied → no task/Run-key left; ATAS/`C:\ta\repo` untouched; `win-push.sh` never used.
Gate: (a) `git diff --stat main -- src tests TradeAgent.sln Directory.Build.props` → empty. (b) `tools/containment-probe` build (Mac + box) → 0 warnings, 0 errors; not in `TradeAgent.sln`. (c) `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings, 0 errors. (d) `names.sh main r-containment` → removed: 0 (sets base 2157 / tip 2157; added 0). (e) one `ci-dispatch.sh` → run 37391256380 @74025d51: ubuntu-latest success, macos-latest success, **windows-latest FAILURE** = `TradeAgent.Tests.Fault.PressIdShapeTests.The_operator_cancel_all_names_its_legs_without_the_brokers_order_id` — a test this diff cannot reach (src/tests byte-identical to current main; main's own windows-latest passed on that same src at run 11a14999, 23:33Z → flake); package skipped. Left to seat P. (f) tests box: NOT RUN — no product code in this leg; experiments never go to the tests box (docs/FLEET.md).
CI secret-scan false positives (prose on secrets/tokens, fake `sk-DUMMY`, the `OPENAI_API_KEY` JSON field name, `PROBE_SECRET_*`) excluded by name via `CI_SCAN_EXCLUDE` (recorded in the seat scratchpad); no real secret/host/IP/user committed.
CI tip 74025d51; final tip adds this report only (docs) — not re-dispatched (only the report changed since the CI run).
