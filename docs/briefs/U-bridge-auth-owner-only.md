# U-bridge-auth-owner-only — the bridge secret is owner-only from its first byte, published by one rename, and read without refusing that rename
**Arrow closed:** the credential rules (`CLAUDE.md`, "Do not commit credentials"; the bridge secret is `BridgePipeAuth`'s authority half). **Depends on `U-test-hygiene-2`, LANDED** (its item 1,
`82613016`, is the model: `SecretStore.Write`/`Read`, `SecretStore.cs:28-52`, `:60-66` on main). **Then:** nothing. **Protects:** who can read the secret, and when; the handshake, unchanged.
**Today** (main `18a7ab10`; unchanged at `cf0760b3`): `BridgePipeAuth.WriteFile` (`AtasConnector.cs:2091-2098`) puts the secret in `bridge.auth.<pid>.tmp` with `File.WriteAllText` (:2094-2095) — created `0666 & ~umask`, 0644
under this Mac's umask 022 — and only then `Restrict` chmods it 0600 (:2096, :2104-2109), then renames (:2097). Between the two, any account that can traverse `Paths.State` can open the temp, and a
descriptor opened then reads on after the chmod; a crash between :2095 and :2096 leaves a 0644 temp holding the LIVE secret (it survives across runs, :2053-2062) that nothing sweeps (the name is
per pid). On Windows `Restrict` is a no-op (:2106) and the temp inherits `Paths.State`'s DACL, which the rename keeps: the window adds no reader the published file lacks — but that DACL denying
other accounts is a claim (:2101-2102) no test reads. The reader (`ReadFile`, :2072-2088) is the one both ends run (`BridgeServer.cs:166`, the bridge DLL references this assembly,
`TradeAgent.AtasBridge.csproj:46`); `File.ReadAllText` shares read only, so on Windows a bridge reading at the instant of the connector's rewrite makes `File.Move`'s replace a sharing violation,
caught by the accept loop with the pipe name unowned for its 1 s pause (:705, :770, :787-789). Nothing reads the file's mode or ACL (`BridgePipeAuthTests.cs:452-463`).
**Observable result:** on macOS and Linux the secret is never on disk at a mode other than 0600, not even inside the write; on Windows a test reads the temp's DACL and the published file's,
on windows-latest and on the tests box; a reader holding `bridge.auth` no longer refuses its rewrite; a failed write leaves no temp; the file's path, JSON and secret lifetime, the proofs, the
protocol and `Versions` do not move. **No schema change.** Read first: `CLAUDE.md`; `82613016`; `AtasConnector.cs:1985-2130`; `BridgeServer.cs:160-235`; `BridgePipeAuthTests.cs`.
Items, one commit each, one-sentence messages:
1. A seam first, over the OLD write: `public static void Write(string path, BridgeCredential c, Action<string>? beforeRename = null)` (production: `EnsureForServer` passes `CredentialFile` and
   nothing else), the seam called with the temp's path once the secret is in it and before anything else touches it (today: between :2095 and :2096). Test (a) lands here, RED on this Mac, quoted.
2. Then the write is `SecretStore.Write`'s shape, in `BridgePipeAuth` and without DPAPI (no new assembly in the bridge DLL's closure: `TradeAgent.Connectors.Atas` references only the SDK, trap 34):
   a temp of this call's own (`{path}.{Guid:n}.tmp`), `FileMode.CreateNew`, `FileShare.None`, `UnixCreateMode` 0600 off Windows, written, `Flush(flushToDisk: true)`, the seam, renamed over,
   deleted in `finally` when the rename did not consume it. `Restrict` goes; the in-process `Gate` (:2059) stays.
3. `ReadFile` opens with `FileShare.ReadWrite | FileShare.Delete` (the model's `Read`), so no reader refuses the replace; its single retry and its refusals (null on JSON, short or non-hex secret) stay.
**The handshake, kept (no compatibility code; refuse, never guess):** path, `BridgeCredential` JSON, the secret's lifetime and `Proof` do not move; `BridgeProtocolVersion` (3) and
`Versions.BridgeCompatible`'s exact match (`Versioning.cs:351`) are untouched; a missing or malformed file is still refused in words by the bridge (`BridgeServer.cs:166-170`) and re-minted by
the connector (:2061-2062). No sweep of an older build's `bridge.auth.<pid>.tmp` (`CLAUDE.md`, no backward compatibility): the report names any found on this Mac or the tests box.
Tests (`BridgePipeAuthTests`, each on its own path under the test home, never the shared `CredentialFile`): (a) `The_secret_is_owner_only_before_the_rename` — Unix: the mode at the seam is exactly
`UserRead | UserWrite` (red at item 1: 0644); Windows: the temp's DACL at the seam allows read to no SID but the current user, SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544), and the
published file's DACL equals it — if that is red on Windows, STOP and report: an explicit DACL at creation is a design question, not this unit; (b) `A_reader_holding_the_credential_does_not
_refuse_its_rewrite` (a handle opened as `ReadFile` opens, held across `Write`; red on windows-latest at item 1, quoted — `rename(2)` ignores handles, so Unix is green both ways, said in the test);
(c) `A_rewrite_refused_at_the_rename_leaves_no_temp_and_the_old_secret` (the seam throws); (d) `Publishing_the_credential_keeps_the_secret_and_restamps_the_owner` and every handshake test in
`BridgePipeAuthTests`/`BridgeRoundTripTests` green unchanged. Mutant, quoted red: `UnixCreateMode` dropped (the temp created at the umask's mode) ⇒ (a), 0644 at the seam.
First, in the report: the umask of this Mac, CI and the tests box; `bridge.auth`'s DACL on the tests box, as read by (a).
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed,
touched classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
