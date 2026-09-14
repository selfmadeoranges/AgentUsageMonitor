using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

/// <summary>
/// 브라우저 OAuth(PKCE)로 Codex(ChatGPT 로그인) 토큰을 직접 받는다.
/// Codex CLI가 쓰는 공개 클라이언트 ID, 콜백 주소, 범위를 그대로 사용한다.
/// 계정 ID는 토큰(JWT)의 "https://api.openai.com/auth".chatgpt_account_id 클레임에서 읽는다.
/// </summary>
public static class CodexOAuth
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const int CallbackPort = 1455;
    private const string CallbackPath = "/auth/callback";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string Scope = "openid profile email offline_access";

    public static async Task<CodexTokens> LoginAsync(HttpClient http, CancellationToken ct)
    {
        string verifier = OAuthCallback.RandomUrlSafe(32);
        string challenge = OAuthCallback.PkceChallenge(verifier);
        string state = OAuthCallback.RandomUrlSafe(32);

        TcpListener listener = OAuthCallback.StartListener(CallbackPort, "Codex 앱이나 CLI가 로그인 중이면 끝낸 뒤 다시 시도하세요.");
        try
        {
            string url = AuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&id_token_add_organizations=true"
                + "&codex_cli_simplified_flow=true"
                + "&state=" + Uri.EscapeDataString(state)
                + "&originator=codex_cli_rs";

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            string code = await OAuthCallback.WaitForCodeAsync(listener, CallbackPath, state, ct);
            return await ExchangeCodeAsync(http, code, verifier, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<CodexTokens> RefreshAsync(HttpClient http, CodexTokens current, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = current.RefreshToken,
            ["scope"] = "openid profile email",
        };

        using HttpResponseMessage response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"토큰 갱신 실패 (HTTP {(int)response.StatusCode})");
        }

        CodexTokens refreshed = await ParseTokenResponseAsync(response, ct);
        if (refreshed.RefreshToken.Length == 0)
        {
            refreshed.RefreshToken = current.RefreshToken;
        }
        if (refreshed.AccountId.Length == 0)
        {
            refreshed.AccountId = current.AccountId;
        }
        return refreshed;
    }

    private static async Task<CodexTokens> ExchangeCodeAsync(HttpClient http, string code, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        };

        using HttpResponseMessage response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"토큰 교환 실패 (HTTP {(int)response.StatusCode})");
        }

        CodexTokens tokens = await ParseTokenResponseAsync(response, ct);
        if (tokens.AccessToken.Length == 0)
        {
            throw new InvalidOperationException("토큰 응답에 access_token 없음");
        }
        if (tokens.AccountId.Length == 0)
        {
            throw new InvalidOperationException("토큰에서 계정 ID를 찾을 수 없음");
        }
        return tokens;
    }

    private static async Task<CodexTokens> ParseTokenResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        var tokens = new CodexTokens();
        string idToken = "";
        if (root.TryGetProperty("access_token", out JsonElement access) && access.ValueKind == JsonValueKind.String)
        {
            tokens.AccessToken = access.GetString() ?? "";
        }
        if (root.TryGetProperty("refresh_token", out JsonElement refresh) && refresh.ValueKind == JsonValueKind.String)
        {
            tokens.RefreshToken = refresh.GetString() ?? "";
        }
        if (root.TryGetProperty("id_token", out JsonElement id) && id.ValueKind == JsonValueKind.String)
        {
            idToken = id.GetString() ?? "";
        }

        // 만료 시각은 access token의 exp 클레임을 우선 쓰고, 없으면 expires_in, 그것도 없으면 1시간.
        long? exp = ReadJwtExpiry(tokens.AccessToken);
        if (exp != null)
        {
            tokens.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.Value);
        }
        else if (root.TryGetProperty("expires_in", out JsonElement expiresIn) && expiresIn.ValueKind == JsonValueKind.Number)
        {
            tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.GetInt64());
        }
        else
        {
            tokens.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        }

        tokens.AccountId = ReadAccountId(tokens.AccessToken);
        if (tokens.AccountId.Length == 0)
        {
            tokens.AccountId = ReadAccountId(idToken);
        }
        return tokens;
    }

    private static long? ReadJwtExpiry(string jwt)
    {
        using JsonDocument? payload = ReadJwtPayload(jwt);
        if (payload != null && payload.RootElement.TryGetProperty("exp", out JsonElement exp) && exp.ValueKind == JsonValueKind.Number)
        {
            return exp.GetInt64();
        }
        return null;
    }

    private static string ReadAccountId(string jwt)
    {
        using JsonDocument? payload = ReadJwtPayload(jwt);
        if (payload == null)
        {
            return "";
        }
        if (payload.RootElement.TryGetProperty("https://api.openai.com/auth", out JsonElement auth) && auth.ValueKind == JsonValueKind.Object
            && auth.TryGetProperty("chatgpt_account_id", out JsonElement accountId) && accountId.ValueKind == JsonValueKind.String)
        {
            return accountId.GetString() ?? "";
        }
        return "";
    }

    /// <summary>JWT의 페이로드(두 번째 구간)를 서명 검증 없이 디코딩한다. 값을 읽는 용도로만 쓴다.</summary>
    private static JsonDocument? ReadJwtPayload(string jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            byte[] bytes = Convert.FromBase64String(b64);
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
