# U-security-sweep-a — the post-exploit sweep's repo changes: the Linux kit's .NET patched to 10.0.13, Brotli no longer accepted, servicing bumps
**Light; seat A; sonnet/high. No rung. THE TAPE DEPLOY WAITS ON THIS UNIT:** the orchestrator builds the Linux kit from its landed main, so it lands as soon as it is green, ahead of
non-P1 work. Source: the orchestrator's sweep `fleet/tmp/SWEEP-2026-10-23/report.md`; read § 4 (the builder's part), § 2.2 and § 2.6. Pointers were re-checked on main `dd16ccbf`.
PHP2Shell does not reach our stack; this unit fixes two problems of our own and does some hygiene. It runs beside U-cli-code-mode, and their files do not overlap.

Items, one commit each, one-sentence messages:
1. **Patch .NET in the Linux kit.** Move the SDK from 10.0.400 to 10.0.402 at `.github/workflows/build.yml:362` (`dotnet-version`) and `:368` (the `global.json` line); `rollForward`
   stays `disable`. Today the kit ships runtime 10.0.11, which misses the fixes in 10.0.12 and 10.0.13 (50 CVEs, 13 of them remote code execution).
   Proof to quote from the branch CI:
   - package-linux's `dotnet --version` prints 10.0.402;
   - the published stage's `TradeAgent.App.runtimeconfig.json` (or `.deps.json`) names Microsoft.NETCore.App 10.0.13. Add a step that prints this if none does;
   - the Windows installer job (`:305-307`, floating on `10.0.x`) shows 10.0.402.
   Leave the "10.0.400" mentions in code comments (`MaterialScanner.cs:604`, `UrlOrigin.cs:17`, `containment-probe.csproj:12`) and in BUILD-STATUS alone: they record what was measured.
2. **Stop accepting Brotli** until .NET ships a fixed copy: CVE-2026-111497, CVSS 9.2; .NET bundles brotli 1.1.0 and Microsoft has no fix.
   - Change `AutomaticDecompression = DecompressionMethods.All` to `DecompressionMethods.GZip | DecompressionMethods.Deflate` at `src/TradeAgent.Provisioning/Downloader.cs:104`,
     `ForwardBarCollector.cs:67`, `InstrumentVerifier.cs:55` and `TapeCollector.cs:90`. `GdeltRecorder.cs:101` already uses `None`. The decompressed body is identical, so no recorded
     data changes meaning.
   - In `src/TradeAgent.AgentRuntime/CodexSurfaceProbe.cs:335`, refuse `"br"` the way it already refuses an unknown encoding.
   - Add a red-first GUARD test: no `HttpClientHandler`/`SocketsHttpHandler` in `src/` may advertise `br`, whether by `DecompressionMethods.All` or `.Brotli`. It finds 4 hits at base.
     Quote its red. One watched mutant: put `All` back on one site ⇒ the guard is red.
   - The report states when to revert: when a .NET 10 servicing release lists brotli ≥ 1.1.1 in `src/native/external/brotli-version.txt` on `release/10.0`, or in its release notes.
3. **Servicing bumps** (no advisory; hygiene):
   - Microsoft.Data.Sqlite 10.0.0 → 10.0.13 (`src/TradeAgent.Core/TradeAgent.Core.csproj:3`, `src/TradeAgent.Connectors.Paper/TradeAgent.Connectors.Paper.csproj:14`);
   - System.Security.Cryptography.ProtectedData 10.0.0 → 10.0.13 (`src/TradeAgent.Security/TradeAgent.Security.csproj:5`);
   - Avalonia, Avalonia.Desktop and Avalonia.Themes.Fluent 12.1.1 → 12.1.4 (`src/TradeAgent.App/TradeAgent.App.csproj:12-14`; 12.1.3 fixes a Linux DBus tray crash, which matters
     on the tape host);
   - Microsoft.NET.Test.Sdk 18.9.0 → 18.11.0 (`tests/Directory.Build.props:8`).
   SQLitePCLRaw stays at 2.1.13 (it bundles SQLite 3.53.3, which has no CVE). The codex pin stays at 0.160.1. Restore through the normal NuGet feed only.
   A bump that breaks the build or a test is reported, not forced: drop that one bump and name it.

NOT in this unit:
- pinning the GitHub Actions by commit SHA (optional; a CARD);
- the TLS denylist, which is `U-tls-denylist`, a protection unit after this one;
- OpenCode's unpinned `releases/latest` download (`RuntimeManifest.cs:1328-1351`; a CARD).
Host note: since about 00:4x this Mac's dotnet is SDK 10.0.402, with runtimes 10.0.11 and 10.0.13. A local exit 137 before then was the host's, not the code's.
Protections: none of the money path is touched. The no-terminal rule holds: the kit still installs itself. Refuse, never guess: an unknown encoding stays refused.
Evidence beyond CI: none needed. Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.
