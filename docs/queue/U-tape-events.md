# U-tape-events — two exchanges' official announcements recorded from first sight as raw items, each screened at every read for text addressed to an automated reader; GDELT leaves the unit (its API refused us)
**Arrow closed:** sources → tape for unstructured items (`docs/EDGE-FACTORY.md` §§ 4.1–4.2, § 3 moat #1, R17 #24). **Depends on `U-tape-store`** (landed `c8fc2cc`); W4's light third (`docs/ORGANISATION.md` § 15):
its builder takes a slot only after the M0-path units are dispatched (the orchestrator's order). **Protects (EDGE § 6):** 6.1 no model call, no key; 6.3 all of it app-computed; 6.4 public exchange items only (MiCA
Art. 89), none grants authority, one addressing an automated reader quarantined (the inbox rule); 6.10 `O-LIVE` only from a built-in origin, item `url`s never fetched; 6.11 advisory until containment.
**Today (SOURCE at `923fb28`, read by the survey leg; NOT runtime-verified):** `tape_obs` keeps any JSON item ≤ 64 KB keyed `subject|source-time-ms` (subject ≤ 40 of `[A-Za-z0-9._-]`); same payload: no write;
another: revision + 1, never upgraded; `AsOf` needs a `BarAudience` (`TapeStore.cs:178-404`; `Holdout.cs:51-80`). The one parser keeps objects of six symbols (`TapeParse.cs:30-144`); the collector runs it for every
row (`TapeCollector.cs:232`; 10 s leash, 4 MB cap), reads the toggle per look (`:207`) and records what `TapeSourceCatalog.Read()` lists (`:122`; `AppHost.cs:585`); `O-LIVE` = built-in row, own origin, cadence + 30
s (`TapeSourceCatalog.cs:284-295`); file rows only add (`:339-386`). `TapeJson`'s default encoder escapes non-ASCII (`Tape.cs:126`; RUN today: `é` → `\u00E9`), so screens read decoded strings.
**Sources (RUN 2026-10-03 01:51–02:15 CEST, this Mac, keyless GETs).** KEPT `okx-eea-announcements`: `https://eea.okx.com/api/v5/support/announcements` → 200 in 0.13 s, `data[].details[]` = ≤ 20 × `{annType, title,
url, pTime, businessPTime}` ≤ 352 B, newest `pTime` first, 32 days a page; its docs (`https://www.okx.com/docs-v5/en/`, read today): auth optional, answer by request IP, 5 calls/2 s/IP, order kept on edits, `pTime`
= first publication, answers may lag ~5 min (`businessPTime` display-only), `eea.okx.com` = EU users' domain. KEPT `bybit-announcements`: `https://api.bybit.eu/v5/announcements/index?locale=en-US&limit=50` → 200 in
0.49 s (`api.bybit.com` identical), `result.list[]` = `{title, description, type, tags, url, dateTimestamp, publishTime, …}` ≤ 647 B, in neither time's order, `publishTime` −8 h to +100 h from the author-filled
`dateTimestamp` (docs read today; 600 calls/5 s/IP). Sites' terms NOT re-read. DROPPED GDELT: `/api/v2/doc/doc?…&mode=artlist&format=json` → 429 ×4 in 8.6–9.5 s, 20 s–3.7 min apart — NOT VERIFIED (SECONDARY,
cyanheads/gdelt-mcp-server#44, 2026-07-27: mostly refused even at 60–150 s); its raw files (`lastupdate.txt` 200 in 0.48 s; per-file MD5s; a 5.0 MB GKG > 4 MB) carry `DATEADDED`, a vendor first-seen time — `O-PIT`
work for `U-tape-archive`, no race to lose. DROPPED Binance: only a key-signed `wss://api.binance.com/sapi/wss` (docs read today); the site's JSON is not an API.
**No schema change** (`tape.db` stays 1; `U-decision-port` keeps rung 2): an item = a `tape_obs` row: subject = first 32 hex of SHA-256 of its `url`, source time = `pTime`/`publishTime`, first seen = revision 1's
arrival, payload = the item whole; same item → nothing, edit → revision + 1, new `url` or time → new key, past page 1 → unwatched. `O-LIVE` iff built-in row, own origin, first seen within cadence + the row's
documented delay + 30 s (OKX 60+300+30 s, Bybit 60+0+30 s); else `O-ARCH`, page 1 at first start too. **Observable result:** per exchange a fetch row a minute and a row per new or edited item; no model, no key.
**Owner control: the existing "Record market context" toggle, unchanged** — the tape is one market-context record (`TapeStore.cs:10`); it is one press and ON because what it reads is public, keyless and grants
nothing (`Trading.cs:378-388`); a second switch would edit the Market data card, settings and `AppHost`, which `U-venue-verify` and `U-paper-friction` edit — a light third overlaps no file.
Read first: `CLAUDE.md`; EDGE §§ 3, 4.1, 4.2, 6; R07 §§ 4–5.1; R03 §§ 2.2, 5; R15 Q14; the five tape files and four test classes; `CONTRACTS.md:2483-2557`; `docs/RESEARCH-REQUIRED.md:209-236` (C5b).
Items, one commit each, one-sentence messages:
1. Parser `announcement-json`: `TapeSeriesEntry` gains `ItemsPath` (dotted, arrays flattened) and `IdField`; each item → digest subject (of `IdField`), `TryTime`, payload whole; a missing path, an item lacking id
   or time or over 64 KB, or one key twice with different payloads (else each look writes two revisions) refuses the answer in words; an empty list is an empty page; the collector dispatches on `row.Parser`.
2. `TapeSourceCatalog.Announcements()`: the two rows (fields as above, terms, doc URL, measured line, a `[JsonIgnore]` delay); `BuiltIn()` stays the five market rows its tests name (`TapeSourceCatalogTests.cs:30`,
   `TapeCollectorTests.cs:102`); `Read()`, the live rule and the id refusal take both; a file row naming the parser is refused; the vendor scan refuses `okx`+`.com`, `bybit`+`.` and asserts the shipped hosts.
3. `ClassOf` measures against cadence + the row's publication delay + 30 s; market rows (delay 0) are classed exactly as today.
4. `TapeScreen` v1 (Core, built-in only): an item is quarantined if its decoded strings — NFKC, case-folded, invisible characters dropped — hold an instruction override, an address to an automated reader or
   chat-role markup, or it holds any zero-width, bidi or tag character; still recorded; every read carries `Quarantine` (rule and version, never the text); `AsOf` withholds its payload from `BarAudience.Pipe`.
5. `CONTRACTS.md` "The tape" (NOT claimed: completeness — page 1 only, Bybit's in no time order, only while the app runs; a screen catching all or making an unflagged item safe; later readers withholding; the
   sites' terms; evaluation evidence), `USER-GUIDE.md`, `docs/RESEARCH-REQUIRED.md` C5c (today's runs), `Trading.cs:378-388` (comment).
Red-first tests (loopback `FakeArchive` with the measured shapes and `example.invalid` item urls; class tests at store level): (a) `An_announcement_is_one_row_and_a_second_look_writes_nothing`; (b)
`An_edit_is_a_revision_a_new_url_a_new_key_a_twice_named_key_a_failure`; (c) `An_announcement_is_live_within_cadence_plus_its_documented_delay` (OKX 390 s live, 391 archive; Bybit 90/91; loopback and a 3-day-old
page-1 item archive); (d) `A_missing_path_id_or_time_fails_and_an_empty_page_does_not`; (e) `An_item_addressing_an_automated_reader_is_recorded_flagged_and_withheld_from_pipe` (a vector per rule; today's real
"Bybit AI Now Supports Main Account Operations" unflagged); (f) `A_file_row_naming_the_announcement_parser_is_refused`.
Mutants to watch red and quote: (i) the delay dropped ⇒ (c) red at 390 s; (ii) `AsOf` serving a quarantined payload to `Pipe` ⇒ (e) red; (iii) the twice-named-key refusal removed ⇒ (b) red.
Seen, not in this unit: `U-tape-read`'s re-check must list these series without a subject and serve quarantined rows payload-less; `U-annotator` decides whether a quarantined item ever reaches a model; a Bybit 403
bans 10 min, past the 5-min backoff (unreachable at 1/min); Deribit's keyless `public/get_announcements` (200 in 0.13 s) is a later row; Kraken's status API holds a 141 KB item.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
