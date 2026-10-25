using Avalonia.Controls;
using Avalonia.Layout;
using TradeAgent.Core;
using TradeAgent.Core.Board;

namespace TradeAgent.App;

/// <summary>
/// THE SECURITY BOARD, READ (<c>U-settings-shed</c> item 1; R24 § 4.7): every decision in force — a founding value, the app's, or a board
/// act — with the version that set it, who decided it, on what basis and since when; every version on demand, each one's reasons shown as
/// the deciders' claim; and the board's seats.
///
/// <para><b>It reads and nothing else.</b> The page is handed a <see cref="BoardReader"/> and nothing that writes: no gateway, no host,
/// no store. Its one press opens the version history, which reads too. The board's acts are its own verbs (<c>U-board-b</c>), never a
/// press on this page, and the owner's veto is his own "For you" page's (R24 § 4.3; <c>U-org-surface-b</c>).</para>
///
/// <para><b>Every act whole</b> (<c>U-org-surface-b</c> item 3; R24 § 4.7: each decision "with its history, rationale, deciders,
/// evidence class and score"): its status, intent, every event with its seat, vendor families and whether they were one, every
/// rationale as its seat's claim, the evidence class ("none yet" for a null — never inferred, EVID-001), its forecasts with the app's
/// resolutions, and the audit score that is not this build's. And the ventures, named empty until <c>U-ventures-a</c> (R24 § 4.6).</para>
///
/// <para><b>Built once and updated in place.</b> One row per field the page has words for (<see cref="Labels.BoardFields"/>), built at
/// construction; <see cref="Update"/> writes their text and shows or hides them. The history is built when the owner opens it, never on
/// the five-second tick. The seats list is rebuilt only when the set of seats changes shape.</para>
/// </summary>
sealed class BoardPage
{
    readonly BoardReader _board;

    sealed record Row(Control Root, TextBlock Value, TextBlock Source);

    readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    readonly Dictionary<string, Control> _groups = new(StringComparer.Ordinal);

    readonly TextBlock _version = Ui.Body("");
    readonly TextBlock _decider = Ui.Body("");
    readonly TextBlock _since = Ui.Body("");
    readonly TextBlock _hash = Ui.Mono("");
    readonly TextBlock _unnamed = Ui.With(Ui.Micro(""), t => t.Foreground = Theme.Caution);
    readonly Border _unreadableCard;
    readonly TextBlock _unreadable = Ui.Body("", Theme.Caution);

    readonly StackPanel _seats = new() { Spacing = Theme.S2 };
    readonly TextBlock _seatsDormant = Ui.Muted(Labels.BoardSeatsDormant);
    string _seatSignature = "";

    readonly StackPanel _history = new() { Spacing = Theme.S3, IsVisible = false };
    readonly Button _showHistory;

    /// <summary>The board's acts, newest first, one card per act made once and refilled (<see cref="LiveList{T}"/>).</summary>
    internal readonly StackPanel ActCards = new() { Spacing = Theme.S5 };
    internal readonly LiveList<BoardActRecord> Acts;
    readonly TextBlock _actsUnreadable = Ui.With(Ui.Body("", Theme.Caution), t => t.IsVisible = false);

    public Control Root { get; }

    public BoardPage(BoardReader board)
    {
        _board = board;

        _unreadableCard = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Avalonia.Thickness(Theme.S5),
            IsVisible = false,
            Child = _unreadable
        };

        var inForce = Ui.Section("The policy in force", Ui.Col(Theme.S2,
            Ui.KeyValueLive("Version", _version),
            Ui.KeyValueLive("Decided by", _decider),
            Ui.KeyValueLive("Since", _since),
            Ui.KeyValueLive("Its fingerprint", _hash),
            Ui.Divider(),
            Ui.Muted("These are the limits and allowances TradeAgent enforces. The security board decides them; until a board seat is "
                     + "active, the app's founding policy does. Nothing on this page changes them, and the AI cannot either.")));

        var left = Ui.Col(Theme.S6, _unreadableCard, inForce);
        foreach (var group in Labels.BoardFields.Select(f => f.Group).Distinct())
        {
            var rows = Ui.Col(0);
            foreach (var words in Labels.BoardFields.Where(f => f.Group == group))
            {
                var value = Ui.With(Ui.Body(""), t => { t.FontWeight = Avalonia.Media.FontWeight.SemiBold; t.TextWrapping = Avalonia.Media.TextWrapping.Wrap; });
                var source = Ui.Micro("");
                var row = Ui.FieldRow(words.Label, value, source);
                row.IsVisible = false;
                _rows[words.Path] = new Row(row, value, source);
                rows.Children.Add(row);
            }
            var section = Ui.Section(group, rows);
            _groups[group] = section;
            left.Children.Add(section);
        }
        _unnamed.IsVisible = false;
        left.Children.Add(_unnamed);

        _showHistory = Ui.Ghost(Labels.ShowBoardHistory, ToggleHistory);
        _showHistory.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(Ui.Section("Every version", Ui.Col(Theme.S3,
            Ui.Muted("Each version of the policy, oldest first: who decided it, on what basis, their reasons as they gave them, the "
                     + "evidence class of anything it allocated, and the audit line's score."),
            _showHistory,
            _history)));

        Acts = new LiveList<BoardActRecord>(ActCards, Ui.Muted(Labels.BoardActsNone), a => a.Act.Id, ActCard);
        left.Children.Add(Ui.Section(Labels.BoardActs, Ui.Col(Theme.S3, _actsUnreadable, ActCards)));
        // VENTURES (R24 § 4.6): a venture is a mandate the organisation proposes and the board approves — none exists in this build
        // (U-ventures-a), so the section says so in words rather than drawing an empty frame.
        left.Children.Add(Ui.Section(Labels.Ventures, Ui.Muted(Labels.VenturesNone)));

        var seats = Ui.Section("The board's seats", Ui.Col(Theme.S3, _seats, Ui.Divider(), _seatsDormant));
        seats.Margin = new Avalonia.Thickness(Theme.S5, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, left));
        grid.Children.Add(Pages.Column(1, seats));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header(Labels.BoardPage,
                "The security board's decisions in force — every limit and allowance TradeAgent enforces, who decided it and since when."),
            grid));

        Update();
    }

    /// <summary>The page's one press: open or close the version history. It reads the history when it opens; it writes nothing.</summary>
    void ToggleHistory()
    {
        if (_history.IsVisible)
        {
            _history.IsVisible = false;
            _showHistory.Content = Labels.ShowBoardHistory;
            return;
        }
        BuildHistory();
        _history.IsVisible = true;
        _showHistory.Content = Labels.HideBoardHistory;
    }

    /// <summary>Built on the press that opens it — never on the tick.</summary>
    void BuildHistory()
    {
        _history.Children.Clear();
        IReadOnlyList<BoardPolicyVersion> versions;
        try { versions = _board.History(); }
        catch (Exception ex)
        {
            _history.Children.Add(Ui.Body($"The versions could not be read: {ex.Message}", Theme.Caution));
            return;
        }

        if (versions.All(v => v.Basis == BoardBasis.Founding))
            _history.Children.Add(Ui.Muted(Labels.BoardHistoryFoundingOnly));

        foreach (var v in versions)
        {
            _history.Children.Add(Ui.Col(2,
                Ui.With(Ui.Body($"Version {v.Version} — {v.Basis}, decided by {Labels.BoardDecider(v.Decider)}"
                                + (Since(v.WrittenAt) is { } at ? $", {at}" : "")),
                    t => t.FontWeight = Avalonia.Media.FontWeight.SemiBold),
                Ui.Micro(Labels.BoardRationale(v.Rationale)),
                Ui.Micro("Deciders: " + (v.Deciders.Count == 0 ? "none" : string.Join(", ", v.Deciders.Select(Labels.BoardDecider)))),
                Ui.Micro(Labels.BoardEvidenceClass(v.EvidenceClass)),
                Ui.Micro(Labels.BoardScore(v.Score)),
                Ui.Micro($"Fingerprint {Short(v.BodySha256)}")));
        }
    }

    /// <summary>The five-second pass: every value written into the row built for it, nothing rebuilt.</summary>
    public void Update()
    {
        UpdateActs();
        BoardPolicyInForce inForce;
        IReadOnlyList<BoardPolicyVersion> history;
        try
        {
            inForce = _board.InForce();
            history = _board.History();
        }
        catch (Exception ex)
        {
            ShowUnreadable(ex.Message);
            return;
        }

        if (inForce.CouldNotBeRead)
        {
            ShowUnreadable(inForce.Why);
            return;
        }
        _unreadableCard.IsVisible = false;

        _version.Text = inForce.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (inForce.Basis == BoardBasis.Founding ? " — the founding policy" : "");
        _decider.Text = Labels.BoardDecider(inForce.Decider);
        _since.Text = Since(inForce.WrittenAt) ?? "the app's founding of this home";
        _hash.Text = Short(inForce.BodySha256);

        var founding = history.Where(v => v.Basis == BoardBasis.Founding).Select(v => v.Version).ToHashSet();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in inForce.Fields)
        {
            if (!_rows.TryGetValue(field.Name, out var row)) continue;
            var words = Labels.BoardField(field.Name)!;
            row.Value.Text = ValueWords(words, field.Value);
            var setBy = history.FirstOrDefault(v => v.Version == field.SetByVersion);
            row.Source.Text = Labels.BoardFieldSource(founding.Contains(field.SetByVersion), field.SetByVersion,
                setBy is null ? null : Since(setBy.WrittenAt));
            row.Root.IsVisible = true;
            shown.Add(field.Name);
        }
        foreach (var (path, row) in _rows)
            if (!shown.Contains(path)) row.Root.IsVisible = false;

        // A FIELD WITH NO WORDS IS NAMED, NEVER SHOWN AS DECIDED (refuse, never guess).
        var unnamed = inForce.Fields.Select(f => f.Name).Where(n => !_rows.ContainsKey(n)).ToList();
        _unnamed.IsVisible = unnamed.Count > 0;
        _unnamed.Text = unnamed.Count > 0 ? Labels.BoardFieldUnnamed(unnamed) : "";

        UpdateSeats();
    }

    void UpdateActs()
    {
        try
        {
            Acts.Update(_board.Acts());
            _actsUnreadable.IsVisible = false;
        }
        catch (Exception ex)
        {
            _actsUnreadable.Text = $"The board's acts could not be read: {ex.Message}";
            _actsUnreadable.IsVisible = true;
        }
    }

    /// <summary>
    /// ONE ACT'S CARD, made once and refilled: every line is the app's record but the rationales and the forecasts' claims, which are
    /// their seats' words in quotation marks (<see cref="OrgText.Claim"/>) — a claim and a measurement are never one sentence.
    /// </summary>
    static (Control, Action<BoardActRecord>) ActCard(BoardActRecord first)
    {
        var title = Ui.With(Ui.Body(""), t => t.FontWeight = Avalonia.Media.FontWeight.SemiBold);
        var status = Ui.Body("");
        var intent = Ui.With(Ui.Mono(""), t => t.TextWrapping = Avalonia.Media.TextWrapping.Wrap);
        var events = Ui.Micro("");
        var rationales = Ui.Body("");
        var evidence = Ui.Micro("");
        var forecasts = Ui.Micro("");
        var score = Ui.Micro(Labels.BoardActAuditScore);
        void Fill(BoardActRecord a)
        {
            var row = a.Act;
            OrgText.Set(title, Labels.BoardActTitle(row.Id, row.Kind, row.Seat, OrgText.When(row.At)));
            OrgText.Set(status, Labels.BoardActStatus(a.Status));
            OrgText.Set(intent, $"Bound to its exact intent (fingerprint {Short(row.IntentSha256)}): {row.Intent}");
            OrgText.Set(events, string.Join("\n", a.Events.Select(e => Labels.BoardActEventLine(e.Kind, e.Seat, OrgText.When(e.At),
                e.ProposerFamily, e.DeciderFamily, e.SameFamily, e.PolicyVersion, e.Detail))));
            OrgText.Set(rationales, string.Join("\n", a.Events.Where(e => e.Rationale is { Length: > 0 })
                .Select(e => OrgText.Claim(e.Seat, e.Rationale!)).Prepend(OrgText.Claim(row.Seat, row.Rationale))));
            OrgText.Set(evidence, Labels.BoardActEvidenceClass(row.EvidenceClass));
            OrgText.Set(forecasts, a.Forecasts.Count == 0
                ? "No forecast."
                : string.Join("\n", a.Forecasts.Select(f =>
                {
                    var resolved = a.Resolutions.FirstOrDefault(r => r.ForecastId == f.Id);
                    return OrgText.Claim(f.Forecaster, $"{f.Predicate} of {f.Subject}: {f.Target} by {OrgText.When(f.ResolveBy)}, "
                                                       + $"probability {f.Probability.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                           + "\n" + Labels.BoardForecastResolution(resolved?.Outcome, resolved?.Why,
                               resolved is null ? null : OrgText.When(resolved.At), f.Scored, f.UnscoredWhy);
                })));
        }
        return (Ui.Col(Theme.S1, title, status, intent, events, rationales, evidence, forecasts, score), Fill);
    }

    void ShowUnreadable(string? why)
    {
        _unreadable.Text = Labels.BoardPolicyUnreadable(why);
        _unreadableCard.IsVisible = true;
        _version.Text = _decider.Text = _since.Text = _hash.Text = "—";
        foreach (var row in _rows.Values) row.Root.IsVisible = false;
        _unnamed.IsVisible = false;
        UpdateSeats();
    }

    void UpdateSeats()
    {
        IReadOnlyList<BoardSeat> seats;
        try { seats = _board.Seats(); }
        catch (Exception ex)
        {
            _seatSignature = "";
            _seats.Children.Clear();
            _seats.Children.Add(Ui.Body($"The seats could not be read: {ex.Message}", Theme.Caution));
            return;
        }

        var signature = string.Join('|', seats.Select(s => $"{s.Id}/{s.SeatClass}/{s.ModelClass}/{s.VendorFamily}/{s.Status}"));
        _seatsDormant.IsVisible = seats.All(s => s.Status != BoardSeatStatus.Active);
        if (signature == _seatSignature) return;
        _seatSignature = signature;

        _seats.Children.Clear();
        foreach (var s in seats)
            _seats.Children.Add(Ui.Col(2,
                Ui.With(Ui.Body(s.Id), t => t.FontWeight = Avalonia.Media.FontWeight.SemiBold),
                Ui.Micro($"{Labels.BoardSeatClass(s.SeatClass)}; a {s.ModelClass} model from {s.VendorFamily}"),
                Ui.Micro(Labels.BoardSeatStatus(s.Status))));
    }

    /// <summary>
    /// A FIELD'S VALUE IN WORDS — the figure exactly as the policy holds it, with what the app's own rules say that figure means
    /// where a figure alone would mislead (a 0 that turns a limit off, an empty list that allows nothing).
    /// </summary>
    internal static string ValueWords(Labels.BoardFieldWords words, string value) => words.Path switch
    {
        "mode" => Enum.TryParse<TradingMode>(value, ignoreCase: false, out var m) && Enum.IsDefined(m) ? Ui.ModeLabel(m) : value,
        "live_activated" => value == "true" ? "on" : value == "false" ? "off — no real money" : value,
        "risk.max_notional_per_order" or "risk.max_loss_per_trade" or "risk.max_daily_loss" =>
            value == "0" ? "0 — not enforced" : value,
        "risk.instrument_allowlist" => value == "none" ? "none — " + Labels.NoInstrumentAllowedHere : value,
        "plan_day_total" => $"{value} a day, counted at list price — not money",
        "role_shares" => value == "none" ? "none — each role an equal share" : value,
        "paper_envelope" => value == "none" ? "none — TradeAgent starts no paper experiment of its own" : value,
        "paper_envelope.days" => value == "none" ? "no end date" : $"{value} days",
        _ => words.Unit is { } unit ? $"{value} {unit}" : value
    };

    /// <summary>A time in the owner's clock, or null for a record that carries none.</summary>
    static string? Since(DateTimeOffset at) =>
        at == default ? null : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
