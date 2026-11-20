using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Flow.Desktop.Controls;

/// <summary>
/// Automation peers for the custom-drawn controls. Without these, screen
/// readers see only an unnamed drawing surface and cannot read the real values.
/// </summary>
internal sealed class TimeSeriesChartAutomationPeer(TimeSeriesChart owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetClassNameCore() => nameof(TimeSeriesChart);

    protected override string? GetNameCore()
    {
        var point = owner.FocusedPointText;
        if (!string.IsNullOrWhiteSpace(point)) return point;
        return string.IsNullOrWhiteSpace(owner.Summary) ? base.GetNameCore() : owner.Summary;
    }

    protected override string? GetHelpTextCore()
        => string.IsNullOrWhiteSpace(owner.Summary) ? base.GetHelpTextCore() : owner.Summary;

    protected override string? GetItemStatusCore()
        => string.IsNullOrWhiteSpace(owner.FocusedPointText) ? owner.Summary : owner.FocusedPointText;

    protected override bool IsKeyboardFocusableCore() => true;
}

internal sealed class SplitBarAutomationPeer(SplitBar owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
    protected override string GetClassNameCore() => nameof(SplitBar);
    protected override string? GetItemStatusCore()
        => $"first {owner.First:P0}, second {owner.Second:P0}";
}

internal sealed class UsageBarAutomationPeer(UsageBar owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
    protected override string GetClassNameCore() => nameof(UsageBar);
    protected override string? GetItemStatusCore() => $"{owner.Value:P0}";
}
