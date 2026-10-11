using TradeAgent.Core.Db;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// ONE MODEL'S PUBLISHED KNOWLEDGE CUTOFF: <see cref="Through"/> is the last day the vendor says its training data
/// reaches — the last day of the month where the vendor names only a month — read from <see cref="Source"/> on
/// <see cref="ReadOn"/>.
/// </summary>
public sealed record ModelCutoff(string Model, DateOnly Through, string ReadOn, string Source)
{
    /// <summary>The first instant the model's training data does not reach: midnight UTC after <see cref="Through"/>.</summary>
    public DateTimeOffset Ends => new(Through.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>Where and when the row was read, as a sealed row records it.</summary>
    public string Cited => $"{Source} (read {ReadOn})";
}

/// <summary>
/// THE KNOWLEDGE CUTOFFS VENDORS PUBLISH, ONE ROW PER MODEL ID — data, in the shape of
/// <c>AgentRuntime.ListPrices</c>: a vendor that publishes a new model or moves a date is a one-line fix here, never a
/// rebuild of the rule that reads it (<c>U-referee-v2c</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5 E4, "flags a holdout the
/// authoring model may remember"; R04 :57, :315).
///
/// <para><b>A model the vendor publishes no cutoff for is absent, never guessed</b> — the rule <c>ListPrices</c> keeps
/// for prices. Its versions are flagged (<see cref="Remembered"/>): an unknown cutoff is never read as an early one. The
/// match is the exact id the runtime's own stream named (<c>ai_attempt.effective_model</c>); a variant spelling is a row
/// of its own or nothing.</para>
///
/// <para><b>Which cutoff.</b> Anthropic publishes a "reliable knowledge cutoff" and a "training data cutoff" (the broader
/// range of data used) per model; the row takes the TRAINING one, which is never the earlier — a model may remember
/// anything it was trained on. OpenAI's model page prints one "knowledge cutoff" per model, to the day.</para>
///
/// <para>Not here because the vendor's page prints none: <c>gpt-6-sol</c> and the GPT-5.6 and older ids (OpenAI's
/// models page, read below, lists no cutoff for them), and every model of a <c>custom</c> runtime.</para>
/// </summary>
public static class ModelCutoffs
{
    /// <summary>The day every row below was read from the page beside it.</summary>
    public const string ReadOn = "2026-10-11";

    /// <summary>Anthropic's models overview: the current lineup's training data cutoffs.</summary>
    public const string AnthropicModels = "https://platform.claude.com/docs/en/models/overview";

    /// <summary>Anthropic's per-model page, for a legacy model the overview no longer tabulates.</summary>
    public static string AnthropicModel(string page) => $"https://platform.claude.com/docs/en/models/{page}/overview";

    /// <summary>OpenAI's models page: one knowledge cutoff per flagship model.</summary>
    public const string OpenAiModels = "https://developers.openai.com/api/docs/models";

    static readonly ModelCutoff[] Rows =
    [
        // Anthropic, the overview's "Training data cutoff" row: Jun 2026 for all four current models.
        new("claude-fable-5-1", new DateOnly(2026, 6, 30), ReadOn, AnthropicModels),
        new("claude-opus-5-5", new DateOnly(2026, 6, 30), ReadOn, AnthropicModels),
        new("claude-sonnet-5-5", new DateOnly(2026, 6, 30), ReadOn, AnthropicModels),
        new("claude-haiku-5-5", new DateOnly(2026, 6, 30), ReadOn, AnthropicModels),
        // Anthropic legacy models, each page's "Training data cutoff".
        new("claude-opus-5", new DateOnly(2026, 5, 31), ReadOn, AnthropicModel("opus-5")),
        new("claude-sonnet-5", new DateOnly(2026, 1, 31), ReadOn, AnthropicModel("sonnet-5")),
        new("claude-fable-5", new DateOnly(2026, 1, 31), ReadOn, AnthropicModel("fable-5")),
        new("claude-opus-4-8", new DateOnly(2026, 1, 31), ReadOn, AnthropicModel("opus-4-8")),
        // OpenAI, the models page's "Knowledge cutoff".
        new("gpt-6-astra", new DateOnly(2026, 4, 30), ReadOn, OpenAiModels),
        new("gpt-6.1-sol", new DateOnly(2026, 4, 30), ReadOn, OpenAiModels),
        new("gpt-6-luna", new DateOnly(2026, 5, 18), ReadOn, OpenAiModels),
    ];

    /// <summary>Every row, in the order above.</summary>
    public static IReadOnlyList<ModelCutoff> All => Rows;

    /// <summary>The published cutoff of exactly <paramref name="model"/>, or null — no model named, or none published.</summary>
    public static ModelCutoff? Of(string? model) =>
        model is { Length: > 0 } id ? Rows.FirstOrDefault(r => string.Equals(r.Model, id, StringComparison.Ordinal)) : null;

    /// <summary>
    /// WHETHER A HOLDOUT FROM <paramref name="holdoutFrom"/> MAY BE REMEMBERED by a version frozen at
    /// <paramref name="frozenAt"/> under a model whose cutoff is <paramref name="cutoff"/> (seat A's Q7 (a), any overlap).
    ///
    /// <para>Never when the holdout starts at or after the freeze: the model's training preceded the authoring, so it
    /// cannot reach months after it, whatever the model. Otherwise when the holdout starts before the cutoff ends — a
    /// remembered first month biases the whole window — or when the cutoff is unknown, which is never read as an early
    /// one.</para>
    /// </summary>
    public static bool Remembered(DateTimeOffset holdoutFrom, DateTimeOffset frozenAt, ModelCutoff? cutoff) =>
        holdoutFrom < frozenAt && (cutoff is null || holdoutFrom < cutoff.Ends);
}

/// <summary>
/// WHO AUTHORED A VERSION AND WHETHER IT MAY REMEMBER ITS HOLDOUT (<c>U-referee-v2c</c>): the version's
/// <see cref="Attempt"/>, the <see cref="Model"/> the runtime's own stream named for it, that model's published
/// <see cref="Cutoff"/>, and <see cref="Remembered"/>.
/// </summary>
public sealed record Authorship(string? Attempt, string? Model, ModelCutoff? Cutoff, bool Remembered)
{
    /// <summary>
    /// THE AUTHOR OF <paramref name="version"/>, read version → <c>attempt</c> → <c>effective_model</c>, and whether a
    /// holdout from <paramref name="holdoutFrom"/> may be remembered by it.
    ///
    /// <para><b>Refuse, never guess.</b> A version with no attempt, an attempt this ledger does not hold, an attempt
    /// whose runtime named no model (codex names none) and a model with no published cutoff all leave the cutoff
    /// unknown, and an unknown cutoff flags. <c>requested_model</c> — what TradeAgent put on the command line — is never
    /// read as the author: a runtime may run another model than the one it was asked for, and only its own stream says
    /// which.</para>
    /// </summary>
    public static Authorship Of(AiAttemptStore attempts, StrategyVersionRow version, DateTimeOffset holdoutFrom)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(version);
        var model = version.Attempt is { Length: > 0 } id ? attempts.Get(id)?.EffectiveModel : null;
        var cutoff = ModelCutoffs.Of(model);
        return new Authorship(version.Attempt, model, cutoff, ModelCutoffs.Remembered(holdoutFrom, version.CreatedAt, cutoff));
    }
}
