using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Flow.Desktop.Controls;

/// <summary>
/// Proportion bar used for RX/TX and attributed/unattributed splits.
/// Values are fractions in the range 0..1.
/// </summary>
public sealed class SplitBar : Control
{
    public static readonly StyledProperty<double> FirstProperty =
        AvaloniaProperty.Register<SplitBar, double>(nameof(First));

    public static readonly StyledProperty<double> SecondProperty =
        AvaloniaProperty.Register<SplitBar, double>(nameof(Second));

    public static readonly StyledProperty<IBrush?> FirstBrushProperty =
        AvaloniaProperty.Register<SplitBar, IBrush?>(nameof(FirstBrush), new SolidColorBrush(Color.Parse("#4EDEA3")));

    public static readonly StyledProperty<IBrush?> SecondBrushProperty =
        AvaloniaProperty.Register<SplitBar, IBrush?>(nameof(SecondBrush), new SolidColorBrush(Color.Parse("#4CD7F6")));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SplitBar, IBrush?>(nameof(TrackBrush), new SolidColorBrush(Color.Parse("#262C34")));

    static SplitBar()
    {
        AffectsRender<SplitBar>(FirstProperty, SecondProperty, FirstBrushProperty, SecondBrushProperty, TrackBrushProperty);
    }

    public double First
    {
        get => GetValue(FirstProperty);
        set => SetValue(FirstProperty, value);
    }

    public double Second
    {
        get => GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    public IBrush? FirstBrush
    {
        get => GetValue(FirstBrushProperty);
        set => SetValue(FirstBrushProperty, value);
    }

    public IBrush? SecondBrush
    {
        get => GetValue(SecondBrushProperty);
        set => SetValue(SecondBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;
        var track = TrackBrush ?? Brushes.DimGray;
        context.DrawRectangle(track, null, new Rect(0, 0, w, h), radius, radius);

        var total = First + Second;
        if (total <= 0) return;

        var firstWidth = w * (First / total);
        var secondWidth = w - firstWidth;

        if (firstWidth > 0)
            context.DrawRectangle(FirstBrush, null, new Rect(0, 0, firstWidth, h), radius, radius);
        if (secondWidth > 0)
            context.DrawRectangle(SecondBrush, null, new Rect(firstWidth, 0, secondWidth, h), radius, radius);
    }
}

/// <summary>
/// Horizontal proportional bar for a single value relative to a maximum (0..1).
/// </summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(BarBrush), new SolidColorBrush(Color.Parse("#4CD7F6")));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(TrackBrush), new SolidColorBrush(Color.Parse("#262C34")));

    static UsageBar()
    {
        AffectsRender<UsageBar>(ValueProperty, BarBrushProperty, TrackBrushProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        context.DrawRectangle(TrackBrush, null, new Rect(0, 0, w, h), radius, radius);
        var fraction = System.Math.Clamp(Value, 0, 1);
        if (fraction <= 0) return;
        var width = System.Math.Max(h, fraction * w);
        context.DrawRectangle(BarBrush, null, new Rect(0, 0, width, h), radius, radius);
    }
}
