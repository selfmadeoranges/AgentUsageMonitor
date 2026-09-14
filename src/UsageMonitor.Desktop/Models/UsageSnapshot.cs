namespace UsageMonitor.Models;

/// <summary>한 개의 한도 창(예: 5시간, 주간)의 사용률.</summary>
public sealed class UsageWindow
{
    public string Label { get; init; } = "";
    public double Percent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>도구 하나(Claude, Codex)의 조회 결과.</summary>
public sealed class UsageSnapshot
{
    public string Tool { get; init; } = "";
    public List<UsageWindow> Windows { get; init; } = new();
    public string? Error { get; init; }
    public bool IsAuthError { get; init; }
    /// <summary>HTTP 429. 서버가 Retry-After를 주면 RetryAfter에 담긴다.</summary>
    public bool IsRateLimited { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>true면 오류라도 마지막 값이 여전히 유효하므로 카드를 회색으로 바꾸지 않고 안내만 표시한다.</summary>
    public bool KeepValues { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;

    public static UsageSnapshot Failure(string tool, string message, bool isAuthError)
    {
        return new UsageSnapshot { Tool = tool, Error = message, IsAuthError = isAuthError };
    }

    public static UsageSnapshot RateLimited(string tool, TimeSpan? retryAfter)
    {
        return new UsageSnapshot { Tool = tool, Error = "요청 제한 (HTTP 429)", IsRateLimited = true, RetryAfter = retryAfter, KeepValues = true };
    }

    /// <summary>429 대기 중 안내. 값은 유지하고 남은 시간만 보여준다.</summary>
    public static UsageSnapshot Paused(string tool, string message)
    {
        return new UsageSnapshot { Tool = tool, Error = message, KeepValues = true };
    }
}
