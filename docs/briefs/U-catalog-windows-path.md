# U-catalog-windows-path — a seat home past MAX_PATH publishes on a Windows whose long paths are off

**Seat A, triage-and-fix of a real-Windows red (2026-10-25). No rung.**

## The red
The tests box (long paths off) failed `ModelCatalogTests.The_seat_home_gets_the_catalog_and_an_edited_one_is_refused_naming_both_hashes`
in runs 20261025-055630, -071030 and -082528; windows-latest (long paths on) is green at the same commits:

    System.IO.IOException : could not open C:\ta\runs\…\seat-home\codex\model-catalog.json.<guid>.tmp to publish it: <path not found>
       at TradeAgent.Core.OwnerOnlyFile.ReplaceWhileReadersRead(String temp, String path) in …\OwnerOnlyFile.cs:line 97

## The cause — a product defect, not the rig
`OwnerOnlyFile.ReplaceWhileReadersRead` hands the temp to its own P/Invoke `CreateFileW` as it is. .NET prefixes its own calls with
`\\?\` past MAX_PATH; a P/Invoke gets nothing, and the temp's name is the file's plus 37 characters (263 there). Every credential,
seat config, catalog, cell manifest and daily report the app publishes goes through it, so an app home placed deep enough refuses a
seat start on the owner's Windows. `SeatProfile.WindowsLinkCount` already had the rule inline for its own `CreateFileW`.

## Items
1. `Win32Path.Verbatim` in Core: the one rule (below 260 unchanged; `\\?\C:\…`, `\\?\UNC\…`; never prefixed twice); pinned on every OS,
   with a publish-and-replace of a file past MAX_PATH (RED on a Windows whose long paths are off).
2. `OwnerOnlyFile`: the temp's `CreateFileW` and the `FILE_RENAME_INFO` name both through it.
3. `SeatProfile.WindowsLinkCount` uses it instead of its own copy.

Past 32,767 characters Windows itself refuses, and the refusal already names the path in the OS's words (`Win32Failure`).
