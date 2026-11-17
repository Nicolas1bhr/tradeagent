using Avalonia;
using Avalonia.Animation;
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
    public static readonly IBrush Bg = B("#0B0D12");
    /// <summary>Raised: cards, the composer, the top strip.</summary>
    public static readonly IBrush BgElevated = B("#13161D");
    /// <summary>Recessed: input wells, code, the chat transcript's own background.</summary>
    public static readonly IBrush BgSunken = B("#07090C");
    public static readonly IBrush BgHover = B("#1A1E27");
    public static readonly IBrush BgActive = B("#222733");
    /// <summary>The left rail. One step darker than the content so the content is where the eye lands.</summary>
    public static readonly IBrush BgRail = B("#090B0F");

    // ---- lines ----------------------------------------------------------------------------
    public static readonly IBrush Line = B("#1F242E");
    public static readonly IBrush LineStrong = B("#303746");

    // ---- text -----------------------------------------------------------------------------
    public static readonly IBrush Text = B("#ECEFF4");
    public static readonly IBrush TextMuted = B("#9BA4B3");
    public static readonly IBrush TextFaint = B("#6F798B");
    public static readonly IBrush TextOnAccent = B("#0B0D12");

    /// <summary>
    /// Text on the one saturated fill in the app — the emergency control. Named here rather than
    /// written at the widget because the control is repainted from code while it is half-pressed,
    /// and the two places that paint it have to agree: arming swaps the button to the "danger"
    /// class, whose foreground is red, over a fill that is also red.
    /// </summary>
    public static readonly IBrush TextOnEmergency = Brushes.White;

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

    // ---- the primary action: light on dark ------------------------------------------------
    /// <summary>
    /// The fill of the one button a screen wants pressed. Near-white, so indigo is left meaning one thing only: the AI — its
    /// light on the floor, the page it is on, the seat that is working. A button that is also indigo would make "press this" and
    /// "the AI is here" the same colour.
    /// </summary>
    public static readonly IBrush Primary = B("#F1F3F8");
    public static readonly IBrush PrimaryHover = B("#FFFFFF");
    public static readonly IBrush PrimaryPress = B("#D5D9E3");

    // ---- type -----------------------------------------------------------------------------
    // INTER, CARRIED IN THE APP (<see cref="TradeAgentApp.Initialize"/> registers it), first in both lists: the same letters and the
    // same widths on Windows, on the Mac and on the Linux host, where the old list fell through to whatever sans the machine had and
    // every measured column was measured for a different face. The system faces stay behind it as the fallback for a glyph Inter lacks.
    public static readonly FontFamily Sans =
        new("fonts:Inter#Inter, Segoe UI Variable Text, Segoe UI Variable, Segoe UI, SF Pro Text, Helvetica Neue, sans-serif");
    public static readonly FontFamily SansDisplay =
        new("fonts:Inter#Inter, Segoe UI Variable Display, Segoe UI Variable, Segoe UI, SF Pro Display, Helvetica Neue, sans-serif");

    /// <summary>
    /// Figures set in the text face: tabular digits, so a column of prices does not jitter, with Inter's open one and slashed zero left
    /// off. For a figure inside a sentence or beside a label, where the mono face would shout.
    /// </summary>
    public static readonly FontFeatureCollection Tabular = [FontFeature.Parse("tnum")];

    /// <summary>How tightly a headline of each size is set. Inter is drawn for text; at display sizes it wants pulling together.</summary>
    public const double DisplayTracking = -0.6, H1Tracking = -0.35, H2Tracking = -0.1;
    /// <summary>
    /// Every number the user might compare to another number is set in this. Proportional digits
    /// make a column of prices jitter, and a jittering price column is how you misread a fill.
    /// </summary>
    public static readonly FontFamily Mono =
        new("Cascadia Mono, Consolas, JetBrains Mono, SF Mono, Menlo, monospace");

    public const double Display = 30;
    /// <summary>
    /// The line box that holds <see cref="Display"/> text. Every TextBlock gets a 20 px line height from the base style below, which
    /// is a line box for 13.5 px text: 30 px glyphs set in it overflow it, the well or card around them clips what overflows (measured
    /// headless with Skia: 14 of 23 px of ink showed; the Linux server showed the top half). Anything set at
    /// <see cref="Display"/> names this.
    /// </summary>
    public const double DisplayLine = 36;
    public const double H1 = 24;
    /// <summary>The line box of <see cref="H1"/> text, for the same reason <see cref="DisplayLine"/> exists.</summary>
    public const double H1Line = 30;
    public const double H2 = 15.5;
    public const double H3 = 13.5;
    public const double Base = 13.5;
    public const double Small = 12.5;
    public const double Micro = 11;

    // ---- rhythm ---------------------------------------------------------------------------
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S8 = 32, S10 = 40;

    /// <summary>The width of a box the owner types a short value into: a figure, an id, a key.</summary>
    public const double FieldWidth = 150;

    /// <summary>The label column of the "At a glance" card (<c>U-ui-glance</c>): wide enough for "Paper runs (simulations)" at the small size.</summary>
    public const double GlanceLabelWidth = 170;

    public static readonly CornerRadius RadiusSm = new(8);
    /// <summary>A button's corner: round enough to read as one, not so round that three lines of an armed sentence run into it.</summary>
    public static readonly CornerRadius RadiusButton = new(10);
    public static readonly CornerRadius Radius = new(14);
    public static readonly CornerRadius RadiusLg = new(18);
    public static readonly CornerRadius Pill = new(999);

    // ---- the shell's frame ----------------------------------------------------------------
    /// <summary>The left rail's width, the header's height, and the widest a page of words is laid: past it a line is too long to read.</summary>
    public const double RailWidth = 210, HeaderHeight = 56, PageMaxWidth = 1180;

    /// <summary>The widest a page's opening paragraph is set.</summary>
    public const double LedeMaxWidth = 820;

    /// <summary>A rail icon's box, and the weight of its line.</summary>
    public const double IconSize = 17, IconStroke = 1.5;

    /// <summary>
    /// A card's edge: the hairline, a little lighter along the top. A flat dark surface has no shadow to stand on (see the class note),
    /// so the lift is said the way a machined edge says it — by the one side the light catches.
    /// </summary>
    public static readonly IBrush CardEdge = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2B3240"), 0), new GradientStop(Color.Parse("#1A1E27"), 0.55) }
    };

    // ---- the mark --------------------------------------------------------------------------
    /// <summary>The app's mark (<see cref="Icons.Mark"/>): a cell's lit top and its two sides.</summary>
    public static readonly IBrush MarkLight = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#F4F5FF"), 0), new GradientStop(Color.Parse("#A9B4FF"), 1) }
    };
    public static readonly IBrush MarkSide = B("#5661AE");
    public static readonly IBrush MarkSideDark = B("#39417A");

    // ---- the floor -------------------------------------------------------------------------
    /// <summary>
    /// What stands behind the sentences laid over the floor's right side: the window's own dark, clear at its inner edge and nearly
    /// whole at the outer, so the words are read on it and the floor is not cut off by a panel.
    /// </summary>
    public static readonly IBrush FloorScrim = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#000B0D12"), 0), new GradientStop(Color.Parse("#CC0B0D12"), 0.45), new GradientStop(Color.Parse("#F20B0D12"), 1) }
    };

    // ---- motion -----------------------------------------------------------------------------
    /// <summary>How long a hover takes to arrive, and a page to come in. Short enough that nothing is ever waited for.</summary>
    public static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(110), PageIn = TimeSpan.FromMilliseconds(220);

    /// <summary>How far below its place a page starts when it comes in.</summary>
    public const double PageRise = 10;

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
                S(TemplatedControl.CornerRadiusProperty, RadiusButton),
                S(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                S(TemplatedControl.BackgroundProperty, BgElevated),
                S(TemplatedControl.BorderBrushProperty, Line),
                S(TemplatedControl.ForegroundProperty, Text),
                S(Button.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Center),
                S(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))
            }
        });

        // A BUTTON NEVER CUTS OR SPILLS ITS WORDS: its text wraps inside the width its place gives it. An armed two-press says in full what
        // the second press does, and on the owner's 1280x800 screen that sentence ran out of its card under the next column (Dashboard) or
        // was cut mid-word (the chief's wake, the onboarding's back link). In a row, where nothing bounds the width, nothing changes.
        // AND ITS FILL ARRIVES, never snaps: a hover that changes colour in one frame reads as a flicker on a screen this dark.
        styles.Add(new Style(x => x.OfType<Button>().Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                S(ContentPresenter.TextWrappingProperty, TextWrapping.Wrap),
                S(Animatable.TransitionsProperty, new Transitions
                {
                    new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = Quick },
                    new BrushTransition { Property = ContentPresenter.BorderBrushProperty, Duration = Quick }
                })
            }
        });

        AddButtonVariant(styles, "primary", Primary, PrimaryHover, PrimaryPress, TextOnAccent, Primary, FontWeight.SemiBold);
        AddButtonVariant(styles, "secondary", BgHover, BgActive, LineStrong, Text, LineStrong);
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
                S(TemplatedControl.ForegroundProperty, TextOnEmergency),
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
                S(TemplatedControl.PaddingProperty, new Thickness(S3, 6)),
                S(TemplatedControl.CornerRadiusProperty, RadiusSm),
                S(TemplatedControl.FontWeightProperty, FontWeight.Medium),
                S(Button.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch)
            }
        });
        Fill(styles, x => x.OfType<Button>().Class("nav").Class(":pointerover"), BgHover);
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class(":pointerover"))
        {
            Setters = { S(TemplatedControl.ForegroundProperty, Text) }
        });
        // THE PAGE YOU ARE ON: a lifted surface and full-strength words. Its icon alone takes the indigo (MainWindow paints it), so a
        // rail of fourteen names has one small light in it rather than a band of colour.
        Fill(styles, x => x.OfType<Button>().Class("nav").Class("on"), BgActive);
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("on"))
        {
            Setters = { S(TemplatedControl.ForegroundProperty, Text), S(TemplatedControl.FontWeightProperty, FontWeight.SemiBold) }
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
