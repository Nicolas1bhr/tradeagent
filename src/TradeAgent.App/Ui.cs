using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// The component vocabulary. Code-built, no XAML and no MVVM framework on purpose: this UI is a
/// handful of screens, and the indirection would cost more than it saves.
///
/// Everything visual comes from <see cref="Tokens"/>. Nothing in this file invents a colour, a size
/// or a gap — if a value is not in the theme it does not belong on the screen, which is the only
/// mechanism that keeps a hand-built UI from drifting into forty slightly different greys.
/// </summary>
static class Ui
{
    /// <summary>
    /// Where a failed button press goes. Set by the window at startup. Without it an exception in an
    /// async click handler is an unobserved fault on a void-returning delegate, which ends the
    /// process — the one failure mode a nontechnical user can do nothing at all about.
    /// </summary>
    public static Action<string>? ReportError;

    static void Report(Exception ex) =>
        ReportError?.Invoke(ex is TradeAgentException t
            ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim()
            : ex.Message);

    // ---- typography ------------------------------------------------------------------------

    public static TextBlock Display(string text) => new()
    {
        Text = text, FontSize = Theme.Display, FontFamily = Theme.SansDisplay,
        FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, LineHeight = 36,
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock H1(string text) => new()
    {
        Text = text, FontSize = Theme.H1, FontFamily = Theme.SansDisplay,
        FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, LineHeight = 28,
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock H2(string text) => new()
    {
        Text = text, FontSize = Theme.H2, FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, LineHeight = 22
    };

    public static TextBlock H3(string text) => new()
    {
        Text = text, FontSize = Theme.H3, FontWeight = FontWeight.SemiBold, Foreground = Theme.Text
    };

    /// <summary>
    /// Body text. The brush is applied only when one is supplied: assigning null to Foreground sets
    /// a local value of null, which overrides the theme and paints nothing at all. Every explanatory
    /// paragraph in this product goes through here, so that one assignment once made the whole app
    /// render as blank space between headings.
    /// </summary>
    public static TextBlock Body(string text, IBrush? brush = null)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = Theme.Base, TextWrapping = TextWrapping.Wrap,
            LineHeight = 21, Foreground = Theme.Text
        };
        if (brush is not null) t.Foreground = brush;
        return t;
    }

    public static TextBlock Muted(string text) => Body(text, Theme.TextMuted);

    public static TextBlock Micro(string text) => new()
    {
        Text = text, FontSize = Theme.Micro, Foreground = Theme.TextFaint, TextWrapping = TextWrapping.Wrap
    };

    /// <summary>A section label. Letterspaced small caps — the quietest way to name a region.</summary>
    public static TextBlock Eyebrow(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = Theme.Micro, FontWeight = FontWeight.SemiBold,
        Foreground = Theme.TextFaint, LetterSpacing = 0.9
    };

    /// <summary>The old name for <see cref="Eyebrow"/>, kept so existing screens keep compiling.</summary>
    public static TextBlock Label(string text) => Eyebrow(text);

    /// <summary>Anything the user might compare with another value: prices, sizes, counts, times.</summary>
    public static TextBlock Mono(string text, IBrush? brush = null) => new()
    {
        Text = text, FontSize = Theme.Small, FontFamily = Theme.Mono, Foreground = brush ?? Theme.Text
    };

    // ---- layout ----------------------------------------------------------------------------

    public static StackPanel Col(double spacing, params Control[] kids)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    public static StackPanel Row(double spacing, params Control[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    /// <summary>
    /// A row that wraps instead of running off the edge of its card.
    ///
    /// Needed wherever a two-step button sits in a row of buttons: an armed confirmation says the
    /// whole sentence — "Confirm: close TradeAgent and install 0.2.0" — so the row is wider AFTER the
    /// first press than it was when it was laid out, and a plain <see cref="Row"/> clips the controls
    /// to its right. Spacing is applied as a margin on each child rather than by the panel, which is
    /// the version-proof way to do it; a caller's own margin on these children is overwritten.
    /// </summary>
    public static WrapPanel Wrap(double spacing, params Control[] kids)
    {
        var p = new WrapPanel();
        foreach (var k in kids)
        {
            k.Margin = new Thickness(0, 0, spacing, spacing);
            p.Children.Add(k);
        }
        return p;
    }

    public static Border Card(Control inner) => new()
    {
        Padding = new Thickness(Theme.S5),
        CornerRadius = Theme.Radius,
        BorderThickness = new Thickness(1),
        BorderBrush = Theme.Line,
        Background = Theme.BgElevated,
        Child = inner
    };

    /// <summary>A card with a named region above it. The standard unit of the dashboard.</summary>
    public static Control Section(string eyebrow, Control body) =>
        Col(Theme.S2, Eyebrow(eyebrow), Card(body));

    public static Control Divider() => new Border
    {
        Height = 1, Background = Theme.Line, Margin = new Thickness(0, Theme.S2)
    };

    public static Control Spacer(double h) => new Border { Height = h };

    // ---- buttons ---------------------------------------------------------------------------

    static Button Make(string cls, string text, Action onClick)
    {
        var b = new Button { Content = text, Classes = { cls } };
        b.Click += (_, _) => { try { onClick(); } catch (Exception ex) { Report(ex); } };
        return b;
    }

    /// <summary>
    /// The async form disables itself while the work runs. Without that, a slow broker round trip
    /// invites a second press, and a second press on "place order" is a second order.
    /// </summary>
    static Button Make(string cls, string text, Func<Task> onClick)
    {
        var b = new Button { Content = text, Classes = { cls } };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            catch (Exception ex) { Report(ex); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Primary(string text, Action onClick) => Make("primary", text, onClick);
    public static Button Primary(string text, Func<Task> onClick) => Make("primary", text, onClick);
    public static Button Secondary(string text, Action onClick) => Make("secondary", text, onClick);
    public static Button Secondary(string text, Func<Task> onClick) => Make("secondary", text, onClick);
    public static Button Ghost(string text, Action onClick) => Make("ghost", text, onClick);
    public static Button Ghost(string text, Func<Task> onClick) => Make("ghost", text, onClick);
    public static Button Danger(string text, Action onClick) => Make("danger", text, onClick);
    public static Button Danger(string text, Func<Task> onClick) => Make("danger", text, onClick);

    /// <summary>Kept so existing call sites read the same. A plain button is the secondary one.</summary>
    public static Button Button(string text, Action onClick, bool emphasised = false) =>
        Make(emphasised ? "primary" : "secondary", text, onClick);

    public static Button Button(string text, Func<Task> onClick, bool emphasised = false) =>
        Make(emphasised ? "primary" : "secondary", text, onClick);

    /// <summary>Arming state for a two-step button, kept on the control so it survives a relabel.</summary>
    sealed class ConfirmState
    {
        public string Label = "";
        public string ConfirmLabel = "";
        public bool Armed;

        /// <summary>
        /// The theme class the control wears when it is NOT armed. It is not always "secondary":
        /// the kill switch is the emergency variant on the Safety page and the danger/primary pair
        /// in the window chrome, and arming has to be able to give that class back afterwards.
        /// </summary>
        public string RestingClass = "secondary";

        /// <summary>
        /// How the control paints itself in each of its two states, where a caller paints it with
        /// LOCAL brushes rather than a class. A local value beats a style in Avalonia, so without
        /// this the armed red of a mode button or the emergency fill is simply not visible — the
        /// class is set and the brush the row painted last is what the owner sees.
        /// </summary>
        public Action<Button, bool>? Repaint;
    }

    /// <summary>
    /// Two-step button. Anything that moves money or removes permission needs a deliberate second
    /// press, so a mis-click cannot liquidate a portfolio. The armed state turns the control red and
    /// says what the second press will do, in full — never just "Confirm".
    /// </summary>
    public static Button Confirm(string label, string confirmLabel, Action onConfirmed)
    {
        var (b, state) = ConfirmShell(label, confirmLabel);
        b.Click += (_, _) =>
        {
            if (Arm(b, state)) return;
            Disarm(b);
            try { onConfirmed(); } catch (Exception ex) { Report(ex); }
        };
        return b;
    }

    /// <summary>
    /// Async form. The result is awaited rather than dropped: a cancel-all that fails must say so,
    /// not disappear into an unobserved task.
    /// </summary>
    public static Button Confirm(string label, string confirmLabel, Func<Task> onConfirmed)
    {
        var (b, state) = ConfirmShell(label, confirmLabel);
        b.Click += async (_, _) =>
        {
            if (Arm(b, state)) return;
            Disarm(b);
            b.IsEnabled = false;
            try { await onConfirmed(); }
            catch (Exception ex) { Report(ex); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    static (Button, ConfirmState) ConfirmShell(string label, string confirmLabel, string restingClass = "secondary")
    {
        var state = new ConfirmState { Label = label, ConfirmLabel = confirmLabel, RestingClass = restingClass };
        return (new Button { Content = label, Classes = { restingClass }, Tag = state }, state);
    }

    static bool Arm(Button b, ConfirmState state)
    {
        if (state.Armed) return false;
        state.Armed = true;
        b.Content = state.ConfirmLabel;
        b.Classes.Remove(state.RestingClass);
        if (!b.Classes.Contains("danger")) b.Classes.Add("danger");
        state.Repaint?.Invoke(b, true);
        return true;
    }

    static void Disarm(Button b)
    {
        if (b.Tag is not ConfirmState state) return;
        state.Armed = false;
        b.Content = state.Label;
        b.Classes.Remove("danger");
        if (!b.Classes.Contains(state.RestingClass)) b.Classes.Add(state.RestingClass);
        state.Repaint?.Invoke(b, false);
    }

    /// <summary>
    /// Whether a two-step button is half-pressed. Callers that repaint on the refresh tick ask
    /// this so a five-second redraw cannot quietly undo an arming the owner is looking at.
    /// </summary>
    public static bool IsArmed(Button b) => b.Tag is ConfirmState { Armed: true };

    /// <summary>
    /// Registers how a two-step button paints itself, and paints it now. See
    /// <see cref="ConfirmState.Repaint"/>: without it a local brush hides the armed state.
    /// </summary>
    public static void Repaints(Button b, Action<Button, bool> paint)
    {
        if (b.Tag is not ConfirmState state) { paint(b, false); return; }
        state.Repaint = paint;
        paint(b, state.Armed);
    }

    /// <summary>
    /// Points a two-step button at a different resting label and variant, from the refresh tick,
    /// without disturbing an arming already half-made — unless the LABEL changed, in which case the
    /// control no longer says what the armed sentence promised and the arming is abandoned.
    /// </summary>
    public static void SetResting(Button b, string label, string restingClass)
    {
        if (b.Tag is not ConfirmState state) return;

        var wasClass = state.RestingClass;
        state.RestingClass = restingClass;

        if (state.Label != label)
        {
            state.Label = label;
            b.Classes.Remove(wasClass);
            Disarm(b);
            return;
        }

        if (!state.Armed && wasClass != restingClass)
        {
            b.Classes.Remove(wasClass);
            if (!b.Classes.Contains(restingClass)) b.Classes.Add(restingClass);
        }
        state.Repaint?.Invoke(b, state.Armed);
    }

    /// <summary>
    /// A two-step button whose second press is required only in the direction that GRANTS. The
    /// delegate is asked at the moment of the press and returns the sentence the second press will
    /// carry out, or null for "this press only takes authority away — do it now".
    ///
    /// It is what separates STOP from RESUME on one control, and a save that lowers a cap from a
    /// save that raises one. Hesitating on the way down costs money; a mis-press on the way up
    /// hands the AI room nobody meant to give it.
    /// </summary>
    public static Button ConfirmIf(string label, Func<string?> armedSentence, Action onPressed,
        string restingClass = "secondary")
    {
        var (b, state) = ConfirmShell(label, "", restingClass);
        b.Click += (_, _) =>
        {
            try
            {
                if (!state.Armed)
                {
                    var sentence = armedSentence();
                    if (sentence is not null)
                    {
                        state.ConfirmLabel = sentence;
                        Arm(b, state);
                        return;
                    }
                }
                Disarm(b);
                onPressed();
            }
            catch (Exception ex) { Report(ex); }
        };
        return b;
    }

    /// <summary>
    /// The kill switch, built the same way in both places it appears. STOP is ONE press — a
    /// mis-press only removes the AI's permission, and hesitation there costs money. RESUME is two,
    /// because in a real-money mode it hands that permission back to something that trades.
    /// </summary>
    public static Button KillSwitch(string restingClass, Func<bool> stopped, Action stop, Action resume) =>
        ConfirmIf(stopped() ? Labels.ResumeAiTrading : Labels.StopAiTrading,
            () => stopped() ? Labels.ResumeAiTradingArmed : null,
            () => { if (stopped()) resume(); else stop(); },
            restingClass);

    /// <summary>Whether choosing this mode gives the AI room it did not have. Both real-money modes do.</summary>
    public static bool ModeGrants(TradingMode m) => m is TradingMode.LIVE_CONFIRM or TradingMode.LIVE_AUTONOMOUS;

    /// <summary>What the second press on a real-money mode does, in the owner's words.</summary>
    public static string ModeArmedLabel(TradingMode m) => m switch
    {
        TradingMode.LIVE_AUTONOMOUS => Labels.ModeAutonomousArmed,
        TradingMode.LIVE_CONFIRM => Labels.ModeAskFirstArmed,
        _ => ModeLabel(m)
    };

    /// <summary>
    /// One button of the trading-mode row. The two real-money modes GRANT, so they are two-press
    /// with the sentence spelled out; Watch only and Practice only ever reduce, so they are one.
    /// </summary>
    public static Button ModeButton(TradingMode m, Action apply) =>
        ModeGrants(m)
            ? Confirm(ModeLabel(m), ModeArmedLabel(m), apply)
            : Secondary(ModeLabel(m), apply);

    /// <summary>
    /// Takes a two-step button back to its resting state from outside. Needed where the thing being
    /// confirmed can change under a button that is already armed — the unconfirmed-orders card ties
    /// each confirmation to a note the user is still typing, and a confirmation armed against one
    /// sentence must not be completable against a different one.
    /// </summary>
    public static void DisarmConfirm(Button b) => Disarm(b);

    /// <summary>Changes what a two-step button says without rebuilding it, disarming it as it goes.</summary>
    public static void Relabel(Button b, string label, string confirmLabel)
    {
        if (b.Tag is not ConfirmState state) return;
        if (state.Label == label && state.ConfirmLabel == confirmLabel) return;
        state.Label = label;
        state.ConfirmLabel = confirmLabel;
        Disarm(b);
    }

    /// <summary>
    /// Marks the selected one of a row of buttons, in place — and remembers how, so a two-step
    /// button in that row can be armed without the row's own brushes painting over its red.
    /// </summary>
    public static void Emphasise(Button b, bool on)
    {
        if (b.Tag is ConfirmState) { Repaints(b, (btn, armed) => PaintEmphasis(btn, on, armed)); return; }
        PaintEmphasis(b, on, false);
    }

    static void PaintEmphasis(Button b, bool on, bool armed)
    {
        if (on) { if (!b.Classes.Contains("on")) b.Classes.Add("on"); }
        else b.Classes.Remove("on");

        if (armed)
        {
            // Hand the control back to the danger class. These are LOCAL values, and a local value
            // beats a style, so leaving them set is how an armed button goes on looking unarmed.
            b.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty);
            b.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty);
            b.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty);
            return;
        }

        // Segmented rows are built from plain buttons, so carry the selection on colour too.
        b.Background = on ? Theme.AccentSoft : Theme.BgElevated;
        b.BorderBrush = on ? Theme.Accent : Theme.Line;
        b.Foreground = on ? Theme.Accent : Theme.TextMuted;
        b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Medium;
    }

    // ---- state ------------------------------------------------------------------------------

    /// <summary>A small tinted label. The tone is a claim: green means good, amber means look.</summary>
    public static Border Pill(string text, IBrush tone) => new()
    {
        Background = Soft(tone),
        CornerRadius = Theme.Pill,
        Padding = new Thickness(Theme.S2, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontSize = Theme.Micro, FontWeight = FontWeight.SemiBold,
            Foreground = tone, LetterSpacing = 0.3
        }
    };

    static IBrush Soft(IBrush tone) =>
        tone == Theme.Positive ? Theme.PositiveSoft
        : tone == Theme.Caution ? Theme.CautionSoft
        : tone == Theme.Danger ? Theme.DangerSoft
        : tone == Theme.Accent || tone == Theme.Info ? Theme.AccentSoft
        : Theme.NeutralSoft;

    public static Control Dot(IBrush tone) => new Ellipse
    {
        Width = 7, Height = 7, Fill = tone, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// One component's state: a coloured dot, its name, and what it has to say for itself.
    ///
    /// THE DETAIL WRAPS, AND IT USED TO BE TRIMMED. In a 340px card, 16 + 180 left about a hundred
    /// pixels for the detail, and `TextTrimming.CharacterEllipsis` then silently ate everything past
    /// a few words. Seen on Windows on 2026-08-31, where the two ATAS rows — whose whole purpose is
    /// to say WHICH half of the trading chain is missing — rendered as `running · 8.0....` and
    /// `connected · ...`. The rows were right and unreadable, which is the worse failure of the two:
    /// a wrong row invites a second look and a truncated one does not.
    ///
    /// The dashboard's bridge-refusal detail is ~450 characters, so under the old rule it displayed
    /// as approximately nothing. Nobody had seen it render, which is exactly why it survived.
    ///
    /// Everything is top-aligned because a wrapped row is two or three lines tall and a centred dot
    /// beside three lines of text reads as belonging to the middle one.
    /// </summary>
    public static Control StatusRow(ComponentHealth h)
    {
        var tone = Tone(h.State);
        return new Grid
        {
            // 140, not 180: "Execution capability" is the longest component name there is, and the
            // pixels saved go to the half of the row that actually varies.
            ColumnDefinitions = new ColumnDefinitions("16,140,*"),
            Margin = new Thickness(0, 3),
            Children =
            {
                // Nudged down to sit on the first line's optical centre rather than the row's.
                Ui.With(Dot(tone), c =>
                {
                    c.VerticalAlignment = VerticalAlignment.Top;
                    c.Margin = new Thickness(0, 5, 0, 0);
                }),
                new TextBlock
                {
                    Text = h.Component, FontSize = Theme.Small, Foreground = Theme.Text,
                    VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap,
                    [Grid.ColumnProperty] = 1
                },
                new TextBlock
                {
                    Text = Describe(h), FontSize = Theme.Small, Foreground = Theme.TextMuted,
                    VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap,
                    [Grid.ColumnProperty] = 2
                }
            }
        };
    }

    static string Describe(ComponentHealth h) => h.State switch
    {
        HealthState.READY => string.IsNullOrWhiteSpace(h.Detail) ? "ready" : h.Detail,
        HealthState.PAUSED => string.IsNullOrWhiteSpace(h.Detail) ? "paused" : $"paused — {h.Detail}",
        _ => string.IsNullOrWhiteSpace(h.Detail)
            ? h.State.ToString().ToLowerInvariant()
            : $"{h.State.ToString().ToLowerInvariant()} — {h.Detail}"
    };

    public static IBrush Tone(HealthState s) => s switch
    {
        HealthState.READY => Theme.Positive,
        HealthState.STARTING => Theme.Info,
        HealthState.DEGRADED => Theme.Caution,
        HealthState.PAUSED => Theme.Caution,
        HealthState.FAILED => Theme.Danger,
        _ => Theme.Neutral
    };

    // ---- data rows ---------------------------------------------------------------------------

    /// <summary>A key/value row whose value control the caller keeps, so it can be updated in place.</summary>
    public static Control KeyValueLive(string key, TextBlock value)
    {
        value.FontWeight = FontWeight.SemiBold;
        value.FontSize = Theme.Small;
        value.VerticalAlignment = VerticalAlignment.Center;
        value[Grid.ColumnProperty] = 1;
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Margin = new Thickness(0, 3),
            Children =
            {
                new TextBlock
                {
                    Text = key, Foreground = Theme.TextMuted, FontSize = Theme.Small,
                    VerticalAlignment = VerticalAlignment.Center
                },
                value
            }
        };
    }

    public static Control KeyValue(string key, string value) =>
        KeyValueLive(key, new TextBlock { Text = value });

    public static Control FieldRow(string label, Control editor, string? hint = null)
    {
        var text = Col(2, new TextBlock
        {
            Text = label, FontSize = Theme.Small, Foreground = Theme.Text,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        });
        if (hint is not null) text.Children.Add(Micro(hint));

        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, Theme.S1)
        };
        g.Children.Add(text);
        editor[Grid.ColumnProperty] = 1;
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.Margin = new Thickness(Theme.S4, 0, 0, 0);
        g.Children.Add(editor);
        return g;
    }

    /// <summary>A labelled number the user edits. Committed only when they press Save.</summary>
    public static NumericUpDown NumberField(decimal value, decimal min = 0m, decimal increment = 1m)
        => new()
        {
            Value = value, Minimum = min, Maximum = 1_000_000_000m, Increment = increment,
            FormatString = increment == 1m ? "0" : "0.####", Width = 150
        };

    public static TextBox TextField(string? text = null, string? placeholder = null) => new()
    {
        Text = text, PlaceholderText = placeholder, Width = 150
    };

    /// <summary>Indeterminate work with a sentence saying what is happening. Never a bare spinner.</summary>
    public static Control Busy(string message) => Row(Theme.S3,
        new ProgressBar { IsIndeterminate = true, Width = 90, VerticalAlignment = VerticalAlignment.Center },
        With(Body(message, Theme.TextMuted), t => t.VerticalAlignment = VerticalAlignment.Center));

    public static T With<T>(T c, Action<T> f) where T : Control { f(c); return c; }

    /// <summary>
    /// How the trading backend is named on screen.
    ///
    /// The qualifier is dropped when the connector's own name already carries it: the built-in
    /// backend is called "Simulator (built in)", and "Simulator (built in) (simulation)" is the kind
    /// of line that makes a product look like nobody read it. Real money is never dropped.
    /// </summary>
    /// <summary>
    /// The platform line. THE THIRD STATE IS THE ONE THAT MATTERS: `IsPaper` is false *before the
    /// platform has answered*, not only when the money is real. `AtasConnector.Capabilities` reports
    /// an all-false set while its handshake is null — deliberately, so the trading gates fail closed
    /// (`TradingGateway.cs:274` leans on exactly that). Rendering that same false as "real money"
    /// turned "I do not know yet" into the most alarming claim this product can make, and it was on
    /// screen beside a "Practice" badge and a simulated account: three labels, contradicting each
    /// other about the only fact that matters. Found by looking at the running app, 2026-09-01.
    ///
    /// Over-warning is not the safe direction here, it is just a different failure. A header that
    /// cries "real money" through every practice session is one the owner has stopped reading by the
    /// day it is true. So when the platform has not answered, say that, and assert neither.
    /// </summary>
    public static string PlatformLabel(GatewayStatus status) =>
        PlatformLabel(status.ConnectorName, status.ConnectorIsPaper,
            status.Health.FirstOrDefault(h => h.Component == Components.TradingConnection)?.State == HealthState.READY);

    public static string PlatformLabel(string? name, bool isPaper, bool platformAnswered = true)
    {
        if (string.IsNullOrWhiteSpace(name)) return "not connected";
        if (!platformAnswered) return $"{name} \u00b7 not connected";
        if (!isPaper) return $"{name} \u00b7 real money";
        return name.Contains("simulat", StringComparison.OrdinalIgnoreCase) ? name : $"{name} \u00b7 simulation";
    }

    public static string ModeLabel(TradingMode m) => m switch
    {
        TradingMode.OBSERVE => "Watch only",
        TradingMode.PAPER => "Practice",
        TradingMode.LIVE_CONFIRM => "Real, ask me first",
        TradingMode.LIVE_AUTONOMOUS => "Real, fully automatic",
        _ => m.ToString()
    };

    /// <summary>Real money is a different kind of state from a mode, so it gets a different colour.</summary>
    public static IBrush ModeTone(TradingMode m) => m switch
    {
        TradingMode.OBSERVE => Theme.Neutral,
        TradingMode.PAPER => Theme.Info,
        TradingMode.LIVE_CONFIRM => Theme.Caution,
        TradingMode.LIVE_AUTONOMOUS => Theme.Danger,
        _ => Theme.Neutral
    };
}
