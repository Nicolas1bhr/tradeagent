using Avalonia.Controls;
using Avalonia.Layout;
using TradeAgent.Core;
using TradeAgent.Core.Audit;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE OWNER'S AUDIT-LINE SECTION IS HANDED (<c>U-org-audit-b</c>) — the line's file and refusal and the two ceilings its budget is
/// carved off, as delegates, so the section the owner sees and the section a test presses are the same control. Built from
/// <see cref="AppHost"/>'s App-internal accessors; nothing here reaches the agent-facing pipe, a verb or the board.
/// </summary>
sealed record AuditSources
{
    /// <summary>The line's own file (<see cref="AppHost.AuditStore"/>), or null while the line is not open.</summary>
    public required Func<AuditStore?> Store { get; init; }

    /// <summary>Why the line is not running, or null.</summary>
    public required Func<string?> Refusal { get; init; }

    /// <summary>The currency the AI is charged in.</summary>
    public required Func<string> Currency { get; init; }

    /// <summary>The owner's real-money daily ceiling, as he set it.</summary>
    public required Func<decimal> MoneyCeiling { get; init; }

    /// <summary>The plan's day-total in force, as the security board set it.</summary>
    public required Func<decimal> PlanCeiling { get; init; }

    /// <summary>The clock a budget row is stamped with.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The sources over the running app.</summary>
    public static AuditSources For(AppHost host) => new()
    {
        Store = () => host.AuditStore,
        Refusal = () => host.AuditRefusal,
        Currency = () => host.SpendToday.Currency,
        MoneyCeiling = () => host.Gateway.Settings.AiDailyCostCap,
        PlanCeiling = () => host.Gateway.Settings.CouldNotBeRead ? 0m : host.Gateway.Policy.PlanDayTotal,
    };
}

/// <summary>
/// THE SECTION'S SENTENCES, apart from its controls so they are tested without a window. Every figure is read from the line's own file: the
/// owner's budgets and the catch rates its canaries MEASURED — never a claim, and nothing any agent wrote.
/// </summary>
sealed record AuditWords(string Status, string Money, string Plan, string Canaries, IReadOnlyList<string> Catches)
{
    public static AuditWords Of(AuditStore? store, string? refusal, string currency, decimal moneyCeiling, decimal planCeiling)
    {
        if (store is null)
        {
            var why = refusal is null ? "it is not open yet" : refusal;
            return new($"The audit line is not running: {why}. While its budget cannot be read, the AI is admitted no new turn.",
                "", "", "No canary run can be read.", []);
        }
        var money = store.Budget(BillingClasses.Key);
        var plan = store.Budget(BillingClasses.Plan);
        string Line(decimal budget, decimal ceiling, string ofWhat, string whose) => budget <= 0m
            ? $"Nothing of {ofWhat} is kept for the audit line: the organisation may use all of {whose} {Amount.ExactMoney(ceiling, currency)} a day."
            : $"{Amount.ExactMoney(budget, currency)} a day of {ofWhat} is kept for the audit line, off the top: the organisation may use "
              + $"{Amount.ExactMoney(AuditBudget.Off(ceiling, budget), currency)} of {whose} {Amount.ExactMoney(ceiling, currency)}.";

        var last = store.LastCanaryRun();
        var canaries = last is null
            ? "No canary run yet. The line runs its canaries a minute after the app starts, then once a day."
            : last.Failure is { } failed
                ? $"The canary run at {When(last.At)} recorded no rate: {failed}"
                : $"Canary run at {When(last.At)}: the line planted known-bad deliveries in a scratch proving home of its own and measured "
                  + "which of its detectors caught them.";
        var catches = last is null || last.Failure is not null
            ? []
            : last.Catches.Select(k => (k.Missed > 0
                    ? $"{k.Detector}: MISSED {k.Missed} of {k.Planted}"
                    : $"{k.Detector}: caught {k.Caught} of {k.Planted}")
                + (k.FalseAlarms > 0 ? $"; {k.FalseAlarms} false alarm(s) on {k.Controls} clean controls" : $"; no false alarm on {k.Controls} clean controls"))
                .ToList();
        return new("The audit line is running: code checks over sampled deliveries every minute, and its canaries once a day. It spends "
                   + "nothing today — every check is code — and it can neither instruct nor block anything.",
            Line(money, moneyCeiling, "real money", "your"), Line(plan, planCeiling, "plan capacity", "the board's"), canaries, catches);
    }

    static string When(DateTimeOffset t) => OversightWords.When(t);
}

/// <summary>
/// THE OWNER'S AUDIT-LINE SECTION ON HIS OWNER PAGE (<c>U-org-audit-b</c>): the line's budget in each billing class — his alone to set, carved
/// off the top of that class's ceiling, two presses to raise and one to lower — and the catch rates its canaries measured. Built once,
/// updated in place.
/// </summary>
sealed class AuditSection
{
    readonly AuditSources _src;
    bool _opened;

    public Control Root { get; }

    internal TextBlock Status { get; } = Ui.Muted("");
    internal TextBlock MoneyText { get; } = Ui.Body("");
    internal TextBlock PlanText { get; } = Ui.Body("");
    internal TextBlock CanaryText { get; } = Ui.Body("");
    internal TextBlock CatchText { get; } = Ui.Body("");

    internal AmountField MoneyBox { get; }
    internal AmountField PlanBox { get; }
    internal Button MoneySave { get; }
    internal Button PlanSave { get; }
    internal TextBlock Note { get; } = Ui.Micro("");

    internal const string MoneyLabel = "Real money kept for the audit line each day";
    internal const string PlanLabel = "Plan capacity kept for the audit line each day";

    public AuditSection(AuditSources sources)
    {
        _src = sources;
        Note.IsVisible = false;
        var store = Store();
        _opened = store is not null;
        MoneyBox = Ui.AmountField(store?.Budget(BillingClasses.Key), AmountKind.Decimal);
        PlanBox = Ui.AmountField(store?.Budget(BillingClasses.Plan), AmountKind.Decimal);
        MoneySave = BuildSaveBudget(Labels.SaveAuditMoneyBudget, MoneyLabel, MoneyBox, () => Current(BillingClasses.Key), "real money",
            _src.Currency, () => Save(BillingClasses.Key, MoneyBox), why => Ui.Refuse(Note, why));
        PlanSave = BuildSaveBudget(Labels.SaveAuditPlanBudget, PlanLabel, PlanBox, () => Current(BillingClasses.Plan), "plan capacity",
            _src.Currency, () => Save(BillingClasses.Plan, PlanBox), why => Ui.Refuse(Note, why));

        Root = Ui.Section("The audit line", Ui.Col(Theme.S2,
            Status,
            Ui.Muted("The public audit line is app-owned and outside the organisation: no position, the chief or the security board can fund, "
                + "charter or re-seat it. What you keep for it here comes off the top of each day's ceiling."),
            Ui.Spacer(Theme.S2),
            MoneyText,
            Ui.FieldRow(MoneyLabel, MoneyBox, "Raising it asks again first; lowering it, or 0, takes one press."),
            MoneySave,
            Ui.Spacer(Theme.S2),
            PlanText,
            Ui.FieldRow(PlanLabel, PlanBox),
            PlanSave,
            Note,
            Ui.Spacer(Theme.S3),
            Ui.Label("What its canaries measured"),
            CanaryText,
            CatchText));
        Update();
    }

    AuditStore? Store()
    {
        try { return _src.Store(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return null; }
    }

    decimal Current(string billingClass) => Store()?.Budget(billingClass) ?? 0m;

    /// <summary>In place on every tick, touching nothing when nothing moved.</summary>
    public void Update()
    {
        AuditWords words;
        var store = Store();
        try { words = AuditWords.Of(store, _src.Refusal(), _src.Currency(), _src.MoneyCeiling(), _src.PlanCeiling()); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; } // the quit closed the file under this tick
        Set(Status, words.Status);
        Set(MoneyText, words.Money);
        Set(PlanText, words.Plan);
        Set(CanaryText, words.Canaries);
        Set(CatchText, string.Join("\n", words.Catches));
        if (!_opened && store is not null)
        {
            _opened = true;
            MoneyBox.OpenOn(store.Budget(BillingClasses.Key));
            PlanBox.OpenOn(store.Budget(BillingClasses.Plan));
        }
    }

    /// <summary>
    /// THE BUDGET'S PRESS OVER ITS OWN BOX: raising asks twice and names the figure; lowering or zero saves in one; an edit disarms an armed
    /// raise; a box that reads as nothing refuses either press — the oversight cap's rule (<see cref="OversightPage.BuildSaveCap(AmountField, Func{decimal}, Func{string}, Action, Action{string}?)"/>).
    /// </summary>
    internal static Button BuildSaveBudget(string label, string boxLabel, AmountField box, Func<decimal> current, string ofWhat,
        Func<string> currency, Action save, Action<string> refuse)
    {
        var b = Ui.ConfirmIf(label,
            () => box.Figure > current() ? Labels.RaiseAuditBudgetArmed(Amount.ExactMoney(box.Figure, currency()), ofWhat) : null,
            save, () => Ui.Refusal((boxLabel, box)), refuse, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        box.ValueChanged += (_, _) => Ui.DisarmConfirm(b);
        return b;
    }

    /// <summary>Appends the owner's figure to the line's own file — never the activity log, which the organisation's reports can carry.</summary>
    void Save(string billingClass, AmountField box)
    {
        if (Store() is not { } store) return;
        var figure = box.Figure;
        store.SetBudget(billingClass, figure, _src.Now());
        Ui.Said(Note, $"Saved. {Amount.ExactMoney(figure, _src.Currency())} a day of "
            + (billingClass == BillingClasses.Key ? "real money" : "plan capacity") + " is kept for the audit line.");
        Update();
    }

    static void Set(TextBlock t, string text)
    {
        if (t.Text != text) t.Text = text;
    }
}
