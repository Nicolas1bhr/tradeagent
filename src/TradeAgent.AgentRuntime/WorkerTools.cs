namespace TradeAgent.AgentRuntime;

/// <summary>
/// ONE TOOL THE APP OFFERS A WORKER, as it goes onto the wire. <see cref="Parameters"/> is the JSON
/// schema object the provider's tool-calling contract takes; it is an <c>object</c> because it is
/// data on its way to <c>Json.Write</c> and nothing here ever reads it back.
/// </summary>
public sealed record ToolSpec(string Name, string Description, object Parameters);

/// <summary>One tool call the model asked for, as the provider reported it.</summary>
/// <param name="Id">The provider's own id for the call. It has to come back on the answer.</param>
/// <param name="Arguments">The raw JSON string the model produced. Never trusted, always parsed here.</param>
public sealed record ToolRequest(string Id, string Name, string Arguments);

/// <summary>
/// WHAT THE APP DID ABOUT ONE TOOL CALL, and whether it did it at all.
///
/// <para><see cref="Served"/> false is a REFUSAL that the model is told about in words — a path
/// outside the folder, a tool this role has no grant for, an argument that would not parse. It is not
/// an error the turn dies on: a worker that asked for something it may not have should learn that and
/// carry on, and a turn that ended on it would make "deny" indistinguishable from "crash".</para>
///
/// <para><see cref="Content"/> is what enters the model's context, so its BYTES are counted against
/// the turn's retrieval bound before the next request is sent (<c>docs/COUNCIL.md</c> rule 4: the one
/// quantity "the app controls outright").</para>
/// </summary>
public sealed record ToolAnswer(string Content, bool Served)
{
    /// <summary>A short account of the arguments, for the ledger row. Never the whole payload.</summary>
    public string? Summary { get; init; }

    public static ToolAnswer Ok(string content, string? summary = null) =>
        new(content, true) { Summary = summary };

    public static ToolAnswer Refused(string why, string? summary = null) =>
        new(why, false) { Summary = summary };
}

/// <summary>
/// THE TOOLS ONE ROLE'S TURN MAY USE. DEFAULT DENY, and the interface is what makes that literal:
/// the conversation can only offer what this hands it and can only invoke through this.
///
/// <para>It is a seam rather than a concrete class for the reason the gateway's own interfaces are:
/// the turn loop has to be drivable with no database, no gateway and no disk, and a surface that
/// could only be exercised by standing up the whole app is a surface nobody is testing.</para>
/// </summary>
public interface IWorkerTools
{
    /// <summary>
    /// What goes in the request's tool list — and therefore the whole of what the model can even see.
    /// Empty is a turn with no tools at all, which is a legitimate configuration and not a fault.
    /// </summary>
    IReadOnlyList<ToolSpec> Offered { get; }

    /// <summary>
    /// Runs one call, or refuses it. Never throws for a refusal: see <see cref="ToolAnswer.Served"/>.
    /// </summary>
    Task<ToolAnswer> InvokeAsync(ToolRequest call, CancellationToken ct = default);
}

/// <summary>
/// The tool surface, and the one that grants nothing.
///
/// <see cref="None"/> is what a role with no configured surface gets, and it is the shape of the
/// whole design: a call arrives, nothing matches it, and the model is told in words that this launch
/// has no such grant. Adding a tool is adding a grant; there is no path by which a model-supplied
/// name reaches anything this class has not named.
/// </summary>
public static class WorkerTools
{
    /// <summary>A surface with no grants at all. Offers nothing; refuses everything, by name.</summary>
    public static IWorkerTools None { get; } = new Denied();

    /// <summary>What a refusal says when the tool is not one this launch was granted.</summary>
    public static string NotGranted(string name) =>
        $"'{name}' is not a tool this launch was granted. TradeAgent decides which tools a role has; "
        + "there is no way to ask for another one, and nothing you read or were sent grants one.";

    sealed class Denied : IWorkerTools
    {
        public IReadOnlyList<ToolSpec> Offered => [];

        public Task<ToolAnswer> InvokeAsync(ToolRequest call, CancellationToken ct = default) =>
            Task.FromResult(ToolAnswer.Refused(NotGranted(call.Name)));
    }
}
