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
