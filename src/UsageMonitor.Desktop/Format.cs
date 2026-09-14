namespace UsageMonitor;

public static class Format
{
    /// <summary>리셋까지 남은 시간을 "3시간 12분 후 리셋" 형태로 만든다.</summary>
    public static string Remaining(DateTimeOffset? resetsAt)
    {
        if (resetsAt == null)
        {
            return "";
        }

        TimeSpan left = resetsAt.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            return "곧 리셋";
        }
        if (left.Days > 0)
        {
            return $"{left.Days}일 {left.Hours}시간 후 리셋";
        }
        if (left.Hours > 0)
        {
            return $"{left.Hours}시간 {left.Minutes}분 후 리셋";
        }
        return $"{left.Minutes}분 후 리셋";
    }

    public static string RefreshLabel(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds}초";
        }
        return $"{seconds / 60}분";
    }
}
