using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>플로팅 카드 창에서 도구 하나의 사용량을 보여주는 카드. 오류 시 회색 처리하고 마지막 값을 유지한다.</summary>
public partial class UsageCard : UserControl
{
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

    public void Apply(UsageSnapshot snapshot)
    {
        bool hasError = snapshot.Error != null;
        // 429 대기처럼 값이 여전히 유효한 오류는 색과 갱신 시각을 유지하고 안내만 덧붙인다.
        bool greyOut = hasError && !snapshot.KeepValues;

        if (snapshot.Windows.Count > 0)
        {
            _lastWindows = snapshot.Windows;
        }

        Rows.ItemsSource = UsageRow.Build(_lastWindows, greyOut);

        if (hasError)
        {
            ErrorText.Text = snapshot.Error;
            ErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorText.Visibility = Visibility.Collapsed;
            UpdatedText.Text = snapshot.FetchedAt.ToString("HH:mm:ss");
        }

        if (greyOut)
        {
            UpdatedText.Text = "";
            Root.Opacity = 0.55;
        }
        else
        {
            Root.Opacity = 1.0;
        }
    }
}
