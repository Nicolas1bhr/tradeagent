using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using Palette = TradeAgent.App.Theme;

namespace TradeAgent.App;

/// <summary>
/// THE ONE BOX A MONEY OR LIMIT FIGURE IS TYPED INTO (<c>U-number-entry-culture</c>): a text box that keeps exactly what was typed,
/// and a live line under it saying what that text reads as — or, in caution colour, why it reads as nothing.
///
/// <para>It replaced a stock number control that read "0,05" as 5, redrew "0.05" as "0", silently put back the previous figure over
/// text it could not read, and stepped the figure on a mouse wheel. None of that is possible here, by construction:</para>
/// <list type="bullet">
/// <item>the text is never written after construction — not by a commit, a stepper, a format string or the five-second refresh;
/// the only writer of <see cref="Box"/>'s text is the owner's keyboard, but for <see cref="OpenOn"/>, which opens a box that opened
/// empty on the figure in force once it is known, and only while nothing has touched it;</item>
/// <item>the figure is read by <see cref="Amount.TryRead"/>, the one culture-free reader, and is null while the text is refused or
/// empty — a press over it refuses rather than falling back to 0 or to the saved figure;</item>
/// <item>there are no steppers and nothing here handles the mouse wheel, so a scroll over the box — clicked into or not — changes
/// nothing and scrolls the page (the owner, 2026-10-22: "values cannot be changed by scrolling over them").</item>
/// </list>
/// </summary>
public sealed class AmountField : StackPanel
{
    readonly decimal _min, _max;

    bool _touched;

    internal AmountField(decimal? value, AmountKind kind, decimal min, decimal max)
    {
        Kind = kind;
        _min = min;
        _max = max;
        Spacing = Palette.S1;
        Width = Palette.FieldWidth;
        // NULL OPENS EMPTY — a figure not known yet (fix pass 2): the box reads as nothing and its press refuses, never 0.
        Box = new TextBox { Text = value is { } v ? Amount.Draw(v) : "", FontFamily = Palette.Mono, FontSize = Palette.Base };
        Reading = new TextBlock { FontSize = Palette.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap };
        Children.Add(Box);
        Children.Add(Reading);
        Read();
        // THE PROPERTY, NOT THE TextChanged EVENT: the property changes synchronously on every keystroke whatever the box is
        // attached to, so the line under it and the press beside it never read a figure the text has already moved past.
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            _touched = true;
            Read();
        };
    }

    /// <summary>
    /// THE ONE WRITE AFTER CONSTRUCTION, AND ONLY OVER AN UNTOUCHED BOX (<c>U-number-entry-culture</c>, fix pass 2): a box opened empty
    /// because the figure in force was not known yet (the oversight cap, before the owner's file is first read) is opened on that figure
    /// when it is — once, and only while nothing has changed its text since construction. A box the owner has typed in is never written,
    /// so no refresh takes a figure out from under his typing. True when it wrote.
    /// </summary>
    internal bool OpenOn(decimal value)
    {
        if (_touched) return false;
        Box.Text = Amount.Draw(value);
        return true;
    }

    /// <summary>The text box the owner types into. Nothing but the owner writes its text.</summary>
    internal TextBox Box { get; }

    /// <summary>The line under the box: "reads as X", or the refusal.</summary>
    internal TextBlock Reading { get; }

    public AmountKind Kind { get; }

    /// <summary>The figure the text reads as, or null while it is refused or empty.</summary>
    public decimal? Value { get; private set; }

    /// <summary>
    /// The figure, for a press that has already asked <see cref="Refusal"/>. There is no fallback: read while refused it throws, and
    /// the two-step buttons report that and save nothing — a refused box is never read as 0 or as the figure already saved.
    /// </summary>
    public decimal Figure => Value ?? throw new InvalidOperationException(Refusal);

    /// <summary>Why the text reads as nothing, in the owner's words; null while it reads as a figure.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Raised when the read figure changes — a figure to another, or to or from a refusal — and only then.</summary>
    public event EventHandler? ValueChanged;

    void Read()
    {
        var was = Value;
        var typed = (Box.Text ?? "").Trim();
        if (!Amount.TryRead(typed, Kind, out var figure, out var why))
            (Value, Refusal) = (null, why);
        else if (figure < _min)
            (Value, Refusal) = (null, Labels.AmountBelow(typed, Amount.Exact(_min)));
        else if (figure > _max)
            (Value, Refusal) = (null, Labels.AmountAbove(typed, Amount.Exact(_max)));
        else
            (Value, Refusal) = (figure, null);

        Reading.Text = Refusal ?? Labels.AmountReads(Amount.Exact(Value!.Value));
        Reading.Foreground = Refusal is null ? Palette.TextMuted : Palette.Caution;
        if (was != Value) ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
