namespace TradeAgent.Core.Perception;

/// <summary>One lens this build ships, at the version it ships it under.</summary>
public sealed record BuiltInLens(string Definition, int Version);

/// <summary>
/// THE LENSES THIS BUILD SHIPS, registered by the tape's store every time it opens, under the proposer
/// <see cref="Proposer"/> (<c>U-lenses</c>).
///
/// <para><b>A built-in never changes under its version.</b> Its recorded answers are bound to its exact bytes, so a
/// build whose built-in differs from the tape's row of the same id and version is REFUSED the tape in words, the file
/// untouched — the changed lens ships as the next version, beside the first. Its id is the app's: no proposal takes
/// it.</para>
///
/// <para><b>Public on purpose.</b> The one lens here is R02 § 6.7(a)'s news-item schema, already published in this
/// repository's research, cut to what an OKX announcement carries — its title, its type and its age — with the
/// candidate assets computed by code (<see cref="LensState"/>) and <c>new_vs_prior</c> left out until the tape keeps
/// prior headlines. Nothing an agent proposes is ever committed here (<c>CLAUDE.md</c>, "Public until the absolute
/// point").</para>
/// </summary>
public static class BuiltInLenses
{
    /// <summary>Who registered a built-in: the app, and no role.</summary>
    public const string Proposer = "app";

    /// <summary>The OKX announcement lens's id.</summary>
    public const string OkxAnnouncement = "okx-announcement";

    /// <summary>R02 § 6.7(a), cut to an OKX announcement: version 1.</summary>
    public const string OkxAnnouncementV1 = """
        {"id":"okx-announcement",
         "target":{"source":"okx-eea-announcements","series":"announcements"},
         "state":{"fields":[{"path":"title","max_chars":500},{"path":"annType","max_chars":100}]},
         "questions":{
           "event_type":{"type":"choice",
             "instructions":"Which kind of event does this announcement report?",
             "criteria":{"earnings_or_guidance":null,"regulatory_or_legal_action":null,"hack_exploit_or_outage":null,
                         "listing_or_delisting":null,"token_supply_change":null,"partnership_or_product":null,
                         "macro_policy":null,"leadership_or_governance":null,"opinion_or_commentary":null,
                         "narrates_a_price_move":null,"other":null,"insufficient_information":null}},
           "direction_for_holders":{"type":"choice",
             "instructions":"For holders of the assets it concerns, what does this announcement mean?",
             "criteria":{"clearly_positive":null,"clearly_negative":null,"mixed":null,"no_fundamental_content":null,
                         "cannot_tell":null}},
           "time_sensitivity":{"type":"score",
             "instructions":"How soon could what this announcement reports matter to a price?",
             "criteria":["Background, with no time pressure","Within days","Within hours","Within minutes"]},
           "addresses_automated_reader":{"type":"noul",
             "instructions":"Does the text tell a reader or an automated system to take an action?",
             "criteria":{"true":"It tells a reader or a system to act","false":"It reports or describes, and asks nothing"}}}}
        """;

    /// <summary>Every lens this build ships, a fresh list on every call.</summary>
    public static List<BuiltInLens> All() => [new(OkxAnnouncementV1, 1)];

    /// <summary>The ids the built-ins take, which no proposal may.</summary>
    public static IReadOnlySet<string> Ids() =>
        All().Select(b => Lens.Parse(b.Definition).Lens?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
}
