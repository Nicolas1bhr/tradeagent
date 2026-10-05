# Containment decision — 2026-10-06

Probe-only spike on the ATAS Windows box (W1's light leg). No product code, no money path, nothing under
`src/` or `tests/`, no order of any kind, no model call. The harness is `tools/containment-probe/`
(standalone, outside `TradeAgent.sln`). Evidence: one file per cell in `./evidence/`, plus `matrix.json`
(31 probes), `run-meta.json`, and the raw in-sandbox `stdout-*.txt`. Labels follow the factory plan §2.1
(`PROBE@` = the actual target binary/OS exercised; `DOC@` = primary doc read that day; `DESIGN` = proposed).

## Threat model used
Arbitrary code running as an AI seat, assumed to actively try to: read or write TradeAgent `state/` and the
gateway DB; read the owner's provider login (`%USERPROFILE%\.codex\auth.json`), `.ssh`, browser profiles;
escape a permitted path via junction/symlink/hardlink; spawn shells/interpreters and grandchildren to do the
above; persist (scheduled task, Run key); open/inject a host process; reach a privileged pipe by name; reach
the network and loopback; survive cancellation; and read whatever provider credential it is given. It is
assumed malicious and competent, not merely a well-behaved agent (factory plan §9).

## Target
- Windows edition/build: **Microsoft Windows 11 Pro, 25H2, build 10.0.26200.9457** (`winver`/CIM; `run-meta.json`).
- Arch: x64. Runtime: **.NET 10.0.11** (host 10.0.400 SDK). Session: elevated (High integrity).
- TradeAgent build: branch `r-containment` off `main@2a12951c`; `Containment.Sandbox()` reports **NONE** on
  every platform today (`src/.../Containment.cs`), and the codex CLI runs unconfined, sharing one `CODEX_HOME`
  (`RuntimeManifest.cs`). The probe is standalone and references no `src/` type.
- On the box: Hyper-V-All = **Disabled**, Windows Sandbox = **Disabled**; `processmodel.dll` present;
  Dev Mode off; node/npm/python/pip/git/curl/icacls/schtasks present; codex installed via npm under `%APPDATA%`.

## Candidates tested
| Candidate | Version/API | Status | Startup | CLI compatibility | Credential result |
|---|---|---|---:|---|---|
| **C2 — stable AppContainer** (`CreateAppContainerProfile` + `SECURITY_CAPABILITIES` via `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`; `DOC@2026-10-06` appcontainer-isolation, ms.date 2025-07-08) | stable Win32 | **PROBE pass** (17/20 cells pass, 2 N/A, 1 NOT RUN, 0 fail) | profile ~20–23 ms; warm worker launch ~0.96 s; worker peak WS ~21 MB | .NET worker runs **only after** granting the container the probe tree, a writable TEMP, and the window station/desktop; a bare launch dies `0xC0000142`. cmd/powershell/Add-Type run; node and codex do **not** run bare | owner login OUTSIDE is hidden from in-sandbox code (err 5); a credential placed INSIDE is readable (Topology D) |
| **C1 — `Experimental_CreateProcessInSandbox`** (`DOC@2026-10-06` E-MS-CREATE-SANDBOX, page ms.date 2026-06-01: *"These APIs are experimental and subject to change"*; *"Header: Not publicly available (use GetProcAddress)"*) | experimental, spec `0.1.0` | **availability PROBE only; matrix NOT RUN** | export resolves; call returns `FALSE`/`ERROR_INVALID_DATA`(13) on an invalid spec | not reached | not reached |
| **C3 — Hyper-V isolation** | — | **NOT RUN** | — | — | — |

- **C1 detail:** `processmodel.dll` loads; **both** `Experimental_CreateProcessInSandbox` and
  `Experimental_CreateProcessAsUserInSandbox` are exported on 26200; an isolated child call rejects a
  deliberately-invalid spec with `ERROR_INVALID_DATA` (13) — the entry point is live and validating. The full
  matrix is **NOT RUN** because the launch needs a *compiled FlatBuffer* `SandboxSpec` (file id `SBOX`, schema
  `SandboxSpec.fbs`, version `0.1.0`) and that schema is **not public**; a valid spec cannot be produced
  honestly, and guessing one is exactly what this project refuses (factory plan §2.3, "refuse, never guess").
- **C3 NOT RUN:** Hyper-V and Windows Sandbox are Disabled on the box; enabling either needs a feature turn-on
  + reboot — the owner's call (brief; no reboot, no feature enablement this leg).

## Property matrix
`PROBE@2026-10-06` on the target above. C2 outcomes; C1/C3 as noted. "pass" = the security property held
(a negative control that was DENIED, a positive control that was ALLOWED). Full detail per row in `./evidence/cell-NN.txt`.

| Property | C1 | C2 | Evidence artifact |
|---|---|---|---|
| workspace RW | NOT RUN | pass (ALLOWED) | cell-01.txt |
| RO mount readable / not writable | NOT RUN | pass / pass (err 5) | cell-02.txt |
| state/ read blocked | NOT RUN | pass (DENIED err 5) | cell-03.txt |
| state/ write blocked | NOT RUN | pass (DENIED err 5) | cell-03.txt |
| user secrets unreadable (.codex/.ssh/browser) | NOT RUN | pass (DENIED err 5, ×3) | cell-04.txt |
| reparse (junction) escape fails | NOT RUN | pass (creation itself refused) | cell-05.txt |
| descendant containment (cmd/PS/node) | NOT RUN | pass (all DENIED) | cell-06.txt |
| persistence (sched task / Run key) blocked | NOT RUN | pass (both DENIED) | cell-07.txt |
| open/inject host process blocked | NOT RUN | pass (DENIED err 5) | cell-08.txt |
| direct privileged IPC (named pipe) blocked | NOT RUN | pass (DENIED, ACL'd as the gateway pipe) | cell-09.txt |
| broker route works | NOT RUN | N/A (broker unbuilt; see Decision) | cell-10.txt |
| revoked/expired grant refused | NOT RUN | N/A (app-layer: AgentGrants) | cell-11.txt |
| public HTTPS only where granted | NOT RUN | pass (DENIED no-cap → ALLOWED with internetClient) | cell-12.txt |
| loopback/LAN reachability | NOT RUN | recorded: loopback BLOCKED by default | cell-13.txt |
| npm/pip install, no host-global change | NOT RUN | NOT RUN (time bound) | cell-14.txt |
| compile + run generated tool | NOT RUN | pass (Add-Type compiled + ran) | cell-15.txt |
| cancellation kills descendants | NOT RUN | pass (child+grandchild alive→dead on job close) | cell-16.txt |
| restart revives no authority | NOT RUN | pass (grants in-memory; profile is identity only) | cell-17.txt |
| provider secret hidden from creative code | NOT RUN | **split**: OUTSIDE hidden (pass); INSIDE readable (Topology D) | cell-18.txt |
| performance | NOT RUN | recorded (≈20 ms profile, ≈0.96 s warm, ≈21 MB) | cell-19.txt |
| no-terminal setup | NOT RUN | pass (per-user API, no elevation, no console) | cell-20.txt |

### The credential question (cell 18 — the headline, factory plan Law 2)
Measured, with a DUMMY `auth.json` only (never the owner's), no model call:
- **Topology D (whole CLI + its `CODEX_HOME` inside one AppContainer) is unsafe here:** code inside the sandbox
  opened a credential file placed in the workspace (18a ALLOWED). Arbitrary in-sandbox code reads any login put in with it.
- **A credential kept OUTSIDE the sandbox is hidden** from in-sandbox code (18b DENIED, err 5) — the same result as
  the owner's real login under `%USERPROFILE%\.codex` (cell 4a).
- **codex does not run in a bare AppContainer** (18c): its npm install lives under `%APPDATA%`, which is not granted.
  To run codex confined you must grant its program dir *and* give it a login; if that login sits inside the
  container, 18a applies.
- **Therefore:** C2 confines a seat from the owner's login and from `state/` (exactly what `U-contain-seats`
  requires). But a *codex-CLI* seat cannot be both functional and have its login hidden from its own generated
  code inside one AppContainer. The provider credential must live in the **app-owned harness (Topology A)**, or be
  a **narrow/disposable token** — never the owner's ChatGPT login dropped into the sandbox (Topology D).

## Decision
**Chosen for the next slice (`U-contain-seats`, ORGANISATION §15): build on C2 — a stable AppContainer launched
with `CreateProcess` + `SECURITY_CAPABILITIES`,** with per-seat grants of exactly: a writable workspace, a
writable TEMP inside it, RX on the seat's program files, the `internetClient` capability only where egress is
intended, and the minimal window-station/desktop ACE a .NET/CLR seat needs to start. Everything else is
default-denied, measured: `state/`, the owner's login/`.ssh`/browser, host processes, the gateway pipe,
persistence, loopback — all refused from inside, and descendants inherit the boundary.

**Why:** C2 is stable, documented, needs no elevation/feature/reboot and no terminal (cell 20), and on the owner's
exact build it delivered every property `U-contain-seats` names: a confined seat **cannot read `state/` or the
owner's login** (cells 3, 4, 18b), its descendants stay in (6), it cannot persist (7), inject (8), or reach the
privileged pipe (9); egress is off unless a capability is granted (12); cancellation is clean (16). C1 fits the UX
but is experimental with a non-public spec schema — benchmark it, never depend on it (factory plan §9.2 decision).

**Not claimed:**
- Not that C1 confines anything — only that its export exists and validates input on 26200 (availability only).
- Not Hyper-V/VM-grade isolation (C3 NOT RUN) — AppContainer is a user-mode boundary, not a kernel boundary.
- Not that a codex-CLI seat can keep the owner's ChatGPT login hidden from its own code inside the sandbox (it cannot — Topology D).
- Not the broker round-trip (cell 10) or grant-revocation-at-the-socket (cell 11): both are app-layer and unbuilt/covered elsewhere.
- Not `npm`/`pip` install-without-host-mutation (cell 14 NOT RUN) — a known follow-up.
- Numbers are one box, one build, elevated session; not a multi-machine or non-elevated claim.

**Fallback:** if a future need requires hiding a *long-lived* provider credential from a code-running seat in the
same boundary, C2 cannot do it and the answer is **Topology A** (app-owned harness holds the key; the sandbox gets
brokered tools, factory plan §8) or **C3/remote sandbox** for a kernel boundary — at the UX/resource cost the owner
must accept. If C2's AppContainer proves too lossy for a real CLI (node/codex needed broad grants), fall back to
Topology A for execution and keep C2 for the generated-code worker.

**Revalidation trigger (factory plan §2.4):** re-run this probe when any of — the Windows build changes; .NET major
changes; codex/node install location or auth model changes; C1 leaves experimental or publishes its spec schema
(then run C1's full matrix); Hyper-V/Windows-Sandbox is enabled on a target (then run C3); or `U-contain-seats`
begins and needs the exact grant set proven against its real seat binary. External facts older than 14 days at
that unit's dispatch are re-read.

## The box, before and after
- **Before (00:55 / 23:04Z):** `win-state.sh` → "everything works"; ATAS installed, not running, 14 Strategies
  files; TradeAgent installed, home present.
- **After (23:49Z):** `win-state.sh` → identical verdict; ATAS unchanged (not running, 14 Strategies files);
  TradeAgent intact.
- **What changed and was undone:** created and then removed `C:\ta\containment-20261006` (probe source, build,
  probe home, workspace); created and then **deleted** the AppContainer profile `TA.RContain.Probe`
  (`DeleteAppContainerProfile` hr=0, Packages folder gone); the window-station/desktop ACE for that SID was
  revoked on teardown. Cells 7a/7b (scheduled task, Run key) were **refused**, so nothing was left behind —
  verified: no `TA_ProbeContainment*` task or Run value remains. ATAS, the installed TradeAgent, and `C:\ta\repo`
  were never touched. Never used `win-push.sh`; the owner's apps were never started, stopped or connected to.
