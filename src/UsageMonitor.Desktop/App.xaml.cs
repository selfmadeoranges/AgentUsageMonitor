using System.Windows;
using System.Windows.Threading;
using UsageMonitor.Models;
using UsageMonitor.Providers;

namespace UsageMonitor;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly List<IUsageProvider> _providers = new()
    {
        new ClaudeUsageProvider(),
        new CodexUsageProvider(),
    };

    // 임계치 알림을 창(도구|라벨)마다 리셋 시각 기준으로 한 번만 보낸다.
    private readonly Dictionary<string, DateTimeOffset?> _alertedResets = new();
    // 인증 실패 알림을 도구마다 한 번만 보낸다(복구되면 초기화).
    private readonly HashSet<string> _authAlerted = new();

    // 도구별 조회 스케줄. 타이머는 10초마다 돌고, 예정 시각이 지난 도구만 조회한다.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new();
    private readonly Dictionary<string, UsageSnapshot> _latest = new();

    // 429를 받은 도구는 일정 시간 조회를 쉰다. 연속 실패마다 대기 시간을 늘린다.
    private static readonly int[] BackoffMinutes = { 2, 5, 10, 15 };
    private readonly Dictionary<string, DateTimeOffset> _pausedUntil = new();
    private readonly Dictionary<string, int> _backoffLevel = new();

    private Settings _settings = new();
    private MainWindow? _window;
    private TrayIcon? _tray;
    private DispatcherTimer? _timer;
    private bool _refreshing;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "UsageMonitor.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        System.Windows.Forms.Application.EnableVisualStyles();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UsageMonitor/1.0");

        _settings = Settings.Load();

        var names = new List<string>();
        foreach (IUsageProvider provider in _providers)
        {
            names.Add(provider.Name);
        }

        _window = new MainWindow(names);
        _window.Opacity = _settings.Opacity;
        _window.ClickThrough = _settings.ClickThrough;
        _window.BarAtBottom = _settings.BarAtBottom;
        foreach (string name in names)
        {
            _window.SetToolVisible(name, IsToolVisible(name));
        }

        // 창 핸들이 있어야 클릭 통과 스타일을 적용할 수 있으므로 일단 표시한 뒤 숨긴다.
        _window.Show();
        if (!_settings.WidgetVisible)
        {
            _window.Hide();
        }

        _tray = new TrayIcon(_settings);
        _tray.SetClaudeLoggedIn(CredentialStore.LoadClaudeTokens() != null);
        _tray.SetCodexLoggedIn(CredentialStore.LoadCodexTokens() != null);
        _tray.RefreshRequested += () => _ = RefreshAsync(manual: true);
        _tray.ClaudeLoginRequested += () => _ = ClaudeLoginAsync();
        _tray.ClaudeLogoutRequested += ClaudeLogout;
        _tray.CodexLoginRequested += () => _ = CodexLoginAsync();
        _tray.CodexLogoutRequested += CodexLogout;
        _tray.WidgetVisibleChanged += OnWidgetVisibleChanged;
        _tray.BarAtBottomChanged += OnBarAtBottomChanged;
        _tray.ToolVisibleChanged += OnToolVisibleChanged;
        _tray.ClickThroughChanged += OnClickThroughChanged;
        _tray.OpacityChanged += OnOpacityChanged;
        _tray.RefreshSecondsChanged += OnRefreshSecondsChanged;
        _tray.AlertEnabledChanged += OnAlertEnabledChanged;
        _tray.AutoStartChanged += OnAutoStartChanged;
        _tray.ExitRequested += Shutdown;

        // 실행 파일 경로가 바뀌었을 수 있으므로 시작 시 레지스트리를 설정과 맞춘다.
        TryApplyAutoStart(_settings.AutoStart);

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        _ = FirstRunAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        _http.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 첫 실행: 저장된 토큰이 없는 도구는 브라우저 로그인을 하나씩 순서대로 띄운다.
    /// 취소하면 다음 실행까지 다시 묻지 않는다. Codex는 앱/CLI의 auth.json이 있으면 건너뛴다.
    /// </summary>
    private async Task FirstRunAsync()
    {
        if (_tray == null)
        {
            return;
        }

        bool needClaude = IsToolVisible("Claude") && CredentialStore.LoadClaudeTokens() == null;
        bool needCodex = IsToolVisible("Codex")
                         && CredentialStore.LoadCodexTokens() == null
                         && !CodexUsageProvider.HasCliCredentials();

        if (!needClaude && !needCodex)
        {
            await RefreshAsync();
            return;
        }

        if (needClaude)
        {
            _tray.ShowAlert("Claude 로그인", "브라우저에서 Claude 로그인을 완료하세요.", System.Windows.Forms.ToolTipIcon.Info);
            await ClaudeLoginAsync();
        }
        if (needCodex)
        {
            _tray.ShowAlert("Codex 로그인", "브라우저에서 ChatGPT 로그인을 완료하세요.", System.Windows.Forms.ToolTipIcon.Info);
            await CodexLoginAsync();
        }
    }

    private bool IsToolVisible(string tool)
    {
        if (tool == "Claude")
        {
            return _settings.ShowClaude;
        }
        return _settings.ShowCodex;
    }

    private int GetRefreshSeconds(string tool)
    {
        if (tool == "Claude")
        {
            return _settings.ClaudeRefreshSeconds;
        }
        return _settings.CodexRefreshSeconds;
    }

    /// <param name="manual">트레이의 "지금 갱신"이면 예정 시각과 429 대기를 무시하고 모두 조회한다.</param>
    private async Task RefreshAsync(bool manual = false)
    {
        if (_refreshing)
        {
            return;
        }
        _refreshing = true;

        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var tasks = new List<Task<UsageSnapshot>>();

            foreach (IUsageProvider provider in _providers)
            {
                string tool = provider.Name;

                // 표시를 끈 도구는 조회하지 않는다.
                if (!IsToolVisible(tool))
                {
                    continue;
                }

                if (!manual && _pausedUntil.TryGetValue(tool, out DateTimeOffset until) && until > now)
                {
                    // 대기 중: 값은 유지하고 남은 시간만 갱신해 보여준다.
                    int minutesLeft = (int)Math.Ceiling((until - now).TotalMinutes);
                    tasks.Add(Task.FromResult(UsageSnapshot.Paused(tool, $"요청 제한, {minutesLeft}분 후 재시도")));
                    continue;
                }

                if (!manual && _nextDue.TryGetValue(tool, out DateTimeOffset due) && due > now)
                {
                    continue;
                }

                _nextDue[tool] = now.AddSeconds(GetRefreshSeconds(tool));
                tasks.Add(FetchSafeAsync(provider));
            }

            if (tasks.Count == 0)
            {
                return;
            }

            UsageSnapshot[] snapshots = await Task.WhenAll(tasks);

            for (int i = 0; i < snapshots.Length; i++)
            {
                UsageSnapshot snapshot = snapshots[i];
                if (snapshot.IsRateLimited)
                {
                    snapshots[i] = ApplyBackoff(snapshot);
                }
                else if (snapshot.Error == null)
                {
                    _backoffLevel.Remove(snapshot.Tool);
                    _pausedUntil.Remove(snapshot.Tool);
                }
                _latest[snapshot.Tool] = snapshots[i];
            }

            _window?.Apply(snapshots);
            _tray?.UpdateStatus(LatestInOrder());
            CheckAlerts(snapshots);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private List<UsageSnapshot> LatestInOrder()
    {
        var list = new List<UsageSnapshot>();
        foreach (IUsageProvider provider in _providers)
        {
            if (_latest.TryGetValue(provider.Name, out UsageSnapshot? snapshot))
            {
                list.Add(snapshot);
            }
        }
        return list;
    }

    /// <summary>429를 받은 도구의 다음 조회 시각을 정하고, 남은 시간을 담은 스냅샷으로 바꾼다.</summary>
    private UsageSnapshot ApplyBackoff(UsageSnapshot snapshot)
    {
        _backoffLevel.TryGetValue(snapshot.Tool, out int level);
        TimeSpan wait = TimeSpan.FromMinutes(BackoffMinutes[Math.Min(level, BackoffMinutes.Length - 1)]);
        if (snapshot.RetryAfter != null && snapshot.RetryAfter.Value > wait)
        {
            wait = snapshot.RetryAfter.Value;
        }

        _backoffLevel[snapshot.Tool] = level + 1;
        _pausedUntil[snapshot.Tool] = DateTimeOffset.Now + wait;

        int minutes = (int)Math.Ceiling(wait.TotalMinutes);
        return UsageSnapshot.Paused(snapshot.Tool, $"요청 제한 (HTTP 429), {minutes}분 후 재시도");
    }

    private bool _loggingIn;

    private async Task ClaudeLoginAsync()
    {
        if (_loggingIn || _tray == null)
        {
            return;
        }
        _loggingIn = true;
        _tray.SetClaudeLoginBusy(true);

        try
        {
            ClaudeTokens tokens = await ClaudeOAuth.LoginAsync(_http, CancellationToken.None);
            CredentialStore.SaveClaudeTokens(tokens);
            _tray.SetClaudeLoggedIn(true);
            _tray.ShowAlert("Claude 로그인 완료", "사용량을 불러옵니다.", System.Windows.Forms.ToolTipIcon.Info);
            _authAlerted.Remove("Claude");
            await RefreshAsync(manual: true);
        }
        catch (Exception ex)
        {
            _tray.ShowAlert("Claude 로그인 실패", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
            // Codex는 로그인과 무관하므로 그대로 조회하고, Claude 카드에는 로그인 필요를 표시한다.
            await RefreshAsync(manual: true);
        }
        finally
        {
            _loggingIn = false;
            _tray.SetClaudeLoginBusy(false);
        }
    }

    private void ClaudeLogout()
    {
        try
        {
            CredentialStore.DeleteClaudeTokens();
            _tray?.SetClaudeLoggedIn(false);
            _ = RefreshAsync(manual: true);
        }
        catch (Exception ex)
        {
            _tray?.ShowAlert("Claude 로그아웃 실패", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private bool _codexLoggingIn;

    private async Task CodexLoginAsync()
    {
        if (_codexLoggingIn || _tray == null)
        {
            return;
        }
        _codexLoggingIn = true;
        _tray.SetCodexLoginBusy(true);

        try
        {
            CodexTokens tokens = await CodexOAuth.LoginAsync(_http, CancellationToken.None);
            CredentialStore.SaveCodexTokens(tokens);
            _tray.SetCodexLoggedIn(true);
            _tray.ShowAlert("Codex 로그인 완료", "사용량을 불러옵니다.", System.Windows.Forms.ToolTipIcon.Info);
            _authAlerted.Remove("Codex");
            await RefreshAsync(manual: true);
        }
        catch (Exception ex)
        {
            _tray.ShowAlert("Codex 로그인 실패", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
            await RefreshAsync(manual: true);
        }
        finally
        {
            _codexLoggingIn = false;
            _tray.SetCodexLoginBusy(false);
        }
    }

    private void CodexLogout()
    {
        try
        {
            CredentialStore.DeleteCodexTokens();
            _tray?.SetCodexLoggedIn(false);
            _ = RefreshAsync(manual: true);
        }
        catch (Exception ex)
        {
            _tray?.ShowAlert("Codex 로그아웃 실패", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private async Task<UsageSnapshot> FetchSafeAsync(IUsageProvider provider)
    {
        try
        {
            return await provider.FetchAsync(_http, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            return UsageSnapshot.Failure(provider.Name, "네트워크 오류", isAuthError: false);
        }
        catch (TaskCanceledException)
        {
            return UsageSnapshot.Failure(provider.Name, "요청 시간 초과", isAuthError: false);
        }
        catch (System.Text.Json.JsonException)
        {
            return UsageSnapshot.Failure(provider.Name, "응답 파싱 실패", isAuthError: false);
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Failure(provider.Name, "오류: " + ex.GetType().Name, isAuthError: false);
        }
    }

    private void CheckAlerts(IReadOnlyList<UsageSnapshot> snapshots)
    {
        if (_tray == null)
        {
            return;
        }

        foreach (UsageSnapshot snapshot in snapshots)
        {
            if (snapshot.IsAuthError)
            {
                if (_authAlerted.Add(snapshot.Tool))
                {
                    _tray.ShowAlert("인증 필요", $"{snapshot.Tool}: {snapshot.Error}", System.Windows.Forms.ToolTipIcon.Warning);
                }
                continue;
            }
            if (snapshot.Error == null)
            {
                _authAlerted.Remove(snapshot.Tool);
            }

            if (!_settings.AlertEnabled)
            {
                continue;
            }

            foreach (UsageWindow window in snapshot.Windows)
            {
                string key = snapshot.Tool + "|" + window.Label;
                if (window.Percent < _settings.AlertThreshold)
                {
                    _alertedResets.Remove(key);
                    continue;
                }

                bool alreadyAlerted = _alertedResets.TryGetValue(key, out DateTimeOffset? alertedReset)
                                      && alertedReset == window.ResetsAt;
                if (alreadyAlerted)
                {
                    continue;
                }

                _alertedResets[key] = window.ResetsAt;
                _tray.ShowAlert(
                    "사용량 경고",
                    $"{snapshot.Tool} {window.Label} 사용량 {window.Percent:0}% (임계치 {_settings.AlertThreshold}%)",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
    }

    private void OnWidgetVisibleChanged(bool visible)
    {
        _settings.WidgetVisible = visible;
        _settings.Save();
        if (visible)
        {
            _window?.Show();
        }
        else
        {
            _window?.Hide();
        }
    }

    private void OnBarAtBottomChanged(bool atBottom)
    {
        _settings.BarAtBottom = atBottom;
        _settings.Save();
        if (_window != null)
        {
            _window.BarAtBottom = atBottom;
        }
    }

    private void OnToolVisibleChanged(string tool, bool visible)
    {
        if (tool == "Claude")
        {
            _settings.ShowClaude = visible;
        }
        else
        {
            _settings.ShowCodex = visible;
        }
        _settings.Save();

        _window?.SetToolVisible(tool, visible);

        if (visible)
        {
            // 다시 켜면 예정 시각을 기다리지 않고 바로 조회한다.
            _nextDue.Remove(tool);
            _ = RefreshAsync();
        }
        else
        {
            // 트레이 아이콘과 툴팁에서도 빠지도록 마지막 값을 지운다.
            _latest.Remove(tool);
            _tray?.UpdateStatus(LatestInOrder());
        }
    }

    private void OnClickThroughChanged(bool enabled)
    {
        _settings.ClickThrough = enabled;
        _settings.Save();
        if (_window != null)
        {
            _window.ClickThrough = enabled;
        }
    }

    private void OnOpacityChanged(double opacity)
    {
        _settings.Opacity = opacity;
        _settings.Save();
        if (_window != null)
        {
            _window.Opacity = opacity;
        }
    }

    private void OnRefreshSecondsChanged(string tool, int seconds)
    {
        if (tool == "Claude")
        {
            _settings.ClaudeRefreshSeconds = seconds;
        }
        else
        {
            _settings.CodexRefreshSeconds = seconds;
        }
        _settings.Save();

        // 새 주기가 더 짧으면 예정 시각을 앞당긴다. 즉시 호출은 하지 않는다.
        DateTimeOffset newDue = DateTimeOffset.Now.AddSeconds(GetRefreshSeconds(tool));
        if (_nextDue.TryGetValue(tool, out DateTimeOffset due) && due > newDue)
        {
            _nextDue[tool] = newDue;
        }
    }

    private void OnAlertEnabledChanged(bool enabled)
    {
        _settings.AlertEnabled = enabled;
        _settings.Save();
    }

    private void OnAutoStartChanged(bool enabled)
    {
        _settings.AutoStart = enabled;
        _settings.Save();
        TryApplyAutoStart(enabled);
    }

    private void TryApplyAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Apply(enabled);
        }
        catch (Exception ex)
        {
            _tray?.ShowAlert("자동 실행 설정 실패", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
        }
    }
}
