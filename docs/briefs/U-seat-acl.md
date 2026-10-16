# U-seat-acl — the seat home is the owner's account's alone on every platform: no ACL entry outlives "owner-only", on the folders or the files already in them
**Arrow closed:** the two CRED-001 gaps `U-seat-home`'s last review left (its record in `BUILD-STATUS.md`, "OWED"); ORGANISATION § 15 row `U-seat-acl`;
`docs/ORCHESTRATION.md` § 2.7 (one app-owned seat home, its credentials pinned to file storage). **A protection — CRED-001: red-first per platform and ONE
watched mutant.** Seat A; opus/high (credentials). **No rung.** After `U-seat-home` (landed, rung 37).
**Today (main `521c5fc9`, re-checked by seat A at dispatch):** `SeatProfile.Prepare` (`src/TradeAgent.AgentRuntime/SeatProfile.cs:33-50`) walks every seat-home folder
through `RealFolder` → `NotALink` + `OwnedHere` (`:72-83`). `OwnedHere` (`:98-121`) makes a folder owner-only by MODE on Linux and macOS
(`UnixFileMode` 0700, `:105`) and, on Windows, `OwnedOnWindows` (`:125-155`) replaces the FOLDER's DACL with a protected list granting this account,
the owner and SYSTEM (`:143-150`). Two holes: (1) **macOS** keeps extended ACL entries independent of the mode bits — an entry such as
`everyone allow read,file_inherit` on `seat-home/codex` survives `chmod 0700`, and an inheritable one is copied onto the `auth.json` codex writes at
sign-in; (2) **Windows** — an EXISTING `codex/auth.json` (or any file or subfolder already in the seat home) keeps its EXPLICIT entries when the parent's
DACL is replaced: only inherited entries follow the parent. Linux needs no change for POSIX ACLs (0700 sets the ACL mask to `---`, which voids named
entries) — a test pins that. Tests today: `SeatHomeTests.Every_seat_home_folder_is_made_owner_only` (`tests/TradeAgent.UnitTests/SeatHomeTests.cs:302`)
checks mode bits and the folder DACL only.
**Observable result:** after `Prepare`, no account but this one (and, on Windows, the owner SID and SYSTEM) holds any access entry on any folder OR file
of the seat home, whatever was there before — or the seat is refused in words naming the path and the repair, never started.
Read first: `CLAUDE.md` (the safety rules; refuse, never guess; no backward compatibility); U-seat-home's record (`grep -n 'U-seat-home —' BUILD-STATUS.md`);
`SeatProfile.cs:16-160`; `OwnerOnlyFile.cs` (how the app writes `config.toml`); `SeatHomeTests.cs:260-330`.
Items, one commit each, one-sentence messages:
1. **macOS: clear the extended ACL** of every seat-home folder and of the files item 3 names (`acl_set_file(path, ACL_TYPE_EXTENDED, acl_init(0))`
   through libSystem P/Invoke, or `acl_delete_*` — the builder's choice, no-follow on links, which `NotALink` already refuses); a failure to clear refuses
   the seat in words (`AI_SEAT_REFUSED`), never starts it.
2. **Windows: no explicit entry below the seat-home root survives** — for every subfolder and every file item 3 names, remove its explicit ACEs and its
   protection so it inherits the root's owner-only list (or set the same protected owner-only list on it); a failure refuses in words.
3. **Every `Prepare`, not only the first:** the walk covers every seat-home FOLDER and the files that hold the credential or the settings
   (`codex/auth.json`, `codex/config.toml`, any other file codex keeps a secret in — read its 0.160.1 source, quote it); session transcripts under
   `sessions/` are covered by their folders' lists (nothing foreign left to inherit). Measure the walk's cost on a seat home with a few thousand
   session files and quote it; it must not grow a launch by seconds.
4. **Linux pinned:** a test that a POSIX ACL entry naming another uid grants nothing after `Prepare` (the mask is `---`); skipped with a reason where
   `setfacl` is absent.
Red-first tests (quote each red at base, on the platform that runs it — CI's macos-latest and windows-latest):
(a) `A_macOS_acl_entry_on_a_seat_folder_does_not_survive_preparation` (red: the entry is still listed by `ls -le` / `acl_get_file`);
(b) `An_inheritable_macOS_acl_entry_does_not_reach_a_file_written_later`; (c) `An_explicit_grant_on_an_existing_auth_json_does_not_survive_on_Windows`
(red: the extra SID still holds an ACE); (d) `A_seat_home_file_that_cannot_be_made_private_refuses_the_seat_in_words`; (e) `Linux_posix_acl_entries_grant_nothing_after_0700`.
**The watched mutant:** skip item 1's clear on folders (or item 2's walk on files) ⇒ (a) (or (c)) red on its platform. Quote it, restore, green.
**Evidence beyond CI:** on this Mac, a throwaway seat home: `chmod +a "everyone allow read,file_inherit,directory_inherit" <folder>`, then the unit's
`Prepare` (through its test), then `ls -le` showing no entry — quoted. The tests box runs the suite if `ready` (Windows' explicit-ACE row on real Windows).
Out of scope: the seat home's location and sign-in (`U-seat-signin`); cells (`U-cell-linux-*`); the owner's own `~/.codex` (never touched).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.

## Report
**Tip:** code `d8c4e4a7` (CI 38934596057), this report on top · **REBASED** `43dbff38` (builder's 5 commits on main `cf0eb94e`, clean) · builder a81abcef stopped with no report; fixer pass 1 wrote this.
**Builder, item 1 (0ab68a3f):** macOS — every seat-home folder and credential file cleared of extended ACL entries via `acl_set_link_np` (no-follow), read back; a failed clear refuses (`AI_SEAT_REFUSED`).
**Builder, item 2 (edfe067a):** Windows — below the root every folder and credential file keeps no entry or protection of its own and inherits the root's owner-only list, written only when not so, read back.
**Builder, item 3 (57fd60f6, b473ff4e):** every `Prepare` walks every folder without following a link; `private_files` = `codex/auth.json`, `codex/.credentials.json` (codex 0.160.1 source quoted in `RuntimeManifest.cs`) + `config_file` made 0600; measured then 17-19 ms / 4,032 session files.
**Builder, item 4 (c8f2a645):** (e) Linux pin. Red-first (c8f2a645): (a), (b) red on this Mac; (c), (d) red on the tests box at base; mutant (item 1's folder clear skipped) ⇒ (a) red, restored green.
**R1 (485592a9) — the TEST was wrong:** `getfacl` prints uid 65534 as `nobody` on ubuntu; now `getfacl -n`, asserting `user:65534:rwx … #effective:---` on that entry's own line + `mask::---`. Green on ubuntu-latest in CI 38934596057.
**R2 (577ec6b9) — the TEST was wrong:** CI's windows runner token changes a file's list past the OWNER RIGHTS deny, so the file was not unprivatisable and `Prepare` rightly made it private. (d) now tries the lock on a throwaway file at discovery and skips with that reason where the lock does not hold; SKIP on windows-latest (the reason is not printed in the log; on Windows only the probe can set Skip). It still runs on macOS. Deviation: its Windows row is proved only on a standard account (the tests box, green 2026-10-11, not re-run).
**Finding 1 (06ecb91a) done:** the walk covers every FILE below the root, never following a link: Windows inherit-only (`OwnedOnWindows`), macOS `NoExtendedAcl`, Linux unchanged; read first, written only when needed. New (f) `An_access_entry_on_an_existing_session_file_does_not_survive_preparation`, RED at REBASED on this Mac: `…/sessions/2026/10/rollout-2026-10-16.jsonl still grants: 0: group:everyone allow read`. Re-measured: 54-72 ms over 377 folders and 4,032 files.
**Finding 2 (d8c4e4a7) done:** every `private_files`/`config_file` path is checked lexically before anything is changed: a rooted path or one leading out refuses in words naming the entry. Every folder from the root to the file must be a real folder (`RealFolder`) before its mode is set. New (g) theory (config/private × absolute/`..`), RED at REBASED: `Expected "OtherRead, GroupRead, UserWrite, UserRead" Actual "UserWrite, UserRead"`. (g′) linked folder, RED likewise: `0644` → `0600` through the link.
**Watched mutant (local, this Mac):** finding 1's file walk off (`var files = false`) ⇒ (f) `[FAIL] … rollout-2026-10-16.jsonl still grants: 0: group:everyone allow read`; restored ⇒ `Passed: 4, Skipped: 2`.
**Gate:** `dotnet build -c Release --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`. `SeatAclTests` 3× via suite.sh → `Passed: 9, Skipped: 2, Total: 11` ×3 (exit 0). Neighbours (SeatHome/PromptStdin/LinuxHostRuntime) → `Passed: 27`. names.sh main u-seat-acl → `removed: 0`, `added: 8`, [Fact]/[Theory] base 2618, tip 2620.
**CI 38934596057 on d8c4e4a7:** macos-latest, ubuntu-latest and windows-latest 1-5 success; package, package-linux, shard-plan and dedupe-gate success. linux-host: red — fleet-wide image break (U-ci-host-pins) (`held broken packages`).
**Re-review:** `fleet/tmp/A-org/u-seat-acl-fix/U-seat-acl-rereview.md`, not acted on (two defects: a hard link into `sessions/` is not a link to `IsLink`; (g′)'s component refusal comes after the root and walk were changed).
**tests box: NOT RUN — the machine does not answer.**
**Not done / not verified:** the new Windows rows of (f) and (g) ran green on CI's windows-latest but never red there (they are red-first on this Mac only). (d)'s Windows row is not re-run on a standard account. Nothing ran on Linux locally (the Colima VM is off-limits). No real codex and no real login.

## Fix pass 2
**Tip:** code `a0158cb6` (CI 38943338805), this report on top · **REBASED** `00cf4eb7` (reds quoted there), re-rebased at seat A's word onto main `bd151496` (U-ci-host-pins) as `e5ab33dd` before the run · fresh fixer, pass 2.
**Item 3 (9c6e1ca7) done:** `Prepare` reads before it writes: the whole home is walked first, and a file with a link count above one is refused in words naming it and the repair before anything changes. The count is read without following a link (`lstat` on macOS, `statx` on Linux, `GetFileInformationByHandle` on a reparse-point handle on Windows). Covered: every walked file on macOS and Windows, and the credential and config files on every platform. **Deviation:** the Linux walk still lists folders only, so a hard link under `sessions/` is not refused there (a Linux preparation changes no session file). The root's owner is also read without any change (uid vs `geteuid`; Windows owner SID) before the walk, so the walk never reads another account's tree; without it `SeatHomeTests`' foreign-root test went red.
**Item 4 (a0158cb6) done:** every component of every manifest path (seat folders, the folders of the credential and config files, and those files) is checked by `Look` (lexical, not a link, not a file) before the root's mode, the walk or any list changes.
**RED at REBASED (this Mac):** (h) `Expected: ["0: group:everyone allow read"] Actual: []`; (h′) `Expected: "OtherRead, GroupRead, UserWrite, UserRead" Actual: "UserWrite, UserRead"`; (i) root `Expected: OtherExecute | OtherRead | GroupExecute | GroupRead | UserExecute | UserWrite | UserRead Actual: UserExecute | UserWrite | UserRead`.
**Watched mutant (local):** link-count check off ⇒ (h) `[FAIL] Expected: ["0: group:everyone allow read"] Actual: []`, (h′) red likewise; restored ⇒ `Passed: 11, Skipped: 2`. Measured: 67-105 ms over 377 folders and 4,032 files (was 54-72).
**Gate:** `dotnet build -c Release --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`. `SeatAclTests` 3× via suite.sh → `Passed: 12, Skipped: 2, Total: 14` ×3 (exit 0). Neighbours `Passed: 27`. names.sh vs the merge-base `bd151496` → `removed: 0`, `added: 11`, base 2618, tip 2622. Against today's `main`, `removed: 30` (tests that landed after the run).
**CI 38943338805 on a0158cb6:** all 12 jobs succeeded: macos-latest, ubuntu-latest, windows-latest 1-5, package, package-linux, shard-plan, dedupe-gate and linux-host. Run 38939250816 on the old base was cancelled.
**Re-review:** `fleet/tmp/A-org/u-seat-acl-fix2/U-seat-acl-rereview2.md`, finished on the first dispatch (same code; not re-run after the rebase). Not acted on. Two P2s: Linux `sessions/` hard links are not refused; an owned folder at 000 is now refused, where the old walk made it 0700 before entering it — a regression.
**tests box: NOT RUN — the machine does not answer.**
**Not done / not verified:** the Windows rows of (h)/(h′) are green on CI but were never seen red on Windows. (i) runs on Linux and macOS only. Subfolder ownership is still decided at write time. After the run, main moved by code (U-seat-signin, O4): the merge is clean and neither touches these files. A stray `git stash pop` applied another worktree's autostash here; it was reverted at once, and the stash is kept.
