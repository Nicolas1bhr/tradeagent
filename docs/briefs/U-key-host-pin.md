# U-key-host-pin — the owner's pasted key goes only to the origin the owner saw when pasting it, never to one an agent wrote into a file
**Protects:** credentials (`CLAUDE.md`; `docs/EDGE-FACTORY.md` § 6.10). **Today (SOURCE at `0f47db7`, manager- and R11-checked, NOT runtime-verified):**
`RuntimeCatalog.Read()` merges `%LOCALAPPDATA%\TradeAgent\runtimes.json` over the built-ins and an override with a built-in's id replaces it WHOLE,
`BaseUrl` included (`AgentRuntime/RuntimeManifest.cs:716-728`, the swap `:724-725`); `Endpoint` = `BaseUrl` + `CompletionsPath` (`:341-342`); the harness is
built lazily once per process from `RuntimeCatalog.Find(ApiAgentRuntime.RuntimeId)` (`App/AppHost.cs:121-150`, every harness id maps to that one object
`:205-208`); a turn reads the key once (`ApiConversation.cs:242-252`) and posts `Authorization: Bearer <key>` to `manifest.Endpoint` (`:466-497`, called
`:345`). `HarnessKey.Read` is referenced only at `AppHost.cs:133`; no other path sends it. The vendor CLI agent runs unconfined (`Containment.cs:43`) and can
write `runtimes.json`; keys are memory-only and re-pasted after each start (`Security/HarnessKey.cs`). So an agent sets a `BaseUrl`, the app restarts
(a self-update does), the owner pastes the key, the next turn sends it there (R10 #6). **Observable result:** the paste box names the ORIGIN (scheme, host,
port) the key will go to and marks a non-built-in one plainly; a key is bound to the origin it was pasted for; a send to any other origin — `http://` for
`https://` included — is refused in words, charges nothing, and clears the key. **No schema change.**
Read first: `CLAUDE.md`; `RuntimeManifest.cs:320-345,700-760` (`Harnesses()` `:746-747`, `IsHarness` `:750-751`); `ApiConversation.cs:160-175` (`StartAsync`
reads the key for its status line), `:235-260`, `:340-350`, `:460-500`; `ApiAgentRuntime.cs:70-80` (`KeyHeld`), `:140-230`; `AgentRuntime/TurnMeter.cs:660-690`
(`Charge`; the only zero branch today is a vendor limit "before any work" at `:677-680`); `AiAttemptStore.cs:325-340` (`End` with no usage charges the
reservation); `Security/HarnessKey.cs`; `App/DashboardView.cs:1035,1640-1644,2007-2015` (the key box, `SaveHarnessKey`); `tests/Shared/Loopback.cs:58`.
Items, one commit each, one-sentence messages:
1. `HarnessKey` holds the key with the ORIGIN it was pasted for (`new Uri(url).GetLeftPart(UriPartial.Authority)`: scheme + host + port). `Held` stays the
   presence check used by `KeyHeld`, health and `StartAsync` — presence never clears anything. Only the SEND path calls `ReadFor(origin)`: a match returns
   the key; a mismatch clears it and returns a typed refusal.
2. `ApiConversation` presents its manifest's endpoint origin at `:242-252`; a refusal names both origins ("the key was pasted for https://api.openai.com;
   this runtime now points at <origin>; paste it again on the Safety page if you meant that") and ends the turn REFUSED BEFORE ANY REQUEST: add that zero
   branch to `TurnMeter.Charge` beside the vendor-limit one, so the turn costs nothing.
3. The paste box shows the origin of the harness's OWN manifest (`Harness.Manifest`, never a fresh `RuntimeCatalog.Find`); when it differs from the BUILT-IN
   row's origin for that id it says "not TradeAgent's built-in address" and needs a tick in the same window. Nothing is persisted: a file an agent can
   write must never pre-confirm an origin.
4. `CONTRACTS.md`: the key-to-origin binding; NOT claimed — protection from a process reading this app's memory, or for keys handed to vendor CLIs
   (`RuntimeManifest.cs:52-67` `ApiKeyPlan`, `OnboardingView.cs:779`), which those agents can read by design until containment. `USER-GUIDE.md` (the box).
   Every future keyed endpoint (decision models, keyed sources) uses this holder rule.
These existing tests break at compile time on the new signature and are REWRITTEN IN PLACE with their names kept: `HarnessRoleTests` `:100-124`, `:134-156`,
`:163-170`; the helpers at `ApiWorkerTests.cs:47`, `HarnessBudgetTests.cs:58`, `HarnessLoopTests.cs:109`.
Red-first tests (loopback listeners, which are all `http://127.0.0.1:<port>`; never a real provider): (a) `A_key_pasted_for_one_origin_is_never_sent_to_another`
— written FIRST against today's API and quoted red behaviourally (the second listener receives the bearer), then moved to the new signature, name kept;
(b) `An_origin_mismatch_refuses_before_any_request_clears_the_key_and_charges_nothing`; (c) `The_built_in_origin_still_receives_the_key` (guard);
(d) `A_presence_check_never_clears_the_key`; (e) `A_non_built_in_origin_needs_the_in_window_tick_and_nothing_is_written_to_disk`. Mutants to watch red and
quote: (i) the origin comparison removed ⇒ (a) red; (ii) `.Host` used in place of the origin ⇒ (a) red (two listeners, one host, two ports).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Tip `1eda5a3`** (last code commit; CI ran on it) on `main` `73cfaca`, one commit per item. Gate, Release: build `--no-incremental` → 0 Warning(s), 0 Error(s); Unit 1218/1218 (27 s), Fault 399/399 (1 m 28 s) → 0 failed; 3× HarnessKeyOrigin 6, HarnessRole 16, ApiWorker 7, HarnessBudget 10, HarnessLoop 2, TurnMeter 9, VendorLimitRead 8, SuiteReachesNoVendor 8 → 66/66 every run; names vs `main` 1969 → 1975, removed 0, added 6.
CI run 37020700007 on `1eda5a3`: success — ubuntu-latest success (12 min), macos-latest success (14 min), windows-latest success (28 min), package success (4 min); no known red to name. Scan exclusions, each hit read, by name: `ApiKeyPlan` (a type name in CONTRACTS), `_cts.Token` (the turn's cancellation token), `PasswordChar` (the box's mask property), `$"Bearer {key}"` (test helper for a pretend key).
1 done `2b7e503`: `HarnessKey.Set(key, pastedFor)` keeps the key with its origin; `Held` stays presence (KeyHeld, health, auth, both StartAsyncs); only the send path calls `ReadFor(origin)` — match → the key, mismatch → cleared + typed `KeyRefusal(PastedFor, PointsAt)`; a turn reads `manifest.Endpoint` once and posts every request to it. The named tests rewritten in place, names kept (HarnessRoleTests ×3; helpers via `FakeProvider.Holding`).
2 done `3786515`: the refusal names both origins (`Labels.HarnessKeyPastedForAnotherOrigin`) and ends `key-origin-refused` before any request; `AgentTurnEnded.KeyWithheld` → zero branch in `TurnMeter.Charge` beside the vendor-limit one; the sentence lands on the row as `refused`.
3 done `43768c9`: the box shows the origin of `Harness.Manifest`; differing from the `RuntimeCatalog.BuiltIn()` row it says "not TradeAgent's built-in address" in caution colour, built-in address beside it. DEVIATION: the "tick" is the page's two-press `Ui.ConfirmIf`, whose armed sentence names the address — no CheckBox exists in this UI and Fluent's would paint the OS accent, not a `Theme.cs` colour; the key is bound to the confirmed address, not taken if it moved between presses (one extra test); nothing persisted.
4 done `1eda5a3`: CONTRACTS.md (the binding; NOT claimed: memory reads, vendor-CLI keys via `ApiKeyPlan`/`OnboardingView.cs:779`; the holder rule for future keyed endpoints) and USER-GUIDE.md (the box), each beside the existing harness-key text.
DEVIATION in item 1: origin = scheme + `Uri.IdnHost` + port, not `GetLeftPart(Authority)`, which on .NET 10.0.400 (measured) keeps user-info — `https://<provider>@other.host` reads as the provider — and returns Unicode look-alike hosts as Unicode.
RED before: (a) on `73cfaca` against today's API → "the listener on port 51085 received 1 request(s), 1 of them carrying the key pasted for the listener on port 51084 (same host, 127.0.0.1)"; (b) before the zero branch → `Expected: 0 / Actual: 1.28` (the reservation charged).
Mutants on `2b7e503`, each (a) red with that leak line: (i) the origin comparison removed (ports 51302/51301); (ii) `ReadFor` comparing `.Host` of the two origins (51341/51340). `.Host` put inside `KeyOrigin.Of` instead fails CLOSED (ReadFor re-normalises; a bare host has no origin) — red on the first listener, not a leak. Extra: the box comparing with the catalogue as read → (e) red at `Assert.False(to.BuiltIn)`.
Start path: judged NOT reached. The diff changes what a HARNESS start runs (`KeyHeld` → `key.Held`, the holder passed in `AppHost.Harness`) with the same presence test as before; the resume path `ResumeOnStartTests` drives starts a CLI probe runtime, never builds the harness, and reads only `HarnessKey.Held` (in `RuntimeForRole`), whose body is unchanged.
NOT done or verified: the Safety page was not run or looked at (no screen control from a builder leg; its controls were pressed in tests only); no real provider; no box run. Seen, not changed: the existing no-key path also sends nothing yet, when admitted, keeps its reservation as cost (source-read `AiAttemptStore.End:333-338`, not run); no page shows the Research conversation, so on screen a refusal reads only as the Safety page's "No key is held".
