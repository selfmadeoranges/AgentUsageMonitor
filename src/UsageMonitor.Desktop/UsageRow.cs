using System.Windows.Media;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>한도 창 하나의 표시 값. 고정 바(UsageBarItem)와 플로팅 카드(UsageCard)가 같은 색 규칙으로 쓴다.</summary>
public sealed class UsageRow
{
    private static readonly Brush GreenBrush = MakeBrush(0x4C, 0xAF, 0x50);
    private static readonly Brush OrangeBrush = MakeBrush(0xFF, 0xA7, 0x26);
    private static readonly Brush RedBrush = MakeBrush(0xEF, 0x53, 0x50);
    private static readonly Brush GreyBrush = MakeBrush(0x90, 0x90, 0x90);

    public string Label { get; init; } = "";
    public double Percent { get; init; }
    public string PercentText { get; init; } = "";
    public string ResetText { get; init; } = "";
    public Brush BarBrush { get; init; } = GreyBrush;

    /// <param name="greyOut">true면 값이 더 이상 유효하지 않은 오류이므로 막대를 회색으로 그린다.</param>
    public static List<UsageRow> Build(IEnumerable<UsageWindow> windows, bool greyOut)
    {
        var rows = new List<UsageRow>();
        foreach (UsageWindow window in windows)
        {
            rows.Add(new UsageRow
            {
                Label = window.Label,
                Percent = Math.Clamp(window.Percent, 0, 100),
                PercentText = $"{window.Percent:0}%",
                ResetText = Format.Remaining(window.ResetsAt),
                BarBrush = greyOut ? GreyBrush : BrushFor(window.Percent),
            });
        }
        return rows;
    }

    private static Brush BrushFor(double percent)
    {
        if (percent >= 95)
        {
            return RedBrush;
        }
        if (percent >= 70)
        {
            return OrangeBrush;
        }
        return GreenBrush;
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
