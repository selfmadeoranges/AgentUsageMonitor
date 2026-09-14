using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageMonitor.Models;

namespace UsageMonitor.Providers;

/// <summary>
/// Codex 사용량을 ChatGPT 사용량 엔드포인트에서 조회한다.
/// 모니터가 직접 로그인한 토큰(자격 증명 관리자)을 우선 쓰고, 없으면 Codex 앱/CLI가 공유하는 auth.json을 읽는다.
/// 토큰은 메모리에서만 사용하고 어디에도 기록하지 않는다.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    public string Name => "Codex";

    /// <summary>Codex 앱이나 CLI가 남긴 auth.json이 있는지. 첫 실행 때 로그인을 띄울지 정하는 데 쓴다.</summary>
    public static bool HasCliCredentials()
    {
        return File.Exists(Path.Combine(CodexHome(), "auth.json"));
    }

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        // 1) 모니터 전용 토큰
        CodexTokens? own = CredentialStore.LoadCodexTokens();
        if (own != null)
        {
            if (own.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                try
                {
                    own = await CodexOAuth.RefreshAsync(http, own, ct);
                    CredentialStore.SaveCodexTokens(own);
                }
                catch (Exception)
                {
                    return UsageSnapshot.Failure(Name, "로그인 만료 (트레이 메뉴에서 Codex 다시 로그인)", isAuthError: true);
                }
            }
            return await QueryAsync(http, own.AccessToken, own.AccountId, ct);
        }

        // 2) Codex 앱/CLI가 공유하는 auth.json
        string path = Path.Combine(CodexHome(), "auth.json");
        if (!File.Exists(path))
        {
            return UsageSnapshot.Failure(Name, "로그인 필요 (트레이 메뉴 → Codex 로그인)", isAuthError: true);
        }

        string token;
        string accountId;
        try
        {
            string json = await File.ReadAllTextAsync(path, ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement tokens = doc.RootElement.GetProperty("tokens");
            token = tokens.GetProperty("access_token").GetString() ?? "";
            accountId = tokens.GetProperty("account_id").GetString() ?? "";
        }
        catch (Exception)
        {
            return UsageSnapshot.Failure(Name, "auth.json 읽기 실패", isAuthError: false);
        }

        if (token.Length == 0 || accountId.Length == 0)
        {
            return UsageSnapshot.Failure(Name, "auth.json에 토큰 없음 (트레이 메뉴 → Codex 로그인)", isAuthError: true);
        }

        return await QueryAsync(http, token, accountId, ct);
    }

    private async Task<UsageSnapshot> QueryAsync(HttpClient http, string token, string accountId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("ChatGPT-Account-Id", accountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return UsageSnapshot.Failure(Name, $"인증 실패 (HTTP {(int)response.StatusCode}, 다시 로그인 필요)", isAuthError: true);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return UsageSnapshot.RateLimited(Name, response.Headers.RetryAfter?.Delta);
        }
        if (!response.IsSuccessStatusCode)
        {
            return UsageSnapshot.Failure(Name, $"조회 실패 (HTTP {(int)response.StatusCode})", isAuthError: false);
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body);
    }

    /// <summary>Codex 설정 폴더. CODEX_HOME이 있으면 그 경로, 없으면 %USERPROFILE%\.codex.</summary>
    private static string CodexHome()
    {
        string? custom = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex");
    }

    private UsageSnapshot Parse(string body)
    {
        var windows = new List<UsageWindow>();
        using JsonDocument doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("rate_limit", out JsonElement rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            AddWindow(rateLimit, "primary_window", windows);
            AddWindow(rateLimit, "secondary_window", windows);
        }

        if (windows.Count == 0)
        {
            return UsageSnapshot.Failure(Name, "응답에 사용량 정보 없음", isAuthError: false);
        }

        return new UsageSnapshot { Tool = Name, Windows = windows };
    }

    private static void AddWindow(JsonElement rateLimit, string property, List<UsageWindow> windows)
    {
        if (!rateLimit.TryGetProperty(property, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        double percent = 0;
        if (window.TryGetProperty("used_percent", out JsonElement used) && used.ValueKind == JsonValueKind.Number)
        {
            percent = used.GetDouble();
        }

        long windowSeconds = 0;
        if (window.TryGetProperty("limit_window_seconds", out JsonElement lw) && lw.ValueKind == JsonValueKind.Number)
        {
            windowSeconds = lw.GetInt64();
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out JsonElement reset) && reset.ValueKind == JsonValueKind.Number)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        }

        windows.Add(new UsageWindow { Label = LabelFor(windowSeconds), Percent = percent, ResetsAt = resetsAt });
    }

    private static string LabelFor(long windowSeconds)
    {
        if (windowSeconds >= 6 * 86400)
        {
            return "주간";
        }
        if (windowSeconds >= 3600)
        {
            return $"{windowSeconds / 3600}시간";
        }
        if (windowSeconds > 0)
        {
            return $"{windowSeconds / 60}분";
        }
        return "한도";
    }
}
