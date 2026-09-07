using System.Text.Json.Serialization;

namespace TradeAgent.Core;

public enum TradingMode { OBSERVE, PAPER, LIVE_CONFIRM, LIVE_AUTONOMOUS }

/// <summary>
/// Order lifecycle. UNKNOWN is first-class: it means "we do not know", never "it failed".
/// </summary>
public enum ExecutionState
{
    CREATED, AWAITING_APPROVAL, DISPATCHING, ACKNOWLEDGED, WORKING,
    PARTIALLY_FILLED, FILLED, CANCEL_PENDING, CANCELLED, REJECTED, UNKNOWN, RECONCILING
}

public enum RequestIntent { PLACE, MODIFY, CANCEL, CANCEL_ALL, CLOSE, CLOSE_ALL }

/// <summary>
/// Limits the gateway enforces before anything reaches a broker. Borrowed in spirit from
/// venture-agent's policy file: the agent's autonomy inside its own environment is broad,
/// but authority that leaves the machine is bounded by numbers a human set.
/// </summary>
public sealed class RiskPolicy
{
    /// <summary>Contracts or shares per order. For leveraged products this is the limit that matters.</summary>
    public decimal MaxOrderQuantity { get; set; } = 1m;

    /// <summary>
    /// Order value cap. Zero means "not enforced", which is the default deliberately: one ES future
    /// is a six-figure notional at a four-figure margin, so any naively chosen cap here refuses every
    /// legitimate futures order while teaching the user nothing. Set it for instruments where face
    /// value is the real exposure; rely on <see cref="MaxOrderQuantity"/> otherwise.
    /// </summary>
    public decimal MaxNotionalPerOrder { get; set; }
    public int MaxOpenPositions { get; set; } = 2;
    public int MaxOrdersPerMinute { get; set; } = 6;

    /// <summary>
    /// THE MOST ONE POSITION MAY BE DOWN BEFORE THE AI MAY ADD TO IT, in the account's currency.
    ///
    /// Everything above this line bounds ONE order. Nothing above it bounds a POSITION that is
    /// already losing: a quantity cap of one and a value cap of nothing between them permit an
    /// agent to average down into the same losing trade all day, one allowed order at a time.
    ///
    /// Zero means NOT ENFORCED, the same reading <see cref="MaxNotionalPerOrder"/> has and the
    /// opposite of the reading <see cref="TradeAgentSettings.AiDailyCostCap"/> has. The reason is
    /// the same as the notional cap's: there is no number here that is right for both a micro
    /// future and a share, so a default that is not zero would refuse ordinary orders on some
    /// account nobody has attached yet — and it is the ORDER-side caps above that are the binding
    /// ones out of the box. It is not the value <see cref="TradeAgentSettings.Unreadable"/> falls
    /// to for anything, so nothing can arrive at zero by way of a row that could not be read.
    /// </summary>
    public decimal MaxLossPerTrade { get; set; }

    /// <summary>
    /// THE MOST THE WHOLE ACCOUNT MAY BE DOWN ON THE DAY BEFORE NEW RISK IS REFUSED, in the
    /// account's currency, realised and unrealised together.
    ///
    /// This is the only limit in this class that is about the DAY rather than about an order, and it
    /// is the one a prop firm would enforce from the outside. Nobody is in the loop for real money
    /// (decided 2026-09-06), so the app is what bounds a losing day, ahead of any broker: reaching
    /// it refuses every order that could increase exposure until UTC midnight. It never refuses a
    /// close or a reduce, and it does not flatten anything — closing on a breach is its own unit.
    ///
    /// Zero means not enforced, exactly as on <see cref="MaxLossPerTrade"/>. What that costs is
    /// stated where it can be acted on: a real-money mode cannot be SELECTED while this is zero
    /// (<c>TradingGateway.SetMode</c>), because an unattended agent trading real money with no
    /// bound on the day is the one configuration this product must not be able to reach quietly.
    /// </summary>
    public decimal MaxDailyLoss { get; set; }
    /// <summary>
    /// THE INSTRUMENTS THE AI MAY TOUCH. AN EMPTY LIST IS NOT A WILDCARD.
    ///
    /// It used to be: <c>InstrumentAllowed</c> began <c>Count == 0 ||</c>, so "the owner has named
    /// nothing" and "the owner has permitted everything" were the same value. Three different things
    /// arrive at that value, and only one of them is a decision:
    ///
    ///   * a fresh install, where nobody has said anything yet;
    ///   * a settings row this build could not read, where the owner's list was replaced by a
    ///     default (REVIEW 2026-09-05 finding 5);
    ///   * an owner who cleared the box meaning "stop trading these".
    ///
    /// Reading any of those as "every instrument the platform offers" is the software inventing a
    /// permission. So the empty list allows NOTHING, and every screen that shows the list says so.
    /// </summary>
    public List<string> InstrumentAllowlist { get; set; } = new();

    public bool InstrumentAllowed(string instrument) =>
        InstrumentAllowlist.Any(i => string.Equals(i, instrument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// WHICH OF THESE CAPS <paramref name="to"/> WIDENS, in the owner's words, in the order the
    /// Safety page shows them. Empty means the proposal takes authority away or leaves it alone.
    ///
    /// A raised cap is a grant: it is the same act as choosing a real-money mode, done with a
    /// number instead of a button (REVIEW 2026-09-05b, Codex F12). So the screen asks twice for a
    /// save that widens and once for a save that narrows, and this is the comparison that decides
    /// which — here rather than at the widget, because "wider" is not "larger" on every field:
    ///
    ///   * <see cref="MaxNotionalPerOrder"/>, <see cref="MaxLossPerTrade"/> and
    ///     <see cref="MaxDailyLoss"/> read ZERO as "not enforced", so zero is the WIDEST value each
    ///     of them has, and nothing can widen one that is already zero.
    ///   * zero on the other three refuses everything, so there larger is wider, always.
    ///   * an allowlist is wider when it names an instrument the current one does not; dropping
    ///     names, including clearing the box, only ever narrows.
    /// </summary>
    public static IReadOnlyList<string> Widenings(RiskPolicy from, RiskPolicy to)
    {
        var wider = new List<string>();

        if (to.MaxOrderQuantity > from.MaxOrderQuantity) wider.Add(Labels.MaxOrderQuantity);

        // Unenforced is the widest there is, so it widens anything bounded and nothing widens it.
        if (Widens(from.MaxNotionalPerOrder, to.MaxNotionalPerOrder)) wider.Add(Labels.MaxNotionalPerOrder);

        if (to.MaxOpenPositions > from.MaxOpenPositions) wider.Add(Labels.MaxOpenPositions);
        if (to.MaxOrdersPerMinute > from.MaxOrdersPerMinute) wider.Add(Labels.MaxOrdersPerMinute);
        if (Widens(from.MaxLossPerTrade, to.MaxLossPerTrade)) wider.Add(Labels.MaxLossPerTrade);
        if (Widens(from.MaxDailyLoss, to.MaxDailyLoss)) wider.Add(Labels.MaxDailyLoss);
        if (to.InstrumentAllowlist.Any(i => !from.InstrumentAllowed(i))) wider.Add(Labels.InstrumentAllowlist);

        return wider;
    }

    /// <summary>
    /// The comparison for a cap whose ZERO means "not enforced" — the notional cap and both loss
    /// budgets. Spelled once rather than three times because it is the rule that is easy to get
    /// backwards: raising the number widens, and so does turning the cap OFF, which is the case a
    /// plain <c>&gt;</c> reads as a narrowing and lets through on one press.
    /// </summary>
    static bool Widens(decimal from, decimal to) =>
        from > 0m && (to <= 0m || to > from);
}

public sealed class TradeAgentSettings
{
    public TradingMode Mode { get; set; } = TradingMode.PAPER;
    public bool LiveActivated { get; set; }
    public bool AiTradingStopped { get; set; }
    public string? SelectedRuntimeId { get; set; }

    /// <summary>
    /// WHICH MODEL THE AI RUNS ON, or null for the one TradeAgent ships as that runtime's default.
    ///
    /// Before this existed the model came from the CLI's own configuration file, so the model
    /// running the owner's money was whichever one that file happened to name — measured on
    /// 2026-09-07, <c>gpt-6-astra</c> at about 1.5 USD a turn, chosen by nobody and visible nowhere.
    /// A product that spends someone's money has to be able to say what it is spending it on.
    ///
    /// It is not a permission and asks once. A dearer model does not let the AI do more; the daily
    /// ceiling is unchanged and bites sooner, which is the safe direction — and a cheaper one buys
    /// more turns under the same ceiling without lowering what a turn is charged at, which is the
    /// difference between this and the two price boxes beside it.
    /// </summary>
    public string? SelectedModelId { get; set; }

    /// <summary>
    /// THE MODEL EACH COUNCIL ROLE RUNS ON, by role id, or absent for "whatever
    /// <see cref="SelectedModelId"/> says" and then the runtime's own default.
    ///
    /// A dictionary rather than a field per role because the roles are data
    /// (<see cref="CouncilRoles.All"/>): a field per role is a field somebody has to remember to add
    /// with the third role, and a settings row from a build that had one is read by a build that has
    /// three. It is not a permission — a dearer model does not let a role do more, it reaches the
    /// day's ceiling sooner — so it saves in one press, exactly as <see cref="SelectedModelId"/> does.
    /// </summary>
    public Dictionary<string, string> RoleModel { get; set; } = [];

    /// <summary>
    /// EACH ROLE'S SLICE OF <see cref="AiDailyCostCap"/>, as a fraction, or absent for an equal
    /// share. Fifty-fifty across the two roles this build runs.
    ///
    /// It is a slice of the owner's ceiling and never an addition to it: the chair allocates INSIDE
    /// what the owner allowed and cannot raise it — <c>docs/COUNCIL.md</c>, "Budgets are reserved,
    /// not checked". A share is applied on top of the global gate rather than instead of it, so
    /// shares that sum to more than one still cannot spend more than the cap; what they would do is
    /// let one role take the whole day, which is the failure the share exists to prevent.
    /// </summary>
    public Dictionary<string, decimal> RoleShare { get; set; } = [];

    /// <summary>
    /// The model this role will be asked to run on: its own choice, then the single choice that
    /// predates the council, then null for the runtime's default.
    /// </summary>
    public string? ModelForRole(string role) =>
        RoleModel.TryGetValue(role, out var m) && m.Length > 0 ? m : SelectedModelId;

    /// <summary>
    /// This role's fraction of the day's ceiling. An equal share when nothing was chosen, and an
    /// equal share when what was chosen is not a fraction — a zero or a negative here would be a
    /// role that can never work, which is a stop nobody pressed.
    /// </summary>
    public decimal ShareForRole(string role) =>
        RoleShare.TryGetValue(role, out var s) && s > 0m && s <= 1m
            ? s
            : 1m / CouncilRoles.All.Length;

    public string? SelectedConnectorId { get; set; }
    public string? SelectedAccountId { get; set; }
    public RiskPolicy Risk { get; set; } = new();

    /// <summary>
    /// THE OWNER HAS SAID THE AI MAY WORK ON ITS OWN — turns back to back, with nobody typing.
    ///
    /// It is persisted because a program that forgot this on every restart would be a program the
    /// owner has to re-authorise every morning, and the whole product is an AI that does not stop.
    /// Setting it is two presses on the Dashboard; clearing it is one, and false surviving a restart
    /// is the half that matters: a paused AI that came back working would be the software granting
    /// itself something.
    ///
    /// It is NOT a trading permission and grants nothing. Mode, the real-money switch, the limits and
    /// the kill switch are unchanged by it, and STOP AI TRADING leaves the loop running on purpose.
    /// </summary>
    public bool AiWorksOnItsOwn { get; set; }

    /// <summary>
    /// Whether a restart puts a working AI back to work. On by default, because the alternative is
    /// an autonomous product that stops for good the first time Windows updates overnight. Turned
    /// off, the app comes up paused and <see cref="AiWorksOnItsOwn"/> is written back to match, so
    /// the card and the record never disagree.
    /// </summary>
    public bool ResumeAiOnStart { get; set; } = true;

    /// <summary>
    /// THE OWNER'S STANDING INSTRUCTIONS TO THE AI, included in every turn's Situation.
    ///
    /// Words, not authority. Nothing typed here can widen a limit, change a mode or lift the kill
    /// switch — those are the controls beside it — and the AI is told as much: guidance tells it what
    /// to spend its time on, and the gateway decides what it may actually do.
    /// </summary>
    public string Guidance { get; set; } = "";

    /// <summary>
    /// WHICH PAIR THE OWNER COLLECTS HISTORY FOR, on the Settings page under Market data.
    ///
    /// It is NOT a permission and not an instrument allowlist: collecting a public archive grants
    /// nothing, changes no limit and touches no order, which is why the press beside it is ONE press
    /// where widening risk is two. What the AI may TRADE is <see cref="RiskPolicy.InstrumentAllowlist"/>
    /// and has nothing to do with this.
    /// </summary>
    public string MarketDataPair { get; set; } = "BTCUSDT";

    /// <summary>
    /// Turns resumed into one agent CLI session before a fresh one starts. Resuming for ever grows
    /// one context until the runtime refuses it or prices it absurdly; nothing is lost by starting
    /// again, because the AI's memory is its files and every turn is told so.
    /// </summary>
    public int MissionTurnsPerSession { get; set; } = 20;

    /// <summary>
    /// HOW OFTEN THE AI IS WOKEN WHEN NOTHING HAS HAPPENED, in minutes. 0 is off, and then only a
    /// real event — the owner typing, material arriving, a fill, an order settling, the day turning
    /// over — starts a turn.
    ///
    /// The AI takes a turn because something happened. That leaves the case where nothing has, which
    /// is not the same as there being nothing worth doing: the research, the backtests and the
    /// journal are the job, and an hour of quiet market is not a reason to stop working on them. So
    /// this is the heartbeat, and it is a setting because every tick is a paid turn — LOWERING it
    /// spends more of the owner's money and therefore asks twice on the Safety page, which is the
    /// opposite direction from every risk limit and is why it is not one of them.
    /// </summary>
    public int MissionReviewMinutes { get; set; } = 30;

    /// <summary>
    /// WHAT ONE TURN IS COMMITTED TO COST BEFORE IT RUNS, in input tokens. See
    /// <see cref="TurnAllowance"/>: it is an upper bound, not a prediction, and the direction is
    /// deliberate — reserving too much costs a turn the owner gets back at midnight, reserving too
    /// little is a ceiling that can be walked past.
    ///
    /// A setting rather than a constant because the right bound depends on the model: a runtime with
    /// a small context window can never use this much, and an owner who has measured their own turns
    /// can say so. Zero or less reads as the shipped default; it is not an allowance of nothing.
    /// </summary>
    public long AiTurnAllowanceInputTokens { get; set; } = TurnAllowance.Default.InputTokens;

    /// <summary>The other half of the allowance, in output tokens. Reasoning is inside it.</summary>
    public long AiTurnAllowanceOutputTokens { get; set; } = TurnAllowance.Default.OutputTokens;

    /// <summary>
    /// THE MOST THE AI MAY SPEND ON ITSELF IN ONE LOCAL DAY, in <c>costs.json</c>'s currency.
    ///
    /// The AI's mission is to make at least enough to pay for itself, and half of that sentence is
    /// its own bill: turns run back to back for as long as the machine is on, so without a ceiling
    /// this is an account being charged with nobody watching. Reaching it pauses the mission loop
    /// until local midnight; it removes no permission and touches no order.
    ///
    /// Five is the default because it is small enough that a mistake is a rounding error on the
    /// owner's month and large enough for a day of real work. RAISING it asks twice — it is a grant,
    /// exactly as a raised risk limit is — and lowering it saves in one press.
    ///
    /// <b>Zero means no spending at all, not "unlimited".</b> That is the opposite of
    /// <see cref="RiskPolicy.MaxNotionalPerOrder"/>, whose zero means unenforced, and it is
    /// deliberate: this is the value <see cref="Unreadable"/> falls to, and a settings row nobody
    /// could read must not be the event that takes the ceiling off.
    /// </summary>
    public decimal AiDailyCostCap { get; set; } = 5m;

    /// <summary>
    /// WHAT THE OWNER SAYS THEIR AI TOOL CHARGES THEM, per million tokens in and out, or null for
    /// "use the list price this build shipped".
    ///
    /// Two numbers rather than a model name because the owner is the one party who can see the bill
    /// and is not the party who knows which model a CLI selected. It is the last word: it beats
    /// <c>costs.json</c> and it beats the dated list prices, for every turn, whether or not anything
    /// named a model — see <c>CostCatalog.Price</c>.
    ///
    /// LOWERING either number is the grant, which is back to front from every other field on that
    /// page and is why the Safety page asks twice for it. A lower price does not let the AI trade
    /// more; it makes each turn count for less against <see cref="AiDailyCostCap"/>, so the same
    /// ceiling buys more turns and more real money is spent on the AI before it stops.
    ///
    /// Both null or both set. One of the two on its own is not a price, and half a price applied to
    /// a whole turn would under-charge it — the one direction that lets the cap be walked past.
    /// </summary>
    public decimal? AiPriceInputPerMillion { get; set; }

    /// <summary>The other half. See <see cref="AiPriceInputPerMillion"/>; neither works alone.</summary>
    public decimal? AiPriceOutputPerMillion { get; set; }

    /// <summary>
    /// IS THE SAVED MODE ONE THIS BUILD ACTUALLY HAS?
    ///
    /// <see cref="TradingMode"/> is persisted as a name, and <c>System.Text.Json</c>'s enum converter
    /// reads NUMBERS as well — and casts a number it does not recognise straight onto the enum. A
    /// settings row saying <c>"mode": 999</c> therefore produced a mode of 999, and every gate below
    /// is a comparison against the named values: 999 is not OBSERVE so it executed, it is not
    /// LIVE_CONFIRM or LIVE_AUTONOMOUS so the real-money activation switch was never consulted, and
    /// it is not PAPER so a real-money account was not refused either. A mode nobody chose, trading
    /// real money with the safety off (REVIEW 2026-09-05, Codex F3).
    ///
    /// It is not a hypothetical row: a newer build writes a mode this one has never heard of, and a
    /// rollback reads it. So the classification fails closed — an unrecognised mode allows nothing —
    /// and <c>TradingGateway.LoadSettings</c> says so in the owner's words.
    /// </summary>
    [JsonIgnore] public bool ModeIsRecognised => Enum.IsDefined(Mode);

    [JsonIgnore] public bool ModeAllowsExecution => ModeIsRecognised && Mode != TradingMode.OBSERVE;
    [JsonIgnore] public bool ModeIsLive => Mode is TradingMode.LIVE_CONFIRM or TradingMode.LIVE_AUTONOMOUS;

    /// <summary>
    /// TRUE WHEN NONE OF THE VALUES ABOVE CAME FROM THE OWNER. Not persisted — it is a fact about
    /// this run, and it stops being true the moment the row is written again (see
    /// <see cref="MarkSaved"/>). The health row and the Safety page read it; nothing else sets it.
    /// </summary>
    [JsonIgnore] public bool CouldNotBeRead { get; private set; }

    /// <summary>The row on disk is now one this build wrote, so it is readable by definition.</summary>
    public void MarkSaved() => CouldNotBeRead = false;

    /// <summary>
    /// THE SETTINGS A BUILD USES WHEN IT CANNOT READ THE ROW THE OWNER SAVED (REVIEW 2026-09-05,
    /// finding 5).
    ///
    /// <c>new TradeAgentSettings()</c> was what that failure produced, and its defaults are the
    /// permissions of a FRESH INSTALL: the kill switch up, an allowlist that used to mean
    /// "everything", a quantity cap of one and two open positions. So the one event proving the
    /// software cannot read what the owner asked for was also the event that granted the AI
    /// authority nobody gave it.
    ///
    /// Every field here is instead the most restrictive value that field has:
    ///
    ///   Mode = OBSERVE            the only mode that executes nothing at all
    ///   AiTradingStopped = true   the kill switch, down
    ///   LiveActivated = false     real money off
    ///   SelectedAccountId = null  no account was chosen, and a guess is not a choice
    ///   allowlist = []            which now allows NOTHING
    ///   quantity, positions, orders-per-minute = 0
    ///   AiWorksOnItsOwn = false   the loop does not start on a row nobody could read
    ///   Guidance = ""             standing instructions nobody can vouch for are no instructions
    ///   AiDailyCostCap = 0        the AI may spend nothing until the row is written again
    ///   AiPrice…PerMillion = null  the AI's turns cost the LIST price, which is the dearer reading
    ///
    /// <c>MaxNotionalPerOrder</c>, <c>MaxLossPerTrade</c> and <c>MaxDailyLoss</c> stay at 0, which on
    /// those three fields alone means "not enforced": none of them has a floor, and a quantity cap of
    /// zero has already refused every order before a notional or a loss is computed. Leaving them is
    /// also the only reading that does not make an unreadable row the event that starts refusing
    /// CLOSES — they bound new risk, and a row nobody could read must not trap a live position.
    /// The emergency controls are deliberately still reachable — they take no mode, no
    /// allowlist and no cap, and an owner holding a live position needs them most on the day the
    /// software cannot read its own settings.
    ///
    /// The raw row is not destroyed by this; the caller keeps it, because the owner's own values are
    /// the only evidence of what they had asked for.
    /// </summary>
    public static TradeAgentSettings Unreadable() => new()
    {
        CouldNotBeRead = true,
        Mode = TradingMode.OBSERVE,
        AiTradingStopped = true,
        LiveActivated = false,
        SelectedAccountId = null,
        AiWorksOnItsOwn = false,
        Guidance = "",
        // Zero is the smallest this field has and it means no spending, so a row nobody could read
        // is not the event that lifts the ceiling. The loop is not running on this row anyway.
        AiDailyCostCap = 0m,
        Risk = new RiskPolicy
        {
            MaxOrderQuantity = 0m,
            MaxOpenPositions = 0,
            MaxOrdersPerMinute = 0,
            InstrumentAllowlist = []
        }
    };
}

/// <summary>
/// THE OWNER'S OWN RATE, per million tokens in and out — the last word on what a turn cost.
///
/// A record rather than two nullable fields passed around, so that "the owner has priced this" is
/// one value that is either there or not. <see cref="From"/> is the only way to make one from the
/// settings row and it refuses a half-filled pair: one number without the other is not a price, and
/// applying it to a whole turn would under-charge it, which is the direction that lets the daily cap
/// be walked past.
///
/// IT REFUSES A ZERO FOR THE SAME REASON, AND THAT ONE IS NOT THEORETICAL. Zero is not a cheap rate,
/// it is the absence of one: every turn costs nothing, the day's total never moves, and the daily
/// cap — the whole of what bounds an AI working non-stop — stops existing while reading as though it
/// were in force. The boxes on the Safety page open on the rate in force, which is <c>0</c> on a
/// runtime this build ships no list price for, so without this guard a single press on an untouched
/// pair of boxes would have written it: no typing, no second press, the cap dead.
///
/// An owner who genuinely pays nothing per token — a subscription rather than an API key — says that
/// by leaving the price alone and raising the ceiling, not by pricing the work at nothing.
/// </summary>
public sealed record OwnerPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    /// <summary>The owner's rate, or null where they have not set a usable one.</summary>
    public static OwnerPrice? From(TradeAgentSettings settings) =>
        settings.AiPriceInputPerMillion is { } input && settings.AiPriceOutputPerMillion is { } output
        && input > 0m && output > 0m
            ? new OwnerPrice(input, output)
            : null;
}

/// <summary>
/// WHAT ONE TURN IS COMMITTED TO COST BEFORE IT RUNS, in tokens.
///
/// A reservation, not a prediction. Nothing can know what a turn will use until it has used it, and
/// the cheapest wrong answer is the one that stops the AI a little early: an over-reservation costs
/// a turn the owner gets back at midnight, and an under-reservation is a ceiling that can be walked
/// past — which is the failure this whole unit exists to close.
///
/// The defaults are an upper bound on the runtime this build recommends rather than an average of
/// anything. Codex 0.153.4 refuses a request over its model's context window, so a turn cannot
/// consume unbounded input; 1,200,000 is above the largest current context and 20,000 output covers
/// a turn that writes at length with reasoning inside it.
/// </summary>
public sealed record TurnAllowance(long InputTokens, long OutputTokens)
{
    public static readonly TurnAllowance Default = new(1_200_000, 20_000);

    /// <summary>Zero or negative reads as the default: a settings row of zeroes is not an allowance of nothing.</summary>
    public static TurnAllowance From(long input, long output) =>
        new(input > 0 ? input : Default.InputTokens, output > 0 ? output : Default.OutputTokens);
}

/// <summary>
/// WHAT THE AI HAS COST TODAY, MEASURED AGAINST WHAT IT IS ALLOWED TO COST.
///
/// In <c>Core</c> rather than beside the meter that fills it in, because three layers that cannot
/// see each other all need this one reading: the loop decides whether to take another turn from it,
/// the card draws it, and the status the agent reads is composed from it.
///
/// <see cref="Metered"/> false is "there is no meter here at all" — a build with no AI prepared, or
/// a test — and is not the same fact as a metered day whose turns could not be priced. That second
/// case is <see cref="Metered"/> true with <see cref="UnpricedTurns"/> above zero, and it is the
/// ordinary case for a runtime whose CLI reports tokens but no model.
/// </summary>
public sealed record AiSpendToday
{
    public static readonly AiSpendToday NotMetered = new();

    /// <summary>False when nothing is metering turns at all. Then nothing below means anything.</summary>
    public bool Metered { get; init; }

    /// <summary>What today's PRICED turns came to. Turns nobody could price are not in it.</summary>
    public decimal Spent { get; init; }

    /// <summary>
    /// WHAT IS COMMITTED TODAY BUT NOT YET RESOLVED — turns that were launched and whose usage has
    /// not come back. It is beside <see cref="Spent"/> rather than inside it because the two are
    /// different facts: one is what the vendor has reported, the other is what has been asked of
    /// them. Both are the owner's money, and only the first can ever be a receipt.
    /// </summary>
    public decimal Reserved { get; init; }

    /// <summary>
    /// What one more turn would commit, at the model TradeAgent is asking for. Zero where nothing
    /// can price a turn at all — and then the ceiling holds nothing back, which the card says.
    /// </summary>
    public decimal NextTurnReservation { get; init; }

    public decimal Cap { get; init; }

    /// <summary>Empty when <c>costs.json</c> could not be read, so a number is never shown bare.</summary>
    public string Currency { get; init; } = "";

    /// <summary>
    /// THE MODEL TRADEAGENT IS ASKING FOR, or null where it asks for none. It is on this reading
    /// because it belongs beside the figure: what a turn costs is mostly which model ran it, and an
    /// owner reading a total that surprises them needs the two facts together.
    /// </summary>
    public string? Model { get; init; }

    public int Turns { get; init; }

    /// <summary>Turns today whose cost is unknown. Above zero, <see cref="Spent"/> is a floor.</summary>
    public int UnpricedTurns { get; init; }

    /// <summary>
    /// Turns today charged at the highest list price because nothing named a model. Counted rather
    /// than inferred, because <see cref="Estimated"/> can be true of an installation on a day that
    /// has had no turns yet, and the two facts are read by different screens.
    /// </summary>
    public int EstimatedTurns { get; init; }

    /// <summary>
    /// THE LABEL A FIGURE THAT IS AN UPPER BOUND HAS TO CARRY, or null when it is a bill.
    ///
    /// <see cref="Spent"/> beside a cap, with no word about where the number came from, is the
    /// reading this record exists to prevent: an owner cannot tell an estimate from a measurement,
    /// and neither can the AI reading it in its own Situation. So the sentence travels WITH the
    /// number, on all three surfaces, rather than being left for a documentation page.
    /// </summary>
    public string? Estimated { get; init; }

    /// <summary>
    /// WHETHER THIS INSTALLATION CAN TURN A TURN INTO A NUMBER AT ALL — asked of the price list now,
    /// not inferred from a day that may have had no turns yet.
    ///
    /// It is what the screens key their wording on, because the misleading moment is the FIRST turn
    /// of the day: with no prices on the machine, <see cref="UnpricedTurns"/> is still zero and a
    /// card reading "0.00 of 5.00 today" would be describing a ceiling nothing can reach.
    /// </summary>
    public bool CanPrice { get; init; }

    /// <summary>Why there is no price, in the owner's words. Null when <see cref="CanPrice"/>.</summary>
    public string? WhyNoPrice { get; init; }

    /// <summary>
    /// The figures come from the two numbers the owner typed, not from a list price. The card says
    /// so, because a total the owner is responsible for and a total this build quoted from a vendor
    /// page are two different things to be looking at when the number surprises them.
    /// </summary>
    public bool PricedByOwner { get; init; }

    /// <summary>The local midnight today's totals expire at — when a capped loop starts again.</summary>
    public DateTimeOffset ResumesAt { get; init; }

    /// <summary>
    /// THE COMPARISON THE MISSION LOOP STOPS ON. Reaching the cap is enough — the owner's number is
    /// a ceiling, not a threshold to cross — so it is <c>&gt;=</c>, and a cap of zero is reached by
    /// a day that has spent nothing, which is what makes zero mean "no spending at all".
    ///
    /// Unpriced turns cannot reach it and deliberately do not: the alternative is stopping the AI on
    /// a number nobody measured. That is why <see cref="UnpricedTurns"/> is on this record and on
    /// the card — an owner whose turns are unpriced has a limit that is holding nothing back, and
    /// they have to be able to see that rather than infer it from an AI that never stops.
    /// </summary>
    public bool CapReached => Metered && Spent >= Cap;

    /// <summary>
    /// THE ADMISSION GATE: may one more turn be launched at all?
    ///
    /// <c>spent + reserved + this turn ≤ cap</c>. It is the difference between a cap and a report.
    /// <see cref="CapReached"/> asks whether money already gone has passed the ceiling, which can
    /// only ever be answered after the turn that passed it has run — measured on 2026-09-07, 5.07
    /// USD against a 5 USD ceiling, because the fourth turn was admitted on a total that did not yet
    /// include the fourth turn. This asks the question the moment it can still change the outcome.
    ///
    /// <see cref="CapReached"/> still stands beside it rather than being folded in, because the two
    /// are different facts the owner reads differently: one says the day is over, the other says the
    /// next turn will not be started. A day at exactly the ceiling is both.
    ///
    /// <see cref="NextTurnReservation"/> is zero where nothing can price a turn, and then this is
    /// the old comparison again — an unpriced day is not stopped, because stopping on a number
    /// nobody measured is worse than not stopping.
    /// </summary>
    public bool AdmitsAnotherTurn =>
        !Metered
        || (!CapReached && Spent + Reserved + NextTurnReservation <= Cap && RoleAdmitsAnotherTurn);

    /// <summary>
    /// WHICH COUNCIL ROLE THIS READING IS ABOUT, or null for the whole day across every role. Null
    /// is what the card, the Safety page and <c>trade status</c> read: an owner is being told what
    /// their AI cost, not what one of its roles cost.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>This role's slice of <see cref="Cap"/>. Meaningless, and equal to it, when <see cref="Role"/> is null.</summary>
    public decimal RoleCap { get; init; }

    /// <summary>What this role's priced turns came to today.</summary>
    public decimal RoleSpent { get; init; }

    /// <summary>What this role has committed today and not yet resolved.</summary>
    public decimal RoleReserved { get; init; }

    /// <summary>
    /// THE SHARE IS A SECOND GATE, NOT A REPLACEMENT FOR THE FIRST. The global comparison above is
    /// what bounds the owner's day; this is what stops one role spending all of it.
    ///
    /// Drop it and the arithmetic still adds up — nothing is over-spent — but the council stops
    /// being one: whichever role woke first takes every turn until midnight, and the other is never
    /// scheduled at all. That is the failure this exists for, and it is why a role's share is
    /// checked before the turn rather than reported after it, exactly as the global cap is.
    ///
    /// Always true for a reading with no role, so nothing that predates the council changes meaning.
    /// </summary>
    public bool RoleAdmitsAnotherTurn =>
        Role is null || RoleSpent + RoleReserved + NextTurnReservation <= RoleCap;

    /// <summary>What is left of this role's slice after what it has spent and committed.</summary>
    public decimal RoleRemaining => Role is null ? Cap - Spent - Reserved
        : RoleCap - RoleSpent - RoleReserved;

    /// <summary>
    /// THE OWNER'S CEILING IS SMALLER THAN ONE TURN'S WORST CASE, so no turn will ever be admitted —
    /// not today, and not tomorrow either, because midnight resets the total and not the arithmetic.
    ///
    /// It is a separate reading from a day that has run out, and the screens say a different
    /// sentence for it, because the repair is different: a day that ran out starts again at
    /// midnight, and this one never does until the owner raises the limit or chooses a cheaper
    /// model. An AI that silently took no turns for ever, under a card reading "waiting until
    /// 00:00", would be the worst version of this.
    ///
    /// It is a consequence of reserving an ENFORCEABLE bound rather than a guess: the bound is a
    /// model's whole context window, and a small daily ceiling cannot cover one of those.
    /// </summary>
    public bool CapCannotFundATurn => Metered && NextTurnReservation > Cap && Cap > 0m;
}

/// <summary>
/// WHAT TODAY HAS LOST, MEASURED AGAINST WHAT THE OWNER ALLOWS IT TO — or the reason nobody can say.
///
/// In <c>Core</c> for the reason <see cref="AiSpendToday"/> is: three layers that cannot see each
/// other read this one figure. The gateway REFUSES on it, the mission's Situation block tells the AI
/// what it has left, and the agent-facing status carries it over the pipe.
///
/// <para><see cref="Unknown"/> is the field the rest of the type is subordinate to. While it is set,
/// every number here is meaningless and a caller reading <see cref="Loss"/> anyway would read a zero
/// that means "the arithmetic did not finish" as though it meant "the day is flat". That is the one
/// direction a loss figure must never be wrong in, because it is the figure a gate refuses on — so
/// an unknown REFUSES rather than being drawn as nothing.</para>
///
/// <para><see cref="Loss"/> is POSITIVE when money is down and zero when the day is flat or ahead,
/// so the comparison reads the way the owner's own sentence does: "the most it may lose".</para>
/// </summary>
public sealed record LossToday
{
    /// <summary>Neither budget is set, so nothing is measured and nothing can be refused.</summary>
    public static readonly LossToday NotEnforced = new();

    /// <summary>False when both budgets are zero. Then nothing below means anything.</summary>
    public bool Enforced { get; init; }

    /// <summary>Why the day's loss could not be worked out, in the owner's words. Null when it was.</summary>
    public string? Unknown { get; init; }

    /// <summary>What the day is down, as a positive number. Zero when it is flat or ahead.</summary>
    public decimal Loss { get; init; }

    /// <summary>Realised today, less the fees the platform DID report. Positive is a profit.</summary>
    public decimal Realized { get; init; }

    /// <summary>Unrealised on everything open. Positive is a profit.</summary>
    public decimal Unrealized { get; init; }

    /// <summary>
    /// What each open position is DOWN, positive, symbol by symbol. A position that is ahead is
    /// absent rather than present as a zero, because the per-position budget asks one question of
    /// this map — "how much is this one losing" — and an absent key is the honest none.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> LossBySymbol { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>
    /// Fills today the platform reported no fee for. Their GROSS is counted and their cost is not,
    /// so the day's loss is understated by exactly those fees. This is the one place these budgets
    /// are knowingly permissive, and every surface that shows the figure says so rather than
    /// inventing a fee — an unknown is never a zero, and it is not a guess either.
    /// </summary>
    public int FeesUnknownFills { get; init; }

    /// <summary><see cref="RiskPolicy.MaxDailyLoss"/> as it stood when this was read. 0 is off.</summary>
    public decimal DayBudget { get; init; }

    /// <summary><see cref="RiskPolicy.MaxLossPerTrade"/> as it stood when this was read. 0 is off.</summary>
    public decimal TradeBudget { get; init; }

    /// <summary>The account's currency, or "" while the platform has not said what it is.</summary>
    public string Currency { get; init; } = "";

    /// <summary>
    /// The comparison the gateway refuses on. Reaching the budget is enough — the owner's number is
    /// a ceiling and not a threshold to cross — so it is <c>&gt;=</c>, and an UNKNOWN is never
    /// "reached": it is refused by its own branch, with its own sentence, because "we could not work
    /// it out" and "you have lost too much" are different things to be told.
    /// </summary>
    public bool DayReached => Unknown is null && DayBudget > 0m && Loss >= DayBudget;

    /// <summary>What one position is down, and whether that is as much as it may be.</summary>
    public decimal LossOn(string symbol) => LossBySymbol.GetValueOrDefault(symbol);

    public bool TradeReached(string symbol) =>
        Unknown is null && TradeBudget > 0m && LossOn(symbol) >= TradeBudget;

    /// <summary>
    /// THE SENTENCE, or null when there is nothing enforced to say. A method so it can be read back
    /// without a running loop, and so the three cases are visibly three — the same shape, and for
    /// the same reason, as <c>MissionSituation.SpendLine</c>.
    ///
    /// The unknown case is spelled out rather than shortened to a number, because "0 lost today"
    /// beside an AI whose next order is about to be refused is the one reading that is actively
    /// misleading: it says the day is fine when what happened is that nobody could price it.
    /// </summary>
    public string? Line()
    {
        if (!Enforced) return null;

        if (Unknown is { } why)
            return "What you have lost today could not be worked out — " + why
                   + "; new positions are refused until it can be. Closing or reducing a position still works.";

        var line = DayBudget > 0m
            ? $"What you have lost today: {Labels.Money(Loss, Currency)} of a {Labels.Money(DayBudget, Currency)} daily budget"
            : $"What you have lost today: {Labels.Money(Loss, Currency)}, against no daily budget";

        if (TradeBudget > 0m)
            line += $", and no one position may lose more than {Labels.Money(TradeBudget, Currency)}";
        line += ".";

        if (FeesUnknownFills > 0)
            line += $" Your platform reported no fee for {FeesUnknownFills} of today's fills, so the real "
                    + "figure is a little worse than that.";

        if (DayReached)
            line += " You are at the daily budget: no new positions until tomorrow (UTC). "
                    + "Closing or reducing a position still works.";

        return line;
    }
}

/// <summary>
/// The three facts about the AI's own work that the agent-facing status carries. Composed by the
/// app, because the gateway cannot see the mission loop and must not learn to.
/// </summary>
/// <param name="State">The loop's own word: stopped, working, waiting or paused.</param>
/// <param name="TurnsToday">Turns metered since local midnight.</param>
/// <param name="CostToday">What they cost, or null when TradeAgent cannot price them.</param>
public sealed record AiActivity(string State, int TurnsToday, decimal? CostToday)
{
    /// <summary>
    /// The model TradeAgent asked its AI tool for, or absent where it asked for none. Init-only for
    /// the reason <see cref="CostEstimated"/> is: every other construction site of this record means
    /// to say nothing about it.
    ///
    /// The agent is told which model is running it because its mission is to cover what it costs and
    /// the model is most of that arithmetic. It is a READ: there is no verb that changes it.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Present when <see cref="CostToday"/> is an upper bound rather than a bill, and then it is the
    /// sentence saying so — the same sentence the owner reads on the card.
    ///
    /// Init-only rather than a fourth positional parameter, for the reason the three AI fields on
    /// <c>GatewayStatus</c> are: every other construction site of this record means to say nothing
    /// about it, and a fourth parameter would make them all say something.
    /// </summary>
    public string? CostEstimated { get; init; }

    /// <summary>No loop wired: the honest reading is that the AI is not working.</summary>
    public static readonly AiActivity None = new("stopped", 0, null);
}

/// <summary>
/// A durable record of one MULTI-TARGET intent — a `cancel-all`, a `close-all`, an operator press.
/// Written before any effect, completed with the answer afterwards, never deleted.
/// </summary>
public sealed class CompositeRequest
{
    /// <summary>The id the CALLER used. This is what a replay is recognised by.</summary>
    public required string RequestId { get; init; }
    public string? AgentSessionId { get; init; }

    /// <summary>The operation, as the protocol names it: `cancel-all`, `close-all`.</summary>
    public required string Op { get; init; }

    /// <summary>What this composite's per-target ids are derived from. Stable across replays.</summary>
    public required string Nonce { get; init; }

    /// <summary>The targets captured when the composite was created, as a JSON array.</summary>
    public required string PlanJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The answer the first run produced, or null while it has not produced one.</summary>
    public string? ResultJson { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A durable record of one mutating intent. Written before dispatch, never deleted.</summary>
public sealed class ExecutionRequest
{
    public required string RequestId { get; init; }
    public string? AgentSessionId { get; init; }
    public required string ConnectorId { get; init; }
    public required string AccountId { get; init; }
    public required string Instrument { get; init; }
    public required RequestIntent Intent { get; init; }
    public required string ParametersJson { get; init; }
    public required string ClientOrderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public ExecutionState State { get; set; }
    public string? ConnectorOrderId { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public bool NeedsReconciliation { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
    public string? LastError { get; set; }
    public TradingMode Mode { get; init; }
}
