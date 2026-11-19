using System.Text.Json;

namespace UsageMonitor;

/// <summary>%APPDATA%\UsageMonitor\settings.json 에 저장되는 사용자 설정. 토큰은 절대 포함하지 않는다.</summary>
public sealed class Settings
{
    public const int MinRefreshSeconds = 30;

    // Claude 사용량 엔드포인트는 계정 단위 요청 제한이 빡빡해 기본 주기를 더 길게 둔다.
    public int ClaudeRefreshSeconds { get; set; } = 120;
    public int CodexRefreshSeconds { get; set; } = 60;
    public double Opacity { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = false;
    public bool AlertEnabled { get; set; } = true;
    public int AlertThreshold { get; set; } = 95;
    public bool AutoStart { get; set; } = false;
    public bool WidgetVisible { get; set; } = true;
    // 표시 방식. false면 화면 가장자리에 붙는 고정 바, true면 자유롭게 옮기는 플로팅 카드.
    public bool FloatingCards { get; set; } = false;
    // 고정 바 위치. false면 주 모니터 맨 위, true면 작업표시줄 바로 위.
    public bool BarAtBottom { get; set; } = false;
    // 도구별 표시 여부. 꺼진 도구는 카드를 숨기고 사용량 조회도 하지 않는다.
    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    // 플로팅 카드 위치. 없으면 작업 영역 우하단 기본 위치를 쓴다.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public static string FilePath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "UsageMonitor", "settings.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        Settings settings = new();
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                Settings? loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded != null)
                {
                    settings = loaded;
                }
            }
        }
        catch (Exception)
        {
            // 손상된 설정 파일은 기본값으로 대체한다.
        }

        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        Normalize();
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("설정 저장 실패: " + ex.Message);
        }
    }

    private void Normalize()
    {
        if (ClaudeRefreshSeconds < MinRefreshSeconds)
        {
            ClaudeRefreshSeconds = MinRefreshSeconds;
        }
        if (CodexRefreshSeconds < MinRefreshSeconds)
        {
            CodexRefreshSeconds = MinRefreshSeconds;
        }
        if (Opacity < 0.2)
        {
            Opacity = 0.2;
        }
        if (Opacity > 1.0)
        {
            Opacity = 1.0;
        }
        if (AlertThreshold < 1)
        {
            AlertThreshold = 1;
        }
        if (AlertThreshold > 100)
        {
            AlertThreshold = 100;
        }
        // 둘 다 끄면 빈 위젯만 남으므로 최소 하나는 켠다.
        if (!ShowClaude && !ShowCodex)
        {
            ShowClaude = true;
            ShowCodex = true;
        }
    }
}
