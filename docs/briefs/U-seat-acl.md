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
