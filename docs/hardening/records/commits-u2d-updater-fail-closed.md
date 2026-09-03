# Commits on `u2d-updater-fail-closed` (tip c519966, base 3931c10 — generated 2026-09-03 for the handoff)

Each commit message is the builder's own account of what changed and why. The per-round build/verify records and mutation tables were lost with the session scratchpad; these messages, the tests on the branch, and `HANDOFF-2026-09-03.md` are the surviving record.

## 6b936f6 — Refuse to install an update TradeAgent cannot account for

The update trust chain is checksum-only and same-release: no Authenticode
anywhere, unsigned installer. Every way of losing that checksum used to be a
silent install of an unverified 90 MB executable rather than a refusal, and the
documented unconfirmed-order stop lived on a button that a second button walked
around.

- A checksum that cannot be resolved stops the install before the download: no
  manifest in the release, a manifest that will not fetch, a manifest that does
  not name our installer (BOM, tab, renamed asset, truncated hash, empty body).
  Nothing can hand Downloader a null hash on this path any more.
- A release carrying two files that both look like the installer is refused
  rather than resolved by position in a JSON array, and ReleaseFeed.Parse now
  says which kind of nothing it returned so a refusal is not shown as up to date.
- The installer is re-hashed immediately before Launch, so anything written to
  updates\<version>\ between download and launch is caught.
- The unconfirmed-order hard stop moved into UpdateService.InstallAsync, where
  both Install buttons meet, and refuses when it cannot tell as well as when the
  count is above zero.
- Every refusal sets Message, is rendered by the banner and the Settings card,
  and is written to the activity log.

## f5f8f5c — Say which kind of failure the Settings card is showing

A check that fails because GitHub did not answer and a check that fails because
TradeAgent refused what it was shown both left Stage=Failed with no Available,
and the card called both of them could not be checked. UpdateService now carries
Refused, and the row says found, and not offered - see below for the second one.

## ddb6e74 — Write the install line after Setup actually starts

Logging you installed TradeAgent before Launch meant a Windows refusal to start
the installer produced a log that argued with itself.

## a77e1b3 — Ask the hard stop again before Launch, and refuse a manifest that contradicts itself or is too big

Round 2, items 1, 4 and 5.

The unconfirmed-order stop was sampled once, before a manifest fetch and a 90 MB
download, so an order that went UNKNOWN during the download still launched the
installer. The check is now ADDED immediately before Launch, beside the re-hash;
the early one stays, because it is what keeps a refusal from costing the owner
the download.

ChecksumManifest.Find gained a problem channel. It reads every line rather than
returning on the first match, so a manifest naming the installer twice with two
different hashes is refused as a contradiction instead of resolved by position.
Two identical lines still resolve: build.ps1 hashes Get-ChildItem -Recurse, so
one installer can appear under two paths with nothing to disambiguate. Size and
line caps are checked before the split.

The class doc now names the replace-after-hash race it does not close.

## 8093bdf — Give an update integrity failure its own error code, and make a started install terminal

Round 2, items 6 and 7.

A download whose bytes did not match the published checksum was reported as
AI_INSTALL_FAILED - The AI assistant could not be installed - which names a
different program. UPDATE_INTEGRITY_FAILED is its own code with its own four
catalogue fields, raised by DownloadVerifiedAsync and rendered by the card, the
banner and the activity line.

Once Launch returns, Setup is running and is going to replace the files this
process executes from. The latch goes up before the activity write, the write is
wrapped, and a second press says the install has already started rather than
launching a second installer over the first.

## 4cf2132 — Refuse new dispatches while TradeAgent is replacing itself

Round 2, item 2. The unconfirmed-order rule only closed one side of the window:
an order placed after the pre-install check and before Setup starts would be
dispatched by a process about to be overwritten, and its answer would arrive
after the thing that was going to reconcile it had gone.

TradingGateway gains one hook, InstallInProgress, consulted first in
TryAuthorizeExecution - above the mode and the kill switch, and with no operator
exemption, because approving a parked order by hand during an install is the
case being closed. A hook that throws reads as installing. UpdateService raises
the latch when an install is confirmed and lowers it on any refusal, keeping it
up after a successful Launch until the process ends.

## 3eaa3e8 — Stop the banner hiding a valid offer, and say up front what cannot be verified

Round 2, items 3 and 8.

The banner rendered any Failed message over the offer, so a six-hourly re-check
that failed replaced a valid update with could not check, and nothing expired a
refusal - a settled order left will not replace itself beside a re-enabled
button until the next check. It now renders only a refusal, drops the
unconfirmed-order one when the order settles, and shows a refused release with
its reason even when there is nothing left to offer.

CanBeVerified says before the press what InstallAsync would say after it: a
release published without a checksum file is named on both surfaces and its
Install button is cosmetically disabled. The hard stop stays in InstallAsync.

## 90b6f91 — Low batch: a negative count, a log that keeps every press, and an installer named like a path

Round 2, item 9.

A negative unconfirmed-work count read as none and failed open; it is now
unknown, which refuses. Refusal deduplication now applies only to the automatic
six-hourly check - two presses are two decisions and both are logged. An asset
name is refused unless it is a plain file name: the installer pattern's .*
matches a separator, so TradeAgent-Setup/../../Startup/x.exe matched and became
Path.Combine(updates\<version>\, name). Refused and Message are set before
Changed fires, so a handler cannot read a refusal as an ordinary failure.

Two gaps a verifier found survivable are now pinned: the real GitHub download
refusing a null hash, and the three AppHost lines that hand UpdateService its
dependencies - the second as a source-text assertion, because TradeAgent.App is
not built by this suite.

## 3a33571 — Keep Later working on a refusal strip, and pin the two banner rules in source

A strip the owner cannot put away is one they learn to look past, so Later stays
visible when a refusal has nothing left to offer and Dismissed hides it the same
way it hides an offer. The banner rules themselves are pinned by a source-text
assertion, for the same reason as the AppHost wiring: this suite does not build
TradeAgent.App.

## 52bbeab — Refuse an oversized checksum file while it arrives, and stop holding the latch across the fetch

Round 3, item 1. The 64 KiB cap ran on a string TryGetStringAsync had already
buffered whole - no content-length check, decompression on, and a thirty-minute
timeout - so the doc claim that an oversized manifest was a refusal rather than
an allocation was false. Downloader.ReadLimitedAsync pulls at most the limit plus
one byte and TryGetSmallTextAsync refuses a declared length unopened, on a
thirty-second leash of its own with a CancellationToken.

The install latch stops the owner trading, so it now covers only the span they
confirmed: it goes up after the manifest is fetched and resolved, immediately
before the download, instead of on entry. Holding it across a network round trip
let a stranger's slow web server stop all trading for up to thirty minutes.

## 158c632 — Move the updater-gateway wiring into a seam a test can run

Round 3, item 2. The three assignments lived in AppHost, which this suite does
not build, so deleting one left 354 tests green and the source-text assertion
that replaced it was defeated by commenting the line out.

UpdateGatewayCoupling.Attach hands each side the narrowest view of the other - a
count, a log sink, a flag - and lives in Diagnostics because that is the only
project that already sees both TradingGateway and UpdateService without
inverting a layer. All three halves are now exercised against a real gateway: an
order the gateway itself flagged stops the install, a refusal lands in the
owner's activity history, and an install in progress makes the gateway refuse to
dispatch. Only the single Attach call in AppHost remains a source assertion.

## c519966 — Low batch: invisible characters, a Later that only hides what it was said about, and no checks after Launch

Round 3, item 3.

An installer name is now refused by Unicode category rather than by
char.IsControl: U+202E reverses how the rest of the name is drawn, U+200B and
U+00AD are invisible outright, and none of the three is a control character.

Later used to silence every refusal for the rest of the session, including the
first sight of a release TradeAgent will not install; a refusal whose reason has
changed clears it. One field now carries the last refusal reason and drives both
that and the log dedupe.

A background check answering after Launch repainted the strip as an ordinary
offer over TradeAgent will close and reopen itself, so checks stop once Setup is
running. The invariant that a true return means this process exits is written
where InstallAsync is declared, not only at its caller.

