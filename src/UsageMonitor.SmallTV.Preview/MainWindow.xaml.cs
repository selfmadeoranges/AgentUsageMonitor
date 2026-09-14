using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace UsageMonitor.SmallTV.Preview;

public partial class MainWindow : Window
{
    private static readonly Brush WhiteBrush = Brushes.White;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _layoutReloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DisplayLayout _layout = null!;
    private FileSystemWatcher? _layoutWatcher;
    private string _layoutPath = "";
    private Brush _backgroundBrush = Brushes.Black;
    private Brush _panelBrush = Brushes.DimGray;
    private Brush _mutedBrush = Brushes.Gray;
    private Brush _greenBrush = Brushes.Green;
    private Brush _orangeBrush = Brushes.Orange;
    private Brush _redBrush = Brushes.Red;
    private int _millisecondsOnPage;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        _layoutPath = DisplayLayout.FindPath();
        _layout = DisplayLayout.Load(_layoutPath);
        ApplyPalette();
        ConfigureLayoutWatcher();

        _timer.Tick += TimerTick;
        _layoutReloadTimer.Tick += LayoutReloadTimerTick;
        _timer.Start();
        _initialized = true;
        Loaded += (_, _) => ApplyPreviewScale();
        DpiChanged += (_, _) => ApplyPreviewScale();
        Closed += (_, _) => DisposeResources();
        ApplyPreviewScale();
        ShowLayoutLoaded();
        RenderPreview();
    }

    private void ConfigureLayoutWatcher()
    {
        _layoutWatcher = new FileSystemWatcher(IOPath.GetDirectoryName(_layoutPath)!, IOPath.GetFileName(_layoutPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _layoutWatcher.Changed += LayoutFileChanged;
        _layoutWatcher.Created += LayoutFileChanged;
        _layoutWatcher.Renamed += LayoutFileChanged;
    }

    private void LayoutFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _layoutReloadTimer.Stop();
            _layoutReloadTimer.Start();
        });
    }

    private void LayoutReloadTimerTick(object? sender, EventArgs e)
    {
        _layoutReloadTimer.Stop();
        try
        {
            _layout = DisplayLayout.Load(_layoutPath);
            ApplyPalette();
            ApplyPreviewScale();
            ShowLayoutLoaded();
            RenderPreview();
        }
        catch (Exception exception)
        {
            LayoutStatusText.Foreground = _redBrush;
            LayoutStatusText.Text = $"레이아웃 오류: {exception.Message}";
        }
    }

    private void ShowLayoutLoaded()
    {
        LayoutStatusText.Foreground = _mutedBrush;
        LayoutStatusText.Text = $"공용 레이아웃 자동 감시 중 · {IOPath.GetFileName(_layoutPath)}";
        AutoSwitchCheck.Content = $"{_layout.Display.PageIntervalMs / 1000.0:0.#}초마다 Claude/Codex 전환";
    }

    private void ApplyPalette()
    {
        _backgroundBrush = FromRgb565(_layout.Color(_layout.Colors.Background));
        _panelBrush = FromRgb565(_layout.Color(_layout.Colors.Panel));
        _mutedBrush = FromRgb565(_layout.Color(_layout.Colors.Muted));
        _greenBrush = FromRgb565(_layout.Color(_layout.Colors.Green));
        _orangeBrush = FromRgb565(_layout.Color(_layout.Colors.Orange));
        _redBrush = FromRgb565(_layout.Color(_layout.Colors.Red));
    }

    private void TimerTick(object? sender, EventArgs e)
    {
        if (AutoSwitchCheck.IsChecked == true && RecoveryCheck.IsChecked != true)
        {
            _millisecondsOnPage += (int)_timer.Interval.TotalMilliseconds;
            if (_millisecondsOnPage >= _layout.Display.PageIntervalMs)
            {
                _millisecondsOnPage = 0;
                ProviderCombo.SelectedIndex = ProviderCombo.SelectedIndex == 0 ? 1 : 0;
            }
        }

        RenderPreview();
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _millisecondsOnPage = 0;
        PercentagesText.Text = SelectedTag(ProviderCombo) == "Codex" ? "7" : "8,18,35";
        RenderPreview();
    }

    private void PreviewChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            RenderPreview();
        }
    }

    private void ScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyPreviewScale();
        }
    }

    private void ApplyPreviewScale()
    {
        if (LcdViewbox == null || ActualSizeText == null || _layout == null)
        {
            return;
        }

        int scale = SelectedTag(ScaleCombo) == "1" ? 1 : 2;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        LcdViewbox.Width = _layout.Display.Width * scale / dpi.DpiScaleX;
        LcdViewbox.Height = _layout.Display.Height * scale / dpi.DpiScaleY;
        LcdCanvas.Width = _layout.Display.Width;
        LcdCanvas.Height = _layout.Display.Height;
        ActualSizeText.Text = $"LCD 영역: {_layout.Display.Width * scale}×{_layout.Display.Height * scale} 물리 픽셀 · Windows 배율 {dpi.DpiScaleX * 100:0}%";
    }

    private void ResetSamplesClicked(object sender, RoutedEventArgs e)
    {
        ProviderCombo.SelectedIndex = 0;
        StateCombo.SelectedIndex = 0;
        IpText.Text = "192.168.50.120";
        RecoveryPasswordText.Text = "UM123456ABCD";
        RecoveryCheck.IsChecked = false;
        AutoSwitchCheck.IsChecked = true;
        PercentagesText.Text = "8,18,35";
        _millisecondsOnPage = 0;
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (LcdCanvas == null || _layout == null)
        {
            return;
        }

        LcdCanvas.Children.Clear();
        LcdCanvas.Background = _backgroundBrush;

        if (RecoveryCheck.IsChecked == true)
        {
            DrawRecoveryPage();
            return;
        }

        string provider = SelectedTag(ProviderCombo);
        string state = SelectedTag(StateCombo);
        bool stale = state == "stale";
        IReadOnlyList<double> percentages = ParsePercentages(PercentagesText.Text);
        string[] labels = provider == "Codex"
            ? ["Week"]
            : ["5 hours", "Week", "Fable"];

        TextPosition title = _layout.UsagePage.Title;
        TextPosition counter = _layout.UsagePage.Counter;
        UsageRowDefinition row = _layout.UsagePage.Row;
        FooterDefinition footer = _layout.UsagePage.Footer;
        int windowCount = state is "auth_required" or "waiting" ? 0 : Math.Min(Math.Min(labels.Length, percentages.Count), row.Count);

        AddText(title.X, title.Y, provider, WhiteBrush, FontSizeFor(title.Font, title.Scale), FontWeights.SemiBold);
        AddText(counter.X, counter.Y, provider == "Claude" ? "1/2" : "2/2", _mutedBrush,
            FontSizeFor(counter.Font, counter.Scale));

        for (int index = 0; index < row.Count; index++)
        {
            int y = row.Y + index * row.Step;
            AddRectangle(row.X, y, row.Width, row.Height, _backgroundBrush);
            if (index >= windowCount)
            {
                continue;
            }

            double percent = percentages[index];
            Brush color = stale ? _mutedBrush : ColorFor(percent);
            AddText(row.LabelX, y + row.LabelOffsetY, labels[index], WhiteBrush, FontSizeFor(row.LabelFont));
            AddRightText(row.PercentRight, y + row.PercentOffsetY, $"{percent:0}%", color,
                FontSizeFor(row.PercentFont), FontWeights.SemiBold);
            AddRectangle(row.BarX, y + row.BarOffsetY, row.BarWidth, row.BarHeight, _panelBrush);
            AddRectangle(row.BarX, y + row.BarOffsetY, row.BarWidth * percent / 100.0, row.BarHeight, color);
            AddText(row.CountdownX, y + row.CountdownOffsetY, CountdownFor(provider, index), _mutedBrush,
                FontSizeFor(row.CountdownFont));
        }

        if (windowCount == 0)
        {
            TextPosition empty = _layout.UsagePage.Empty;
            string message = state switch
            {
                "auth_required" => _layout.Text.ConfigureAccount,
                "waiting" => _layout.Text.LoadingUsage,
                "rate_limited" => _layout.Text.RetryingLater,
                _ => _layout.Text.UsageUnavailable,
            };
            AddText(empty.X, empty.Y, message, _mutedBrush, FontSizeFor(empty.Font, empty.Scale));
        }

        AddRectangle(footer.X, footer.Y, footer.Width, footer.Height, _backgroundBrush);
        string displayState = DisplayState(state);
        AddText(footer.StateX, footer.StateY, displayState, displayState == "ok" ? _mutedBrush : _orangeBrush,
            FontSizeFor(footer.StateFont));
        AddText(footer.IpX, footer.IpY, $"{_layout.Text.IpPrefix}{IpText.Text.Trim()}", _mutedBrush,
            FontSizeFor(footer.IpFont));
    }

    private void DrawRecoveryPage()
    {
        RecoveryPageDefinition page = _layout.RecoveryPage;
        AddPositionedText(page.Title, page.Title.Text, WhiteBrush, FontWeights.SemiBold);
        AddPositionedText(page.ConnectLabel, page.ConnectLabel.Text, _mutedBrush);
        AddPositionedText(page.Ssid, "UsageMonitor-ABC123", _orangeBrush);
        AddPositionedText(page.PasswordLabel, page.PasswordLabel.Text, _mutedBrush);
        AddPositionedText(page.Password, RecoveryPasswordText.Text.Trim(), _orangeBrush);
        AddPositionedText(page.OpenAddress, page.OpenAddress.Text, WhiteBrush);
        AddPositionedText(page.User, page.User.Text, _mutedBrush);
    }

    private void AddPositionedText(TextPosition position, string text, Brush color, FontWeight? weight = null)
    {
        AddText(position.X, position.Y, text, color, FontSizeFor(position.Font, position.Scale), weight);
    }

    private static IReadOnlyList<double> ParsePercentages(string value)
    {
        var result = new List<double>();
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                result.Add(Math.Clamp(parsed, 0, 100));
            }
        }
        return result;
    }

    private static string CountdownFor(string provider, int index)
    {
        if (provider == "Codex")
        {
            return "reset 5d 0h";
        }

        return index switch
        {
            0 => "reset 3h 18m",
            1 => "reset 6h 8m",
            _ => "reset 6h 8m",
        };
    }

    private string DisplayState(string state)
    {
        return state switch
        {
            "stale" => _layout.Text.StateStale,
            "rate_limited" => _layout.Text.StateRateLimited,
            "auth_required" => _layout.Text.StateAuthRequired,
            "network_error" => _layout.Text.StateNetworkError,
            "invalid_response" => _layout.Text.StateInvalidResponse,
            _ => state,
        };
    }

    private static string SelectedTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    }

    private Brush ColorFor(double percent)
    {
        if (percent >= 95)
        {
            return _redBrush;
        }
        if (percent >= 70)
        {
            return _orangeBrush;
        }
        return _greenBrush;
    }

    private static double FontSizeFor(int font, int scale = 1)
    {
        double baseSize = font switch
        {
            1 => 8,
            4 => 20,
            _ => 11,
        };
        return baseSize * Math.Max(scale, 1);
    }

    private void AddText(double x, double y, string text, Brush color, double size, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = color,
            FontFamily = new FontFamily("Consolas"),
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        LcdCanvas.Children.Add(block);
    }

    private void AddRightText(double right, double y, string text, Brush color, double size, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = color,
            FontFamily = new FontFamily("Consolas"),
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(block, right - block.DesiredSize.Width);
        Canvas.SetTop(block, y);
        LcdCanvas.Children.Add(block);
    }

    private void AddRectangle(double x, double y, double width, double height, Brush fill)
    {
        var rectangle = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = height,
            Fill = fill,
        };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        LcdCanvas.Children.Add(rectangle);
    }

    private static Brush FromRgb565(ushort value)
    {
        byte red = (byte)Math.Round(((value >> 11) & 0x1F) * 255.0 / 31.0);
        byte green = (byte)Math.Round(((value >> 5) & 0x3F) * 255.0 / 63.0);
        byte blue = (byte)Math.Round((value & 0x1F) * 255.0 / 31.0);
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void DisposeResources()
    {
        _timer.Stop();
        _layoutReloadTimer.Stop();
        _layoutWatcher?.Dispose();
    }
}
