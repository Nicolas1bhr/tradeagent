using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

public sealed record WorkspaceContext(string ConnectorName, bool ConnectorIsPaper, string? AccountId,
    TradingMode Mode, bool ExecutionAvailable, string? ExecutionBlockedReason, RiskPolicy Risk);

/// <summary>
/// Creates and maintains the agent's home. The agent is broadly free inside this directory — shell,
/// subprocesses, packages, internet, its own code — and has no authority outside it. The instruction
/// file below is regenerated on every start so it can never describe a stale world.
/// </summary>
public static class WorkspaceBuilder
{
    public static readonly string[] SubDirs = ["trading", "research", "strategies", "data", "scripts", "logs", "scratch"];

    public static string Build(WorkspaceContext ctx, string? root = null)
    {
        var ws = root ?? Paths.Workspace;
        Directory.CreateDirectory(ws);
        foreach (var d in SubDirs) Directory.CreateDirectory(Path.Combine(ws, d));
        Directory.CreateDirectory(Path.Combine(ws, ".tradeagent"));

        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), Instructions(ctx));
        File.WriteAllText(Path.Combine(ws, ".tradeagent", "context.json"), Json.Write(ctx, pretty: true));
        return ws;
    }

    /// <summary>Environment handed to the agent process. The trade CLI is on PATH; no secrets are present.</summary>
    public static Dictionary<string, string> EnvironmentFor(string sessionId, string workspace)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return new Dictionary<string, string>
        {
            ["PATH"] = $"{Paths.Bin}{Path.PathSeparator}{path}",
            ["TRADEAGENT_SESSION"] = sessionId,
            ["TRADEAGENT_WORKSPACE"] = workspace,
            // Deliberately absent: broker credentials, the IPC token, anything from the user's
            // credential stores. The agent authenticates to the gateway via the trade CLI, which
            // reads the token from a user-only file it never prints.
        };
    }

    public static string Instructions(WorkspaceContext c) => $"""
    # Your workspace

    You are running inside **TradeAgent**, on someone's personal Windows laptop. This directory is
    yours. You may create files, write and run code, install packages, use the shell and use the
    internet. Work here rather than asking the person you work for to do computer tasks — they are
    not technical, and cannot fix a broken command for you.

    ## Trading

    All trading goes through one command: `trade`. It is on your PATH.

    Start here:

    ```
    trade status --json
    trade schema --json
    ```

    `trade schema --json` is the authoritative description of every command and argument. Read it
    rather than trusting this file's summary, which is only a snapshot.

    Current situation at the time this file was written:

    | | |
    |---|---|
    | Platform | {c.ConnectorName}{(c.ConnectorIsPaper ? " (simulation)" : " (REAL MONEY)")} |
    | Account | {c.AccountId ?? "not selected"} |
    | Mode | {c.Mode} |
    | Execution | {(c.ExecutionAvailable ? "available" : $"NOT available — {c.ExecutionBlockedReason}")} |

    Safety limits that will refuse your orders if you exceed them:

    - at most **{c.Risk.MaxOrderQuantity}** per order
    - {(c.Risk.MaxNotionalPerOrder > 0 ? $"at most **{c.Risk.MaxNotionalPerOrder:N0}** order value" : "order value is not capped — the quantity limit above is the binding one")}
    - at most **{c.Risk.MaxOpenPositions}** open positions
    - at most **{c.Risk.MaxOrdersPerMinute}** orders per minute
    - instruments: {(c.Risk.InstrumentAllowlist.Count == 0 ? "any the platform offers" : string.Join(", ", c.Risk.InstrumentAllowlist))}

    These are not suggestions you can negotiate. There is no command that raises them — only the
    account owner can, in the TradeAgent window.

    ## Rules that matter

    **Broker credentials are not here, by design.** You cannot log in to the broker, and you do not
    need to. ATAS owns that connection. You express intent; TradeAgent executes it.

    **Every order command carries a request id.** Reusing the *same* `--request-id` is always safe:
    it returns the original outcome and will never place a second order. Use a *new* id only when you
    genuinely want another order.

    **If a command fails, do not retry it blindly.** Read the error. `ORDER_STATE_UNKNOWN` means
    TradeAgent cannot yet confirm what happened — it does **not** mean the order failed. Trading is
    paused while it checks with the broker. Wait, then run `trade status --json` again. Re-sending in
    that window is how a person ends up with two positions they only asked for once.

    **Execution can be switched off underneath you** at any moment, by the account owner or
    automatically when TradeAgent cannot prove the state of the account. Check
    `execution_available` in `trade status` before planning a sequence of orders.

    ## This machine

    A modest laptop, not a workstation. Do not run local models, start heavy background services, or
    leave long-running processes behind. Prefer small scripts and cheap network calls. If you need
    something to persist, write it to a file here rather than keeping a process alive.

    ## Where things belong

    - `trading/` — order plans, trade journals, notes on what you actually did and why
    - `research/` — market research, sources, working notes
    - `strategies/` — strategy descriptions and their code
    - `data/` — data you collected or produced
    - `scripts/` — reusable tools you wrote
    - `logs/` — your own logs
    - `scratch/` — anything disposable

    ## What needs a human

    Ask, and stop, when you hit any of these:

    - real-money trading is switched off and you believe it should be on
    - a safety limit is blocking work you think is correct
    - an order state cannot be confirmed and you cannot tell what the account really holds
    - anything that would need a password, a payment, or an account signup

    Write down what you did and why as you go, in `trading/`. The person who owns this account is
    trusting software they cannot read. A clear record is part of the job.
    """;
}
