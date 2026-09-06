namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE VENDOR'S PUBLISHED LIST PRICES, READ FROM THE VENDOR'S OWN PAGE ON A NAMED DAY.
///
/// <c>U-meter</c> shipped no prices at all, on the reasoning that a per-token figure is a claim
/// about a bill this software cannot see. The reasoning was right about the CLAIM and wrong about
/// the consequence: with no prices every turn read "unpriced", the daily cap could not bite, and the
/// only repair was a <c>costs.json</c> the owner is never going to write — the software promises
/// they will not have to. So the prices ship, and what changes is what they are allowed to be:
///
/// <list type="bullet">
/// <item><b>Dated.</b> Every entry carries <see cref="ModelPrice.PricedAt"/> and
/// <see cref="ModelPrice.Source"/>. A price with no date is a price nobody can check, and these go
/// stale on the vendor's schedule rather than this repository's.</item>
/// <item><b>Read, not remembered.</b> Each number below was read from the page named beside it on
/// <see cref="ReadOn"/>. A model the page does not price is ABSENT from this file rather than
/// estimated from a neighbouring row — see the spark note under <see cref="CodexModels"/>.</item>
/// <item><b>A list price, never the owner's bill.</b> The same CLI costs per-token on an API key
/// and nothing per-token on a subscription. So this is the CEILING the software charges itself
/// against, the owner's two numbers on the Safety page beat it, and <c>costs.json</c> sits between
/// them for an engineer.</item>
/// </list>
///
/// <b>Why both runtimes are priced against OpenAI.</b> <c>codex</c> is OpenAI's own CLI. <c>opencode</c>
/// ships no model and bills nothing itself, and TradeAgent's built-in sign-in for it writes an
/// OPENAI key — <see cref="RuntimeCatalog"/>'s <c>opencode</c> manifest carries
/// <c>ApiKeyPlan.Label = "your OpenAI API key"</c> and an <c>auth.json</c> template keyed
/// <c>"openai"</c> — so under the configuration this build ships, an OpenCode turn is charged by
/// OpenAI. An owner who points OpenCode at another provider is the case <c>costs.json</c> and the
/// Safety page's two numbers exist for; nothing here pretends to know what they pay.
///
/// <b>What is deliberately NOT here.</b> The pricing page also lists the previous generations —
/// <c>gpt-4.1</c>, <c>gpt-4o</c>, the <c>o</c> series, <c>gpt-3.5-turbo</c>, <c>chat-latest</c> —
/// and they are left out on purpose rather than forgotten. Neither CLI selects one, and including
/// them would drag the unknown-model estimate (which takes the HIGHEST entry a runtime has) up to
/// <c>o1-pro</c> at $150/$600 per million: a ceiling reached in two turns, which is a cap that stops
/// the work rather than one that bounds it. An owner running a legacy model names it and its price
/// in <c>costs.json</c>, and that entry wins.
/// </summary>
public static class ListPrices
{
    /// <summary>The day every number in this file was read from the page beside it.</summary>
    public const string ReadOn = "2026-09-06";

    /// <summary>Where the per-million figures were read.</summary>
    public const string OpenAiPrices = "https://developers.openai.com/api/docs/pricing";

    /// <summary>Where the list of models Codex can be set to was read.</summary>
    public const string OpenAiModels = "https://learn.chatgpt.com/docs/models";

    /// <summary>
    /// The currency the figures below are in. The built-ins are only consulted when the catalogue
    /// they are merged under agrees — see <see cref="CostCatalog.ListPricesApply"/> — because
    /// pricing a turn with dollars and labelling it euros is a wrong bill, not a rounding error.
    /// </summary>
    public const string Currency = "USD";

    /// <summary>
    /// OpenAI's current-generation text models, per MILLION tokens: input, cached input, CACHE
    /// WRITES, output. Read from <see cref="OpenAiPrices"/> on <see cref="ReadOn"/>, from the
    /// STANDARD tier's short-context columns, in the order the page lists them.
    ///
    /// A null figure is the page showing a dash or no column at all for that row, and null means
    /// "the input rate" everywhere in <see cref="ModelPrice"/> — the conservative reading for a
    /// cached-input dash, since the alternative is inventing a discount the vendor does not publish.
    ///
    /// CACHE WRITES are a column on that page and are DEARER than plain input, so a null there is
    /// not conservative at all: it charges 10.00 where the page says 12.50, and an under-charge is
    /// the one direction that lets the daily cap be walked past. The page gives the column for the
    /// four Daybreak rows only; the rows below them carry null because there is nothing to read.
    ///
    /// Two figures on the page are deliberately NOT taken. The LONG-CONTEXT columns are dearer
    /// again (astra 20.00/75.00), and Codex's FAST MODE prices <c>gpt-5.3-codex</c> at 3.50/28.00
    /// rather than 1.75/14.00 — but neither the token counts nor the stream say which was in force,
    /// and inventing the dearer reading would over-charge every ordinary turn. An owner on either
    /// has the two numbers on the Safety page.
    /// </summary>
    static readonly (string Model, decimal Input, decimal? Cached, decimal? Write, decimal Output)[] OpenAi =
    [
        ("gpt-6-astra",   10.00m, 1.000m, 12.50m,  50.00m),
        ("gpt-5.6-sol",    4.00m, 0.400m,  5.00m,  20.00m),
        ("gpt-5.6-terra",  2.00m, 0.200m,  2.50m,  12.00m),
        ("gpt-5.6-luna",   0.20m, 0.020m,  0.25m,   1.20m),
        ("gpt-5.5",        5.00m, 0.500m, null,    30.00m),
        ("gpt-5.5-pro",   30.00m, null,   null,   180.00m),
        ("gpt-5.4",        2.50m, 0.250m, null,    15.00m),
        ("gpt-5.4-mini",   0.75m, 0.075m, null,     4.50m),
        ("gpt-5.4-nano",   0.20m, 0.020m, null,     1.25m),
        ("gpt-5.4-pro",   30.00m, null,   null,   180.00m),
        ("gpt-5.3-codex",  1.75m, 0.175m, null,    14.00m),
        ("gpt-5.2",        1.75m, 0.175m, null,    14.00m),
        ("gpt-5.2-pro",   21.00m, null,   null,   168.00m),
        ("gpt-5.1",        1.25m, 0.125m, null,    10.00m),
        ("gpt-5",          1.25m, 0.125m, null,    10.00m),
        ("gpt-5-mini",     0.25m, 0.025m, null,     2.00m),
        ("gpt-5-nano",     0.05m, 0.005m, null,     0.40m),
        ("gpt-5-pro",     15.00m, null,   null,   120.00m)
    ];

    /// <summary>
    /// The models Codex itself can be set to, read from <see cref="OpenAiModels"/> on
    /// <see cref="ReadOn"/> — its five recommended and its five legacy ids, minus the two that are
    /// not on the pricing page.
    ///
    /// <c>gpt-5.3-codex-spark</c> is one of the five recommended and is NOT priced here: the pricing
    /// page has no row for it. That is the rule this whole file turns on — a model the vendor does
    /// not publish a price for is absent, not guessed from the plain <c>gpt-5.3-codex</c> row beside
    /// it. A turn on it is priced by the estimate over the rest, or by the owner's own numbers.
    ///
    /// The other absence is deliberate in the same way: the page also lists <c>gpt-5.4</c> and
    /// <c>gpt-5.4-mini</c> as retiring 2026-08-31 and <c>gpt-5.2</c>/<c>gpt-5.3-codex</c> as
    /// deprecated with ChatGPT sign-in. They stay, because a deprecated model still bills.
    /// </summary>
    static readonly string[] CodexModels =
    [
        "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
        "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.2", "gpt-5.3-codex"
    ];

    /// <summary>
    /// The whole catalogue: one entry per runtime id AND model, in the shape <c>costs.json</c> uses,
    /// so an owner's file and this list are the same kind of thing and merge without translation.
    /// </summary>
    public static IReadOnlyList<ModelPrice> All { get; } = Build();

    static List<ModelPrice> Build()
    {
        var all = new List<ModelPrice>();

        // codex: OpenAI's own CLI, restricted to the ids its own model page names.
        foreach (var row in OpenAi.Where(r => CodexModels.Contains(r.Model)))
            all.Add(Price("codex", row));

        // opencode: TradeAgent signs it in with an OpenAI key, and it puts no restriction of its
        // own on which OpenAI model is chosen, so the whole current generation applies.
        foreach (var row in OpenAi)
            all.Add(Price("opencode", row));

        // `custom` is deliberately absent. Its command, and therefore its provider, is written by an
        // engineer in runtimes.json; a price shipped for it would be a guess about a vendor this
        // build has never heard of.
        return all;
    }

    static ModelPrice Price(string runtime,
        (string Model, decimal Input, decimal? Cached, decimal? Write, decimal Output) row) => new()
    {
        Runtime = runtime,
        Model = row.Model,
        InputPerMillion = row.Input,
        CachedInputPerMillion = row.Cached,
        CacheWritePerMillion = row.Write,
        OutputPerMillion = row.Output,
        PricedAt = ReadOn,
        Source = OpenAiPrices
    };
}
