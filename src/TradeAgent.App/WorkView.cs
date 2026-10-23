using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>ONE READ OF THE WORK: the open mandates, the deliveries waiting to be judged, and the decisions of the last week with their forecasts.</summary>
sealed record WorkRead(
    OrgList<OpenMandate> Open,
    OrgList<DeliveryToJudge> Deliveries,
    OrgList<RecentDecision> Decisions,
    DateTimeOffset Now,
    string Currency)
{
    /// <summary>How far back the decisions go.</summary>
    public static readonly TimeSpan DecisionWindow = TimeSpan.FromDays(7);

    /// <summary>The most decisions the page lists.</summary>
    public const int DecisionLimit = 25;

    public static WorkRead From(OrgStore org, DateTimeOffset now, string currency) =>
        new(org.MandatesOpen(), org.DeliveriesAwaiting(), org.DecisionsSince(now - DecisionWindow, DecisionLimit), now, currency);
}

/// <summary>One open mandate as the page says it. <see cref="Objective"/> is its decider's claim; the rest is the app's record.</summary>
sealed record MandateWords(string Id, string Header, string Status, string Terms, string Objective, string Spend);

/// <summary>One delivery waiting to be judged. <see cref="Report"/> and <see cref="Deviations"/> are the holder's claims; <see cref="Checks"/> are the app's results.</summary>
sealed record DeliveryWords(string Id, string Header, string Report, string Deviations, string Checks);

/// <summary>One decision. <see cref="Choice"/>, <see cref="Options"/> and <see cref="Rationale"/> are its author's claims; <see cref="Forecasts"/> says each claim and then the app's resolution.</summary>
sealed record DecisionWords(string Id, string Header, string Choice, string Options, string Rationale, string Forecasts);

/// <summary>
/// THE WORK PAGE'S SENTENCES, SEPARATED FROM ITS CONTROLS (<c>U-org-surface-a</c>): <b>measurement and claim stay apart.</b> A status, a deadline,
/// a spend, a check's result and a forecast's resolution are the app's, printed as its own; an objective, a report, a deviation, a choice, a
/// rationale and a forecast are the organisation's WORDS, printed only through <see cref="OrgText.Claim"/> — quoted, after who wrote them — so
/// text addressed to the owner or to the app inside one reads as what it is: something a position wrote.
/// </summary>
sealed record WorkWords(
    string Status,
    IReadOnlyList<MandateWords> Mandates,
    IReadOnlyList<DeliveryWords> Deliveries,
    IReadOnlyList<DecisionWords> Decisions,
    string Unreadable)
{
    public static WorkWords Of(WorkRead? read, string? unreadable)
    {
        if (read is null)
            return new WorkWords(unreadable is { Length: > 0 } ? Labels.SurfaceUnreadable(unreadable) : Labels.SurfaceNotRead, [], [], [], "");

        var refused = read.Open.Refused.Concat(read.Deliveries.Refused).Concat(read.Decisions.Refused).ToList();
        return new WorkWords(
            "",
            [.. read.Open.Rows.Select(o => Mandate(o, read))],
            [.. read.Deliveries.Rows.Select(Delivery)],
            [.. read.Decisions.Rows.Select(Decision)],
            string.Join("\n", refused));
    }

    static MandateWords Mandate(OpenMandate o, WorkRead read)
    {
        var m = o.Mandate;
        var late = read.Now > m.Deadline ? ", past its deadline" : "";
        var terms = $"A {m.Kind} mandate, latitude {m.Latitude}; due {OrgText.When(m.Deadline)}{late}; "
                    + $"{Labels.Money(m.Allocation, read.Currency)} allocated; up to {m.RetryLimit} {(m.RetryLimit == 1 ? "try" : "tries")}.";
        var objective = OrgText.Claim(m.Decider, m.Objective)
                        + (m.Scope is { Length: > 0 } scope ? "\nScope. " + OrgText.Claim(m.Decider, scope) : "");

        var spend = $"AI spend counted: {Labels.Money(o.Spend.Spent, read.Currency)} of {Labels.Money(m.Allocation, read.Currency)}, "
                    + $"over {Count(o.Spend.Launches, "launch", "launches")}";
        if (o.Spend.Reserved > 0m) spend += $"; {Labels.Money(o.Spend.Reserved, read.Currency)} more is held by a turn in flight";
        if (o.Spend.Unknown > 0)
            spend += $"; {Count(o.Spend.Unknown, "turn", "turns")} reported no cost and {(o.Spend.Unknown == 1 ? "is" : "are")} not in that figure";
        if (o.ChargedAtReservation > 0)
            spend += $"; {Count(o.ChargedAtReservation, "turn", "turns")} reported no usage and {(o.ChargedAtReservation == 1 ? "is" : "are")} charged "
                     + "the reservation, so that figure is not a bill";
        spend += ".";

        return new MandateWords(m.Id, $"{m.Id}: {m.Holder} holds it for {m.Decider}",
            $"Status: {o.Status ?? "no status is recorded"}.", terms, objective, spend);
    }

    static DeliveryWords Delivery(DeliveryToJudge d)
    {
        var holder = d.Mandate.Holder;
        var deviations = d.Delivery.Deviations.Count == 0
            ? "No deviation is named."
            : string.Join("\n", d.Delivery.Deviations.Select(x =>
                OrgText.Claim(holder, $"{x.What}; found: {x.Found}; cost: {x.Cost}")));
        var checks = d.Checks.Count == 0
            ? "The app ran no check on it."
            : string.Join("\n", d.Checks.Select(c => $"The app's check {c.Check}: {(c.Passed ? "passed" : "failed")} — {c.Reason}"));
        return new DeliveryWords(d.Delivery.Id,
            $"{d.Mandate.Id}: {holder} delivered {OrgText.When(d.Delivery.At)}; {d.Mandate.Decider} has not judged it.",
            OrgText.Claim(holder, d.Delivery.Report), deviations, checks);
    }

    static DecisionWords Decision(RecentDecision r)
    {
        var d = r.Decision;
        var rationale = r.Rationale is { Length: > 0 } text ? OrgText.Claim(d.Position, text) : "No rationale is recorded for it.";
        var forecasts = r.Forecasts.Count == 0
            ? "No forecast was made on it."
            : string.Join("\n", r.Forecasts.Select(Forecast));
        return new DecisionWords(d.Id, $"{d.Position} decided {OrgText.When(d.At)} ({d.Kind}).",
            "Chose — " + OrgText.Claim(d.Position, d.Choice),
            "Among — " + OrgText.Claim(d.Position, string.Join("; ", d.Options)),
            "Because — " + rationale,
            forecasts);
    }

    static string Forecast(RecentForecast f)
    {
        var c = f.Forecast;
        var said = OrgText.Claim(c.Forecaster,
            $"{c.Predicate} {c.Subject} → {c.Target}, by {OrgText.When(c.ResolveBy)}, probability {c.Probability.ToString(CultureInfo.InvariantCulture)}");
        var scored = c.Scored ? "" : $" The app does not score it: {c.UnscoredWhy}.";
        var outcome = f.Resolution is { } r
            ? $"The app resolved it: {r.Outcome}, resting on {r.RecordKind} {r.RecordId} ({OrgText.When(r.At)}) — {r.Why}."
            : $"Not resolved yet; due by {OrgText.When(c.ResolveBy)}.";
        return $"{said}\n{outcome}{scored}";
    }

    static string Count(int n, string one, string many) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";
}

/// <summary>
/// THE WORK (<c>U-org-surface-a</c>; R24 § 4.7 "the work: mandates, deliveries, decisions, forecasts"): what is open, what waits for a decision and
/// what was decided lately. <b>It only reads</b> — a mandate is judged, accepted or cancelled by the position that decides it, through its own
/// verbs, never from here — and it shows the organisation's words as words: quoted and attributed.
///
/// <para><b>Built once, updated in place.</b> The skeleton is made in the constructor; each mandate's, delivery's and decision's card is made the
/// first time its id is seen and kept (<see cref="LiveList{T}"/>), and a refresh writes new text into the same text blocks.</para>
/// </summary>
sealed class WorkPage
{
    readonly OrgSurfaceSources _src;
    readonly TextBlock _status = Ui.Muted("");
    readonly TextBlock _unreadable = Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    readonly StackPanel _mandates = new() { Spacing = Theme.S3 };
    readonly StackPanel _deliveries = new() { Spacing = Theme.S3 };
    readonly StackPanel _decisions = new() { Spacing = Theme.S3 };
    readonly LiveList<MandateWords> _mandateList;
    readonly LiveList<DeliveryWords> _deliveryList;
    readonly LiveList<DecisionWords> _decisionList;

    public Control Root { get; }

    internal StackPanel MandateCards => _mandates;

    internal StackPanel DeliveryCards => _deliveries;

    internal StackPanel DecisionCards => _decisions;

    internal TextBlock StatusText => _status;

    internal TextBlock UnreadableText => _unreadable;

    public WorkPage(OrgSurfaceSources sources)
    {
        _src = sources;
        _status.FontSize = Theme.Small;
        _status.Text = Labels.SurfaceNotRead;
        _unreadable.IsVisible = false;
        _mandateList = new LiveList<MandateWords>(_mandates, Ui.Muted(Labels.WorkNoMandates), m => m.Id, MakeMandate);
        _deliveryList = new LiveList<DeliveryWords>(_deliveries, Ui.Muted(Labels.WorkNoDeliveries), d => d.Id, MakeDelivery);
        _decisionList = new LiveList<DecisionWords>(_decisions, Ui.Muted(Labels.WorkNoDecisions), d => d.Id, MakeDecision);

        var body = Ui.Col(Theme.S6,
            _status,
            _unreadable,
            Ui.Col(Theme.S2, Ui.Eyebrow(Labels.WorkMandatesHeading), _mandates),
            Ui.Col(Theme.S2, Ui.Eyebrow(Labels.WorkDeliveriesHeading), _deliveries),
            Ui.Col(Theme.S2, Ui.Eyebrow(Labels.WorkDecisionsHeading), Ui.Micro(Labels.WorkForecastNote), _decisions));
        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header(Labels.NavWork, Labels.WorkSubtitle + " " + Labels.SurfaceReadOnly));
        root.Children.Add(scroll);
        Root = root;
    }

    internal Control? MandateCardOf(string id) => _mandateList.CardOf(id);

    internal Control? DecisionCardOf(string id) => _decisionList.CardOf(id);

    /// <summary>In place on every tick, touching nothing when nothing moved.</summary>
    public void Update()
    {
        WorkWords words;
        var read = false;
        try
        {
            if (_src.Org() is { } org)
            {
                words = WorkWords.Of(WorkRead.From(org, _src.Now(), _src.Currency()), null);
                read = true;
            }
            else words = WorkWords.Of(null, null);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; }   // the quit closed the file under this tick
        catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
        {
            words = WorkWords.Of(null, ex.Message);
        }

        OrgText.Set(_status, words.Status);
        _status.IsVisible = words.Status.Length > 0;
        OrgText.Set(_unreadable, words.Unreadable);
        _unreadable.IsVisible = words.Unreadable.Length > 0;
        if (!read) return;   // the status above says nothing below is current
        _mandateList.Update(words.Mandates);
        _deliveryList.Update(words.Deliveries);
        _decisionList.Update(words.Decisions);
    }

    static TextBlock Small() => Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);

    /// <summary>An organisation's words on the page: the body size, in the text colour, and slanted — they are not the app's voice.</summary>
    static TextBlock Said() => Ui.With(Ui.Body(""), t => t.FontStyle = FontStyle.Italic);

    static (Control Card, Action<MandateWords> Fill) MakeMandate(MandateWords first)
    {
        var header = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var status = Small();
        var terms = Small();
        var objective = Said();
        var spend = Small();
        var card = Ui.Card(Ui.Col(Theme.S2, header, status, terms, objective, spend));
        return (card, m =>
        {
            OrgText.Set(header, m.Header);
            OrgText.Set(status, m.Status);
            OrgText.Set(terms, m.Terms);
            OrgText.Set(objective, m.Objective);
            OrgText.Set(spend, m.Spend);
        });
    }

    static (Control Card, Action<DeliveryWords> Fill) MakeDelivery(DeliveryWords first)
    {
        var header = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var report = Said();
        var deviations = Ui.With(Said(), t => t.FontSize = Theme.Small);
        var checks = Small();
        var card = Ui.Card(Ui.Col(Theme.S2, header, report, deviations, checks));
        return (card, d =>
        {
            OrgText.Set(header, d.Header);
            OrgText.Set(report, d.Report);
            OrgText.Set(deviations, d.Deviations);
            OrgText.Set(checks, d.Checks);
        });
    }

    static (Control Card, Action<DecisionWords> Fill) MakeDecision(DecisionWords first)
    {
        var header = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var choice = Said();
        var options = Ui.With(Said(), t => t.FontSize = Theme.Small);
        var rationale = Said();
        var forecasts = Small();
        var card = Ui.Card(Ui.Col(Theme.S2, header, choice, options, rationale, forecasts));
        return (card, d =>
        {
            OrgText.Set(header, d.Header);
            OrgText.Set(choice, d.Choice);
            OrgText.Set(options, d.Options);
            OrgText.Set(rationale, d.Rationale);
            OrgText.Set(forecasts, d.Forecasts);
        });
    }
}
