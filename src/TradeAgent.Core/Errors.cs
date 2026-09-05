namespace TradeAgent.Core;

public enum ErrorCode
{
    UNKNOWN_ERROR,
    AI_RUNTIME_NOT_FOUND, AI_INSTALL_FAILED, AI_VERSION_UNSUPPORTED,
    AI_AUTH_REQUIRED, AI_AUTH_FAILED, AI_AUTH_TIMEOUT,
    ATAS_NOT_FOUND, ATAS_NOT_RUNNING, ATAS_VERSION_UNSUPPORTED,
    ATAS_BRIDGE_MISSING, ATAS_BRIDGE_LOAD_FAILED, ATAS_BRIDGE_DISCONNECTED,
    TRADING_CONNECTION_MISSING, ACCOUNT_NOT_FOUND, MARKET_DATA_UNAVAILABLE,
    TRADING_PERMISSION_UNAVAILABLE,
    ORDER_REJECTED, ORDER_STATE_UNKNOWN, RECONCILIATION_FAILED,
    IPC_UNAVAILABLE, IPC_UNAUTHENTICATED, INCOMPATIBLE_PROTOCOL, WORKSPACE_CORRUPT, STATE_DATABASE_CORRUPT,
    // Authority / policy codes (TradeAgent-owned, not in the original brief).
    AI_TRADING_STOPPED, LIVE_NOT_ACTIVATED, MODE_FORBIDS_EXECUTION, MODE_ACCOUNT_MISMATCH,
    APPROVAL_REQUIRED, APPROVAL_EXPIRED, RISK_LIMIT_EXCEEDED, RISK_CHECK_UNAVAILABLE, TRADING_PAUSED_UNRECONCILED,
    EMERGENCY_PRESS_UNRESOLVED,
    AUTONOMY_REQUIRES_PROVABLE_STATE,
    INVALID_REQUEST, GATEWAY_ALREADY_RUNNING, ILLEGAL_STATE_TRANSITION,
    UPDATE_FAILED, UPDATE_INTEGRITY_FAILED, UPDATE_INSTALL_IN_PROGRESS
}

/// <summary>Technical detail, plain-language explanation, suggested repair, and whether we can fix it ourselves.</summary>
public sealed record ErrorInfo(ErrorCode Code, string Technical, string UserMessage, string Repair, bool AutoRepairable);

public static class Errors
{
    static readonly Dictionary<ErrorCode, (string User, string Repair, bool Auto)> Catalogue = new()
    {
        [ErrorCode.UNKNOWN_ERROR]                  = ("Something unexpected went wrong.", "Run Check everything from the Diagnostics screen.", false),
        [ErrorCode.AI_RUNTIME_NOT_FOUND]           = ("The AI assistant program is not installed yet.", "TradeAgent can install it for you.", true),
        [ErrorCode.AI_INSTALL_FAILED]              = ("The AI assistant could not be installed.", "Check your internet connection, then press Retry.", true),
        [ErrorCode.AI_VERSION_UNSUPPORTED]         = ("The installed AI assistant is too old for this version of TradeAgent.", "TradeAgent can update it for you.", true),
        [ErrorCode.AI_AUTH_REQUIRED]               = ("You need to sign in to your AI account.", "Press Sign in. A browser window will open.", false),
        [ErrorCode.AI_AUTH_FAILED]                 = ("Signing in to the AI account did not work.", "Press Sign in again and complete the browser steps.", false),
        [ErrorCode.AI_AUTH_TIMEOUT]                = ("Signing in took too long and was cancelled.", "Press Sign in again.", false),
        [ErrorCode.ATAS_NOT_FOUND]                 = ("ATAS is not installed on this computer.", "Install ATAS, then press Retry.", false),
        [ErrorCode.ATAS_NOT_RUNNING]               = ("ATAS is not running.", "Press Open ATAS.", true),
        [ErrorCode.ATAS_VERSION_UNSUPPORTED]       = ("Your ATAS version changed and the TradeAgent bridge needs updating.", "Press Repair.", true),
        [ErrorCode.ATAS_BRIDGE_MISSING]            = ("The TradeAgent bridge is not installed into ATAS yet.", "Press Install bridge.", true),
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
        [ErrorCode.APPROVAL_EXPIRED]               = ("An order the AI proposed waited too long for your approval and was declined.", "Nothing was sent. If you still want it, ask the AI to propose it again.", false),
        [ErrorCode.RISK_LIMIT_EXCEEDED]            = ("The order was refused because it breaks a safety limit you set.", "Change the limit in Settings if it is too strict.", false),
        // Distinct from RISK_LIMIT_EXCEEDED, and the difference is the whole of it: no limit was
        // broken — TradeAgent could not work out whether one would be. A change to an order it
        // cannot read is a change whose effect on your exposure is unknown, and an unknown is
        // refused rather than waved through.
        [ErrorCode.RISK_CHECK_UNAVAILABLE]         = ("TradeAgent could not read the order it was asked to change, so it could not check the change against your safety limits.", "Nothing was sent. Check the order on the trading platform, then ask again.", false),
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
