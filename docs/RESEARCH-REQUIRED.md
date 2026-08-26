# Research required before release

Every item here is something this build could not verify from a macOS host with no ATAS install and
no AI-provider account. They are recorded as data-driven or single-file so that correcting one is a
small change, never a redesign.

**Rule: check against current official sources, not against this document and not from memory.**
Prefer official docs, then official release notes/source, then maintained examples.

---

## A1 — ATAS extension API  ·  BLOCKING for real trading

**File:** `src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs` (the only file in the product that cannot
compile without ATAS). **Reference implementation:** `LoopbackAtasAdapter.cs` in the same folder shows
the exact shape and honesty each method needs.

Confirm, from ATAS's own documentation and the assemblies in your install:

| # | Question |
|---|---|
| 1 | Correct base class and lifecycle hooks for a user-loadable chart strategy, and the assembly names to reference (the `.csproj` guesses `ATAS.Indicators`, `ATAS.Strategies`, `ATAS.DataFeedsCore`). |
| 2 | Target framework ATAS loads (`AtasBridgeTargetFramework` defaults to `net8.0-windows` — unverified). |
| 3 | The folder ATAS loads user strategies from, and whether a restart is required after copying files. |
| 4 | Portfolio/account enumeration, and how to tell a simulation connection from a live one. |
| 5 | Security/instrument enumeration, with tick size, tick value and contract size. |
| 6 | Best bid/ask access, and the timestamp of the last update (staleness detection depends on it). |
| 7 | Position enumeration and the position-changed callback. |
| 8 | **Order placement carrying a client-supplied identifier, readable back from the order list.** |
| 9 | **Order history including finished orders, covering an arbitrary `since` timestamp.** |
| 10 | Modify, cancel, cancel-all, and programmatic position flattening. |
| 11 | Execution/trade callbacks, and whether they carry the client identifier. |
| 12 | Which failures are definite broker rejections versus ambiguous ones. |

Items 8 and 9 decide how much autonomy is safe. Report them truthfully in `Describe()`:

- no client-id round trip → `SupportsClientOrderId = false`
- incomplete history → `SupportsOrderHistory = false`

The gateway then refuses `LIVE_AUTONOMOUS` on that connector, by design. **Do not report a capability
you have not proven.** A partial history is worse than none: it makes "this order does not exist" look
provable when it is not.

Item 12 maps onto `AtasRejectedException` (definite) versus any other exception (indefinite). Getting
this backwards is the one mistake that can produce a duplicate live position.

## A2 — ATAS install layout

**File:** `src/TradeAgent.Connectors.Atas/AtasInstallation.cs` → `AtasLayout`.
Overridable at runtime via `%LOCALAPPDATA%\TradeAgent\atas.json`.

Confirm install directories, strategy/indicator folder, process names, executable names. Then set
`Verified = true` — until then the Doctor warns the user that these paths are guesses.

## A3 — ATAS version compatibility

How does an ATAS update affect a compiled bridge assembly? Which version range does one build cover?
`Versions.BridgeProtocolVersion` already gates a mismatched bridge (tested), but the *assembly*
compatibility rule is unknown. Feed the answer into the "Trading paused — press Repair" path.

---

## B1 — OpenCode CLI

**File:** `src/TradeAgent.AgentRuntime/RuntimeManifest.cs` → `RuntimeCatalog.BuiltIn()`.
Overridable at runtime via `%LOCALAPPDATA%\TradeAgent\runtimes.json`.

The manifest now claims a self-contained Windows x64 download, a headless conversation and a
browser sign-in. Every one of those values was read from published metadata and documentation, not
from running the program, which is why `Verified` is still `false`. Confirm against
<https://opencode.ai/docs/> **and by running the real CLI on Windows**:

- The install route: the GitHub repo, the asset pattern, and the path of the executable inside the
  archive. Then the npm fallback, through TradeAgent's own private Node.
- The version, sign-in and sign-in-status commands, and what success looks like on stdout — that
  string is what makes the wizard advance by itself, and the sign-in URL pattern is what lets
  TradeAgent open the browser instead of showing a console.
- The one-shot and resume commands, the flag that turns stdout into a machine-readable stream, and
  the approval/sandbox flags. A headless run that still waits for a keypress hangs the chat panel
  forever, which is the failure mode to look for.

Then set `Verified = true`.

## B2 — Codex CLI

Same fields, against <https://developers.openai.com/codex/cli/>. Additionally:

- Confirm ChatGPT-account sign-in works without an API key, and whether a device-code flow exists for
  when a browser cannot open.
- Confirm the sandbox and git-repo flags the manifest passes are still the right ones. The agent's
  workspace is not a git repository, and a CLI that refuses to run outside one would fail every
  message.

## B3 — Agent workspace conventions

Confirm both runtimes read `AGENTS.md` from the working directory. If either expects a different
filename, `WorkspaceBuilder.Build` should write both.

---

## C1 — Windows secret storage

`SecretStore` uses DPAPI (`ProtectedData`, CurrentUser) for the IPC token, falling back to a
`0600` file elsewhere. Confirm this is the right choice versus the Windows Credential Manager for a
per-user, per-machine secret. **Broker credentials are deliberately not in scope: ATAS owns those.**

## C2 — Installer and signing

`packaging/TradeAgent.iss` has compiled and installed on real Windows 11. What has changed since and
is therefore unconfirmed:

- `PrivilegesRequiredOverridesAllowed` is now `commandline`, not `dialog`, so setup no longer asks a
  non-technical user an all-users/just-me question it has no way to answer. Confirm setup runs with
  no elevation prompt at all, and that `{autopf}` lands under `%LOCALAPPDATA%\Programs`.
- `CloseApplications=yes` with `RestartApplications=no`. Confirm that installing over a **running**
  TradeAgent names the running program and offers to close it, and that the app is launched exactly
  once afterwards, by the `[Run]` entry.
- `AppMutex=TradeAgent.SingleInstance` is inert: the app's single-instance guard is a file lock,
  which Setup cannot see. Either create a named mutex with that exact name at startup, or delete the
  directive so nobody mistakes it for protection that exists.
- Confirm the uninstaller still leaves `%LOCALAPPDATA%\TradeAgent` (trading records and the AI's
  work) intact.

Code signing remains open: unsigned builds show a SmartScreen warning, which
[docs/USER-GUIDE.md](USER-GUIDE.md) now tells the user to expect. Budget for a certificate before
this goes to anyone who did not build it.

## C2b — Icons and setup artwork

There is no `.ico` or bitmap anywhere in this repository, so `SetupIconFile`, `WizardImageFile` and
`WizardSmallImageFile` are deliberately absent from `TradeAgent.iss` (naming a file that does not
exist fails the ISCC compile), and `TradeAgent.exe` carries the default .NET icon. A trading
application whose taskbar button is a generic icon looks unfinished on a machine it did not build
itself. Add artwork, then add those three directives and `<ApplicationIcon>` to
`src/TradeAgent.App/TradeAgent.App.csproj`.

## C3 — Bridge dependency closure

The bridge currently references `TradeAgent.ConnectorSdk`, which transitively drags
`Microsoft.Data.Sqlite` into the ATAS process. It is never used there (no connection is ever opened),
but the DLLs land in the ATAS folder. Before release, either trim the closure — move the shared enums
into a small contracts assembly — or confirm the extra assemblies are harmless inside ATAS.

## C4 — .NET runtime on the target laptop

The app and CLI publish self-contained, so no runtime is required. Confirm the resulting install size
and cold-start time are acceptable on the actual laptop this will run on, and revisit
`PublishReadyToRun` / trimming only if they are not.
