using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace TradeAgent.App;

/// <summary>
/// THE OWNER'S PAGES ON GLASS (<c>U-ui-world</c>, builder two): the few sizes and the one style the owner's, the board's, "For you"'s and
/// the private line's pages add to the language — a line of a page that opens (its title set as a name, not an eyebrow), a field's name
/// over its box, a decided value set as a figure, an item that reached him held by a rule of light at its left, and a closed list drawn
/// as the same well a figure is typed into.
/// </summary>
static partial class Theme
{
    /// <summary>A line of the owner's page that opens: its name, and the figure or sentence it holds at a glance.</summary>
    public const double LineTitle = 17, LineTitleLine = 22;

    /// <summary>A value a policy holds as a number, set as a figure beside its name: large and light, but a size under a page's one figure.</summary>
    public const double ValueFigure = 22, ValueFigureLine = 28;

    /// <summary>A value a policy holds in words: the reading size, a step over the name it answers.</summary>
    public const double ValueWords = 15;

    /// <summary>The column a decided value's name is set in, on the Board page: room for "Most it may lose on one position" in two lines.</summary>
    public const double ValueNameWidth = 230;

    /// <summary>The widest a measure drawn under a line's summary is: a measure reads as a length only while it is short enough to see whole.</summary>
    public const double LineMeasureWidth = 260;

    /// <summary>The rule of light at the left of a thing that reached the owner, and the room between it and the words.</summary>
    public const double ItemRule = 2, ItemIndent = 16;

    /// <summary>The rule's light: a press waits on him (the window's light), or only a record (the pane's hairline, stronger).</summary>
    public static readonly IBrush ItemRuleWaiting = B("#FFFFFF", 0.55);

    /// <summary>
    /// A CLOSED LIST AS GLASS (<see cref="ChoiceBox"/>): the same sunken well, hairline and corner as a box a figure is typed into, so a
    /// choice and a figure read as the same kind of thing on the pane; its list opens on the window's own dark, never Fluent's grey.
    /// The orchestrator calls this from <see cref="Build"/>.
    /// </summary>
    public static void AddOwnerStyles(Styles styles)
    {
        styles.Add(new Style(x => x.OfType<ComboBox>())
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
                S(TemplatedControl.PaddingProperty, new Thickness(S3 + 2, 8, S2, 8)),
                S(Avalonia.Input.InputElement.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
            }
        });
        // Fluent paints the ComboBox's own Border inside its template from a nested style, as it does a TextBox's: the well has to be
        // written onto that Border in every state, or the resting list shows Fluent's lighter grey on the pane.
        foreach (var state in new[] { "", ":pointerover", ":pressed", ":dropdownopen", ":focus" })
        {
            var edge = state switch { ":pointerover" => LineStrong, ":dropdownopen" or ":focus" => FocusLine, _ => Line };
            styles.Add(new Style(x => (state.Length == 0 ? x.OfType<ComboBox>() : x.OfType<ComboBox>().Class(state)).Template().OfType<Border>())
            {
                Setters = { S(Border.BackgroundProperty, BgSunken), S(Border.BorderBrushProperty, edge) }
            });
        }
        styles.Add(new Style(x => x.OfType<ComboBox>().Class(":disabled"))
        {
            Setters = { S(Visual.OpacityProperty, 0.45) }
        });
        styles.Add(new Style(x => x.OfType<ComboBox>().Template().OfType<Popup>().Descendant().OfType<Border>())
        {
            Setters =
            {
                S(Border.BackgroundProperty, Solid),
                S(Border.BorderBrushProperty, LineStrong),
                S(Border.CornerRadiusProperty, RadiusSm)
            }
        });
        styles.Add(new Style(x => x.OfType<ComboBoxItem>())
        {
            Setters =
            {
                S(TemplatedControl.ForegroundProperty, TextMuted),
                S(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                S(TemplatedControl.PaddingProperty, new Thickness(S3, 7))
            }
        });
        Fill(styles, x => x.OfType<ComboBoxItem>().Class(":pointerover"), BgHover);
        Fill(styles, x => x.OfType<ComboBoxItem>().Class(":selected"), BgActive);
        Fill(styles, x => x.OfType<ComboBoxItem>().Class(":selected").Class(":pointerover"), BgActive);
        styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        {
            Setters = { S(ContentPresenter.ForegroundProperty, Text) }
        });
        styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class(":pointerover").Template().OfType<ContentPresenter>())
        {
            Setters = { S(ContentPresenter.ForegroundProperty, Text) }
        });
    }
}
