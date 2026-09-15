using System.IO;
using System.Text.Json;

namespace UsageMonitor.SmallTV.Preview;

internal sealed class DisplayLayout
{
    public int SchemaVersion { get; set; }
    public DisplayDefinition Display { get; set; } = new();
    public ColorDefinition Colors { get; set; } = new();
    public UsagePageDefinition UsagePage { get; set; } = new();
    public RecoveryPageDefinition RecoveryPage { get; set; } = new();
    public TextDefinition Text { get; set; } = new();

    public static DisplayLayout Load(string path)
    {
        string json = File.ReadAllText(path);
        DisplayLayout result = JsonSerializer.Deserialize<DisplayLayout>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("화면 레이아웃 파일이 비어 있습니다.");

        if (result.SchemaVersion != 1)
        {
            throw new InvalidDataException("지원하지 않는 화면 레이아웃 버전입니다.");
        }
        if (result.Display.Width != 240 || result.Display.Height != 240)
        {
            throw new InvalidDataException("SmallTV-Ultra 화면은 240×240이어야 합니다.");
        }
        if (result.Display.PageIntervalMs < 1000)
        {
            throw new InvalidDataException("페이지 전환 시간은 1초 이상이어야 합니다.");
        }
        if (result.UsagePage.Row.Count is < 1 or > 3)
        {
            throw new InvalidDataException("사용량 행 수는 1~3이어야 합니다.");
        }

        string[] colors =
        [
            result.Colors.Background,
            result.Colors.Panel,
            result.Colors.Muted,
            result.Colors.Green,
            result.Colors.Orange,
            result.Colors.Red,
        ];
        foreach (string color in colors)
        {
            result.Color(color);
        }

        string[] requiredText =
        [
            result.RecoveryPage.Title.Text,
            result.RecoveryPage.ConnectLabel.Text,
            result.RecoveryPage.PasswordLabel.Text,
            result.RecoveryPage.OpenAddress.Text,
            result.RecoveryPage.User.Text,
            result.Text.UsageUnavailable,
            result.Text.SynchronizingTime,
            result.Text.ConfigureAccount,
            result.Text.RefreshFailed,
            result.Text.LoadingUsage,
            result.Text.RetryingLater,
            result.Text.StateStale,
            result.Text.StateRateLimited,
            result.Text.StateAuthRequired,
            result.Text.StateRefreshFailed,
            result.Text.StateNetworkError,
            result.Text.StateInvalidResponse,
            result.Text.IpPrefix,
        ];
        if (requiredText.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("필수 화면 문구가 비어 있습니다.");
        }

        return result;
    }

    public static string FindPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "shared", "smalltv-display-layout.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        string packaged = Path.Combine(AppContext.BaseDirectory, "smalltv-display-layout.json");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        throw new FileNotFoundException("smalltv-display-layout.json을 찾을 수 없습니다.");
    }

    public ushort Color(string value)
    {
        string digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return Convert.ToUInt16(digits, 16);
    }
}

internal sealed class DisplayDefinition
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int PageIntervalMs { get; set; }
}

internal sealed class ColorDefinition
{
    public string Background { get; set; } = "";
    public string Panel { get; set; } = "";
    public string Muted { get; set; } = "";
    public string Green { get; set; } = "";
    public string Orange { get; set; } = "";
    public string Red { get; set; } = "";
}

internal class TextPosition
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Font { get; set; }
    public int Scale { get; set; } = 1;
    public string Text { get; set; } = "";
}

internal sealed class UsagePageDefinition
{
    public TextPosition Title { get; set; } = new();
    public TextPosition Counter { get; set; } = new();
    public UsageRowDefinition Row { get; set; } = new();
    public TextPosition Empty { get; set; } = new();
    public FooterDefinition Footer { get; set; } = new();
}

internal sealed class UsageRowDefinition
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Step { get; set; }
    public int Count { get; set; }
    public int LabelX { get; set; }
    public int LabelOffsetY { get; set; }
    public int LabelFont { get; set; }
    public int PercentRight { get; set; }
    public int PercentOffsetY { get; set; }
    public int PercentFont { get; set; }
    public int BarX { get; set; }
    public int BarOffsetY { get; set; }
    public int BarWidth { get; set; }
    public int BarHeight { get; set; }
    public int CountdownX { get; set; }
    public int CountdownOffsetY { get; set; }
    public int CountdownFont { get; set; }
}

internal sealed class FooterDefinition
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int StateX { get; set; }
    public int StateY { get; set; }
    public int StateFont { get; set; }
    public int IpX { get; set; }
    public int IpY { get; set; }
    public int IpFont { get; set; }
}

internal sealed class RecoveryPageDefinition
{
    public TextPosition Title { get; set; } = new();
    public TextPosition ConnectLabel { get; set; } = new();
    public TextPosition Ssid { get; set; } = new();
    public TextPosition PasswordLabel { get; set; } = new();
    public TextPosition Password { get; set; } = new();
    public TextPosition OpenAddress { get; set; } = new();
    public TextPosition User { get; set; } = new();
}

internal sealed class TextDefinition
{
    public string UsageUnavailable { get; set; } = "";
    public string SynchronizingTime { get; set; } = "";
    public string ConfigureAccount { get; set; } = "";
    public string RefreshFailed { get; set; } = "";
    public string LoadingUsage { get; set; } = "";
    public string RetryingLater { get; set; } = "";
    public string StateStale { get; set; } = "";
    public string StateRateLimited { get; set; } = "";
    public string StateAuthRequired { get; set; } = "";
    public string StateRefreshFailed { get; set; } = "";
    public string StateNetworkError { get; set; } = "";
    public string StateInvalidResponse { get; set; } = "";
    public string IpPrefix { get; set; } = "";
}
