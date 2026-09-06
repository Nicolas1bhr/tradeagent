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
    EMERGENCY_PRESS_UNRESOLVED, POSITION_MOVED,
    AUTONOMY_REQUIRES_PROVABLE_STATE,
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

    /// <summary>The page holding the mode, the real-money switch, the limits and the allowlist.</summary>
    public const string SafetyPage = "Safety";

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

    // The five safety limits, named once. The Safety page labels its fields with these and the
    // armed save sentence names the one that widened, so the two cannot drift apart.
    public const string MaxOrderQuantity = "Most it may buy or sell in one order";
    public const string MaxNotionalPerOrder = "Most money one order may be worth";
    public const string MaxOpenPositions = "Most positions it may hold at once";
    public const string MaxOrdersPerMinute = "Most orders per minute";
    public const string InstrumentAllowlist = "Instruments it may touch";

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
        [ErrorCode.APPROVAL_EXPIRED]               = ("An order the AI proposed waited too long for your approval and was declined.", "Nothing was sent. If you still want it, ask the AI to propose it again.", false),
        [ErrorCode.RISK_LIMIT_EXCEEDED]            = ("The order was refused because it breaks a safety limit you set.", "Change the limit in Settings if it is too strict.", false),
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
