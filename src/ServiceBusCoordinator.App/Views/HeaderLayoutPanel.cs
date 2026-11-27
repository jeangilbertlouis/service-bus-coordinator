using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ServiceBusCoordinator.App.Views;

/// <summary>
/// Lays out a header (first child) and its actions (second child) side by side, actions on the right, while both fit
/// at their natural width. When they don't, the actions move onto their own line below, left-aligned, and the header
/// gets the full width. This keeps a long entity name from running under the buttons in a narrow window.
/// </summary>
public sealed class HeaderLayoutPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<HeaderLayoutPanel, double>(nameof(Spacing), 12);

    static HeaderLayoutPanel() => AffectsMeasure<HeaderLayoutPanel>(SpacingProperty);

    /// <summary>The gap between the header and the actions, beside or below it.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Whether the actions are currently on their own line.</summary>
    public bool IsStacked { get; private set; }

    private Control? Header => Children.Count > 0 ? Children[0] : null;

    private Control? Actions => Children.Count > 1 && Children[1].IsVisible ? Children[1] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var header = Header;
        if (header is null)
        {
            return default;
        }

        var actions = Actions;
        if (actions is null)
        {
            IsStacked = false;
            header.Measure(availableSize);
            return header.DesiredSize;
        }

        // Natural sizes first, to decide whether both fit on one line.
        header.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        actions.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var naturalHeader = header.DesiredSize;
        var naturalActions = actions.DesiredSize;

        IsStacked = naturalHeader.Width + Spacing + naturalActions.Width > availableSize.Width;
        if (!IsStacked)
        {
            header.Measure(new Size(Math.Max(0, availableSize.Width - naturalActions.Width - Spacing), availableSize.Height));
            return new Size(
                double.IsInfinity(availableSize.Width) ? naturalHeader.Width + Spacing + naturalActions.Width : availableSize.Width,
                Math.Max(header.DesiredSize.Height, naturalActions.Height));
        }

        var lineSize = new Size(availableSize.Width, double.PositiveInfinity);
        header.Measure(lineSize);
        actions.Measure(lineSize); // A WrapPanel of buttons wraps further in a very narrow window.
        return new Size(
            Math.Max(header.DesiredSize.Width, actions.DesiredSize.Width),
            header.DesiredSize.Height + Spacing + actions.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var header = Header;
        if (header is null)
        {
            return finalSize;
        }

        var actions = Actions;
        if (actions is null)
        {
            header.Arrange(new Rect(finalSize));
            return finalSize;
        }

        if (!IsStacked)
        {
            var actionsWidth = actions.DesiredSize.Width;
            var headerWidth = Math.Max(0, finalSize.Width - actionsWidth - Spacing);
            header.Arrange(new Rect(0, 0, headerWidth, finalSize.Height));
            var y = actions.VerticalAlignment == VerticalAlignment.Center ? Math.Max(0, (finalSize.Height - actions.DesiredSize.Height) / 2) : 0;
            actions.Arrange(new Rect(finalSize.Width - actionsWidth, y, actionsWidth, actions.DesiredSize.Height));
            return finalSize;
        }

        var headerHeight = header.DesiredSize.Height;
        header.Arrange(new Rect(0, 0, finalSize.Width, headerHeight));
        actions.Arrange(new Rect(0, headerHeight + Spacing, finalSize.Width, actions.DesiredSize.Height));
        return finalSize;
    }
}
