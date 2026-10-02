# R16 — Belgian national rules for the venue decision (researched 2026-10-02)

Scope: closes R08 § 6's open item. R08 checked EU rules (MiFID II, MiCA); this leg checks BELGIAN rules for one Belgian-resident
private owner trading his own money: FSMA product bans, MiCA in Belgium, prop-firm eligibility, the 2026 tax regime, reporting
duties, and what follows for `docs/EDGE-FACTORY.md` §§ 4.8 and 10.1. **This is research for the owner's own decision, not legal
or tax advice.** Labels: **DOC** = read today on the primary source named (official text, regulator, register, venue's own page);
**SECONDARY** = third-party page or search-index excerpt; **INFERENCE** = this leg's reasoning; **UNKNOWN** = not established today.
Method: 18 of the 30 allowed web searches; the rest were direct fetches of official pages, PDFs and the ESMA register (parsed
locally; nothing kept in the repo). Dutch and French texts are paraphrased. Blocked today: EUR-Lex (empty reply, so MiFID II was
read from legislation.gov.uk's archived copy of the EU text as adopted), news.belgium.be (403), the NBB FAQ (403), and the Fisconet
circular viewer (JavaScript only). Sources are numbered S1–S32 in § 5.

## 0. Ten-line answer

1. Two FSMA regulations bind anyone who distributes in Belgium, wherever they are established, and Justel shows neither amended:
   the 3 April 2014 regulation bans selling retail clients any financial product whose return depends on "virtual money", with no
   trading-venue exemption; the 26 May 2016 regulation bans selling consumers leveraged, binary or under-one-hour derivatives
   traded electronically, unless they are admitted to a regulated market or a market-operator MTF (DOC S1–S5).
2. Crypto perpetuals are leveraged derivatives whose return depends on a crypto-asset. **On the texts, they are closed to the owner
   as a Belgian retail client at Kraken Futures EU and at OKX Europe**, and in 2024 the FSMA used the 2014 ban against MEXC (DOC S6).
3. Yet Kraken's Belgian page markets perps at up to 10x, and neither Kraken nor OKX lists Belgium as restricted (DOC S9–S11). That
   conflict is unresolved. Only a written answer from the venue or the FSMA (or counsel) settles it. Building the short side there
   now risks being forced out.
4. The lawful door is **elective professional status**: both bans exclude professional clients. MiFID II needs two of three criteria
   (about 10 significant trades a quarter for four quarters, a portfolio including cash above EUR 500,000, or a year in a relevant
   finance job), a written procedure and the firm's acceptance, and the owner loses retail protections (DOC S12).
5. **Spot is open.** The MiCA transition ended on 1 July 2026, and the FSMA has authorised no Belgian CASP. ESMA's register (30 Sep
   2026) shows Revolut, Kraken (Payward Europe Solutions), Bitvavo, OKX Europe, Coinbase and Bitstamp passported into Belgium.
   Binance, MEXC and Hyperliquid are not on it (DOC S15–S18).
6. **CME futures through a prop firm are open on eligibility.** Neither Zenit (Brussels, Belgian law) nor Topstep excludes Belgium.
   Zenit's accounts are simulated, and an algorithm is allowed only if the trader owns its source code. Topstep bars "unfair" use of
   software or AI. Both need to say yes in writing to AI-authored code (DOC S22–S25).
7. **Prediction markets are closed as venues.** polymarket.com is on the Belgian Gaming Commission's blacklist, and players who
   knowingly use a listed site can be fined (DOC S20–S21). Event contracts that are binary options fall under the 2016 ban
   (INFERENCE).
8. **Tax since 1 Jan 2026:** gains on financial assets, derivatives and crypto included, are taxed at 10% above about EUR 10,000 a
   year when they come from normal management. Speculation is taxed at 33%, and gains from a professional activity count as
   professional income. The bill lists automated software, the number of transactions, borrowing and crypto's share of wealth as
   signs of abnormal management, and TradeAgent matches several of them (DOC S26–S28).
9. The stock-exchange tax (TOB) does not reach crypto, futures or perps (SECONDARY S29). Foreign accounts are reported once to the
   NBB's Central Point of Contact and every year in the tax return. For crypto accounts, the FPS Finance calls declaring
   "advisable" (DOC S30).
10. Before the venue decision (§ 10.1), get three answers: written replies from Kraken and OKX on Belgian retail perps; an
    accountant's view, ideally an advance tax ruling (DVB), on which regime TradeAgent's pattern falls under; and the owner's choice
    between seeking professional status or staying with long/flat spot plus prop-firm CME.

## 1. Evidence table

| # | Claim | Source | Label | Limits |
|---|---|---|---|---|
| E1 | It is forbidden to professionally sell, in Belgium, to retail clients any financial product whose return depends directly or indirectly on "virtual money" (any unregulated digital money that is not legal tender). In force since 1 Jul 2014, with no amendment shown | S1 Justel, FSMA Regulation of 3 Apr 2014 (RD 24 Apr 2014, MB 20 May 2014); S2 FSMA EN translation | DOC | Justel's consolidation as served today. Whether MiCA-era bitcoin is still "unregulated digital money" is a matter of interpretation → UNKNOWN |
| E2 | The 2014 note: the ban applies to selling on Belgian territory, whatever the seller's nationality or seat. It leaves professional clients untouched. "Selling" means presenting a product to induce a purchase, and advertising is the typical case. The 2014 FSMA news says it targets products that are essentially derivatives on virtual currencies such as bitcoin | S1 (explanatory note); S3 FSMA news, 21 May 2014 | DOC | — |
| E3 | It is forbidden to professionally sell consumers in Belgium derivatives traded via an electronic trading system that are binary options, last under one hour, or carry leverage. Referral rewards, conditional bonuses, external call centres, loss-linked pay and credit-card funding are also banned. In force since 18 Aug 2016, with no amendment shown | S4 Justel, FSMA Regulation of 26 May 2016 (RD 21 Jul 2016, MB 8 Aug 2016); S5 FSMA EN text and commentary | DOC | — |
| E4 | The 2016 exemptions: derivatives admitted to a regulated market or to an MTF run by a market operator; consumers treated as professional clients. A company is not a consumer. The ban reaches selling "addressed to" Belgium from any state. The signs: Belgian language or locale, Belgians able to register online, no disclaimer, a freedom-of-services notification | S5 commentary | DOC | If "regulated market" means an EEA market, CME's status → UNKNOWN |
| E5 | Leverage means any process that lets the consumer's exposure exceed the amount committed. The FSMA rejected carving out informed investors | S5 | DOC | — |
| E6 | The FSMA used the 2014 ban against a crypto derivatives exchange: on 23 Jul 2024 it ordered MEXC to stop selling virtual-money products to Belgian retail clients, and to stop custody under AML-law Art. 136 | S6 FSMA press release, 24 Jul 2024 | DOC | MEXC is outside the EEA. No FSMA action against an EEA MiFID firm's perps was found → UNKNOWN |
| E7 | The FSMA monitors finfluencers who promote derivatives whose marketing is banned in Belgium (CFDs, forex) | S7 FSMA annual report 2023 | DOC | Not specific to crypto |
| E8 | ESMA: products sold as "perpetual futures" are likely in scope of national CFD product-intervention measures. The trading venue, the funding mechanism and voluntary negative-balance protection do not change that. A PRIIPs KID and an appropriateness test apply | S8 ESMA public statement, 24 Feb 2026 | DOC | EU level. Belgium's 2016 ban is stricter |
| E9 | Kraken's Belgian-locale page markets crypto perps at up to 10x through PEDSL-CY (CySEC 342/17) and shows no retail restriction. Kraken's eligibility page (updated 28 Jul 2026) restricts no EEA country | S9, S10 Kraken | DOC | Whether Kraken onboards Belgian retail for perps → UNKNOWN |
| E10 | OKX says X-Perps reaches "all 30" EEA countries, possibly not all at once, after an appropriateness test, with the same product for retail and professional clients (updated 4 Sep 2026) | S11 OKX | DOC | Belgium is not named |
| E11 | Elective professional (MiFID II Annex II § II): two of three criteria — significant-size trades on the relevant market averaging 10 a quarter over four quarters; a portfolio of financial instruments plus cash above EUR 500,000; at least one year in a financial-sector job that needs the relevant knowledge. Then a written request, the firm's written warning, a separate written acknowledgement, and the firm's own checks | S12 MiFID II Annex II as adopted | DOC | Archived copy; later amendments not checked. Whether spot crypto trades count toward criterion 1 → UNKNOWN |
| E12 | Dealing on own account needs no licence unless the person is a market maker, a member or participant of a regulated market or MTF, has direct electronic access, uses high-frequency trading, or executes client orders | S13 MiFID II Art. 2(1)(d) | DOC | INFERENCE: the owner trading his own money through a venue needs no licence. Third-party money (§ 10.10) changes this |
| E13 | The Belgian MiCA law (11 Dec 2025, MB 24 Dec 2025, in force 3 Jan 2026) makes the FSMA the competent authority (the NBB for ARTs and EMTs) and lets the FSMA ban or restrict crypto-assets (art. 30bis of the 2 Aug 2002 law amended). It does not touch the 2014 or 2016 regulations | S14 Justel | DOC | No shortening of the MiCA transition found |
| E14 | The FSMA: the transition ended on 1 Jul 2026; it granted no registrations under the RD of 8 Feb 2022; its lists of authorised and Art.-60-notified Belgian CASPs both read "Nihil" (checked 02/10/2026) | S15, S16 (25 Jun 2026), S17 FSMA | DOC | The register's two Belgian-home entries (KBC, BNY) are NBB-supervised banks |
| E15 | The ESMA register (file modified 30 Sep 2026): 194 of 364 entries serve Belgium. BE is a host for Revolut Digital Assets (Europe) (CY, including a trading platform), Payward Europe Solutions (Kraken, IE), Bitvavo (NL), OKX Europe (MT), Coinbase Lux., Bitstamp Europe, Bitpanda, Bybit EU, Gemini and Foris DAX (Crypto.com). Kraken's platform operator (Payward Global Solutions) lists only CY and IE. No Binance, MEXC, Hyperliquid, Bitget or HTX | S18 ESMA interim CASP CSV, parsed today | DOC | A passport is not proof of a product or of onboarding. What Payward Global's CY/IE scope means → UNKNOWN |
| E16 | The FSMA (6 Jul 2026) warns against six unauthorised crypto-asset service providers and points consumers to ESMA's register | S19 FSMA | DOC | — |
| E17 | The Belgian Gaming Commission's blacklist has polymarket.com (decision 30 Jan 2025, MB 5 Feb 2025); Kalshi is not listed. Knowingly playing on a listed site: a fine of EUR 26–25,000 times a multiplier | S20, S21 Gaming Commission | DOC | Whether Kalshi is gambling or a MiFID instrument in Belgium → UNKNOWN |
| E18 | Zenit: Rhea Digital Partners SRL, Brussels, Belgian law. It refuses BG, HR, MT, RO and SI plus about 50 non-EU states, not Belgium. Accounts are simulated demo accounts. Algorithms only if you own the source code; copy trading and HFT are banned | S22 T&C, S23 site | DOC | Whether AI-authored code counts as owned → ask in writing |
| E19 | Topstep: Belgium is on neither the ineligible list nor the express-funded-only list (Germany is express-funded-only). Prohibited conduct includes using software, AI or ultra-fast systems for unfair advantage, and VPNs | S24, S25 Topstep help centre | DOC | The AI clause is ambiguous. R06: API bots are allowed via TopstepX |
| E20 | Apex forbids AI and fully automated trading on funded and live accounts. Belgian eligibility was not read | R06 (search-indexed; Cloudflare blocked) | SECONDARY / UNKNOWN | — |
| E21 | The capital-gains law of 6 Apr 2026 (MB 21 Apr 2026) takes effect on 1 Jan 2026. New art. 90 9° c CIR 92: gains on financial assets from normal management. Art. 92 § 1 scope: MiFID financial instruments (derivatives included), some life insurance, crypto-assets (NFTs included), cash and e-money not on a payment account, investment gold, CBDCs. § 2: emigrating counts as a transfer | S26 Justel / MB | DOC | — |
| E22 | Art. 96/2 exempts a base EUR 4,855 a year, plus a carry-forward slice (art. 33: EUR 1,000 for income from 2027). Withholding is 10% (art. 269), taken by Belgian intermediaries from 1 Jun 2026 with an opt-out (art. 35). Pre-2026 gains are excluded by using the 31 Dec 2025 value (art. 102 § 4). Losses offset only within the same period and the same category (art. 102 § 5). The bill: 10% after EUR 10,000 indexed, carry-forward up to EUR 15,000 | S26; S27 Chamber bill | DOC | The final-rate article (171 2°) was read only through the amendment. Yearly indexation not verified |
| E23 | The bill's memorandum: normal management → 10%; abnormal management or speculation → art. 90 1° at 33%; gains inside a professional activity → professional income. Classification is a question of fact, judged against a normally prudent person. For crypto it lists the share of movable wealth, borrowing, use of an automated process or software, and the number of transactions | S27 DOC 56 1244/001, 17 Dec 2025 | DOC | A memorandum, not the statute. Case law is still to come |
| E24 | The tax ruling service's (DVB) crypto questionnaire asks about frequency, strategy (technical/trend, day trading, scalping, arbitrage), automated software and whether you designed it, profession, share of wealth, loans, trading for others, and advice taken | S28 DVB (2022) | DOC | Predates the 2026 law |
| E25 | TOB: a resident using a foreign intermediary declares and pays by the end of the second month (MyMinfin). Options, futures, swaps and CFDs are excluded. Rates 0.12 / 0.35 / 1.32% with caps. Crypto is not covered | S29 OECCBB summary of circular 2026/C/42 (18 Mar 2026) | SECONDARY | The official Fisconet page is JavaScript only |
| E26 | Foreign accounts: report once to the CPC (at the latest with the return) and mention every year (box XIII A). The FPS Finance calls declaring foreign crypto accounts advisable, while the rules may evolve. The NBB FAQ (excerpt): a crypto account needs reporting only at a foreign bank, exchange or credit institution (art. 318 CIR 92) | S30 FPS Finance; S31 NBB; S32 NBB FAQ excerpt | DOC / SECONDARY | The two official sources differ in tone → declare (INFERENCE) |

## 2. Venue paths for a Belgian retail resident

| Path | Status | Why | What would confirm it |
|---|---|---|---|
| Crypto spot at a MiCA service provider passported into BE (Revolut X, Kraken spot, Bitvavo, OKX Europe, Coinbase, Bitstamp) | **open** | E15. A crypto-asset bought outright is not a "financial product" under E1 (INFERENCE) | Onboarding as a Belgian resident; key scopes per R03/R08 |
| Kraken Futures EU perps (PEDSL-CY), retail | **closed** on the FSMA texts; **unknown** in practice | E1–E6; the venue still markets them to Belgium (E9) | Kraken compliance in writing: Belgian retail eligibility and its legal basis; FSMA contact centre |
| OKX Europe X-Perps, retail | **closed** on the texts; **unknown** in practice | E1–E6, E10 | The same, plus whether X-Perps is enabled in-app for BE |
| Perps at Kraken or OKX as an **elective professional** | **open** if two of three criteria are met and the firm accepts | E4, E11. Leverage caps and negative-balance protection may no longer apply | The firm's opt-up procedure; evidence (statements above EUR 500k, trade history, CV) |
| Trading through a company (BV/SRL) | **unknown** | The 2016 ban covers consumers only (DOC). The 2014 ban covers retail clients, which a small company remains unless opted up (INFERENCE). Company tax differs | Accountant and venue |
| Hyperliquid, live | **closed** (owner decision, R08) | Unlicensed; E1 on the text; no FSMA action found | —. Public data stays open |
| Binance, Bybit global, MEXC, live | **closed** | Not on ESMA's register (E15); the MEXC order (E6); Bybit's EEA API is broker-only (R08) | —. Binance and Bybit public data stay open |
| CME futures through Zenit (prop firm) | **open** on eligibility | E18. Accounts are simulated; ATAS order history is false, so the gateway refuses autonomy (repo) | Zenit in writing on AI-authored code; payout tax treatment |
| CME futures through Topstep | **open** on eligibility | E19 | Topstep in writing on AI-authored strategies through the TopstepX API |
| CME index futures in the owner's own broker account | **unknown** | The 2016 exemption names a regulated market or MTF; the commentary targets only OTC products; CME is outside the EEA | A broker (e.g. IBKR Ireland) onboarding Belgian retail for CME futures by API |
| CME crypto futures; crypto ETNs | **closed** for retail (INFERENCE) | The 2014 ban has no trading-venue exemption | A broker refusal would confirm it |
| Polymarket | **closed** | E17; Polymarket itself makes BE close-only (R03) | — |
| Kalshi and other event contracts | **closed** (INFERENCE) | The binary-option ban for consumers (E3); ESMA, 3 Jul 2026 (R03); no EU authorisation | —. Reading prices as signals is fine where reachable |

**Consequence for EDGE-FACTORY.** The first target (positioning features traded long/flat on spot, § 2) is unaffected.
**§ 4.8's "Kraken Futures EU first" does not hold for a Belgian retail client:** the short side (§ 10.1) becomes "professional
status, or no short side". The CME edge (§ 10.2) stays open through Zenit or Topstep. R08's rule-1 and rule-2 findings on Kraken
still decide the connector if the owner opts up.

## 3. Tax notes for the economics (EDGE-FACTORY § 8)

- **Three regimes** (DOC E21–E23): 10% above about EUR 10,000 a year (normal management); 33% (speculation, art. 90 1°); professional
  income at progressive rates plus self-employed social contributions (rates not checked → UNKNOWN). The EUR 10,000 exemption is
  tied to 9° c, so it does not cover speculative gains (INFERENCE).
- **Classification is the real risk** (DOC E23–E24, INFERENCE): the legislator's crypto criteria include automated software and the
  number of transactions, and TradeAgent is automated by construction. What argues for normal management: low turnover, a small
  share of wealth, no borrowing, holdings over days to weeks.
- **Scope** (DOC E21): derivatives and crypto fall inside the 2026 regime when the management is normal. How perps' funding
  payments, fees, liquidations and crypto-to-stablecoin swaps enter the gain → UNKNOWN.
- **Losses** (DOC E22): offset only in the same period and the same category. No loss carry-forward is visible in the text
  (INFERENCE).
- **Step-up** (DOC E22): pre-2026 holdings count from their 31 Dec 2025 value, which for listed assets is the last 2025 closing
  price. Keep that evidence.
- **Collection** (DOC E22, INFERENCE): Belgian intermediaries withhold the 10%. No candidate venue is Belgian, so the owner declares
  in the annual return.
- **Emigration** (DOC E21): moving domicile abroad counts as a transfer.
- **Size of the effect** (INFERENCE on § 8's figures): the learning tier (about $250 a year on $10,000; about $2,500 on $100,000)
  falls inside the exemption under normal management, so 0 tax; at 33% it is about $80 or $825 a year. At modest capital the tax is
  not the binding constraint; the uncertainty about the regime is.
- **TOB** (SECONDARY E25): none on crypto, futures or perps. If the system ever trades ETFs or shares through a foreign broker, every
  trade costs 0.12–1.32% (capped) and is self-declared, a real drag at high turnover.
- **Reporting** (DOC / SECONDARY E26): report the Kraken (CY/IE), Revolut X (CY), OKX (MT) and Bitvavo (NL) accounts to the CPC
  once and in box XIII every year (INFERENCE: declare when in doubt). DAC8 reporting by crypto service providers, in Belgium's
  transposition → UNKNOWN (not verified today).
- **Prop-firm payouts** (UNKNOWN): Zenit's accounts are simulated, so a payout is not a gain on an asset the owner holds. Whether it
  is miscellaneous or professional income, and whether VAT applies, is for the accountant.
- **Ruling** (DOC E24): the tax ruling service (DVB) works from its crypto questionnaire. An advance ruling before live trading can
  fix the classification.

## 4. Questions for the owner's accountant, the venues and the FSMA (before § 10.1)

Accountant:
1. Given TradeAgent's pattern (automated, frozen deterministic rules authored by AI, N trades a month, X% of wealth, no borrowing):
   10%, 33% or professional income? Is a DVB advance ruling, using its questionnaire (S28), worth filing before going live?
2. Would running it make the owner self-employed (social contributions)? What follows if only part of it is judged professional?
3. For perps and futures: when is a gain realised; do funding payments and fees count; how are crypto-to-crypto and stablecoin
   swaps treated?
4. Prop-firm payouts (Zenit, Topstep): which income category, VAT, and what documents to keep?
5. Which accounts go to the CPC and box XIII (Kraken IE/CY, Revolut CY, OKX MT, Bitvavo NL, prop firms)? Which records to keep
   (31 Dec 2025 values, trade logs; TradeAgent's ledger can export them)?
6. Would a company (BV/SRL) change the tax, and does it change product access (the 2016 consumer ban versus the 2014 retail ban)?

Venues and the regulator (in writing):
7. Kraken (PEDSL-CY): do you onboard Belgian-resident retail clients for perps, and on what basis given FSMA Regulations of
   3 Apr 2014 and 26 May 2016? Are your perps admitted to a regulated market or MTF? Do you accept elective-professional requests,
   and with what evidence?
8. OKX Europe Markets: is X-Perps enabled for Belgian retail clients? The same legal-basis and MTF questions.
9. FSMA consumer contact centre: does the 2014 virtual-money ban still reach crypto-asset derivatives from EEA MiFID firms after
   MiCA? Expected answer: the bans bind sellers, not the client (INFERENCE).
10. Zenit: does AI-authored source code that the owner holds count as "own source code"? Is ATAS automation allowed?
11. Topstep: does an owner-held, AI-authored automated strategy run through the TopstepX API fall under "unfair technology"?
12. A broker (e.g. IBKR Ireland): can a Belgian retail client trade CME index futures by API, and are KIDs or permissions needed?

## 5. Source registry (all read 2026-10-02 unless marked)

- S1 Justel, Reglement FSMA 3 Apr 2014, commercialisation ban (dossier 2014-04-03/41) — https://www.ejustice.just.fgov.be/cgi_loi/change_lg.pl?language=nl&la=N&cn=2014040341&table_name=wet
- S2 FSMA, 2014 regulation, EN translation — https://www.fsma.be/sites/default/files/legacy/sitecore/media%20library/Files/fsmafiles/wetgeving/reglem/en/reglem_24-04-2014.pdf
- S3 FSMA news "Ban on the marketing of certain financial products", 21 May 2014 — https://www.fsma.be/en/news/ban-marketing-certain-financial-products
- S4 Justel, Reglement FSMA 26 May 2016 (dossier 2016-05-26/43) — https://www.ejustice.just.fgov.be/cgi_loi/change_lg.pl?language=nl&la=N&cn=2016052643&table_name=wet ; approving RD of 21 Jul 2016 — https://www.ejustice.just.fgov.be/eli/besluit/2016/07/21/2016011324/justel
- S5 FSMA, 2016 regulation with commentary, EN translation — https://www.fsma.be/sites/default/files/legacy/reglem_26-05-2016_en.pdf
- S6 FSMA press release, MEXC order, 24 Jul 2024 — https://www.fsma.be/en/news/mexc-global-ltd-wwwmexccom-order
- S7 FSMA, Rapport annuel 2023 — https://www.fsma.be/sites/default/files/media/files/2024-06/fsma_ra2023_fr.pdf
- S8 ESMA35-243228190-8024, statement on derivatives in scope of the CFD measures, 24 Feb 2026 — https://www.esma.europa.eu/sites/default/files/2026-02/ESMA35-243228190-8024_-_Public_statement_on_derivatives_in_scope_of_the_CFD_product_intervention_measures.pdf
- S9 Kraken, Belgian perps page — https://www.kraken.com/be/pro/perps/crypto-perpetuals
- S10 Kraken, derivatives eligibility (upd. 28 Jul 2026) — https://support.kraken.com/articles/360023786632-kraken-derivatives-eligibility ; EEA derivatives offerings (upd. 3 Jul 2025) — https://support.kraken.com/articles/derivatives-offerings-for-eea-clients
- S11 OKX, X-Perps EEA eligibility (upd. 4 Sep 2026) — https://www.okx.com/en-eu/help/okx-x-perps-eea-regional-availability-eligibility
- S12 Directive 2014/65/EU Annex II, as adopted (UK archive) — https://www.legislation.gov.uk/eudr/2014/65/annex/II/adopted
- S13 Directive 2014/65/EU Art. 2, as adopted (UK archive) — https://www.legislation.gov.uk/eudr/2014/65/article/2/adopted
- S14 Law of 11 Dec 2025 implementing MiCA (MB 24 Dec 2025) — https://www.ejustice.just.fgov.be/eli/wet/2025/12/11/2025009586/justel
- S15 FSMA, crypto-asset service provider (CASP) page — https://www.fsma.be/en/crypto-asset-service-provider-casp
- S16 FSMA news, end of the transitional period, 25 Jun 2026 — https://www.fsma.be/en/news/crypto-assets-service-providers-end-transitional-period
- S17 FSMA lists, authorised and notified Belgian CASPs — https://www.fsma.be/en/list/authorised-belgian-crypto-asset-service-providers ; https://www.fsma.be/en/list/belgian-crypto-asset-service-providers-who-have-notified-their-intention-provide-crypto-asset
- S18 ESMA interim MiCA CASP register (Last-Modified 30 Sep 2026) — https://www.esma.europa.eu/sites/default/files/2024-12/CASPS.csv
- S19 FSMA warning on unauthorised crypto service providers, 6 Jul 2026 — https://www.fsma.be/en/warnings/beware-unauthorized-crypto-asset-service-providers
- S20 Gaming Commission, list of blocked illegal sites — https://gamingcommission.be/en/gaming-commission/illegal-games-of-chance/list-of-illegal-gambling-sites-blocked-by-the-gc
- S21 Gaming Commission FAQ on players' liability — https://gamingcommission.be/en/gaming-commission/faq/online-gambling/am-i-breaking-the-law-if-i-play-on-an-illegal-gambling-site
- S22 Zenit Funding T&C — https://zenitfunding.com/en/termsandconditions ; S23 Zenit site/FAQ — https://zenitfunding.com/en/index
- S24 Topstep eligibility — https://help.topstep.com/en/articles/8284116-am-i-eligible-to-trade-with-topstep ; S25 prohibited conduct — https://help.topstep.com/en/articles/10296582-prohibited-conduct
- S26 Law of 6 Apr 2026 on a tax on capital gains on financial assets (MB 21 Apr 2026) — https://www.ejustice.just.fgov.be/eli/wet/2026/04/06/2026002780/justel
- S27 Chamber bill DOC 56 1244/001, 17 Dec 2025 — https://www.dekamer.be/FLWB/PDF/56/1244/56K1244001.pdf
- S28 DVB crypto questionnaire (NL, 2022) — https://www.ruling.be/sites/default/files/content/download/files/vragenlijst_cryptomunten_nl_2022.pdf
- S29 OECCBB summary of TOB circular 2026/C/42 — https://blog.oeccbb.be/nl/article/circulaire-2026c42-faq-tob-taks-op-de-beursverrichtingen-versie-2/30749 (official: https://www.minfin.fgov.be/myminfin-web/pages/public/fisconet/document/47927f7f-0e8a-4be6-b536-91e121e22935, JavaScript only)
- S30 FPS Finance, foreign accounts — https://fin.belgium.be/en/private-individuals/international/foreign-income-accounts/accounts
- S31 NBB, Central Point of Contact — https://www.nbb.be/en/central-point-contact
- S32 NBB FAQ on foreign crypto accounts (403 today; search-index excerpt) — https://www.nbb.be/en/faq/do-i-have-report-foreign-crypto-account
- Repository: R03 §§ 1, 5; R06 § 1; R08 §§ 5–8 (same directory).
