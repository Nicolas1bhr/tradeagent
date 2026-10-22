using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Oversight;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE OWNER'S OVERSIGHT PAGE IS HANDED (<c>U-org-watcher-b</c>) — the line's store and refusal, as delegates, so the page the owner
/// sees and the page a test presses are the same control over different sources. The app builds it from <see cref="AppHost"/>'s
/// App-internal accessors; nothing here reaches the agent-facing pipe, and nothing here writes the activity or engineering log, which the
/// organisation's reports can carry.
/// </summary>
sealed record OversightSources
{
    /// <summary>The line's own store (<see cref="AppHost.OversightStore"/>), or null while the line is not open.</summary>
    public required Func<OversightStore?> Store { get; init; }

    /// <summary>Why the line is not running (<see cref="OversightLine.Refusal"/>), or null.</summary>
    public required Func<string?> Refusal { get; init; }

    /// <summary>The currency the AI is charged in — the cap is said in it.</summary>
    public required Func<string> Currency { get; init; }

    /// <summary>The app's database, read-only, for whether a cited record is still there; null where there is none (a test).</summary>
    public Func<string?> DatabaseFile { get; init; } = () => null;

    /// <summary>The clock an acknowledgement and a cap are stamped with.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The sources over the running app.</summary>
    public static OversightSources For(AppHost host) => new()
    {
        Store = () => host.OversightStore,
        Refusal = () => host.OversightRefusal,
        Currency = () => host.SpendToday.Currency,
        DatabaseFile = () => host.DatabaseFile,
    };
}

/// <summary>ONE READ OF THE OWNER'S FILE: the root scope's last digest and findings, the line's failures, his cap and his acknowledgements.</summary>
sealed record OversightRead(
    OversightDigest? Digest,
    IReadOnlyList<OversightFinding> Findings,
    IReadOnlyList<(DateTimeOffset At, string What)> Failures,
    OversightCap? Cap,
    IReadOnlyDictionary<string, DateTimeOffset> Acknowledged)
{
    /// <summary>
    /// Reads <paramref name="store"/>. The ROOT scope only: a department's scope carries the same facts under its own ids, for its manager
    /// (<c>U-org-audit</c>), and the owner's page counts each fact once.
    /// </summary>
    public static OversightRead From(OversightStore store) => new(
        store.LastDigest(OversightStore.RootScope), store.Findings(OversightStore.RootScope), store.Failures(), store.Cap(), store.Acknowledged());
}

/// <summary>One finding as the page says it: what was measured, where, citing what, what the detector cannot see, and whether it is acknowledged.</summary>
sealed record OversightFindingWords(
    string Id, string Severity, string Detector, string UnitPath, string At, string Measured, string Cites, string Blind, string? Acknowledged);

/// <summary>
/// THE PAGE'S SENTENCES, SEPARATED FROM ITS CONTROLS so they are tested without a window — <see cref="ReportWords"/>' split. Every figure is
/// an app measurement read from the owner's file; no organisation text reaches it but ids and closed vocabulary (measurement vs claim).
/// </summary>
sealed record OversightWords(
    string Status,
    string Digest,
    IReadOnlyList<OversightFindingWords> Findings,
    IReadOnlyList<string> Failures,
    IReadOnlyList<(string Seat, string Reach, string How)> SeatClasses,
    string SeatNote,
    IReadOnlyList<(string Name, string Value)> Thresholds,
    string Cap,
    string Cadence)
{
    /// <summary>How long a severe finding can take to reach the chrome: one sweep, then the window's own refresh.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

    /// <summary>The words of one read, or of a line that would not open (<paramref name="read"/> null, <paramref name="refusal"/> its reason).</summary>
    public static OversightWords Of(OversightRead? read, string? refusal, string currency)
    {
        var status = read is not null
            ? $"The line is running: it sweeps the organisation's own records every {Seconds(OversightLine.Cadence)} and writes only to its own file."
            : refusal is { Length: > 0 }
                ? $"The line is not running, and nothing below is current: {refusal}"
                : "The line is not open yet.";

        var findings = read is null
            ? []
            : read.Findings
                .OrderBy(f => f.UnitPath, StringComparer.Ordinal).ThenByDescending(f => f.Seq)
                .Select(f => new OversightFindingWords(
                    f.Id, f.Severity, f.Detector, f.UnitPath, When(f.At),
                    string.Join(", ", f.Measured.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}={m.Value}")),
                    string.Join(", ", f.Cites.Select(c => c.ToString())),
                    f.Blind,
                    read.Acknowledged.TryGetValue(f.Id, out var at) ? $"acknowledged {When(at)}" : null))
                .ToList();

        var digest = read?.Digest is { } d
            ? d.Text
            : "No digest yet — one is written only when a finding newer than the last digest exists.";

        var cap = read?.Cap is { } c
            ? $"Oversight cap: {Labels.Money(c.Amount, c.Currency)}, set {When(c.SetAt)}."
            : $"Oversight cap: none set, which is {Labels.Money(0m, currency)}.";
        cap += " Today there is no model layer — code checks only; this line spends nothing today. The cap sits BESIDE the organisation's daily limit, "
               + "never inside it, and is read by nothing but this page until the line has a model layer.";

        return new OversightWords(
            status,
            digest,
            findings,
            read is null ? [] : [.. read.Failures.Select(x => $"{When(x.At)} — {x.What}")],
            SeatClassRows,
            "Advisory until containment: a CLI seat runs as your own user and could read the app's files, and the public repository names this line.",
            [
                ("Sweep cadence", Seconds(OversightLine.Cadence)),
                ("spend-no-accept: window", Days(OversightPolicy.SpendWindow)),
                ("spend-no-accept: floor", OversightPolicy.SpendFloor.ToString(CultureInfo.InvariantCulture)),
                ("stall: no accepted work for", Days(OversightPolicy.StallAfter)),
                ("owner-words-late: deadline", Hours(OversightPolicy.OwnerWordsDeadline)),
                ("material-unwoken: longest interval", Hours(OversightPolicy.MaterialMaxInterval)),
                ("claim-without-evidence: grace", Hours(OversightPolicy.ClaimGrace)),
                ("Most records one finding cites", OversightPolicy.MaxCites.ToString(CultureInfo.InvariantCulture)),
            ],
            cap,
            $"A severe finding shows in the window's chrome within one sweep: up to {Seconds(OversightLine.Cadence)}, plus the "
            + $"{Seconds(Refresh)} refresh. Never a sound, a pop-up or a system notification.");
    }

    /// <summary>ORCHESTRATION § 5's invisibility row: how far the line is hidden from each seat class.</summary>
    static readonly IReadOnlyList<(string Seat, string Reach, string How)> SeatClassRows =
    [
        ("Legacy pair, CLI, unconfined", "advisory", "runs as your own user; the line's file is readable there"),
        ("Thinking seat, API harness", "hard on app paths", "no tool of the harness names the line or its file"),
        ("Thinking seat, broker-only", "mediated", "reaches only what the broker serves, and the broker serves nothing of the line"),
        ("Doing seat, confined cell", "hard", "state/ is refused to every cell grant"),
    ];

    /// <summary>An instant as the page prints it: UTC to the second.</summary>
    internal static string When(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s";
    static string Hours(TimeSpan t) => $"{t.TotalHours.ToString(CultureInfo.InvariantCulture)} h";
    static string Days(TimeSpan t) => $"{t.TotalDays.ToString(CultureInfo.InvariantCulture)} days";
}

/// <summary>
/// THE OWNER'S OVERSIGHT PAGE (ORGANISATION § 9; ORCHESTRATION § 2.15 "The watcher's page is his alone"). It reads the owner's file only,
/// through the line's own store; its nav entry is the owner's word and is never shown to an agent. It has no authority over the organisation
/// and no press here removes anything: the line reports, the owner acts.
///
/// <para><b>Built once, updated in place</b>, like every page here: each finding's card is made once and kept by its id; the list is re-laid
/// only when a finding arrives or its acknowledgement changes, and nothing at all is touched when nothing moved.</para>
/// </summary>
sealed class OversightPage
{
    readonly OversightSources _src;

    readonly TextBlock _status = Ui.Muted("");
    readonly TextBlock _failures = Ui.Body("");
    bool _capRead;
    readonly Dictionary<string, (Control Card, TextBlock Ack)> _cards = new(StringComparer.Ordinal);
    string _signature = "";

    public Control Root { get; }

    /// <summary>The findings' cards, in the order shown.</summary>
    internal StackPanel FindingCards { get; } = new() { Spacing = Theme.S3 };

    /// <summary>The latest digest, as written.</summary>
    internal TextBlock DigestText { get; } = Ui.Body("");

    /// <summary>What the page says of the cap.</summary>
    internal TextBlock CapText { get; } = Ui.Body("");

    /// <summary>
    /// The figure the owner types, and the press that saves it (<see cref="BuildSaveCap"/>): the one money box (<c>U-number-entry-culture</c>,
    /// fix pass 2) — read culture-free with its reading line, refused or empty never pressed through, no wheel. It opens on the cap in force,
    /// or EMPTY while the owner's file has not been read — never on a 0 nobody chose — and is opened on the cap at the first read only while
    /// nothing has been typed in it (<see cref="AmountField.OpenOn"/>).
    /// </summary>
    internal AmountField CapBox { get; }

    internal Button CapSave { get; }

    /// <summary>The press's note: a refusal, or what was saved, with the exact figure.</summary>
    internal TextBlock CapNote { get; } = Ui.Micro("");

    /// <summary>The cap box's label, as the page and a refusal say it.</summary>
    internal const string CapLabel = "The most your private line may spend in a day";

    public OversightPage(OversightSources sources)
    {
        _src = sources;
        DigestText.FontSize = Theme.Small;
        _failures.FontSize = Theme.Small;
        CapText.FontSize = Theme.Small;
        CapNote.IsVisible = false;
        decimal? opened = null;
        try { opened = _src.Store() is { } open ? open.Cap()?.Amount ?? 0m : null; }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        _capRead = opened is not null;
        CapBox = Ui.AmountField(opened, AmountKind.Decimal);
        CapSave = BuildSaveCap(CapBox, CurrentCap, _src.Currency, SaveCap, why => Ui.Refuse(CapNote, why));

        var empty = OversightWords.Of(null, null, "");
        var seats = Ui.Col(Theme.S2, [.. empty.SeatClasses.Select(s => Ui.KeyValue(s.Seat, $"{s.Reach} — {s.How}"))]);
        seats.Children.Add(Ui.With(Ui.Muted(empty.SeatNote), t => t.FontSize = Theme.Small));
        var thresholds = Ui.Col(Theme.S2, [.. empty.Thresholds.Select(t => Ui.KeyValue(t.Name, t.Value))]);

        var body = Ui.Col(Theme.S6,
            Ui.Col(Theme.S2, _status, Ui.With(Ui.Muted(empty.Cadence), t => t.FontSize = Theme.Small)),
            Ui.Section("Latest digest", DigestText),
            Ui.Col(Theme.S2, Ui.Eyebrow("Findings, by unit, newest first"), FindingCards),
            Ui.Section("The line's own failures", _failures),
            Ui.Section("How hidden it is, by seat", seats),
            Ui.Section("Thresholds", thresholds),
            Ui.Section("Oversight cap", Ui.Col(Theme.S3, CapText,
                Ui.FieldRow(CapLabel, CapBox), CapSave, CapNote)));

        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Oversight",
            "Your private line over the organisation: code checks over its own records, shown to you alone. It reports facts and "
            + "recommends nothing; it cannot instruct, pause or block anything."));
        root.Children.Add(scroll);
        Root = root;
    }

    /// <summary>In place on every tick, touching nothing when nothing moved.</summary>
    public void Update()
    {
        OversightRead? read;
        try { read = _src.Store() is { } store ? OversightRead.From(store) : null; }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; } // the quit closed the file under this tick
        var words = OversightWords.Of(read, _src.Refusal(), _src.Currency());

        Set(_status, words.Status);
        Set(DigestText, words.Digest);
        Set(_failures, words.Failures.Count == 0 ? "None." : string.Join("\n", words.Failures));
        Set(CapText, words.Cap);
        if (!_capRead && read is not null)
        {
            _capRead = true;
            CapBox.OpenOn(read.Cap?.Amount ?? 0m);
        }

        var signature = string.Join('|', words.Findings.Select(f => f.Id + "#" + f.Acknowledged));
        if (signature == _signature) return;
        _signature = signature;

        FindingCards.Children.Clear();
        if (words.Findings.Count == 0) FindingCards.Children.Add(Ui.Muted("No findings."));
        foreach (var f in words.Findings)
        {
            if (!_cards.TryGetValue(f.Id, out var card)) _cards[f.Id] = card = Card(f);
            Set(card.Ack, f.Acknowledged ?? (f.Severity == OversightSeverity.Severe ? "not acknowledged" : ""));
            FindingCards.Children.Add(card.Card);
        }
    }

    // ---- the cap ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE OVERSIGHT CAP'S PRESS: RAISING IT ASKS TWICE AND NAMES THE FIGURE; LOWERING IT, OR ZERO, SAVES IN ONE PRESS — the perception
    /// budget's rule (<see cref="PerceptionCard.BuildSaveBudget"/>). A factory, so a test presses the control the owner sees.
    /// </summary>
    internal static Button BuildSaveCap(Func<decimal> current, Func<decimal> pending, Func<string> currency, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SaveOversightCap,
            () => pending() > current() ? Labels.RaiseOversightCapArmed(Amount.ExactMoney(pending(), currency())) : null,
            save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE PRESS OVER ITS OWN BOX: a confirmation completes only the figure it named, so editing the box disarms a raise armed for another
    /// figure, and the new one asks twice of its own — the daily limit's rule (<see cref="SafetyPage.BuildSaveDailyCap(AmountField, Func{decimal}, Func{string}, Action, Action{string}?)"/>).
    /// A BOX THAT READS AS NOTHING REFUSES EITHER PRESS (<c>U-number-entry-culture</c>, fix pass 2): it never saves 0 or the cap in force.
    /// </summary>
    internal static Button BuildSaveCap(AmountField box, Func<decimal> current, Func<string> currency, Action save, Action<string>? refuse = null)
    {
        var b = Ui.ConfirmIf(Labels.SaveOversightCap,
            () => box.Figure > current() ? Labels.RaiseOversightCapArmed(Amount.ExactMoney(box.Figure, currency())) : null,
            save, () => Ui.Refusal((CapLabel, box)), refuse ?? (_ => { }), "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        box.ValueChanged += (_, _) => Ui.DisarmConfirm(b);
        return b;
    }

    decimal CurrentCap() => _src.Store()?.Cap()?.Amount ?? 0m;

    /// <summary>Appends the owner's figure to his own file — never the activity log, which the organisation's reports can carry.</summary>
    void SaveCap()
    {
        if (_src.Store() is not { } store) return;
        var cap = CapBox.Figure;
        store.SetCap(cap, _src.Currency(), _src.Now());
        Ui.Said(CapNote, $"Saved. Your private line may spend up to {Amount.ExactMoney(cap, _src.Currency())} a day.");
        Update();
    }

    /// <summary>One finding's card, made once.</summary>
    static (Control Card, TextBlock Ack) Card(OversightFindingWords f)
    {
        var severe = f.Severity == OversightSeverity.Severe;
        var ack = Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
        var head = Ui.Row(Theme.S2,
            Ui.Pill(f.Severity, severe ? Theme.Danger : Theme.Neutral),
            Ui.With(Ui.Body(f.UnitPath), t => t.FontWeight = FontWeight.SemiBold),
            Ui.Muted(f.Detector),
            Ui.With(Ui.Muted(f.At), t => t.FontSize = Theme.Small));
        head.VerticalAlignment = VerticalAlignment.Center;
        return (Ui.Card(Ui.Col(Theme.S2,
            head,
            Ui.With(Ui.Body(f.Measured), t => t.FontSize = Theme.Small),
            Ui.With(Ui.Mono(f.Cites + "  [" + f.Id + "]", Theme.TextMuted), t => t.TextWrapping = TextWrapping.Wrap),
            Ui.With(Ui.Muted(f.Blind), t => t.FontSize = Theme.Small),
            ack)), ack);
    }

    static void Set(TextBlock t, string text)
    {
        if (t.Text != text) t.Text = text;
    }
}

/// <summary>
/// THE IMMEDIATE IN-APP ALERT FOR A SEVERE FINDING (ORGANISATION § 9 "an immediate in-app alert for a severe detector"): a strip in the
/// window's chrome, in its own row AFTER the approval banner — money first — shown while an unacknowledged severe finding is in the owner's
/// file. Its sentence is the digest's own (<see cref="DigestComposer.Sentence"/>), cited. "Open" jumps to the Oversight page; "Acknowledge"
/// appends a row for each finding the banner COUNTED when it was drawn — one that arrived since is not acknowledged unseen — and removes
/// nothing. Never a toast, a sound or a system notification. Within one sweep (60 s) plus the window's 5 s refresh, as the page says.
/// </summary>
sealed class OversightBanner
{
    readonly OversightSources _src;
    IReadOnlyList<string> _counted = [];
    string _shown = "";

    public Border Root { get; } = new()
    {
        Background = Theme.DangerSoft,
        BorderBrush = Theme.Danger,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(Theme.S5, Theme.S3),
        IsVisible = false
    };

    internal TextBlock Text { get; } = new()
    {
        FontSize = Theme.Small, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };

    internal Button Open { get; }

    internal Button Acknowledge { get; }

    public OversightBanner(OversightSources sources, Action open)
    {
        _src = sources;
        Open = Ui.Secondary("Open", open);
        Acknowledge = Ui.Ghost("Acknowledge", AcknowledgeCounted);
        var actions = Ui.Row(Theme.S2, Open, Acknowledge);
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions[Grid.ColumnProperty] = 2;

        var dot = Ui.With(Ui.Dot(Theme.Danger), d => d.Margin = new Thickness(0, 0, Theme.S3, 0));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(Ui.With(Text, t => t[Grid.ColumnProperty] = 1));
        grid.Children.Add(actions);
        Root.Child = grid;
    }

    /// <summary>In place on every tick: the newest unacknowledged severe finding, or nothing.</summary>
    public void Update()
    {
        List<OversightFinding> waiting;
        try
        {
            if (_src.Store() is not { } store) { Hide(); return; }
            var acknowledged = store.Acknowledged();
            waiting = [.. store.Findings(OversightStore.RootScope)
                .Where(f => f.Severity == OversightSeverity.Severe && !acknowledged.ContainsKey(f.Id))
                .OrderByDescending(f => f.Seq)];
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; } // the quit closed the file under this tick

        if (waiting.Count == 0) { Hide(); return; }
        _counted = [.. waiting.Select(f => f.Id)];
        var newest = waiting[0];
        var shown = newest.Id + "#" + waiting.Count;
        if (shown != _shown)
        {
            _shown = shown;
            Text.Text = $"Severe finding{(waiting.Count == 1 ? "" : $" ({waiting.Count} unacknowledged, newest first)")}: {Sentence(newest)}";
        }
        Root.IsVisible = true;
    }

    /// <summary>The digest's sentence for <paramref name="f"/>, its cites checked against the app's database where there is one.</summary>
    string Sentence(OversightFinding f)
    {
        if (_src.DatabaseFile() is not { } file || !File.Exists(file)) return DigestComposer.Sentence(f, _ => true);
        try
        {
            using var records = new OrgRecords(file);
            return DigestComposer.Sentence(f, records.Resolves);
        }
        catch (Exception) { return DigestComposer.Sentence(f, _ => true); }
    }

    void AcknowledgeCounted()
    {
        if (_src.Store() is not { } store) return;
        var at = _src.Now();
        var acknowledged = store.Acknowledged();
        foreach (var id in _counted.Where(id => !acknowledged.ContainsKey(id))) store.Acknowledge(id, at);
        Hide();
    }

    void Hide()
    {
        _counted = [];
        _shown = "";
        Root.IsVisible = false;
    }
}
