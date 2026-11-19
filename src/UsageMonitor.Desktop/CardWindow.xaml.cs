using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>플로팅 카드: 테두리 없는 투명 Topmost 창. 드래그로 옮길 수 있고 작업표시줄과 Alt-Tab에 나타나지 않는다.</summary>
public partial class CardWindow : Window, IUsageWindow
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly Dictionary<string, UsageCard> _cards = new();
    private bool _clickThrough;
    private bool _useDefaultPosition = true;

    /// <summary>드래그가 끝나거나 위치가 초기화될 때 (Left, Top)을 알린다.</summary>
    public event Action<double, double>? PositionChanged;

    public CardWindow(IEnumerable<string> toolNames)
    {
        InitializeComponent();

        foreach (string name in toolNames)
        {
            var card = new UsageCard();
            card.SetTitle(name);
            _cards[name] = card;
            CardPanel.Children.Add(card);
        }

        SourceInitialized += (_, _) => ApplyExStyles();
        // 기본 위치(우하단)에 있는 동안은 사용량이 채워지며 창이 커질 때마다 다시 맞춰야 화면 아래로 넘치지 않는다.
        SizeChanged += (_, _) =>
        {
            if (_useDefaultPosition)
            {
                MoveToDefaultPosition();
            }
        };
    }

    /// <summary>true면 마우스 입력이 아래 창으로 통과한다(드래그 불가).</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyExStyles();
        }
    }

    public void ApplyPosition(double? left, double? top)
    {
        if (left == null || top == null)
        {
            _useDefaultPosition = true;
            return;
        }

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // 저장된 위치가 현재 화면 밖이면(모니터 구성 변경 등) 기본 위치를 쓴다.
        if (!virtualScreen.Contains(new Point(left.Value + 20, top.Value + 20)))
        {
            _useDefaultPosition = true;
            return;
        }

        _useDefaultPosition = false;
        Left = left.Value;
        Top = top.Value;
    }

    /// <summary>도구 카드를 숨기거나 다시 보여준다. 창 크기는 내용에 맞춰 자동으로 바뀐다.</summary>
    public void SetToolVisible(string tool, bool visible)
    {
        if (_cards.TryGetValue(tool, out UsageCard? card))
        {
            card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void ResetPosition()
    {
        _useDefaultPosition = true;
        MoveToDefaultPosition();
        PositionChanged?.Invoke(Left, Top);
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

        // 다른 Topmost 창에 밀렸을 수 있으므로 갱신마다 다시 올린다.
        if (IsVisible)
        {
            Topmost = false;
            Topmost = true;
        }
    }

    private void MoveToDefaultPosition()
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 16;
        Top = work.Bottom - ActualHeight - 16;
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

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // DragMove는 마우스를 놓을 때까지 블록된다.
        DragMove();
        // 사용자가 옮긴 뒤로는 크기가 바뀌어도 그 자리를 지킨다.
        _useDefaultPosition = false;
        PositionChanged?.Invoke(Left, Top);
    }
}
