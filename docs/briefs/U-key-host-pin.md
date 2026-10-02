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
