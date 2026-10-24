# U-tls-denylist — every TLS connection the app makes refuses a chain through the eight compromised eMudhra intermediates, and keeps the platform's validation
**Protection unit (seat A; opus/high; no rung). CRED-001: exchange keys, sign-ins and the app's downloads travel over this TLS.** It comes after U-security-sweep-a, which is landed.
Source: the orchestrator's sweep `fleet/tmp/SWEEP-2026-10-23/report.md`: § 1.5 item 1 (the compromise), § 4 item 3 (the decision: option ii), § 6 (what was not checked).

**Why the trust store does not close it.** The private keys of 8 eMudhra intermediates are compromised: six are for TLS (EM EV/OV/DV TLS CA - G3B and G3C) and two for code signing.
Sectigo cross-signed all eight under AAA Certificate Services, which stays trusted everywhere, so a host's trust-store update leaves them valid. No handler in `src/` sets custom
validation or revocation checking: `git grep` finds no `ServerCertificateCustomValidationCallback`, `CertificateRevocationCheckMode` or `CheckCertificateRevocationList`.
So the app accepts their certificates today, on the owner's Linux host above all. Go fixed it with an SPKI denylist in `crypto/x509` (Go 1.27.3, https://go.dev/cl/851047);
Mozilla's bug is https://bugzilla.mozilla.org/show_bug.cgi?id=2083449 (oss-security 2026/10/20/1).

**Observable result.** Every app TLS client (`HttpClient`/`HttpClientHandler`/`SocketsHttpHandler`, `SslStream`, and any client a library builds for us) validates through ONE app
policy:
- the platform's own validation must pass: `sslPolicyErrors == None`, never bypassed;
- AND no certificate in the built chain may have a denylisted SPKI (SHA-256 of the DER SubjectPublicKeyInfo).
A refused connection says so in words, naming the intermediate and the source of the entry. It is never a silent retry and never a fallback without the check.

Items, one commit each, one-sentence messages:
1. **The denylist as data,** like vendor commands (`runtimes.json`, `atas.json`): a shipped data file. Each entry carries the subject name, the SPKI SHA-256 (hex), the source
   (Go CL 851047 / Mozilla 2083449) and the date. Take all eight, the two code-signing ones included. Fetch each hash from the primary sources with curl or WebFetch (Go's
   `crypto/x509` change and Mozilla's bug or OneCRL). Quote two independent sources per hash where they exist, or say which hash has only one.
   A missing or malformed file is REFUSED in words naming the file and the repair (reinstall). It is never read on a guess and never treated as an empty list.
2. **One validation policy:** a single app type, e.g. `TlsPolicy.Validate(cert, chain, errors)`, which a central handler factory installs on every client. It keeps the platform
   result (`errors == None`) and refuses a chain element on the list.
3. **Every client through it:** enumerate every construction in `src/` (`grep -rn 'new HttpClient\|HttpClientHandler\|SocketsHttpHandler\|SslStream' src/`). That includes the
   Provisioning handlers U-security-sweep-a just touched, the connectors, the updater and the downloader. Route each through the factory.
   Loopback-only plain HTTP (the codex surface probe's mock, the app's pipe) is not TLS: name each such site and why.
4. **A GUARD test** (like `NoBrotliGuardTests`): no TLS client in `src/` is constructed outside the factory, and no callback anywhere returns true while `errors != None`.

Red-first (quote each red at base):
- (a) `A_chain_through_a_denylisted_intermediate_is_refused_even_when_the_platform_accepts_it`: a synthetic root, intermediate and leaf built with `CertificateRequest`. The chain
  is evaluated under a custom trust root (`X509ChainPolicy.CustomTrustStore`, `TrustMode = CustomRootTrust`) so the platform result is None. The intermediate's SPKI is on a
  test list ⇒ refused, and the words name it.
- (b) `A_clean_chain_the_platform_accepts_is_allowed`.
- (c) `A_platform_error_is_never_overridden_by_the_list`.
- (d) `A_missing_or_malformed_denylist_is_refused_in_words`.
- (e) the GUARD.
- (f) the shipped list holds the eight entries with their sources: count and hash shape.
**ONE watched mutant:** drop the denylist check from `Validate` ⇒ (a) red. Quote it, restore it, green.

Protections:
- **CRED-001:** the check sits in front of every key-carrying connection.
- **Refuse, never guess:** a bad list is refused.
- **The platform's validation is never weakened:** its errors are never ignored.
- **Vendor data is data:** a later list update is a data change.
- **The no-terminal rule:** nothing for the owner to do.
- The money rules are unaffected. An exchange connection refused for a denylisted chain is a definite TLS refusal, not an order outcome: it never becomes
  `AtasRejectedException` or a guessed order state.
NOT in scope:
- codex's and Claude Code's own TLS (they use the OS store or their own; § 6 of the sweep);
- online revocation checking (option iii; it costs availability);
- the host trust stores (the owner's machines, sweep § 5).
Evidence beyond CI: none needed. NOT claimed: that the eight are the only compromised keys. The list is what the sources name on the date it was fetched.
Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.

## Report
Code tip 13c37624 (u-tls-denylist on main 3dacac76; the report commit follows). Build pass CI 40113336192 green; fix-pass CI 40126826449 green.
1. `dotnet build TradeAgent.sln -c Release --no-incremental` → "0 Warning(s) 0 Error(s)".
2. 3× Release, suite lock, each run green: TlsClientGuardTests 2/2, TlsPolicyTests 5/5, TlsDenylistTests 4/4, TlsRefusalWordsTests 2/2, HarnessBudgetTests 16/16, DecisionPortTests 9/9.
3. CI 40126826449 (Windows ×5, ubuntu, macos, linux-host, packages): "success". Scan exclusion: exact phrase `leash.Token` (a cancellation token's name, no secret). Reviews: fleet/tmp/A-org/tls-denylist/U-tls-denylist-review.md (four findings, all fixed below); re-review U-tls-denylist-rereview.md: "No defects found … all four prior findings are closed".
4. names.sh main u-tls-denylist → "removed: 0", added 16; "[Fact]/[Theory] base: 3153  tip: 3168".
5. tests box: RUN at 426a37ca — run 20261024-185011-426a37ca, --filter Tls, GREEN, UnitTests 10/10, his files unchanged. Fix pass: NOT RUN — `ready` exit 4, another run in progress.
Item 1 done 428b88fc — all eight hashes, two sources each, re-fetched: Go CL 851047 verify.go `distrustedSPKIHashes` equals the shipped set; the six TLS hashes equal OneCRL's pubKeyHash with matching subjects; the two code-signing hashes recomputed with openssl from the crt.sh certificates that Mozilla 2083449 names. Item 2 done 0aad837d. Item 3 done 87f9c536 (eight sites; the loopback listeners and named pipes are named in TlsClients with the reason). Item 4 done 426a37ca.
Fix 1 (P1, npm fallback) 13c37624: a refused download or release lookup refuses in the policy's words. RED: progress "Installing Node.js first — TradeAgent keeps its own…" (the fallback was taken).
Fix 2 (P1, `$` anchor) 69c71407: `\A…\z`. RED: "Assert.Throws() Failure: No exception was thrown".
Fix 3 (P2, words lost) f7c1aed5: ApiConversation, TypeSafeWire (`tls-refused` + new DecisionAnswer.Problem, shown on the card; not on the tape), Downloader (rethrows instead of null; StatusAsync's Because). RED: "No exception was thrown" / "Filter not matched in collection" / Expected "tls-refused" Actual "transport".
Fix 4 (P2, guard) 557ef492: scans whole files and matches qualified names. RED: "Assert.Single() Failure: The collection was empty". MUTANT (qualified-name group dropped): the same failure; restored, green.
RED before (a)–(d),(f): at base, "error CS0234: The type or namespace name 'Tls' does not exist in the namespace 'TradeAgent.Core'". Guard (e) on base-shaped src: failed, nine sites listed. MUTANT (denylist loop dropped from Validate): "A_chain_through_a_denylisted_intermediate… Assert.False() Failure"; restored, green.
Deviations: the Connectors have no HTTP client, so there was nothing to route. Provisioning gains InternalsVisibleTo(UnitTests) and CliAgentRuntime gains init-only Download/NodeCanProvide seams, both for the tests.
NOT done/verified: no live handshake against a real eMudhra-chained server; the full local suite was not run (CI ran it); other collectors still fold a refusal into their own "unreachable" notes (outside the four findings).
