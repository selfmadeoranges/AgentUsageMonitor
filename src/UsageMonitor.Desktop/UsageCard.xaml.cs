using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>도구 하나의 사용량을 보여주는 카드. 오류 시 회색 처리하고 마지막 값을 유지한다.</summary>
public partial class UsageCard : UserControl
{
    private static readonly Brush GreenBrush = MakeBrush(0x4C, 0xAF, 0x50);
    private static readonly Brush OrangeBrush = MakeBrush(0xFF, 0xA7, 0x26);
    private static readonly Brush RedBrush = MakeBrush(0xEF, 0x53, 0x50);
    private static readonly Brush GreyBrush = MakeBrush(0x90, 0x90, 0x90);

    private List<UsageWindow> _lastWindows = new();

    public UsageCard()
    {
        InitializeComponent();
    }

    public void SetTitle(string title)
    {
        TitleText.Text = title;

        // Assets/<도구 이름 소문자>.png 가 리소스로 있으면 제목 옆에 아이콘을 보여준다.
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{title.ToLowerInvariant()}.png", UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            IconImage.Source = bitmap;
            IconImage.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            IconImage.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>앞에 다른 도구가 보일 때 왼쪽 구분선을 켠다.</summary>
    public void SetDividerVisible(bool visible)
    {
        Divider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Apply(UsageSnapshot snapshot)
    {
        bool hasError = snapshot.Error != null;
        // 429 대기처럼 값이 여전히 유효한 오류는 색과 갱신 시각을 유지하고 안내만 덧붙인다.
        bool greyOut = hasError && !snapshot.KeepValues;

        if (snapshot.Windows.Count > 0)
        {
            _lastWindows = snapshot.Windows;
        }

        var rows = new List<UsageRow>();
        foreach (UsageWindow window in _lastWindows)
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
        Rows.ItemsSource = rows;

        if (hasError)
        {
            ErrorText.Text = snapshot.Error;
            ErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorText.Visibility = Visibility.Collapsed;
            // 한 줄 바를 덜 복잡하게 하려고 갱신 시각은 마우스를 올렸을 때만 보여준다.
            Root.ToolTip = "마지막 갱신 " + snapshot.FetchedAt.ToString("HH:mm:ss");
        }

        if (greyOut)
        {
            Root.ToolTip = null;
            Root.Opacity = 0.55;
        }
        else
        {
            Root.Opacity = 1.0;
        }
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

    private sealed class UsageRow
    {
        public string Label { get; init; } = "";
        public double Percent { get; init; }
        public string PercentText { get; init; } = "";
        public string ResetText { get; init; } = "";
        public Brush BarBrush { get; init; } = GreyBrush;
    }
}
