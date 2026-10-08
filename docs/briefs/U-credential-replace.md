# U-credential-replace — every credential the app writes goes through ONE owner-only atomic publish: the agent pipe's key cannot be rewritten on Windows while anything holds it open (the app will not start), and the owner's pasted AI key is written in place, truncated first and world-readable
**Protects:** CREDENTIAL (`CLAUDE.md`): who can read a credential and when — owner-only from its first byte, never truncated in place — the standard
`U-bridge-auth-owner-only` set; and that a reader of an old file never stops the app opening. Keys, paths, lifetimes and the handshake unchanged. Seat P; no rung.
**Facts (SOURCE at `main` `28a2e3fb` — unchanged since `7e29fc65` in these files; `S` = `src/TradeAgent.Security/SecretStore.cs`, `A` = `src/TradeAgent.Connectors.Atas/AtasConnector.cs`).**
- `SecretStore.Write` (`S:28-53`) publishes by `File.Move(temp, path, overwrite: true)` (`S:46`) — `MoveFileExW`, whose replace windows-latest refused under ANY open
  handle, sharing Delete included ("Access to the path is denied", runs 37675671465, 37675951756; the `U-bridge-auth-owner-only` record). `Read`'s summary (`S:55-59`)
  says a reader sharing Delete never stands in the rename's way: MEASURED FALSE. Its one caller, `IpcToken.Ensure` (`S:112-122`), writes (`S:119`) only over a file
  holding no usable credential (`S:125`, `S:79-82`), which the `trade` CLI (`IpcToken.Peek`, `src/TradeAgent.TradeCli/PipeClient.cs:17`) or a scanner may hold open.
- The owner: `AppHost.StartAsync` calls it (`src/TradeAgent.App/AppHost.cs:694`); the throw becomes `StartupProblem` (`:722`), shown fatal (`MainWindow.cs:163-168`) as
  .NET's bare "Access to the path is denied." Nothing is lost (`S:48-52`); the next start tries again.
- `CliAgentRuntime`'s key sign-in writes the owner's pasted key into the CLI's auth file IN PLACE (`src/TradeAgent.AgentRuntime/CliAgentRuntime.cs:621-628`,
  `File.WriteAllTextAsync`): truncated first (a crash can leave it empty), default permissions off Windows (0644 under umask 022). AgentRuntime references Core.
- The cure is landed and COPIED: `BridgePipeAuth.Write`, `Publish`, `ReplaceWhileReadersRead`, `Win32Failure` and the `CreateFileW`/`SetFileInformationByHandle`
  externs (`A:2112-2211, 2359-2365`), "copied rather than referenced" because Security is outside the bridge DLL's closure (`A:2114-2115`). `TradeAgent.Core` is in
  every chain (Security, AgentRuntime, AtasBridge → Connectors.Atas → ConnectorSdk → Core) and already holds P/Invoke (`src/TradeAgent.Core/Containment.cs:93-105`).
- No test replaces a held credential: `IpcTokenTests` (`tests/TradeAgent.UnitTests/IpcTokenTests.cs:33-65`) races a fresh home only; owner-only at `:62-63`.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; that record; `S`; `A:2097-2211, 2340-2366`; `CliAgentRuntime.cs:560-630`; `tests/TradeAgent.IntegrationTests/BridgePipeAuthTests.cs:456-650`.
Must NOT: change what is written or read (DPAPI to the account with its entropy, the paths, the 64-hex mint, `Ensure`'s lock and second read, `Matches`, the auth file's
template and content — it still replaces the whole file: name that in the report, do not change it) or the bridge key's JSON, seam, `BridgeProtocolVersion`,
`Versions.BridgeCompatible`; widen the fallback (`File.Move` only on errors 1, 50, 87); add a retry, a delete-then-move or an in-place write; add an assembly to the
bridge DLL's closure; loosen owner-only (0600 from the temp's open off Windows; Windows' DACL as today); touch `CoidWitness` (its same false comment waits for the
ATAS box — name it); add a pipe op or a rung. Items, one commit each, one-sentence messages:
0. **Red-first tests and the seam they need:** `internal static FileStream OpenToRead(string path)` in `S` (`Read`'s own open, `S:65`) + `InternalsVisibleTo
   TradeAgent.UnitTests`. (a) a reader holding the credential does not refuse its rewrite: held reader reads the old bytes whole, `Read` the new, one file in the folder;
   (b) a start over an unusable credential a reader holds publishes a new one (`IpcToken.Ensure` returns 64 hex digits); (c) the pasted key file is owner-only from its
   first byte and a failed write leaves the old file whole.
1. **One publish, shared:** a new file in `src/TradeAgent.Core/` (e.g. `OwnerOnlyFile.Write(path, bytes, beforeRename)`) takes `BridgePipeAuth.Write`'s body with
   `Publish`, `ReplaceWhileReadersRead`, `Win32Failure` and both externs MOVED out of `A`, not copied; `BridgePipeAuth.Write` calls it; `BridgePipeAuthTests` 16/16.
2. **`SecretStore.Write` publishes through it** (sealed first on Windows, as today); `S:16-27, 55-59` say what was measured.
3. **The key sign-in's auth file publishes through it** (`CliAgentRuntime.cs:621-628`): owner-only, atomic, the same bytes.
Proof: RED before items 2-3 — (a), (b) on windows-latest ("Access to the path is denied"; `ci-dispatch.sh` at item 0 or 1, run id quoted; ubuntu and macOS green);
(c) on this Mac (0644 read back). Green on all three after. ONE mutant: `UnixCreateMode` dropped in the shared write ⇒ `BridgePipeAuthTests.The_secret_is_owner_only_
before_the_rename`, `IpcTokenTests` (`:63`) and (c) red on this Mac, quoted, restored. NOT provable off Windows (say so): the claim itself, the fallback, the
32-bit `FILE_RENAME_INFO` layout, DPAPI, the start screen's words (no app run).
Gate and report per `docs/FLEET.md` "The builder pass": rebase; Release 0 warnings; Unit, Fault 0 failed; touched classes 3×; CI on all three; tests box or NOT RUN; names 0 removed; `## Report` ≤ 20 lines.
