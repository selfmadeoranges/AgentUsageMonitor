using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>트레이 아이콘과 컨텍스트 메뉴. 설정 변경은 이벤트로 App에 알린다.</summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly int[] RefreshChoices = { 30, 60, 120, 300, 600 };
    private static readonly int[] OpacityChoices = { 30, 50, 70, 90, 100 };

    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _showItem = new("위젯 표시") { CheckOnClick = true };
    private readonly ToolStripMenuItem _displayModeMenu = new("표시 방식");
    private readonly ToolStripMenuItem _modeBarItem = new("고정 바 (화면 가장자리)");
    private readonly ToolStripMenuItem _modeCardsItem = new("플로팅 카드 (자유 배치)");
    private readonly ToolStripMenuItem _resetPositionItem = new("카드 위치 초기화");
    private readonly ToolStripMenuItem _positionMenu = new("바 위치");
    private readonly ToolStripMenuItem _positionTopItem = new("화면 맨 위");
    private readonly ToolStripMenuItem _positionBottomItem = new("작업표시줄 바로 위");
    private readonly ToolStripMenuItem _claudeShowItem = new("Claude 표시") { CheckOnClick = true };
    private readonly ToolStripMenuItem _codexShowItem = new("Codex 표시") { CheckOnClick = true };
    private readonly ToolStripMenuItem _clickThroughItem = new("클릭 통과") { CheckOnClick = true };
    private readonly ToolStripMenuItem _alertItem = new("95% 도달 시 알림") { CheckOnClick = true };
    private readonly ToolStripMenuItem _autoStartItem = new("Windows 시작 시 실행") { CheckOnClick = true };
    private readonly ToolStripMenuItem _opacityMenu = new("불투명도");
    private readonly ToolStripMenuItem _claudeRefreshMenu = new("Claude 갱신 주기");
    private readonly ToolStripMenuItem _codexRefreshMenu = new("Codex 갱신 주기");
    private readonly ToolStripMenuItem _claudeLoginItem = new("Claude 로그인...");
    private readonly ToolStripMenuItem _claudeLogoutItem = new("Claude 로그아웃");
    private readonly ToolStripMenuItem _codexLoginItem = new("Codex 로그인...");
    private readonly ToolStripMenuItem _codexLogoutItem = new("Codex 로그아웃");
    private Icon? _currentIcon;

    public event Action? RefreshRequested;
    public event Action? ClaudeLoginRequested;
    public event Action? ClaudeLogoutRequested;
    public event Action? CodexLoginRequested;
    public event Action? CodexLogoutRequested;
    public event Action<bool>? WidgetVisibleChanged;
    /// <summary>true면 작업표시줄 바로 위, false면 화면 맨 위.</summary>
    public event Action<bool>? BarAtBottomChanged;
    /// <summary>true면 플로팅 카드, false면 고정 바.</summary>
    public event Action<bool>? FloatingCardsChanged;
    public event Action? ResetPositionRequested;
    /// <summary>(도구 이름, 표시 여부)</summary>
    public event Action<string, bool>? ToolVisibleChanged;
    public event Action<bool>? ClickThroughChanged;
    public event Action<double>? OpacityChanged;
    /// <summary>(도구 이름, 초)</summary>
    public event Action<string, int>? RefreshSecondsChanged;
    public event Action<bool>? AlertEnabledChanged;
    public event Action<bool>? AutoStartChanged;
    public event Action? ExitRequested;

    public TrayIcon(Settings settings)
    {
        _alertItem.Text = $"{settings.AlertThreshold}% 도달 시 알림";

        var refreshNow = new ToolStripMenuItem("지금 갱신");
        refreshNow.Click += (_, _) => RefreshRequested?.Invoke();

        _showItem.Click += (_, _) => WidgetVisibleChanged?.Invoke(_showItem.Checked);
        _modeBarItem.Click += (_, _) => OnDisplayModeClicked(false);
        _modeCardsItem.Click += (_, _) => OnDisplayModeClicked(true);
        _displayModeMenu.DropDownItems.Add(_modeBarItem);
        _displayModeMenu.DropDownItems.Add(_modeCardsItem);
        _resetPositionItem.Click += (_, _) => ResetPositionRequested?.Invoke();
        _positionTopItem.Click += (_, _) => OnPositionClicked(false);
        _positionBottomItem.Click += (_, _) => OnPositionClicked(true);
        _positionMenu.DropDownItems.Add(_positionTopItem);
        _positionMenu.DropDownItems.Add(_positionBottomItem);
        _claudeShowItem.Click += (_, _) => OnToolShowClicked(_claudeShowItem, _codexShowItem, "Claude");
        _codexShowItem.Click += (_, _) => OnToolShowClicked(_codexShowItem, _claudeShowItem, "Codex");
        _clickThroughItem.Click += (_, _) => ClickThroughChanged?.Invoke(_clickThroughItem.Checked);
        _alertItem.Click += (_, _) => AlertEnabledChanged?.Invoke(_alertItem.Checked);
        _autoStartItem.Click += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);

        foreach (int percent in OpacityChoices)
        {
            var item = new ToolStripMenuItem($"{percent}%") { Tag = percent };
            item.Click += (sender, _) =>
            {
                CheckOnly(_opacityMenu, (ToolStripMenuItem)sender!);
                OpacityChanged?.Invoke(percent / 100.0);
            };
            _opacityMenu.DropDownItems.Add(item);
        }

        FillRefreshMenu(_claudeRefreshMenu, "Claude");
        FillRefreshMenu(_codexRefreshMenu, "Codex");

        _claudeLoginItem.Click += (_, _) => ClaudeLoginRequested?.Invoke();
        _claudeLogoutItem.Click += (_, _) => ClaudeLogoutRequested?.Invoke();
        _codexLoginItem.Click += (_, _) => CodexLoginRequested?.Invoke();
        _codexLogoutItem.Click += (_, _) => CodexLogoutRequested?.Invoke();

        var exit = new ToolStripMenuItem("종료");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(refreshNow);
        menu.Items.Add(_claudeLoginItem);
        menu.Items.Add(_claudeLogoutItem);
        menu.Items.Add(_codexLoginItem);
        menu.Items.Add(_codexLogoutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showItem);
        menu.Items.Add(_displayModeMenu);
        menu.Items.Add(_positionMenu);
        menu.Items.Add(_resetPositionItem);
        menu.Items.Add(_claudeShowItem);
        menu.Items.Add(_codexShowItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_opacityMenu);
        menu.Items.Add(_claudeRefreshMenu);
        menu.Items.Add(_codexRefreshMenu);
        menu.Items.Add(_alertItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _notify = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Usage Monitor",
            Visible = true,
        };
        _notify.DoubleClick += (_, _) =>
        {
            _showItem.Checked = !_showItem.Checked;
            WidgetVisibleChanged?.Invoke(_showItem.Checked);
        };

        SyncFromSettings(settings);
        SetIcon(DrawIcon(Array.Empty<UsageSnapshot>()));
    }

    /// <summary>모니터 자체 로그인 여부에 따라 로그인/로그아웃 항목을 바꿔 보여준다.</summary>
    public void SetClaudeLoggedIn(bool loggedIn)
    {
        _claudeLoginItem.Visible = !loggedIn;
        _claudeLogoutItem.Visible = loggedIn;
    }

    public void SetClaudeLoginBusy(bool busy)
    {
        _claudeLoginItem.Enabled = !busy;
        _claudeLoginItem.Text = busy ? "Claude 로그인 중... (브라우저 확인)" : "Claude 로그인...";
    }

    public void SetCodexLoggedIn(bool loggedIn)
    {
        _codexLoginItem.Visible = !loggedIn;
        _codexLogoutItem.Visible = loggedIn;
    }

    public void SetCodexLoginBusy(bool busy)
    {
        _codexLoginItem.Enabled = !busy;
        _codexLoginItem.Text = busy ? "Codex 로그인 중... (브라우저 확인)" : "Codex 로그인...";
    }

    public void SyncFromSettings(Settings settings)
    {
        _showItem.Checked = settings.WidgetVisible;
        _modeBarItem.Checked = !settings.FloatingCards;
        _modeCardsItem.Checked = settings.FloatingCards;
        UpdateModeItems(settings.FloatingCards);
        _positionTopItem.Checked = !settings.BarAtBottom;
        _positionBottomItem.Checked = settings.BarAtBottom;
        _claudeShowItem.Checked = settings.ShowClaude;
        _codexShowItem.Checked = settings.ShowCodex;
        _clickThroughItem.Checked = settings.ClickThrough;
        _alertItem.Checked = settings.AlertEnabled;
        _autoStartItem.Checked = settings.AutoStart;
        CheckByTag(_opacityMenu, (int)Math.Round(settings.Opacity * 100));
        CheckByTag(_claudeRefreshMenu, settings.ClaudeRefreshSeconds);
        CheckByTag(_codexRefreshMenu, settings.CodexRefreshSeconds);
    }

    private void OnDisplayModeClicked(bool floatingCards)
    {
        _modeBarItem.Checked = !floatingCards;
        _modeCardsItem.Checked = floatingCards;
        UpdateModeItems(floatingCards);
        FloatingCardsChanged?.Invoke(floatingCards);
    }

    /// <summary>바 위치는 고정 바에서만, 카드 위치 초기화는 플로팅 카드에서만 쓸 수 있다.</summary>
    private void UpdateModeItems(bool floatingCards)
    {
        _positionMenu.Enabled = !floatingCards;
        _resetPositionItem.Enabled = floatingCards;
    }

    private void OnPositionClicked(bool atBottom)
    {
        _positionTopItem.Checked = !atBottom;
        _positionBottomItem.Checked = atBottom;
        BarAtBottomChanged?.Invoke(atBottom);
    }

    /// <summary>마지막으로 남은 도구는 끌 수 없다. 체크를 되돌리고 알리지 않는다.</summary>
    private void OnToolShowClicked(ToolStripMenuItem clicked, ToolStripMenuItem other, string tool)
    {
        if (!clicked.Checked && !other.Checked)
        {
            clicked.Checked = true;
            return;
        }
        ToolVisibleChanged?.Invoke(tool, clicked.Checked);
    }

    private void FillRefreshMenu(ToolStripMenuItem menu, string tool)
    {
        foreach (int seconds in RefreshChoices)
        {
            var item = new ToolStripMenuItem(Format.RefreshLabel(seconds)) { Tag = seconds };
            item.Click += (sender, _) =>
            {
                CheckOnly(menu, (ToolStripMenuItem)sender!);
                RefreshSecondsChanged?.Invoke(tool, seconds);
            };
            menu.DropDownItems.Add(item);
        }
    }

    public void UpdateStatus(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var lines = new List<string>();
        foreach (UsageSnapshot snapshot in snapshots)
        {
            if (snapshot.Error != null)
            {
                lines.Add($"{snapshot.Tool}: {snapshot.Error}");
                continue;
            }

            var parts = new List<string>();
            foreach (UsageWindow window in snapshot.Windows)
            {
                parts.Add($"{window.Label} {window.Percent:0}%");
            }
            lines.Add($"{snapshot.Tool}: {string.Join(" · ", parts)}");
        }

        string text = string.Join("\n", lines);
        if (text.Length > 127)
        {
            text = text.Substring(0, 127);
        }
        _notify.Text = text;

        SetIcon(DrawIcon(snapshots));
    }

    public void ShowAlert(string title, string text, ToolTipIcon icon)
    {
        _notify.ShowBalloonTip(5000, title, text, icon);
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
    }

    private void SetIcon(Icon icon)
    {
        Icon? old = _currentIcon;
        _currentIcon = icon;
        _notify.Icon = icon;
        old?.Dispose();
    }

    /// <summary>도구별 최대 사용률을 세로 막대로 그린 아이콘.</summary>
    private static Icon DrawIcon(IReadOnlyList<UsageSnapshot> snapshots)
    {
        int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int barCount = Math.Max(snapshots.Count, 2);
            int gap = 4;
            int barWidth = (size - gap * (barCount + 1)) / barCount;

            for (int i = 0; i < barCount; i++)
            {
                int x = gap + i * (barWidth + gap);
                var track = new Rectangle(x, 2, barWidth, size - 4);
                using (var trackBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                {
                    g.FillRectangle(trackBrush, track);
                }

                if (i >= snapshots.Count)
                {
                    continue;
                }

                UsageSnapshot snapshot = snapshots[i];
                double percent = 0;
                foreach (UsageWindow window in snapshot.Windows)
                {
                    if (window.Percent > percent)
                    {
                        percent = window.Percent;
                    }
                }

                Color color;
                if (snapshot.Error != null)
                {
                    color = Color.Gray;
                }
                else if (percent >= 95)
                {
                    color = Color.FromArgb(0xEF, 0x53, 0x50);
                }
                else if (percent >= 70)
                {
                    color = Color.FromArgb(0xFF, 0xA7, 0x26);
                }
                else
                {
                    color = Color.FromArgb(0x4C, 0xAF, 0x50);
                }

                int fillHeight = (int)Math.Round(track.Height * Math.Clamp(percent, 0, 100) / 100.0);
                if (fillHeight < 2)
                {
                    fillHeight = 2;
                }
                var fill = new Rectangle(x, track.Bottom - fillHeight, barWidth, fillHeight);
                using var fillBrush = new SolidBrush(color);
                g.FillRectangle(fillBrush, fill);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void CheckOnly(ToolStripMenuItem parent, ToolStripMenuItem selected)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Checked = ReferenceEquals(menuItem, selected);
            }
        }
    }

    private static void CheckByTag(ToolStripMenuItem parent, int value)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Checked = menuItem.Tag is int tag && tag == value;
            }
        }
    }
}
