using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace TradeAgent.App;

/// <summary>
/// The visual language, in one place.
///
/// Dark only, deliberately. This window sits next to ATAS on a trading desk, usually at night and
/// usually for hours; every platform this audience already uses is dark, and a light panel between
/// dark charts is the thing that looks wrong. Supporting both would double the palette for a
/// preference nobody in this audience has expressed.
///
/// Two rules govern colour here:
///   - Green, amber and red are SPENT. They mean profit, caution and loss/stop, and nothing else.
///     That is why the accent is indigo: it is the only strong colour left that carries no P&amp;L
///     meaning, so "the button you probably want" never reads as "you are making money".
///   - Depth comes from luminance, not from shadows. Drop shadows under a flat dark surface look
///     like a sticker of a card. Three surface levels and a hairline do the same job honestly.
/// </summary>
static class Theme
{
    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    static IBrush B(string hex, double opacity) => new SolidColorBrush(Color.Parse(hex), opacity);

    // ---- surfaces -------------------------------------------------------------------------
    /// <summary>The window itself. Near-black with a blue-slate cast; pure black is harsh on an LCD.</summary>
    public static readonly IBrush Bg = B("#0E1116");
    /// <summary>Raised: cards, the composer, the top strip.</summary>
    public static readonly IBrush BgElevated = B("#161A21");
    /// <summary>Recessed: input wells, code, the chat transcript's own background.</summary>
    public static readonly IBrush BgSunken = B("#0A0C10");
    public static readonly IBrush BgHover = B("#1E232C");
    public static readonly IBrush BgActive = B("#252B36");
    /// <summary>The left rail. One step darker than the content so the content is where the eye lands.</summary>
    public static readonly IBrush BgRail = B("#0B0E13");

    // ---- lines ----------------------------------------------------------------------------
    public static readonly IBrush Line = B("#232932");
    public static readonly IBrush LineStrong = B("#333B48");

    // ---- text -----------------------------------------------------------------------------
    public static readonly IBrush Text = B("#E7EAF0");
    public static readonly IBrush TextMuted = B("#98A2B2");
    public static readonly IBrush TextFaint = B("#6A7382");
    public static readonly IBrush TextOnAccent = B("#0B0E13");

    // ---- accent: indigo, the one colour with no money meaning ------------------------------
    public static readonly IBrush Accent = B("#7C8CF8");
    public static readonly IBrush AccentHover = B("#93A1FF");
    public static readonly IBrush AccentPress = B("#6675E0");
    public static readonly IBrush AccentSoft = B("#7C8CF8", 0.14);

    // ---- semantics: each of these is a claim about money or safety -------------------------
    public static readonly IBrush Positive = B("#3FB68B");
    public static readonly IBrush PositiveSoft = B("#3FB68B", 0.14);
    public static readonly IBrush Caution = B("#E0A458");
    public static readonly IBrush CautionSoft = B("#E0A458", 0.14);
    public static readonly IBrush Danger = B("#E5484D");
    public static readonly IBrush DangerHover = B("#F0595E");
    public static readonly IBrush DangerSoft = B("#E5484D", 0.14);
    public static readonly IBrush Info = B("#7C8CF8");
    public static readonly IBrush InfoSoft = B("#7C8CF8", 0.14);
    public static readonly IBrush Neutral = B("#6A7382");
    public static readonly IBrush NeutralSoft = B("#98A2B2", 0.12);

    // ---- type -----------------------------------------------------------------------------
    // Fallback lists, best face first. Segoe UI Variable is Windows 11's own modern face, so on the
    // target machine this is the system's best typography rather than an imported approximation.
    public static readonly FontFamily Sans =
        new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Inter, SF Pro Text, Helvetica Neue, sans-serif");
    public static readonly FontFamily SansDisplay =
        new("Segoe UI Variable Display, Segoe UI Variable, Segoe UI, Inter, SF Pro Display, Helvetica Neue, sans-serif");
    /// <summary>
    /// Every number the user might compare to another number is set in this. Proportional digits
    /// make a column of prices jitter, and a jittering price column is how you misread a fill.
    /// </summary>
    public static readonly FontFamily Mono =
        new("Cascadia Mono, Consolas, JetBrains Mono, SF Mono, Menlo, monospace");

    public const double Display = 30;
    public const double H1 = 21;
    public const double H2 = 15.5;
    public const double H3 = 13.5;
    public const double Base = 13.5;
    public const double Small = 12.5;
    public const double Micro = 11;

    // ---- rhythm ---------------------------------------------------------------------------
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S8 = 32, S10 = 40;

    public static readonly CornerRadius RadiusSm = new(6);
    public static readonly CornerRadius Radius = new(10);
    public static readonly CornerRadius RadiusLg = new(14);
    public static readonly CornerRadius Pill = new(999);

    static Setter S(AvaloniaProperty p, object? v) => new(p, v);

    /// <summary>
    /// App-level styles. These reach into the Fluent templates rather than replacing them: Fluent
    /// paints a button's hover and pressed states on the ContentPresenter inside the template, so
    /// setting Background on the Button alone leaves the default grey showing on every hover.
    /// </summary>
    public static Styles Build()
    {
        var styles = new Styles();

        // ---- window and text defaults ----
        styles.Add(new Style(x => x.OfType<Window>())
        {
            Setters = { S(TemplatedControl.BackgroundProperty, Bg), S(TemplatedControl.FontFamilyProperty, Sans) }
        });

        styles.Add(new Style(x => x.OfType<TextBlock>())
        {
            Setters =
            {
                S(TextBlock.ForegroundProperty, Text),
                S(TextBlock.FontFamilyProperty, Sans),
                S(TextBlock.FontSizeProperty, Base),
                S(TextBlock.LineHeightProperty, 20.0)
            }
        });

        // ---- buttons -------------------------------------------------------------------
        // Shared geometry. Variants below change only colour, so a new variant cannot drift in size.
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                S(TemplatedControl.FontFamilyProperty, Sans),
                S(TemplatedControl.FontSizeProperty, Base),
                S(TemplatedControl.FontWeightProperty, FontWeight.Medium),
                S(TemplatedControl.PaddingProperty, new Thickness(S4, 9)),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                S(TemplatedControl.BackgroundProperty, BgElevated),
                S(TemplatedControl.BorderBrushProperty, Line),
                S(TemplatedControl.ForegroundProperty, Text),
                S(Button.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Center),
                S(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))
            }
        });

        AddButtonVariant(styles, "primary", Accent, AccentHover, AccentPress, TextOnAccent, Accent, FontWeight.SemiBold);
        AddButtonVariant(styles, "secondary", BgElevated, BgHover, BgActive, Text, Line);
        AddButtonVariant(styles, "ghost", Brushes.Transparent, BgHover, BgActive, TextMuted, Brushes.Transparent);
        AddButtonVariant(styles, "danger", DangerSoft, Danger, DangerHover, Danger, Danger, FontWeight.SemiBold);

        // The emergency control. Full-bleed, unmistakable, and the only saturated fill in the app.
        styles.Add(new Style(x => x.OfType<Button>().Class("emergency"))
        {
            Setters =
            {
                S(TemplatedControl.FontSizeProperty, 15.0),
                S(TemplatedControl.FontWeightProperty, FontWeight.Bold),
                S(TemplatedControl.PaddingProperty, new Thickness(S4, 15)),
                S(TemplatedControl.CornerRadiusProperty, Radius),
                S(TemplatedControl.ForegroundProperty, Brushes.White),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(0))
            }
        });

        // The left rail's items. Selected state is a class, not a rebuild.
        styles.Add(new Style(x => x.OfType<Button>().Class("nav"))
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                S(TemplatedControl.ForegroundProperty, TextMuted),
                S(TemplatedControl.PaddingProperty, new Thickness(S3, 9)),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.FontWeightProperty, FontWeight.Medium),
                S(Button.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Left)
            }
        });
        Fill(styles, x => x.OfType<Button>().Class("nav").Class(":pointerover"), BgHover);
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class(":pointerover"))
        {
            Setters = { S(TemplatedControl.ForegroundProperty, Text) }
        });
        Fill(styles, x => x.OfType<Button>().Class("nav").Class("on"), AccentSoft);
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("on"))
        {
            Setters = { S(TemplatedControl.ForegroundProperty, Accent), S(TemplatedControl.FontWeightProperty, FontWeight.SemiBold) }
        });

        styles.Add(new Style(x => x.OfType<Button>().Class(":disabled"))
        {
            Setters = { S(Visual.OpacityProperty, 0.45) }
        });

        // ---- text input ----------------------------------------------------------------
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, BgSunken),
                S(TemplatedControl.BorderBrushProperty, Line),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.ForegroundProperty, Text),
                S(TemplatedControl.FontFamilyProperty, Sans),
                S(TemplatedControl.FontSizeProperty, Base),
                S(TemplatedControl.PaddingProperty, new Thickness(S3, 9)),
                S(TextBox.SelectionBrushProperty, AccentSoft),
                S(TextBox.CaretBrushProperty, Accent)
            }
        });
        // Fluent does not honour TextBox.Background: its ControlTheme carries a nested style that
        // paints the template's own Border, and a nested style beats the templated parent's value.
        // So the fill has to be written onto that Border in EVERY state, including the resting one.
        // Without the first rule here the field renders in Fluent's default #4C4D50 grey — measured,
        // not guessed — which is lighter than the card it sits on and reads as a disabled control.
        styles.Add(new Style(x => x.OfType<TextBox>().Template().OfType<Border>())
        {
            Setters = { S(Border.BackgroundProperty, BgSunken), S(Border.BorderBrushProperty, Line) }
        });
        styles.Add(new Style(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>())
        {
            Setters = { S(Border.BorderBrushProperty, Accent), S(Border.BackgroundProperty, BgSunken) }
        });
        styles.Add(new Style(x => x.OfType<TextBox>().Class(":pointerover").Template().OfType<Border>())
        {
            Setters = { S(Border.BorderBrushProperty, LineStrong), S(Border.BackgroundProperty, BgSunken) }
        });

        // A field with no chrome of its own, for when the container is already the well — the chat
        // composer, where a second rounded rectangle inside the card is one border too many.
        styles.Add(new Style(x => x.OfType<TextBox>().Class("bare").Template().OfType<Border>())
        {
            Setters =
            {
                S(Border.BackgroundProperty, Brushes.Transparent),
                S(Border.BorderBrushProperty, Brushes.Transparent),
                S(Border.BorderThicknessProperty, new Thickness(0))
            }
        });

        styles.Add(new Style(x => x.OfType<NumericUpDown>().Template().OfType<Border>())
        {
            Setters = { S(Border.BackgroundProperty, BgSunken), S(Border.BorderBrushProperty, Line) }
        });

        styles.Add(new Style(x => x.OfType<NumericUpDown>())
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, BgSunken),
                S(TemplatedControl.BorderBrushProperty, Line),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.ForegroundProperty, Text),
                S(TemplatedControl.FontFamilyProperty, Mono),
                S(TemplatedControl.FontSizeProperty, Base)
            }
        });

        // A NumericUpDown's steppers are RepeatButtons, and NOTHING above reaches them: Avalonia's
        // `OfType<T>()` is an EXACT-type selector, so the global `Button:disabled` rule never
        // matched a `RepeatButton` and Fluent's own disabled paint won by default. On this dark
        // theme that paint is LIGHTER than the resting control, so the one stepper the owner cannot
        // press — a limit already sat at its minimum — rendered as a raised, rounded, pale box while
        // its enabled neighbours stayed flat and dark. The single most prominent control in the
        // group was the dead one. Seen on Windows 2026-09-01; this is trap 4 for the fourth time,
        // and the lesson is unchanged: a state nobody writes a rule for gets the theme's idea of it.
        Fill(styles, x => x.OfType<RepeatButton>(), Brushes.Transparent);
        Fill(styles, x => x.OfType<RepeatButton>().Class(":disabled"), Brushes.Transparent);
        Fill(styles, x => x.OfType<RepeatButton>().Class(":pointerover"), BgHover);
        Fill(styles, x => x.OfType<RepeatButton>().Class(":pressed"), BgActive);
        styles.Add(new Style(x => x.OfType<RepeatButton>())
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                S(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                S(TemplatedControl.ForegroundProperty, TextMuted),
                S(TemplatedControl.CornerRadiusProperty, new CornerRadius(0))
            }
        });
        styles.Add(new Style(x => x.OfType<RepeatButton>().Class(":disabled"))
        {
            // Dimmer than the enabled chevrons and nothing else. "Not available" should recede.
            Setters = { S(Visual.OpacityProperty, 0.3) }
        });

        // ---- progress ------------------------------------------------------------------
        styles.Add(new Style(x => x.OfType<ProgressBar>())
        {
            Setters =
            {
                S(TemplatedControl.ForegroundProperty, Accent),
                S(TemplatedControl.BackgroundProperty, BgSunken),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                S(TemplatedControl.CornerRadiusProperty, Pill),
                S(Layoutable.MinHeightProperty, 0.0),
                S(Layoutable.HeightProperty, 4.0)
            }
        });

        // ---- scrolling: a hairline, not a chrome bar -----------------------------------
        styles.Add(new Style(x => x.OfType<ScrollBar>())
        {
            Setters = { S(TemplatedControl.BackgroundProperty, Brushes.Transparent) }
        });
        styles.Add(new Style(x => x.OfType<Thumb>())
        {
            Setters = { S(TemplatedControl.BackgroundProperty, LineStrong), S(TemplatedControl.CornerRadiusProperty, Pill) }
        });

        styles.Add(new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, BgActive),
                S(TemplatedControl.BorderBrushProperty, LineStrong),
                S(TemplatedControl.ForegroundProperty, Text),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.FontSizeProperty, Small)
            }
        });

        return styles;
    }

    /// <summary>
    /// One button variant: rest, hover and pressed fills plus text and border. Hover and press must
    /// be written onto the template's ContentPresenter, because that is where Fluent's own
    /// pointerover brush lives and a Setter on the Button loses to it.
    /// </summary>
    static void AddButtonVariant(Styles styles, string cls, IBrush rest, IBrush hover, IBrush press,
        IBrush text, IBrush border, FontWeight weight = FontWeight.Medium)
    {
        styles.Add(new Style(x => x.OfType<Button>().Class(cls))
        {
            Setters =
            {
                S(TemplatedControl.BackgroundProperty, rest),
                S(TemplatedControl.BorderBrushProperty, border),
                S(TemplatedControl.ForegroundProperty, text),
                S(TemplatedControl.FontWeightProperty, weight)
            }
        });
        Fill(styles, x => x.OfType<Button>().Class(cls).Class(":pointerover"), hover);
        Fill(styles, x => x.OfType<Button>().Class(cls).Class(":pressed"), press);

        // Disabled has to be written out too, for the same reason hover does: Fluent swaps in a grey
        // from its own resources, so a disabled primary stops looking like the primary action and
        // starts looking like a bug. Keeping the variant's own colour and dimming it — the Opacity
        // setter below does the dimming — says "not yet" instead of "broken".
        Fill(styles, x => x.OfType<Button>().Class(cls).Class(":disabled"), rest);
        styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":disabled").Template().OfType<ContentPresenter>())
        {
            Setters = { S(ContentPresenter.ForegroundProperty, text) }
        });

        // A filled variant inverts its text on hover; an outlined one keeps it.
        if (!ReferenceEquals(rest, Brushes.Transparent) && cls is "primary" or "danger")
        {
            styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":pointerover").Template().OfType<ContentPresenter>())
            {
                Setters = { S(ContentPresenter.ForegroundProperty, cls == "danger" ? Brushes.White : TextOnAccent) }
            });
        }
    }

    static void Fill(Styles styles, Func<Selector?, Selector> selector, IBrush brush) =>
        styles.Add(new Style(s => selector(s).Template().OfType<ContentPresenter>())
        {
            Setters = { S(ContentPresenter.BackgroundProperty, brush) }
        });
}
