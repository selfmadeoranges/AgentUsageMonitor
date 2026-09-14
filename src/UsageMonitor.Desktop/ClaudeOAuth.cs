using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace UsageMonitor;

/// <summary>
/// 브라우저 OAuth(PKCE)로 Claude 토큰을 직접 받는다. Claude Code가 쓰는 공개 클라이언트 ID와
/// localhost 콜백 주소를 사용하며, 사용량 조회에 필요한 최소 범위(user:profile)만 요청한다.
/// </summary>
public static class ClaudeOAuth
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const int CallbackPort = 54545;
    private const string CallbackPath = "/callback";
    private const string RedirectUri = "http://localhost:54545/callback";
    private const string Scope = "user:profile";

    public static async Task<ClaudeTokens> LoginAsync(HttpClient http, CancellationToken ct)
    {
        string verifier = OAuthCallback.RandomUrlSafe(32);
        string challenge = OAuthCallback.PkceChallenge(verifier);
        string state = OAuthCallback.RandomUrlSafe(32);

        TcpListener listener = OAuthCallback.StartListener(CallbackPort, "Claude Code가 로그인 중이면 끝낸 뒤 다시 시도하세요.");
        try
        {
            string url = AuthorizeUrl
                + "?code=true"
                + "&client_id=" + Uri.EscapeDataString(ClientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state);

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            string code = await OAuthCallback.WaitForCodeAsync(listener, CallbackPath, state, ct);
            return await ExchangeCodeAsync(http, code, state, verifier, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<ClaudeTokens> RefreshAsync(HttpClient http, ClaudeTokens current, CancellationToken ct)
    {
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = current.RefreshToken,
        };

        using HttpResponseMessage response = await http.PostAsJsonAsync(TokenUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"토큰 갱신 실패 (HTTP {(int)response.StatusCode})");
        }

        ClaudeTokens refreshed = await ParseTokenResponseAsync(response, ct);
        if (refreshed.RefreshToken.Length == 0)
        {
            // 서버가 refresh_token을 다시 주지 않으면 기존 것을 유지한다.
            refreshed.RefreshToken = current.RefreshToken;
        }
        return refreshed;
    }

    private static async Task<ClaudeTokens> ExchangeCodeAsync(HttpClient http, string code, string state, string verifier, CancellationToken ct)
    {
        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        };

        using HttpResponseMessage response = await http.PostAsJsonAsync(TokenUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"토큰 교환 실패 (HTTP {(int)response.StatusCode})");
        }

        ClaudeTokens tokens = await ParseTokenResponseAsync(response, ct);
        if (tokens.AccessToken.Length == 0)
        {
            throw new InvalidOperationException("토큰 응답에 access_token 없음");
        }
        return tokens;
    }

    private static async Task<ClaudeTokens> ParseTokenResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        var tokens = new ClaudeTokens();
        if (root.TryGetProperty("access_token", out JsonElement access) && access.ValueKind == JsonValueKind.String)
        {
            tokens.AccessToken = access.GetString() ?? "";
        }
        if (root.TryGetProperty("refresh_token", out JsonElement refresh) && refresh.ValueKind == JsonValueKind.String)
        {
            tokens.RefreshToken = refresh.GetString() ?? "";
        }

        long expiresIn = 3600;
        if (root.TryGetProperty("expires_in", out JsonElement exp) && exp.ValueKind == JsonValueKind.Number)
        {
            expiresIn = exp.GetInt64();
        }
        tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return tokens;
    }
}
