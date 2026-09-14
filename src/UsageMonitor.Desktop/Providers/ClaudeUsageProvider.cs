using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageMonitor.Models;

namespace UsageMonitor.Providers;

/// <summary>
/// 모니터가 직접 로그인해 자격 증명 관리자에 저장한 Claude OAuth 토큰으로 사용량을 조회한다.
/// Claude Code의 토큰 파일은 읽지 않는다(호출 버킷을 공유하면 429가 나기 쉽다).
/// 토큰은 메모리에서만 사용하고 어디에도 기록하지 않는다.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    public string Name => "Claude";

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        ClaudeTokens? tokens = CredentialStore.LoadClaudeTokens();
        if (tokens == null)
        {
            return UsageSnapshot.Failure(Name, "로그인 필요 (트레이 메뉴 → Claude 로그인)", isAuthError: true);
        }

        if (tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            try
            {
                tokens = await ClaudeOAuth.RefreshAsync(http, tokens, ct);
                CredentialStore.SaveClaudeTokens(tokens);
            }
            catch (Exception)
            {
                return UsageSnapshot.Failure(Name, "로그인 만료 (트레이 메뉴에서 Claude 다시 로그인)", isAuthError: true);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
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

    private UsageSnapshot Parse(string body)
    {
        var windows = new List<UsageWindow>();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        AddWindow(root, "five_hour", "5시간", windows);
        AddWindow(root, "seven_day", "주간", windows);

        // 모델별 주간 한도(limits 배열의 weekly_scoped)가 있으면 함께 표시한다.
        if (root.TryGetProperty("limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement limit in limits.EnumerateArray())
            {
                if (!limit.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "weekly_scoped")
                {
                    continue;
                }

                string label = "주간(모델)";
                if (limit.TryGetProperty("scope", out JsonElement scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("display_name", out JsonElement displayName) && displayName.ValueKind == JsonValueKind.String)
                {
                    label = "주간·" + displayName.GetString();
                }

                double percent = 0;
                if (limit.TryGetProperty("percent", out JsonElement p) && p.ValueKind == JsonValueKind.Number)
                {
                    percent = p.GetDouble();
                }

                windows.Add(new UsageWindow { Label = label, Percent = percent, ResetsAt = ReadTime(limit, "resets_at") });
            }
        }

        if (windows.Count == 0)
        {
            return UsageSnapshot.Failure(Name, "응답에 사용량 정보 없음", isAuthError: false);
        }

        return new UsageSnapshot { Tool = Name, Windows = windows };
    }

    private static void AddWindow(JsonElement root, string property, string label, List<UsageWindow> windows)
    {
        if (!root.TryGetProperty(property, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        double percent = 0;
        if (element.TryGetProperty("utilization", out JsonElement util) && util.ValueKind == JsonValueKind.Number)
        {
            percent = util.GetDouble();
        }

        windows.Add(new UsageWindow { Label = label, Percent = percent, ResetsAt = ReadTime(element, "resets_at") });
    }

    private static DateTimeOffset? ReadTime(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        if (DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed))
        {
            return parsed;
        }
        return null;
    }
}
