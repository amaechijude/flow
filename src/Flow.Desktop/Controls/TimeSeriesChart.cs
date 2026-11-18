using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(EmptyText), "No persisted buckets in this range.");

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
        AffectsRender<TimeSeriesChart>(PointsProperty, RxBrushProperty, TxBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<ChartPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
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

        // X labels: first / middle / last real bucket timestamps.
        DrawText(context, points[0].Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), new Point(plot.Left, plot.Bottom + 6), 10, labelBrush);
        if (points.Count > 2)
        {
            var mid = points[points.Count / 2].Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            DrawText(context, mid, new Point(plot.Left + plot.Width / 2 - 12, plot.Bottom + 6), 10, labelBrush);
        }
        var lastLabel = points[^1].Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        DrawText(context, lastLabel, new Point(plot.Right - 26, plot.Bottom + 6), 10, labelBrush);

        // Series geometry.
        DrawSeries(context, points, p => p.RxBytes, maxValue, plot, rxBrush, 0x33);
        DrawSeries(context, points, p => p.TxBytes, maxValue, plot, txBrush, 0x2A);

        // Peak marker based on total traffic (rx+tx).
        var peakIndex = 0;
        for (var i = 1; i < points.Count; i++)
            if (points[i].Total > points[peakIndex].Total) peakIndex = i;
        var peak = points[peakIndex];
        if (peak.Total > 0)
        {
            var x = plot.Left + (points.Count == 1 ? plot.Width : peakIndex / (double)(points.Count - 1) * plot.Width);
            var y = plot.Bottom - Math.Max(peak.RxBytes, peak.TxBytes) / (double)maxValue * plot.Height;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#884CD7F6")), 1, new DashStyle([3, 3], 0)), new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#4CD7F6")), null, new Point(x, y), 3.5, 3.5);
            var label = $"peak {FormatBytes(peak.Total)} @ {peak.Timestamp.ToLocalTime():HH:mm}";
            DrawText(context, label, new Point(Math.Max(plot.Left, Math.Min(plot.Right - 130, x + 6)), plot.Top + 2), 10, txBrush);
        }
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<ChartPoint> points, Func<ChartPoint, long> selector, long maxValue, Rect plot, IBrush brush, byte fillAlpha)
    {
        if (points.Count == 0) return;

        Point PointAt(int i)
        {
            var x = points.Count == 1 ? plot.Left + plot.Width / 2 : plot.Left + i / (double)(points.Count - 1) * plot.Width;
            var y = plot.Bottom - selector(points[i]) / (double)maxValue * plot.Height;
            return new Point(x, y);
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
