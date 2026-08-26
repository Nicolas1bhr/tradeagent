namespace TradeAgent.Provisioning;

/// <summary>
/// One step of an install, reported in language a non-technical person can read.
/// <paramref name="Fraction"/> is 0..1 when the step has a measurable size (a download), null when
/// it does not (unpacking, verifying).
/// </summary>
public sealed record ProvisionProgress(string StepId, string Message, double? Fraction = null);

/// <summary>
/// Something TradeAgent needs on the machine, and the code that puts it there.
///
/// The whole point of this interface is that the answer to "what do I have to do?" is always
/// "nothing, or click Yes on one Windows prompt". Anything that would send the user to a terminal,
/// a download page or a PATH dialog belongs behind an implementation of this, not in front of them.
/// </summary>
public interface IPrerequisite
{
    /// <summary>Stable identifier: "node", "ai-runtime", "atas".</summary>
    string Id { get; }

    /// <summary>User-facing name, plain language.</summary>
    string Title { get; }

    /// <summary>One sentence saying why this is needed, in plain language.</summary>
    string Why { get; }

    /// <summary>True only when Windows itself will have to ask for permission.</summary>
    bool RequiresAdmin { get; }

    Task<bool> IsSatisfiedAsync(CancellationToken ct = default);

    Task InstallAsync(IProgress<ProvisionProgress>? progress, CancellationToken ct = default);
}
