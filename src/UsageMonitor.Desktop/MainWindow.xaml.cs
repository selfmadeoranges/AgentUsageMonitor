using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>
/// 주 모니터 맨 위 또는 작업표시줄 바로 위에 붙는 한 줄 바(AppBar). 셸에 화면 영역을 예약하므로
/// 다른 창을 최대화하면 이 바를 피해서만 커진다. 작업표시줄과 Alt-Tab에는 나타나지 않는다.
/// </summary>
public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;

    // SHAppBarMessage 메시지와 알림 코드
    private const uint AbmNew = 0x0;
    private const uint AbmRemove = 0x1;
    private const uint AbmQueryPos = 0x2;
    private const uint AbmSetPos = 0x3;
    private const uint AbmActivate = 0x6;
    private const uint AbmWindowPosChanged = 0x9;
    private const uint AbeTop = 1;
    private const uint AbeBottom = 3;
    private const int AbnPosChanged = 0x1;
    private const int AbnFullScreenApp = 0x2;

    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDisplayChange = 0x007E;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>바 높이(DIP).</summary>
    private const double BarHeight = 44;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect32 rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly Dictionary<string, UsageCard> _cards = new();
    private bool _clickThrough;
    private bool _atBottom;
    private IntPtr _hwnd;
    private uint _appBarCallback;
    private bool _appBarRegistered;
    private bool _fullScreenAppOpen;

    public MainWindow(IEnumerable<string> toolNames)
    {
        InitializeComponent();

        foreach (string name in toolNames)
        {
            var card = new UsageCard();
            card.SetTitle(name);
            _cards[name] = card;
            CardPanel.Children.Add(card);
        }
        UpdateDividers();

        SourceInitialized += OnSourceInitialized;

        // 숨기면 예약한 영역을 돌려주고, 다시 보이면 예약한다.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RegisterAppBar();
            }
            else
            {
                UnregisterAppBar();
            }
        };
        Closed += (_, _) => UnregisterAppBar();
    }

    /// <summary>true면 마우스 입력이 아래 창으로 통과한다.</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyExStyles();
        }
    }

    /// <summary>true면 작업표시줄 바로 위, false면 주 모니터 맨 위에 붙는다.</summary>
    public bool BarAtBottom
    {
        get => _atBottom;
        set
        {
            _atBottom = value;
            // 구분선은 바가 화면 안쪽을 향하는 면에 둔다.
            if (_atBottom)
            {
                RootBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            }
            else
            {
                RootBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            UpdateAppBarPosition();
        }
    }

    /// <summary>도구 표시를 켜거나 끈다. 바 안의 내용은 가운데 정렬을 유지한다.</summary>
    public void SetToolVisible(string tool, bool visible)
    {
        if (_cards.TryGetValue(tool, out UsageCard? card))
        {
            card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateDividers();
    }

    /// <summary>보이는 도구 사이에만 구분선을 둔다. 맨 앞에 보이는 도구에는 구분선이 없다.</summary>
    private void UpdateDividers()
    {
        bool hasVisibleBefore = false;
        foreach (UsageCard card in CardPanel.Children)
        {
            bool visible = card.Visibility == Visibility.Visible;
            card.SetDividerVisible(visible && hasVisibleBefore);
            if (visible)
            {
                hasVisibleBefore = true;
            }
        }
    }

    public void Apply(IReadOnlyList<UsageSnapshot> snapshots)
    {
        foreach (UsageSnapshot snapshot in snapshots)
        {
            if (_cards.TryGetValue(snapshot.Tool, out UsageCard? card))
            {
                card.Apply(snapshot);
            }
        }

        // 다른 Topmost 창에 밀렸을 수 있으므로 갱신마다 다시 올린다. 전체화면 앱이 떠 있으면 그대로 둔다.
        if (IsVisible && !_fullScreenAppOpen)
        {
            Topmost = false;
            Topmost = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExStyles();
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

        // 처음 보이기 전에 자리를 잡아 두면 기본 크기로 잠깐 그려지는 일이 없다.
        RegisterAppBar();
    }

    private void RegisterAppBar()
    {
        if (_appBarRegistered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_appBarCallback == 0)
        {
            _appBarCallback = RegisterWindowMessage("UsageMonitor.AppBar");
        }

        AppBarData data = NewAppBarData();
        data.uCallbackMessage = _appBarCallback;
        if (SHAppBarMessage(AbmNew, ref data) == UIntPtr.Zero)
        {
            return;
        }

        _appBarRegistered = true;
        UpdateAppBarPosition();
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered)
        {
            return;
        }

        AppBarData data = NewAppBarData();
        SHAppBarMessage(AbmRemove, ref data);
        _appBarRegistered = false;
    }

    /// <summary>주 모니터 위쪽 또는 아래쪽에 바 높이만큼 영역을 예약하고 창을 그 자리로 옮긴다. 좌표는 물리 픽셀이다.</summary>
    private void UpdateAppBarPosition()
    {
        if (!_appBarRegistered)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int height = (int)Math.Round(BarHeight * scale);
        int screenHeight = GetSystemMetrics(SmCyScreen);

        // 주 모니터의 왼쪽 위는 항상 (0, 0)이다.
        AppBarData data = NewAppBarData();
        data.rc.Left = 0;
        data.rc.Right = GetSystemMetrics(SmCxScreen);
        if (_atBottom)
        {
            data.uEdge = AbeBottom;
            data.rc.Top = screenHeight - height;
            data.rc.Bottom = screenHeight;
        }
        else
        {
            data.uEdge = AbeTop;
            data.rc.Top = 0;
            data.rc.Bottom = height;
        }

        // 같은 쪽에 다른 AppBar(작업표시줄 등)가 있으면 셸이 겹치지 않는 위치로 조정해 준다.
        SHAppBarMessage(AbmQueryPos, ref data);
        if (_atBottom)
        {
            data.rc.Top = data.rc.Bottom - height;
        }
        else
        {
            data.rc.Bottom = data.rc.Top + height;
        }
        SHAppBarMessage(AbmSetPos, ref data);

        MoveWindow(_hwnd, data.rc.Left, data.rc.Top, data.rc.Right - data.rc.Left, height, true);
    }

    private AppBarData NewAppBarData()
    {
        var data = new AppBarData();
        data.cbSize = (uint)Marshal.SizeOf<AppBarData>();
        data.hWnd = _hwnd;
        return data;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_appBarRegistered)
        {
            return IntPtr.Zero;
        }

        if (msg == (int)_appBarCallback)
        {
            int notification = wParam.ToInt32();
            if (notification == AbnPosChanged)
            {
                UpdateAppBarPosition();
            }
            else if (notification == AbnFullScreenApp)
            {
                OnFullScreenAppChanged(lParam != IntPtr.Zero);
            }
            return IntPtr.Zero;
        }

        // AppBar는 활성화와 위치 변경을 셸에 알려야 한다.
        if (msg == WmActivate)
        {
            AppBarData data = NewAppBarData();
            SHAppBarMessage(AbmActivate, ref data);
        }
        else if (msg == WmWindowPosChanged)
        {
            AppBarData data = NewAppBarData();
            SHAppBarMessage(AbmWindowPosChanged, ref data);
        }
        else if (msg == WmDisplayChange)
        {
            UpdateAppBarPosition();
        }
        return IntPtr.Zero;
    }

    /// <summary>작업표시줄처럼 전체화면 앱(영상, 게임)이 떠 있는 동안에는 그 위에 올라가지 않는다.</summary>
    private void OnFullScreenAppChanged(bool opened)
    {
        _fullScreenAppOpen = opened;
        if (opened)
        {
            Topmost = false;
            SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        else
        {
            Topmost = true;
        }
    }

    private void ApplyExStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExToolWindow | WsExLayered;
        if (_clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }
        SetWindowLong(hwnd, GwlExStyle, style);
    }
}
