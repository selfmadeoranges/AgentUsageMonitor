namespace UsageMonitor;

/// <summary>
/// 앱 시작과 종료 사유를 %APPDATA%\UsageMonitor\app.log 에 남긴다. 앱이 예고 없이 사라졌을 때 원인을 가리기 위한 기록이다.
/// 토큰 같은 인증 정보는 절대 기록하지 않는다.
/// </summary>
public static class AppLog
{
    // 이 크기를 넘으면 app.old.log 로 넘기고 새로 쓴다.
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "UsageMonitor", "app.log");
        }
    }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (dir != null)
                {
                    Directory.CreateDirectory(dir);
                }

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                }

                File.AppendAllText(path, line);
            }
            catch (Exception)
            {
                // 로그를 못 남겨도 앱 동작에는 영향이 없어야 하므로 무시한다.
            }
        }
    }
}
