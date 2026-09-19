namespace TradeAgent.Core;

public enum ErrorCode
{
    UNKNOWN_ERROR,
    AI_RUNTIME_NOT_FOUND, AI_INSTALL_FAILED, AI_VERSION_UNSUPPORTED,
    AI_AUTH_REQUIRED, AI_AUTH_FAILED, AI_AUTH_TIMEOUT,
    ATAS_NOT_FOUND, ATAS_NOT_RUNNING, ATAS_VERSION_UNSUPPORTED,
    ATAS_BRIDGE_MISSING, ATAS_BRIDGE_IN_USE, ATAS_BRIDGE_LOAD_FAILED, ATAS_BRIDGE_DISCONNECTED,
    TRADING_CONNECTION_MISSING, ACCOUNT_NOT_FOUND, MARKET_DATA_UNAVAILABLE,
    TRADING_PERMISSION_UNAVAILABLE,
    ORDER_REJECTED, ORDER_STATE_UNKNOWN, RECONCILIATION_FAILED,
    IPC_UNAVAILABLE, IPC_UNAUTHENTICATED, INCOMPATIBLE_PROTOCOL, WORKSPACE_CORRUPT, STATE_DATABASE_CORRUPT,
    // Authority / policy codes (TradeAgent-owned, not in the original brief).
    AI_TRADING_STOPPED, LIVE_NOT_ACTIVATED, MODE_FORBIDS_EXECUTION, MODE_ACCOUNT_MISMATCH,
    APPROVAL_REQUIRED, APPROVAL_EXPIRED, RISK_LIMIT_EXCEEDED, RISK_CHECK_UNAVAILABLE, TRADING_PAUSED_UNRECONCILED,
    // A PARKED PROPOSAL THAT PREDATES A LOSS-BUDGET BREACH DIES WITH IT, and it needs its own code
    // because the three neighbouring ones each tell the owner to do something different. It is not
    // APPROVAL_EXPIRED: nothing waited too long, and the AI may well be right to propose it again in
    // a minute. It is not LOSS_BUDGET_REACHED: that says the scope is shut, and by the time this can
    // be read the scope may be open again. What it says is that the ACCOUNT THIS ORDER WAS SIZED
    // AGAINST no longer exists — TradeAgent closed the book after the proposal was written — so
    // approving it would be answering a question about a position that has been closed.
    APPROVAL_PREDATES_LOSS_BREACH,
    // Its own code rather than RISK_LIMIT_EXCEEDED, because the two say different things to whoever
    // reads them: a breached ORDER limit is answered by asking for a smaller order, and a reached
    // LOSS budget is answered by not asking again today. An agent told the first when the second is
    // true will halve its size and try again, all day.
    LOSS_BUDGET_REACHED,
    EMERGENCY_PRESS_UNRESOLVED, POSITION_MOVED,
    // AN ORDER THAT IS STILL UNKNOWN ON THE INSTRUMENT A CLOSE IS BEING SIZED FROM, and the two
    // codes are the two callers rather than two rules. POSITION_MOVED is about a position that has
    // ALREADY changed; these are about one that is about to, by an order this gateway recorded and
    // never got an answer for. CLOSE_UNRESOLVED refuses the agent's own close or reduce;
    // CLOSE_UNRESOLVED_ON_INSTRUMENT is the word on the emergency press's leg, which refuses ONE
    // instrument and still closes the others.
    CLOSE_UNRESOLVED, CLOSE_UNRESOLVED_ON_INSTRUMENT,
    AUTONOMY_REQUIRES_PROVABLE_STATE,
    // The caller is authenticated and is not allowed to do THIS. Its own code rather than
    // IPC_UNAUTHENTICATED, which reads "your token is wrong" and would send whoever owns that peer
    // hunting a credential problem that is not there: the credential was fine and the role was not.
    ROLE_MAY_NOT_TRADE,
    // The ARMED live configuration will not start an AI runtime that nothing confines. Not
    // MODE_FORBIDS_EXECUTION, which is about an ORDER this mode will not send: this is about the
    // assistant's own program not being started at all, and the repair is a different one.
    CONTAINMENT_REQUIRED,
    // A TURN THAT REACHED ITS OWN BOUND, and stopped before the request that would have passed it.
    // Its own code because it is not a failure and not a refusal of anything the owner asked for: the
    // turn did work, that work is kept, and the repair is either a larger allowance on the Safety
    // page or a task split in two. It is the bound the app-owned harness enforces INSIDE a turn,
    // which is the thing a vendor CLI's advisory caps could never do.
    CONTEXT_BUDGET_EXCEEDED,
    // A WORKER ASKED FOR A TOOL ITS LAUNCH WAS NOT GRANTED, or for a path outside its own folder.
    // Not IPC_UNAUTHENTICATED and not ROLE_MAY_NOT_TRADE: the first reads "your credential is wrong"
    // and the second is specifically about moving money, while this covers every tool the surface
    // denies — and the answer to it is never "ask again", because there is nothing to ask.
    TOOL_NOT_GRANTED,
    // THE WINDOW ASKED FOR REACHES A DATASET'S HOLDOUT. Its own code rather than INVALID_REQUEST,
    // which reads "you sent something malformed", or MARKET_DATA_UNAVAILABLE, which reads "this
    // installation does not have it": the request was perfectly well formed, the data is there, and
    // this caller may not see it — a distinction an agent has to be able to act on, because the
    // repair is to ask for an earlier window rather than to fix the frame or collect more months.
    HOLDOUT_WITHHELD,
    // THE CAMPAIGN'S TRIAL OR VERDICT BUDGET IS SPENT. Not RISK_LIMIT_EXCEEDED, which is about an
    // order, and not HOLDOUT_WITHHELD, which is about a window: the request was legitimate and the
    // campaign has no attempts left, so the repair is a renewal the OWNER authorises, never a
    // smaller version of the same ask. The distinction LOSS_BUDGET_REACHED already makes.
    CAMPAIGN_BUDGET_REACHED,
    // THE DECISION BEHIND THIS ORDER IS OLDER THAN THE PROGRAM SAID IT MAY BE, or the closed bar it
    // was computed from is. `docs/COUNCIL.md`:33: "an expired opportunity takes the policy's safe
    // outcome, never a late trade".
    //
    // Its own code rather than MARKET_DATA_UNAVAILABLE, which is what the QUOTE-age refusal says and
    // means "no price is arriving": here prices are arriving perfectly well and the SIGNAL is old,
    // and an agent told to check its data connection would go hunting a fault that is not there.
    // Not RISK_LIMIT_EXCEEDED either — nothing about the order's size is wrong, and halving it would
    // not help. The repair is to decide again on the bar that has just closed, which is a thing the
    // runner does by itself and the owner does not have to do anything about.
    DECISION_EXPIRED,
    // THE CAPITAL GATE, AND IT IS TWO CODES BECAUSE THE TWO HAVE DIFFERENT REPAIRS.
    //
    // ALLOCATION_NONE: this version has no capital behind it at all — nobody allocated any, or the
    // promotion the allocation rested on no longer stands. A smaller order does not help and nothing
    // the caller can do helps: the owner allocates capital, in the app, and no verb asks for it.
    //
    // ALLOCATION_EXCEEDED: capital IS allocated and this order would take the version past it. A
    // smaller order genuinely does help, which is the whole difference, and it is the same
    // distinction LOSS_BUDGET_REACHED makes against RISK_LIMIT_EXCEEDED.
    //
    // Neither is RISK_LIMIT_EXCEEDED, which is about a limit the owner set on EVERY order: these are
    // about what one promoted strategy version may hold, and an agent told the wrong one would go
    // looking in Settings for a number that is not there.
    ALLOCATION_NONE, ALLOCATION_EXCEEDED,
    // THE ACCOUNT THE OWNER HANDED TO TRADEAGENT FOR EXPERIMENTS IS NOT A FREE ACCOUNT.
    //
    // An order naming no strategy version is not charged against any allocation — the owner's own buy
    // and the emergency press have nothing to be charged against, which is the reading
    // `AllocationCeilingOrThrow` takes and `docs/CONTRACTS.md` states. On an account under a standing
    // paper envelope that reading has a hole in it: an AGENT placing unattributed would be trading
    // inside the owner's grant while standing outside every bound the grant has — its ceiling, its
    // instrument, its deployment count and the version's own verdict.
    //
    // Its own code because the repair is its own: nothing about the order's SIZE is wrong and a
    // smaller one does not help, so ALLOCATION_EXCEEDED would send an agent looking for room that is
    // not the problem, and ALLOCATION_NONE would say "ask the owner for capital" when what is needed
    // is a version to place under. The owner's own press is never refused by it.
    ENVELOPE_ACCOUNT_RESERVED,
    INVALID_REQUEST, GATEWAY_ALREADY_RUNNING, ILLEGAL_STATE_TRANSITION,
    UPDATE_FAILED, UPDATE_INTEGRITY_FAILED, UPDATE_INSTALL_IN_PROGRESS,
    // An override file EXISTS and could not be parsed. Their own codes because the codes that used
    // to carry this said the opposite of the truth: AI_RUNTIME_NOT_FOUND reads "the AI assistant
    // program is not installed yet" and offers to install it, and ATAS_NOT_FOUND sends the owner to
    // atas.net — neither of which is the repair when the program is there and the file describing
    // it is not readable.
    RUNTIME_COMMANDS_UNREADABLE, ATAS_LAYOUT_UNREADABLE
}

/// <summary>
/// The labels of controls the app's own sentences have to name.
///
/// A repair sentence that says "press X" is a promise that a control called X is on a screen the
/// owner can reach. This file used to make that promise as "Press Install bridge." — and the only
/// Install bridge button there has ever been lives inside the setup wizard, which renders solely
/// while onboarding is unfinished. Once setup completed, three separate sentences sent the owner to
/// a control that had ceased to exist. Spelling the label once is what stops the sentence and the
/// button drifting apart again.
/// </summary>
public static class Labels
{
    /// <summary>On the Checks page when the bridge needs it, and on Settings always.</summary>
    public const string ReinstallBridge = "Reinstall the bridge";

    /// <summary>The button on the Safety page that writes the limits back.</summary>
    public const string SaveLimits = "Save limits";

    /// <summary>
    /// THE HOLDOUT CARD ON THE DATA PAGE, WHICH IS THE ONLY WAY A CUTOFF IS EVER SET.
    ///
    /// <para>Two presses, because it is the boundary of the evidence the AI is judged on and because
    /// the direction matters: setting one withholds bars, and the column can never be moved back
    /// afterwards (<c>DatasetStore.SetHoldout</c>). The armed sentence names the date and says in the
    /// owner's own words what it means, rather than the bare word "Confirm".</para>
    /// </summary>
    public const string HoldoutCutoff = "The date from which bars are held back";

    public const string SetHoldout = "Hold these bars back";

    public const string SetHoldoutFixture = "These are fixture bars";

    /// <summary>What the second press does, in full. The date is the one the owner typed.</summary>
    public static string SetHoldoutArmed(string date) =>
        $"Confirm: bars from {date} on are evidence the research process never sees";

    /// <summary>
    /// The other class. A fixture is bars that exist to prove the plumbing works, so a run over them
    /// is charged nothing and is never evidence — which is a grant, and therefore also two presses.
    /// </summary>
    public static string SetHoldoutFixtureArmed(string date) =>
        $"Confirm: these are fixture bars from {date} on — runs over them are never evidence";

    /// <summary>
    /// THE ALLOCATION CARD ON THE SAFETY PAGE, WHICH IS THE ONLY WAY CAPITAL IS EVER ALLOCATED.
    ///
    /// <para>Two presses in both directions, which the holdout card is the precedent for and which the
    /// ordinary "widening asks twice, narrowing asks once" rule does not cover. An allocation row
    /// cannot be edited or deleted (<c>Allocations</c>): a smaller ceiling is a NEW allocation from a
    /// later instant, so every press writes a permanent record of capital the owner put behind one
    /// version, and there is no press that takes one back. The first press is therefore the last
    /// moment they can change their mind, exactly as it is on a holdout.</para>
    ///
    /// <para>The armed sentence carries the VERSION and the FIGURE, because "Confirm" on its own would
    /// not say whose money is going where — and when the number widens what already stands it says so
    /// in the words the Safety page's own <c>Widenings</c> uses.</para>
    /// </summary>
    public const string AllocationVersion = "The promoted strategy version this capital is for";

    public const string AllocationQuantity = "The most it may hold at once";

    public const string AllocationNotional = "The most that may be worth (0 for no value limit)";

    public const string Allocate = "Allocate capital to this version";

    /// <summary>What the second press will do, in full, with the figures the owner typed.</summary>
    public static string AllocateArmed(string version, string quantity, string? value, bool widens) =>
        $"Confirm: version {version} may hold up to {quantity} at a time"
        + (value is { Length: > 0 } v ? $", worth at most {v}" : "")
        + (widens ? " — more than it may hold now" : "");

    /// <summary>
    /// THE PAPER-ENVELOPE CARD, BESIDE THE ALLOCATION CARD, AND IT IS A DIFFERENT KIND OF GRANT.
    ///
    /// <para>The allocation card puts the owner's MONEY behind one version. This one puts no money
    /// anywhere: it lets TradeAgent run experiments on an account both the platform and the account
    /// itself say is a simulation, up to a size, on one instrument, until a date — and then lets the
    /// app decide which eligible version goes into it, without asking again. That is why it is two
    /// presses: the thing being handed over is the deciding, and it is handed over for a period rather
    /// than for one press.</para>
    ///
    /// <para>Withdrawing is ONE press plus a confirm, because it only ever takes authority away — the
    /// kill switch's rule rather than the allocation card's, and the allocation card's rule does not
    /// apply because withdrawing an envelope is not a permanent record of a smaller grant: it stops
    /// every paper allocation underneath it at once.</para>
    /// </summary>
    public const string EnvelopeSymbol = "The instrument the experiments may trade";

    public const string EnvelopeQuantity = "The most any one of them may hold at once";

    public const string EnvelopeNotional = "The most that may be worth (0 for no value limit)";

    public const string EnvelopeUntil = "Until (days from now)";

    public const string GrantEnvelope = "Let TradeAgent run paper experiments";

    public const string WithdrawEnvelope = "Withdraw this paper envelope";

    public const string WithdrawEnvelopeArmed =
        "Confirm: stop all paper experiments under this grant";

    /// <summary>What the second press will do, in full, with the figures the owner typed.</summary>
    public static string EnvelopeArmed(string symbol, string quantity, string? value, DateTimeOffset until) =>
        $"Confirm: TradeAgent may run paper experiments in {symbol}, up to {quantity} at a time"
        + (value is { Length: > 0 } v ? $", worth at most {v}" : "")
        + $", until {until:yyyy-MM-dd} — practice account only, no capital, no real orders";

    /// <summary>The AI's own daily spending ceiling, and the press that writes it.</summary>
    public const string DailyCostCap = "The most the AI may spend on itself in a day";

    public const string SaveDailyCap = "Save the daily limit";

    /// <summary>
    /// What the second press of <see cref="SaveDailyCap"/> will do. RAISING the ceiling is a grant —
    /// the same act as raising a risk limit, done with a different number — so it asks twice and
    /// names the figure; LOWERING it is one press, because hesitating on the way down costs money.
    /// </summary>
    public static string RaiseDailyCapArmed(string amount) =>
        $"Confirm: let the AI spend up to {amount} a day";

    /// <summary>
    /// WHAT THE OWNER IS TOLD WHEN A TURN IS REFUSED BY THE CEILING, in the transaction that would
    /// otherwise have reserved it. One sentence in one place because three surfaces say it: the
    /// Chat page as a System line when their own message is the turn that cannot run, the mission
    /// card, and the report.
    ///
    /// It says nothing was started, because that is the half an owner cannot see for themselves —
    /// a chat that simply went quiet reads like a broken AI rather than a limit doing its job.
    /// </summary>
    public const string DailySpendingLimitReached =
        "The AI has reached the most it may spend on itself today, so this turn was not started. "
        + "Nothing was sent to the AI tool. It starts again after midnight, or sooner if you raise "
        + "the daily limit on the Safety page.";

    /// <summary>
    /// The same refusal from a ROLE's share rather than the owner's whole day. A different sentence
    /// because it has a different repair: the day's money is not gone, one role's share of it is,
    /// and the owner changes a split rather than a ceiling.
    /// </summary>
    public static string RoleShareReached(string role) =>
        $"The {role} has used its share of today's AI spending limit, so this turn was not started. "
        + "Nothing was sent to the AI tool. The split is on the Safety page.";

    /// <summary>
    /// What the Chat page says when the owner types while the AI is working. Beside the two refusals
    /// above because all three are the same promise: their words were kept, and here is what happens
    /// next.
    /// </summary>
    public const string HeldWhileTheAiIsWorking =
        "The AI is working. It will see this at the start of its next turn.";

    /// <summary>
    /// WHAT A ROLE ON THE APP-OWNED HARNESS SAYS WHEN NO KEY IS HELD, and why holding it in memory is
    /// the choice rather than an omission.
    ///
    /// It is one sentence rather than a repair code because it appears in three places that must not
    /// word it differently: the conversation the turn did not start in, the health row, and the box on
    /// the Safety page.
    /// </summary>
    public const string HarnessKeyNotHeld =
        "No API key is held for TradeAgent's own worker, so it started nothing. Paste one on the "
        + "Safety page — it is kept in memory for this session only and never written to disk.";

    /// <summary>The box the key is pasted into. Masked, and the sentence beside it says why.</summary>
    public const string HarnessKey = "API key for TradeAgent's own worker";

    /// <summary>
    /// WHY THE KEY IS NOT SAVED, said beside the box rather than in a document nobody opens. It is the
    /// round-4 rule in the owner's words: a key is "not retained beside an unsandboxed CLI process
    /// until containment lands", and `Containment.Sandbox()` still says NONE on every platform this
    /// builds on.
    /// </summary>
    public const string HarnessKeyHint =
        "Kept in memory for this session only. TradeAgent never writes it to disk and clears it when "
        + "it closes, because nothing on this computer yet confines the AI's own process from reading "
        + "the files TradeAgent keeps. Paste it again after a restart.";

    /// <summary>What the daily report's AI-spending section says about the key. Two words, no key.</summary>
    public static string HarnessKeyLine(bool held) => held ? "held" : "not held";

    /// <summary>The press that takes the pasted key, and the one that forgets it.</summary>
    public const string SaveHarnessKey = "Use this key";

    public const string ForgetHarnessKey = "Forget the key";

    /// <summary>What the page says about the key it is holding. Never any part of the key itself.</summary>
    public static string HarnessKeyState(bool held) => held
        ? "A key is held for this session. TradeAgent has not written it down; it is gone when the app closes."
        : "No key is held, so TradeAgent's own worker will not start a turn.";

    /// <summary>
    /// WHICH AI TOOL A ROLE RUNS ON. Two words for the two kinds of thing, because they are not two
    /// brands: one is a program on this computer that TradeAgent starts, and the other is TradeAgent
    /// calling the provider itself with the tools it chose and a budget it enforces per request.
    /// </summary>
    public const string ResearchRuntime = "What the Research Director runs on";

    public const string RuntimeIsTheCli = "The AI tool on this computer";

    public const string RuntimeIsTheHarness = "TradeAgent's own worker";

    /// <summary>
    /// THE TWO BOXES THAT SET WHAT ONE TURN IS COMMITTED TO COST BEFORE IT RUNS, beside the ceiling
    /// they are measured against. They are an upper bound rather than a prediction, and the note on
    /// the page says which way the arithmetic runs: a bigger allowance means fewer turns fit the day.
    /// </summary>
    public const string TurnAllowanceIn = "The most one turn may use, input tokens";

    public const string TurnAllowanceOut = "The most one turn may use, output tokens (reasoning included)";

    public const string SaveTurnAllowance = "Save what one turn may use";

    /// <summary>
    /// The formula, in the owner's words, on the page that sets it. Stated in one place because it
    /// is stated in three: here, <c>CONTRACTS.md</c> and the user guide.
    /// </summary>
    public const string TurnAllowanceHint =
        "TradeAgent sets this much aside before each turn: the input tokens at the dearer of the "
        + "plain and cache-write rates, plus the output tokens at the output rate. A bigger "
        + "allowance is a safer limit and fewer turns a day; a smaller one buys more turns and holds "
        + "less back. 0 in either box means the shipped default.";

    /// <summary>What the page says once the two boxes are written, naming what it works out to.</summary>
    public static string TurnAllowanceReads(long input, long output, string reservation) =>
        $"Saved. One turn now sets aside up to {reservation}: {input:N0} input tokens and "
        + $"{output:N0} output tokens.";

    /// <summary>How often the AI is woken when nothing has happened, and the press that writes it.</summary>
    public const string ReviewEvery = "Wake the AI to look around every, minutes (0 = only when something happens)";

    public const string SaveReviewEvery = "Save how often it looks";

    /// <summary>
    /// What the second press of <see cref="SaveReviewEvery"/> will do. LOWERING the interval is what
    /// asks twice here, which is the opposite of every risk limit and is the point: a shorter
    /// interval is more turns a day and every turn is charged to the owner. Raising it, or switching
    /// the tick off, only ever spends less, so it saves in one press.
    /// </summary>
    public static string LowerReviewEveryArmed(int minutes) =>
        $"Confirm: wake the AI every {minutes} minute{(minutes == 1 ? "" : "s")}, which costs more turns a day";

    /// <summary>
    /// WHAT A TURN NOBODY COULD IDENTIFY WAS CHARGED AT, in the same words on the card, in the block
    /// the AI reads and on <c>trade status</c>.
    ///
    /// It is one sentence in one place because it is one claim, and the claim is doing two jobs at
    /// once: it says the figure is real enough to hold the AI to a limit, and it says the figure is
    /// an upper bound rather than a bill. Softening either half breaks something — an owner who
    /// reads it as a bill will think the AI costs more than it does, and an owner who reads it as a
    /// guess will stop trusting the ceiling it is enforcing.
    /// </summary>
    public const string PricedAtHighestListPrice =
        "estimated at the highest list price — the AI did not say which model it used";

    /// <summary>What the card says once the owner has typed their own rate. Three words, on purpose.</summary>
    public const string PricedByYou = "priced by you";

    /// <summary>The row of models on the Safety page, and the button that means "whatever ships".</summary>
    public const string AiModel = "The model the AI runs on";

    public const string AiModelDefault = "TradeAgent's choice";

    /// <summary>
    /// THE COUNCIL'S TWO ROWS. The AI is two roles now — a chair and a Research Director, run one at
    /// a time — and these are the only two things about that the owner sets.
    ///
    /// The split is a REALLOCATION and never an addition: it divides the daily limit above rather
    /// than raising it, and neither role can change it. That is why it saves in one press, exactly
    /// as the model row does: a press that only moves money between two roles inside a ceiling the
    /// owner already set is not a grant.
    /// </summary>
    public const string ResearchShare = "Of that limit, the Research Director's share, %";

    public const string SaveResearchShare = "Save the split";

    public const string ResearchModel = "The model the Research Director runs on";

    /// <summary>What the split works out to, in the words the owner reads on the page.</summary>
    public static string SplitReads(int researchPercent, string operations, string research) =>
        $"{operations} gets {100 - researchPercent}% of the daily limit and {research} gets "
        + $"{researchPercent}%. It is the same limit either way.";

    /// <summary>
    /// WHAT A TURN WAS CHARGED AT WHEN TRADEAGENT CHOSE THE MODEL AND THE CLI DID NOT SAY WHICH ONE
    /// IT RAN — measured on codex 0.153.4, whose stream names no model even when <c>-m</c> was on
    /// the command line.
    ///
    /// It is a different sentence from <see cref="PricedAtHighestListPrice"/> because it is a
    /// different claim, and the difference is the whole of item 5: the dearest-model figure is a
    /// ceiling over a catalogue nobody chose from, and this one is the list price of the model
    /// TradeAgent put on the command line itself. Still not a bill — the vendor could serve
    /// something else and no event would say so — so it is still labelled wherever it is shown.
    /// </summary>
    public static string PricedAtTheModelAskedFor(string model) =>
        $"priced at {model}, the model TradeAgent asked for — the AI did not say which model it used";

    /// <summary>The Safety page's two boxes, and the press that writes them.</summary>
    public const string AiPriceIn = "What your AI tool charges, per million tokens in";

    public const string AiPriceOut = "…and per million tokens out";

    public const string SaveAiPrice = "Save the price";

    /// <summary>
    /// What the second press of <see cref="SaveAiPrice"/> will do, and WHY THIS ONE IS BACK TO
    /// FRONT. Everywhere else on that page the larger number is the grant. Here the LOWER price is:
    /// it does not let the AI trade more, it makes every turn count for less against the daily
    /// limit, so the same ceiling buys more turns and more of the owner's money is spent on the AI
    /// before anything stops it. The sentence has to say that, because the owner's instinct — that
    /// a smaller number is a smaller permission — is wrong here and nowhere else.
    /// </summary>
    public static string LowerAiPriceArmed(string amount) =>
        $"Confirm: price the AI's work at {amount} — a lower price lets it take more turns before "
        + "your daily limit stops it";

    /// <summary>The page holding the mode, the real-money switch, the limits and the allowlist.</summary>
    public const string SafetyPage = "Safety";

    /// <summary>
    /// AN AMOUNT WITH ITS CURRENCY, OR WITHOUT ONE WHERE NOTHING NAMED A CURRENCY.
    ///
    /// In Core because four layers that cannot see each other print money in the owner's words —
    /// the mission's Situation block, the Dashboard, the gateway's own refusals, and the agent's
    /// error text — and two formatters for one figure is how "5 USD" and "5.0000" end up on one
    /// screen. An empty currency prints the bare number rather than a guessed symbol.
    /// </summary>
    public static string Money(decimal amount, string currency) =>
        currency.Length == 0 ? amount.ToString("0.####") : $"{amount:0.####} {currency}";

    /// <summary>
    /// WHAT THE OWNER READS WHEN THE SAVED SETTINGS ROW CANNOT BE PARSED.
    ///
    /// It is the <c>Execution capability</c> health row's detail, so it appears on the Dashboard they
    /// are already looking at. It names the one page that can repair it and nothing else: the owner
    /// of this product never sees a terminal, a file path or a database, so a sentence mentioning any
    /// of those is a sentence they cannot act on.
    /// </summary>
    public const string SettingsCouldNotBeRead =
        "your settings could not be read; trading is stopped until you review them on the " + SafetyPage + " page";

    /// <summary>The card the Safety page shows above everything else while that is true.</summary>
    public const string SettingsCouldNotBeReadTitle = "Settings TradeAgent could not read";

    /// <summary>The same fact, said on the page that repairs it, where it can be a whole sentence.</summary>
    public const string SettingsCouldNotBeReadBanner =
        "Your settings could not be read, so TradeAgent has stopped the AI and is allowing nothing. "
        + "Check every value on this page, then press " + SaveLimits + " to save them again.";

    /// <summary>
    /// THE HALF OF THE REPAIR THAT IS NOT ON THE SAFETY PAGE. Everything the owner had set was lost
    /// together, and two of those values are set elsewhere: the mode is above this card, and the
    /// account is on Settings. A repair sentence that only mentions the limits leaves an owner who
    /// followed it exactly with the AI still unable to trade and no reason given.
    /// </summary>
    public const string SettingsCouldNotBeReadNext =
        "The trading mode and your account were lost with them. Set the mode above, and choose your "
        + "account again on the Settings page.";

    /// <summary>
    /// What an empty instrument allowlist means, said wherever the list is shown. An empty list is
    /// not a wildcard (see <see cref="RiskPolicy.InstrumentAllowlist"/>), and the box the owner
    /// clears has to say which of the two it did.
    /// </summary>
    public const string NoInstrumentAllowed = "No instrument is allowed until you add one.";

    // ---- the two files that say what TradeAgent runs, and where it looks -------------------------
    //
    // These name a FILE, which the sentences above deliberately never do — and the difference is the
    // reader. Nobody meets runtimes.json or atas.json by accident: they exist so that somebody who
    // has decided to correct a vendor's command or folder can, and the one thing that person needs
    // told is that the edit did not take. Saying it without naming the file would be useless to
    // them and no gentler to anyone else. There is still no path, no command and no console here.

    /// <summary>The file holding the AI assistants' install, sign-in and sandbox commands.</summary>
    public const string RuntimesFile = "runtimes.json";

    /// <summary>The file holding ATAS's folders, process names and executables.</summary>
    public const string AtasFile = "atas.json";

    /// <summary>The file holding what the AI assistants charge, per model, per million tokens.</summary>
    public const string CostsFile = "costs.json";

    /// <summary>
    /// WHAT THE OWNER READS WHEN <see cref="RuntimesFile"/> EXISTS AND CANNOT BE PARSED. It is the
    /// <c>Agent runtime</c> health row's detail, the Checks page's row, and the refusal to start the
    /// AI — one sentence in all three, which is why <see cref="Errors"/> quotes this rather than
    /// spelling its own. It promises what the product then does: nothing built in is put in the
    /// broken file's place, because a manifest decides which program runs and under what sandbox.
    ///
    /// <paramref name="why"/> is omitted where the sentence is already carrying a repair beside it
    /// (the error catalogue), and given where it is the whole of what the row says.
    /// </summary>
    public static string RuntimesCouldNotBeRead(string? why = null) =>
        RuntimesFile + " could not be read, so TradeAgent will not start an AI assistant and is not "
        + "falling back to the commands it ships with." + Because(why);

    /// <summary>
    /// The same, for <see cref="CostsFile"/> — and it promises something SMALLER than the other two,
    /// on purpose. An unreadable <see cref="RuntimesFile"/> stops the AI, because that file decides
    /// which program runs and under what sandbox. This one only prices turns that have already
    /// happened, so an unreadable one cannot be allowed to stop the work — what it does instead is
    /// make every turn's cost unknown, which makes the daily limit unenforceable, and the owner is
    /// told exactly that rather than shown a limit that is holding nothing back.
    /// </summary>
    public static string CostsCouldNotBeRead(string? why = null) =>
        CostsFile + " could not be read, so TradeAgent cannot say what the AI is costing and cannot "
        + "hold it to your daily limit." + Because(why);

    /// <summary>The same, for <see cref="AtasFile"/>: both ATAS rows and the Checks page.</summary>
    public static string AtasLayoutCouldNotBeRead(string? why = null) =>
        AtasFile + " could not be read, so TradeAgent will not look for ATAS and is not falling back "
        + "to the folders it ships with." + Because(why);

    static string Because(string? why) => why is null ? "" : $" The reason: {why}.";

    /// <summary>What to do about either of them, said once.</summary>
    public static string OverrideFileRepair(string file) =>
        $"Correct {file} or delete it. Deleting it puts TradeAgent back on the settings it ships with.";

    // ---- the armed sentences of every control that GRANTS authority ----------------------------
    //
    // A two-step control says what its second press will do, in full, and the sentence lives here
    // rather than at the widget so a test and a guide can quote the words the owner actually reads.
    // The rule the sentences implement: a press that gives the AI more room is two, a press that
    // takes room away is one (REVIEW 2026-09-05b finding 3, Codex F12).

    /// <summary>The kill switch, in the window chrome and on the Safety page. One press, always.</summary>
    public const string StopAiTrading = "STOP AI TRADING";

    /// <summary>The same control once the AI is stopped. This direction hands permission back.</summary>
    public const string ResumeAiTrading = "RESUME AI TRADING";

    /// <summary>What RESUME's second press does. STOP has no armed sentence: it is one press.</summary>
    public const string ResumeAiTradingArmed = "Confirm: let the AI trade again";

    /// <summary>The mode that places real orders with nobody in the loop.</summary>
    public const string ModeAutonomousArmed = "Confirm: let the AI place real orders without asking";

    /// <summary>The mode that proposes real orders. Still real money, still a grant, still two.</summary>
    public const string ModeAskFirstArmed = "Confirm: let the AI propose real orders";

    // The seven safety limits, named once. The Safety page labels its fields with these and the
    // armed save sentence names the one that widened, so the two cannot drift apart.
    public const string MaxOrderQuantity = "Most it may buy or sell in one order";
    public const string MaxNotionalPerOrder = "Most money one order may be worth";
    public const string MaxOpenPositions = "Most positions it may hold at once";
    public const string MaxOrdersPerMinute = "Most orders per minute";

    /// <summary>The per-position loss budget. Everything else on the page bounds an ORDER.</summary>
    public const string MaxLossPerTrade = "Most it may lose on one position";

    /// <summary>The day's loss budget — the one limit that is about the day rather than an order.</summary>
    public const string MaxDailyLoss = "Most it may lose in one day";

    /// <summary>
    /// What both loss boxes say under them: which reading a zero has, and what unit the number is
    /// in. The currency is the ACCOUNT'S and arrives only once the platform has answered, so it is
    /// named when it is known and left out when it is not — a money limit labelled with a guessed
    /// currency is worse than one labelled with none.
    /// </summary>
    public static string LossBudgetHint(string currency = "") =>
        currency.Length == 0
            ? "0 means not enforced."
            : $"0 means not enforced. This is in {currency}, your account's currency.";

    /// <summary>
    /// THE CLOSURE LENGTH, as the owner's own number. Shortening it is the grant, which is the
    /// opposite of the four caps above it — see <c>RiskPolicy.LossMinClosureHours</c>.
    /// </summary>
    public const string LossMinClosure = "How long it stays closed after a losing day (hours)";

    /// <summary>The strike window, in UTC dates. Shortening it is the grant.</summary>
    public const string LossStrikeWindow = "Days in which a second losing day holds it for you";

    /// <summary>
    /// What the two duration boxes say under them. It has to say which direction is the risky one,
    /// because on these two the smaller number is the one that hands the AI more room, and every
    /// other box on the page reads the other way round.
    /// </summary>
    public const string LossClosureHint =
        "TradeAgent will not reopen a scope that reaches the budget twice inside that many days — you "
        + "release that yourself. Making either number SMALLER gives the AI more room, so it asks again "
        + "first. Neither one changes a closure that is already standing.";

    /// <summary>
    /// THE DATA-LOSS EXIT, as the owner's own number. LENGTHENING it is the grant here — see
    /// <c>RiskPolicy.ValuationLossExitMinutes</c> — so it is the one box on this page whose risky
    /// direction is neither of the two the boxes above it have.
    /// </summary>
    public const string ValuationLossExit = "Close a position nobody can value after (minutes)";

    /// <summary>
    /// What the data-loss box says under it. It has to carry the zero's reading and what the exit
    /// is NOT, because an owner who reads "TradeAgent closed my position" and assumes their loss
    /// budget went will go looking for a loss that did not happen.
    /// </summary>
    public const string ValuationLossExitHint =
        "If TradeAgent cannot work out what an open position is worth for that long without a break — a "
        + "silent feed, a platform that stops marking — it closes the position, because your loss budget "
        + "cannot bound what nothing is measuring. It is NOT a budget breach: no day and no instrument is "
        + "closed by it. 0 switches the exit off and leaves such a position open. A LONGER wait gives the "
        + "AI more room, so lengthening it asks again first.";

    public const string InstrumentAllowlist = "Instruments it may touch";

    /// <summary>
    /// WHY A REAL-MONEY MODE CANNOT BE CHOSEN WHILE THE DAY HAS NO BOUND, named where it is refused
    /// and quoted by the test that proves the refusal.
    ///
    /// There is no human in the loop for real money (decided 2026-09-06), so the day's budget is the
    /// only thing between an agent that trades non-stop and an account that ends the day empty. The
    /// sentence names the field the owner has to fill in, because a refusal that does not is a
    /// refusal they cannot act on.
    /// </summary>
    public static string LiveNeedsADailyLossBudget(string mode) =>
        $"{mode} places real orders and nothing here is watching the day, so it cannot be chosen "
        + $"while “{MaxDailyLoss}” is 0. Set that limit on the " + SafetyPage + " page first.";

    /// <summary>
    /// What the second press of <see cref="SaveLimits"/> will do, when the values in the boxes give
    /// the AI more room than the ones it is working under. A save that only narrows is one press.
    /// One widened cap is named; several are counted, because five of these names on one button is
    /// a sentence nobody reads.
    /// </summary>
    public static string WidenLimitsArmed(IReadOnlyList<string> wider) =>
        wider.Count == 1
            ? $"Confirm: widen “{wider[0]}”"
            : $"Confirm: widen {wider.Count} of your safety limits";

    // ---- the review hold and its release (U-reopen-2) ---------------------------------------------

    /// <summary>
    /// THE ONE PRESS THAT LIFTS A REVIEW HOLD. A scope that reached the loss budget twice inside the
    /// strike window is not reopened by code at all, and this is the only thing anywhere in the
    /// product that changes that — in process, on the Safety page, with a note, and twice.
    /// </summary>
    public const string ReopenAfterReview = "Reopen after review";

    /// <summary>The label on the required note. It is the durable trace of the decision.</summary>
    public const string ReopenAfterReviewNote = "What you looked at — required";

    /// <summary>
    /// The hint under the box, which has to say what the press does NOT do: an owner who reads
    /// "reopen" and expects the account to trade on the next tick has been told the wrong thing.
    /// </summary>
    public const string ReopenAfterReviewHint =
        "This lifts the review hold only. TradeAgent still reopens the closure itself, once it has run "
        + "its time and a fresh reading of your platform shows nothing open. The AI cannot press this "
        + "and has no command to ask for it.";

    /// <summary>
    /// WHAT THE SECOND PRESS WILL DO, IN FULL — never the bare word "Confirm", and it names the
    /// scopes rather than counting them, because "release 2 holds" does not say which account or
    /// which instrument is about to be allowed back in.
    /// </summary>
    public static string ReleaseHoldArmed(IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return scopes.Count == 0
            ? "Nothing is being held for review"
            : $"Confirm: release the review hold on {string.Join(", ", scopes)} — TradeAgent may reopen "
              + $"{(scopes.Count == 1 ? "it" : "them")} once the closure has run its time";
    }
}

/// <summary>Technical detail, plain-language explanation, suggested repair, and whether we can fix it ourselves.</summary>
public sealed record ErrorInfo(ErrorCode Code, string Technical, string UserMessage, string Repair, bool AutoRepairable);

public static class Errors
{
    static readonly Dictionary<ErrorCode, (string User, string Repair, bool Auto)> Catalogue = new()
    {
        // There is no Diagnostics screen. The page is called Checks and the button on it is called
        // Check everything, and this is the sentence an owner gets when the bridge repair fails for
        // a reason nothing else in this catalogue names.
        [ErrorCode.UNKNOWN_ERROR]                  = ("Something unexpected went wrong.", "Press Check everything on the Checks page.", false),
        [ErrorCode.AI_RUNTIME_NOT_FOUND]           = ("The AI assistant program is not installed yet.", "TradeAgent can install it for you.", true),
        // The other sentence that named Retry. The control it means is the setup step's "Try again"
        // (OnboardingView.cs:597), which is what an owner is actually looking at when they read this.
        [ErrorCode.AI_INSTALL_FAILED]              = ("The AI assistant could not be installed.", "Check your internet connection, then press Try again.", true),
        [ErrorCode.AI_VERSION_UNSUPPORTED]         = ("The installed AI assistant is too old for this version of TradeAgent.", "TradeAgent can update it for you.", true),
        [ErrorCode.AI_AUTH_REQUIRED]               = ("You need to sign in to your AI account.", "Press Sign in. A browser window will open.", false),
        [ErrorCode.AI_AUTH_FAILED]                 = ("Signing in to the AI account did not work.", "Press Sign in again and complete the browser steps.", false),
        [ErrorCode.AI_AUTH_TIMEOUT]                = ("Signing in took too long and was cancelled.", "Press Sign in again.", false),
        // No control anywhere in this product is called Retry, and this is the sentence an owner gets
        // for pressing the bridge repair on a computer where ATAS is not installed. It now names the
        // action instead of a button that does not exist.
        [ErrorCode.ATAS_NOT_FOUND]                 = ("ATAS is not installed on this computer.", "Install ATAS on this computer, then try again.", false),
        [ErrorCode.ATAS_NOT_RUNNING]               = ("ATAS is not running.", "Press Open ATAS.", true),
        [ErrorCode.ATAS_VERSION_UNSUPPORTED]       = ("Your ATAS version changed and the TradeAgent bridge needs updating.", "Press Repair.", true),
        [ErrorCode.ATAS_BRIDGE_MISSING]            = ("The TradeAgent bridge is not installed into ATAS yet.", $"Press {Labels.ReinstallBridge} on the Checks page.", true),
        // The one failure of the copy that is not the owner's mistake and not a broken machine: ATAS
        // has the assembly loaded, so Windows refuses to replace the file. The repair is one action
        // and the sentence says only that action — an owner who never sees a terminal cannot be sent
        // to a folder, a process list or a command.
        [ErrorCode.ATAS_BRIDGE_IN_USE]             = ("ATAS is using the bridge file, so TradeAgent could not replace it.", $"Close ATAS, then press {Labels.ReinstallBridge} again.", true),
        [ErrorCode.ATAS_BRIDGE_LOAD_FAILED]        = ("ATAS could not load the TradeAgent bridge.", "Press Repair, then restart ATAS.", true),
        [ErrorCode.ATAS_BRIDGE_DISCONNECTED]       = ("TradeAgent lost its connection to ATAS.", "Make sure ATAS is open and the TradeAgent Bridge strategy is started.", false),
        [ErrorCode.TRADING_CONNECTION_MISSING]     = ("ATAS has no trading connection logged in.", "Log in to your broker inside ATAS.", false),
        [ErrorCode.ACCOUNT_NOT_FOUND]              = ("The trading account could not be found.", "Choose your account again in Settings.", false),
        [ErrorCode.MARKET_DATA_UNAVAILABLE]        = ("No live prices are arriving.", "Check your ATAS data connection and your internet.", false),
        [ErrorCode.TRADING_PERMISSION_UNAVAILABLE] = ("This account cannot place orders right now.", "Check with your broker that trading is enabled.", false),
        [ErrorCode.ORDER_REJECTED]                 = ("The broker rejected the order.", "The reason is in the activity history. No money moved.", false),
        [ErrorCode.ORDER_STATE_UNKNOWN]            = ("TradeAgent cannot yet confirm what happened to an order.", "AI trading is paused until it is confirmed. This is deliberate.", false),
        [ErrorCode.RECONCILIATION_FAILED]          = ("TradeAgent could not confirm the true state of your orders.", "Open ATAS and check your orders, then press Resume.", false),
        [ErrorCode.IPC_UNAVAILABLE]                = ("The AI cannot reach the trading service.", "Restart TradeAgent.", true),
        [ErrorCode.IPC_UNAUTHENTICATED]            = ("A program tried to use trading without permission.", "No action needed. The request was refused.", false),
        [ErrorCode.ROLE_MAY_NOT_TRADE]             = ("A part of the AI that is not allowed to trade asked to place, change or cancel an order.", "No action needed. The request was refused and recorded.", false),
        // The owner's own sentence for both of these says what was refused and that nothing is broken:
        // the AI asking for the months you held back, and the AI running out of attempts, are the
        // referee's protocol working rather than a fault to repair.
        [ErrorCode.HOLDOUT_WITHHELD]               = ("The AI asked to read the market data you are holding back, and was refused.", "No action needed. The bars you held back stay private; TradeAgent uses them itself to judge a finished strategy.", false),
        [ErrorCode.CAMPAIGN_BUDGET_REACHED]        = ("The AI has used all the attempts this research campaign allows.", "No action needed unless you want to allow more: the budgets are on the Safety page, and a new campaign keeps the same held-back data.", false),
        [ErrorCode.CONTAINMENT_REQUIRED]           = ("TradeAgent will not start the AI assistant while real-money trading is switched on, because nothing on this computer confines the assistant's own program.", "Switch real-money trading off, or choose Practice or Watch only. Everything else about the AI is unchanged.", false),
        // NOT A FAILURE, and the repair sentence says so: the work the turn did is kept and the next
        // turn starts fresh. The two boxes named here are the ones on the Safety page.
        [ErrorCode.CONTEXT_BUDGET_EXCEEDED]        = ("One AI turn reached the most it is allowed to read and write, so TradeAgent stopped it before its next request.", $"Nothing was lost. Raise “{Labels.TurnAllowanceIn}” on the Safety page if its work needs more room.", false),
        [ErrorCode.TOOL_NOT_GRANTED]               = ("The AI asked for something TradeAgent does not let that part of it do.", "No action needed. The request was refused and recorded.", false),
        // Deliberately NOT IPC_UNAUTHENTICATED. A peer refused here may hold a perfectly good token;
        // what it does not share is the shape of the conversation, and telling its owner to go
        // looking for a permission problem sends them after a fault that is not there. It is the
        // agent-pipe twin of the bridge's version mismatch, and it says the same thing: the two
        // halves were built against different protocols, so update the half that is behind.
        [ErrorCode.INCOMPATIBLE_PROTOCOL]          = ("A program tried to talk to TradeAgent using a version of its trading protocol this build does not speak.", "Update TradeAgent, or the program that is talking to it, so both are the same version.", false),
        [ErrorCode.WORKSPACE_CORRUPT]              = ("The AI's working folder is damaged.", "Press Repair workspace.", true),
        [ErrorCode.STATE_DATABASE_CORRUPT]         = ("TradeAgent's records are damaged.", "Press Repair. Your broker account is not affected.", true),
        [ErrorCode.AI_TRADING_STOPPED]             = ("AI trading is stopped.", "Press Enable AI trading when you want it to resume.", false),
        [ErrorCode.LIVE_NOT_ACTIVATED]             = ("Real-money trading has not been switched on.", "Switch it on in Settings if that is what you want.", false),
        // True for every mode that reaches this code. It is raised when the mode forbids the AI to
        // trade at all (observe-only) AND when a mode that does allow trading is not the mode this
        // particular order was proposed under — approving a confirm-each-order proposal after
        // switching to paper or to fully automatic. "The AI is not allowed to trade" would be false
        // in those last two.
        [ErrorCode.MODE_FORBIDS_EXECUTION]         = ("TradeAgent's current mode does not allow this order.", "Check the mode on the Dashboard. An order the AI has already proposed can only be approved in the confirm-each-order mode it was proposed in.", false),
        [ErrorCode.MODE_ACCOUNT_MISMATCH]          = ("Paper mode refused to send an order to a real-money account.", "Select a simulation account, or switch mode deliberately.", false),
        [ErrorCode.APPROVAL_REQUIRED]              = ("The AI is asking permission to place an order.", "Approve or decline it in TradeAgent.", false),
        [ErrorCode.EMERGENCY_PRESS_UNRESOLVED]     = ("The last press of this emergency control has not been resolved yet.", "Open the Dashboard, read what it did, and confirm each line. Then you can press it again.", false),
        // A CLOSING ORDER IS SIZED FROM A POSITION, and the position it was sized from is not the one
        // that is there now. Sending it anyway is the failure this code exists to prevent: closing 2
        // of a position that is now 1 opens a short, and closing a long that has already flipped
        // doubles it. Nothing was sent, and it is a changed decision rather than a broken machine —
        // which is why the repair is "ask again", not "check something".
        [ErrorCode.POSITION_MOVED]                 = ("The position moved while TradeAgent was preparing to close it, so the closing order no longer matched it.", "Nothing was sent and your position is untouched. Ask again and it will be sized against the position as it is now.", false),
        // AN OLD SIGNAL, AND THE STRATEGY'S OWN NUMBER IS WHAT MADE IT OLD. The sentence says whose
        // rule it was, because "TradeAgent refused your order" reads as a fault in the software and
        // this is the software doing exactly what the promoted program asked for. Nothing for the
        // owner to press: the runner decides again on the next closed bar without being told to.
        [ErrorCode.DECISION_EXPIRED]               = ("The trading signal behind this order was older than the strategy itself allows, so it was not sent.", "Nothing was sent and your position is untouched. The strategy decides again when the next bar closes; there is nothing for you to do.", false),
        // THE TWO CAPITAL SENTENCES, AND THE OWNER IS THE ONLY PERSON WHO CAN ANSWER EITHER. Nothing
        // the AI does changes the first: capital is allocated on the Safety page, by the owner, and no
        // command anywhere asks for it. The second is a ceiling doing its job and needs nothing done at
        // all, which the sentence says so that a working limit does not read as a fault.
        [ErrorCode.ALLOCATION_NONE]                = ("The strategy that tried to place this order has no capital behind it, so nothing was sent.", "Nothing was sent and your positions are untouched. Allocate capital to that version on the Safety page — and if it used to have some, TradeAgent has withdrawn the evidence its promotion rested on, which the Capital card names.", false),
        [ErrorCode.ALLOCATION_EXCEEDED]            = ("This order would take a strategy past the capital you allocated to it, so it was not sent.", "Nothing was sent and your positions are untouched. There is nothing to do: the strategy may trade again once it is holding less, or you can raise its allocation on the Safety page.", false),
        // THE PAPER-ENVELOPE SENTENCE. Nothing is wrong and nothing needs pressing: the AI asked to
        // trade an account the owner set aside for TradeAgent's own experiments without saying which
        // experiment, and the account is doing exactly what they granted it for.
        [ErrorCode.ENVELOPE_ACCOUNT_RESERVED]      = ("The AI tried to place an order on the practice account you set aside for TradeAgent's own paper experiments, without saying which strategy version it was for, so nothing was sent.", "Nothing was sent and your positions are untouched. There is nothing for you to do: that account only accepts orders from a version TradeAgent has allocated to it, and your own orders on it are unaffected. Withdraw the paper envelope on the Safety page if you want the account back.", false),
        // THE OTHER HALF OF POSITION_MOVED, and the owner has to be told which half this is. Above:
        // the position already changed. Here: TradeAgent is holding an earlier order on the same
        // instrument that it never got an answer for, and that order can still fill and move the
        // position the same way this close would. Sending a second one sized from the position as it
        // reads NOW is how a long 2 becomes a short 2. The repair is an outcome for that order, and
        // there are two places one comes from: the unconfirmed card, and Close all positions, which
        // reads the order back and stops it before it closes anything.
        [ErrorCode.CLOSE_UNRESOLVED]               = ("TradeAgent has an earlier order on this instrument that it could not confirm, and that order could still move the position, so it refused to send a second one sized from the position as it looks now.", "Nothing was sent and your position is untouched. Confirm the unconfirmed order on the Dashboard, or press Close all positions — it reads that order back and stops it first.", false),
        // The same fact, said about ONE LEG of an emergency press. It is a different sentence because
        // it prescribes a different reading: the press did close every other instrument, so what the
        // owner is being told is that this one is the exception and may still be open.
        [ErrorCode.CLOSE_UNRESOLVED_ON_INSTRUMENT] = ("One instrument was left alone by the emergency press: TradeAgent has an earlier order on it that it could not confirm and could not stop, so it sent nothing rather than close on top of it. That position may still be open.", "Every other position was closed. Open ATAS and look at this instrument, confirm the unconfirmed order on the Dashboard, then press Close all positions again.", false),
        [ErrorCode.APPROVAL_EXPIRED]               = ("An order the AI proposed waited too long for your approval and was declined.", "Nothing was sent. If you still want it, ask the AI to propose it again.", false),
        // NOT "you waited too long" and NOT "the budget is reached". The proposal was written against
        // a book TradeAgent has since closed for you, so approving it would put on a position sized
        // from an account that is gone. The repair is the AI's, and it takes one turn.
        [ErrorCode.APPROVAL_PREDATES_LOSS_BREACH]  = ("An order the AI proposed BEFORE your loss budget was reached was declined. TradeAgent closed the account to new risk after that, and closed what was open, so the position this order was sized against is no longer there.", "Nothing was sent. If you still want it, ask the AI to propose it again against the account as it is now.", false),
        [ErrorCode.RISK_LIMIT_EXCEEDED]            = ("The order was refused because it breaks a safety limit you set.", "Change the limit in Settings if it is too strict.", false),
        // NOT the same sentence as a breached order limit, and not the same repair. Nothing about
        // this order was wrong; the day, or the position, is already down as far as the owner said
        // it may go. Closing and reducing are untouched, which is the half an owner has to be told
        // — a refusal that reads as "trading is off" is one they would answer by raising the budget.
        [ErrorCode.LOSS_BUDGET_REACHED]            = ("The AI has lost as much as you allow it to, so it is not being allowed to take on any more risk.", $"Nothing was sent. Closing or reducing a position is still allowed. If TradeAgent has confirmed the breach it has also closed what was open, and it reopens the account itself once the closure has run its 24 hours and it can see your book is flat — there is nothing to press. Change the budget on the {Labels.SafetyPage} page if it is too tight.", false),
        // Distinct from RISK_LIMIT_EXCEEDED, and the difference is the whole of it: no limit was
        // broken — TradeAgent could not work out whether one would be. A change to an order it
        // cannot read is a change whose effect on your exposure is unknown; so is an order in an
        // instrument whose contract size the platform will not report, since the value limit is that
        // size times the price. Either way the unknown is refused rather than waved through.
        [ErrorCode.RISK_CHECK_UNAVAILABLE]         = ("TradeAgent could not read something it needed from the trading platform, so it could not check this order against your safety limits.", "Nothing was sent. Check the platform is connected and showing the instrument, then ask again.", false),
        [ErrorCode.AUTONOMY_REQUIRES_PROVABLE_STATE] = ("Fully automatic real-money trading is refused because this platform cannot confirm what happened to an order after a disconnection.", "Use confirm-each-order mode instead, or paper mode.", false),
        [ErrorCode.TRADING_PAUSED_UNRECONCILED]    = ("Trading is paused because an earlier order is unconfirmed.", "TradeAgent is checking with the broker. It resumes on its own.", true),
        [ErrorCode.INVALID_REQUEST]                = ("The AI sent a request TradeAgent did not understand.", "No action needed.", false),
        [ErrorCode.GATEWAY_ALREADY_RUNNING]        = ("TradeAgent is already running.", "Use the window that is already open.", false),
        [ErrorCode.ILLEGAL_STATE_TRANSITION]       = ("An internal safety check blocked an inconsistent update.", "Nothing was sent to the broker. Create a support package.", false),
        [ErrorCode.UPDATE_FAILED]                  = ("The new version of TradeAgent could not be installed.", "Check your internet connection and press Install update again. The version you have is untouched.", true),
        // Distinct from UPDATE_FAILED on purpose. "Could not be installed" is a download that did
        // not arrive; this is a download that arrived and was not what the publisher said it would
        // be, which is the one failure in this product that is never the owner's internet. It used
        // to be reported as AI_INSTALL_FAILED — "The AI assistant could not be installed" — which
        // names the wrong program entirely.
        [ErrorCode.UPDATE_INTEGRITY_FAILED]        = ("The new version of TradeAgent did not match the checksum published with it, so it was not installed.", "Nothing was installed and the version you are running is untouched. Press Install update again; if it keeps happening the published release is at fault, not your computer.", false),
        [ErrorCode.UPDATE_INSTALL_IN_PROGRESS]     = ("TradeAgent is installing a new version of itself and is about to close, so it is not sending orders.", "Wait for TradeAgent to reopen. Nothing was sent to your broker.", false),
        // The words come from Labels because the health row and the Checks page say the same thing,
        // and the specific reason travels in the technical text rather than being spelled twice.
        [ErrorCode.RUNTIME_COMMANDS_UNREADABLE]    = (Labels.RuntimesCouldNotBeRead(), Labels.OverrideFileRepair(Labels.RuntimesFile), false),
        [ErrorCode.ATAS_LAYOUT_UNREADABLE]         = (Labels.AtasLayoutCouldNotBeRead(), Labels.OverrideFileRepair(Labels.AtasFile), false),
    };

    public static ErrorInfo Get(ErrorCode code, string? technical = null)
    {
        var e = Catalogue.TryGetValue(code, out var v) ? v : Catalogue[ErrorCode.UNKNOWN_ERROR];
        return new ErrorInfo(code, technical ?? code.ToString(), e.User, e.Repair, e.Auto);
    }

    public static IReadOnlyCollection<ErrorCode> All => Catalogue.Keys;
}

public sealed class TradeAgentException(ErrorCode code, string? technical = null, Exception? inner = null)
    : Exception(technical ?? code.ToString(), inner)
{
    public ErrorCode Code { get; } = code;
    public ErrorInfo Info => Errors.Get(Code, Message);
}
