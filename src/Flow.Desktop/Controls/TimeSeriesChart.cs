using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Flow.Desktop.ViewModels;

namespace Flow.Desktop.Controls;

/// <summary>
/// Lightweight, dependency-free dual-series area chart rendered with Avalonia's
/// vector drawing primitives. Chosen over third-party chart packages so Wayland
/// rendering/DPI behavior stays on the verified Avalonia Skia path (see ADR 0004).
/// </summary>
public sealed class TimeSeriesChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    /// <summary>The selected range's start; when set with <see cref="WindowEnd"/>, the x axis spans it.</summary>
    public static readonly StyledProperty<DateTimeOffset?> WindowStartProperty =
        AvaloniaProperty.Register<TimeSeriesChart, DateTimeOffset?>(nameof(WindowStart));

    /// <summary>The selected range's end; when set with <see cref="WindowStart"/>, the x axis spans it.</summary>
    public static readonly StyledProperty<DateTimeOffset?> WindowEndProperty =
        AvaloniaProperty.Register<TimeSeriesChart, DateTimeOffset?>(nameof(WindowEnd));

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(EmptyText), "No persisted buckets in this range.");

    /// <summary>Textual alternative for the drawing, also used as automation help text.</summary>
    public static readonly StyledProperty<string> SummaryProperty =
        AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(Summary), "");

    public static readonly StyledProperty<IBrush?> RxBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(RxBrush), new SolidColorBrush(Color.Parse("#4EDEA3")));

    public static readonly StyledProperty<IBrush?> TxBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(TxBrush), new SolidColorBrush(Color.Parse("#4CD7F6")));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(GridBrush), new SolidColorBrush(Color.Parse("#26313C")));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(LabelBrush), new SolidColorBrush(Color.Parse("#819096")));

    static TimeSeriesChart()
    {
        AffectsRender<TimeSeriesChart>(PointsProperty, WindowStartProperty, WindowEndProperty,
            RxBrushProperty, TxBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<ChartPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public DateTimeOffset? WindowStart
    {
        get => GetValue(WindowStartProperty);
        set => SetValue(WindowStartProperty, value);
    }

    public DateTimeOffset? WindowEnd
    {
        get => GetValue(WindowEndProperty);
        set => SetValue(WindowEndProperty, value);
    }

    /// <summary>Fraction of the selected window at which a bucket starts, clamped to [0,1].</summary>
    internal static double WindowFraction(DateTimeOffset timestamp, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var startTicks = windowStart.UtcDateTime.Ticks;
        var endTicks = windowEnd.UtcDateTime.Ticks;
        if (endTicks <= startTicks) return 0;
        return Math.Clamp((timestamp.UtcDateTime.Ticks - startTicks) / (double)(endTicks - startTicks), 0, 1);
    }

    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public string Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    private int _focusedIndex = -1;

    /// <summary>The keyboard-selected data point as text, or empty when none is selected.</summary>
    public string FocusedPointText
    {
        get
        {
            var points = Points;
            if (points is null || _focusedIndex < 0 || _focusedIndex >= points.Count) return "";
            var point = points[_focusedIndex];
            return $"point {_focusedIndex + 1} of {points.Count}, {DisplayTime.Full(point.Timestamp)} local, " +
                   $"RX {FormatBytes(point.RxBytes)}, TX {FormatBytes(point.TxBytes)}, total {FormatBytes(point.Total)}";
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimeSeriesChartAutomationPeer(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var points = Points;
        if (points is not null && points.Count > 0)
        {
            var handled = true;
            switch (e.Key)
            {
                case Key.Left:
                    _focusedIndex = _focusedIndex < 0 ? points.Count - 1 : Math.Max(0, _focusedIndex - 1);
                    break;
                case Key.Right:
                    _focusedIndex = _focusedIndex < 0 ? 0 : Math.Min(points.Count - 1, _focusedIndex + 1);
                    break;
                case Key.Home:
                    _focusedIndex = 0;
                    break;
                case Key.End:
                    _focusedIndex = points.Count - 1;
                    break;
                default:
                    handled = false;
                    break;
            }
            if (handled)
            {
                e.Handled = true;
                InvalidateVisual();
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // Expose a text alternative immediately on focus, before any arrow key.
        if (Points is { Count: > 0 } && _focusedIndex < 0)
            _focusedIndex = 0;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        _focusedIndex = -1;
        base.OnLostFocus(e);
    }

    public IBrush? RxBrush
    {
        get => GetValue(RxBrushProperty);
        set => SetValue(RxBrushProperty, value);
    }

    public IBrush? TxBrush
    {
        get => GetValue(TxBrushProperty);
        set => SetValue(TxBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 60 || bounds.Height < 60) return;

        var points = Points;
        var labelBrush = LabelBrush ?? Brushes.Gray;
        var gridBrush = GridBrush ?? Brushes.DimGray;
        var rxBrush = RxBrush ?? Brushes.SeaGreen;
        var txBrush = TxBrush ?? Brushes.DeepSkyBlue;

        var left = 48d;
        var right = 14d;
        var top = 18d;
        var bottom = 24d;
        var plot = new Rect(left, top, Math.Max(1, bounds.Width - left - right), Math.Max(1, bounds.Height - top - bottom));

        if (points is null || points.Count == 0)
        {
            DrawCentered(context, EmptyText, bounds, labelBrush);
            return;
        }

        var maxValue = points.Max(p => Math.Max(p.RxBytes, p.TxBytes));
        if (maxValue <= 0) maxValue = 1;

        // When the selected window is supplied, buckets are placed by their
        // timestamp so the chart reflects the range the user chose (including
        // periods with no data). Without a window, fall back to even spacing.
        var hasWindow = WindowStart is not null && WindowEnd is not null && WindowEnd.Value > WindowStart.Value;
        var windowStartTicks = hasWindow ? WindowStart!.Value.UtcDateTime.Ticks : 0;
        var windowEndTicks = hasWindow ? WindowEnd!.Value.UtcDateTime.Ticks : 0;

        double XAt(int index)
        {
            var fraction = hasWindow
                ? WindowFraction(points[index].Timestamp, WindowStart!.Value, WindowEnd!.Value)
                : (points.Count == 1 ? 0.5 : index / (double)(points.Count - 1));
            return plot.Left + fraction * plot.Width;
        }

        // Horizontal grid + y labels.
        const int gridLines = 4;
        for (var i = 0; i <= gridLines; i++)
        {
            var fraction = i / (double)gridLines;
            var y = plot.Bottom - fraction * plot.Height;
            context.DrawLine(new Pen(gridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            var value = (long)(maxValue * fraction);
            DrawText(context, FormatBytes(value), new Point(4, y - 7), 10, labelBrush);
        }

        // X labels: the selected window's boundaries, or the data extent when
        // no window was supplied.
        if (hasWindow)
        {
            var midTicks = windowStartTicks + (windowEndTicks - windowStartTicks) / 2;
            DrawText(context, DisplayTime.Axis(WindowStart!.Value), new Point(plot.Left, plot.Bottom + 6), 10, labelBrush);
            DrawText(context, DisplayTime.Axis(new DateTimeOffset(midTicks, TimeSpan.Zero)),
                new Point(plot.Left + plot.Width / 2 - 12, plot.Bottom + 6), 10, labelBrush);
            DrawText(context, DisplayTime.Axis(WindowEnd!.Value), new Point(plot.Right - 26, plot.Bottom + 6), 10, labelBrush);
        }
        else
        {
            DrawText(context, DisplayTime.Axis(points[0].Timestamp), new Point(plot.Left, plot.Bottom + 6), 10, labelBrush);
            if (points.Count > 2)
            {
                var mid = DisplayTime.Axis(points[points.Count / 2].Timestamp);
                DrawText(context, mid, new Point(plot.Left + plot.Width / 2 - 12, plot.Bottom + 6), 10, labelBrush);
            }
            DrawText(context, DisplayTime.Axis(points[^1].Timestamp), new Point(plot.Right - 26, plot.Bottom + 6), 10, labelBrush);
        }

        // Series geometry.
        DrawSeries(context, points, p => p.RxBytes, maxValue, plot, rxBrush, 0x33, XAt);
        DrawSeries(context, points, p => p.TxBytes, maxValue, plot, txBrush, 0x2A, XAt);

        // Keyboard-focused point marker (also exposed to assistive technology).
        if (_focusedIndex >= 0 && _focusedIndex < points.Count)
        {
            var fx = XAt(_focusedIndex);
            var fy = plot.Bottom - Math.Max(points[_focusedIndex].RxBytes, points[_focusedIndex].TxBytes) / (double)maxValue * plot.Height;
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White), 2), new Point(fx, fy), 6, 6);
        }

        // Peak marker based on total traffic (rx+tx).
        var peakIndex = 0;
        for (var i = 1; i < points.Count; i++)
            if (points[i].Total > points[peakIndex].Total) peakIndex = i;
        var peak = points[peakIndex];
        if (peak.Total > 0)
        {
            var x = XAt(peakIndex);
            var y = plot.Bottom - Math.Max(peak.RxBytes, peak.TxBytes) / (double)maxValue * plot.Height;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#884CD7F6")), 1, new DashStyle([3, 3], 0)), new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#4CD7F6")), null, new Point(x, y), 3.5, 3.5);
            var label = $"peak {FormatBytes(peak.Total)} @ {DisplayTime.Axis(peak.Timestamp)}";
            DrawText(context, label, new Point(Math.Max(plot.Left, Math.Min(plot.Right - 130, x + 6)), plot.Top + 2), 10, txBrush);
        }
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<ChartPoint> points, Func<ChartPoint, long> selector,
        long maxValue, Rect plot, IBrush brush, byte fillAlpha, Func<int, double> xAt)
    {
        if (points.Count == 0) return;

        Point PointAt(int i)
        {
            var y = plot.Bottom - selector(points[i]) / (double)maxValue * plot.Height;
            return new Point(xAt(i), y);
        }

        // A single bucket is a bar/dot at its place in the window, not a
        // zero-length line that would draw nothing.
        if (points.Count == 1)
        {
            var only = PointAt(0);
            context.DrawLine(new Pen(brush, 1.6), new Point(only.X, plot.Bottom), only);
            context.DrawEllipse(brush, null, only, 3, 3);
            return;
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(PointAt(0), false);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(PointAt(i));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, 1.6), line);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(PointAt(0).X, plot.Bottom), true);
            ctx.LineTo(PointAt(0));
            for (var i = 1; i < points.Count; i++) ctx.LineTo(PointAt(i));
            ctx.LineTo(new Point(PointAt(points.Count - 1).X, plot.Bottom));
            ctx.EndFigure(true);
        }

        var color = brush is ISolidColorBrush scb ? scb.Color : Colors.Transparent;
        var fill = new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B));
        context.DrawGeometry(fill, null, area);
    }

    private static void DrawCentered(DrawingContext context, string text, Rect bounds, IBrush brush)
    {
        var formatted = CreateText(text, 12, brush);
        context.DrawText(formatted, new Point((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2));
    }

    internal static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush)
        => context.DrawText(CreateText(text, size, brush), origin);

    private static FormattedText CreateText(string text, double size, IBrush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), size, brush);

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var s = unit == 0 ? bytes.ToString(CultureInfo.InvariantCulture) : (Math.Sign(bytes) * value).ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        return $"{s} {units[unit]}";
    }
}
