using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace UsageMonitor;

/// <summary>
/// 브라우저 OAuth 리디렉션을 localhost에서 받는 최소 HTTP 서버와 PKCE 도우미.
/// HttpListener는 URL ACL 문제가 있어 TcpListener로 직접 처리한다.
/// </summary>
public static class OAuthCallback
{
    public static TcpListener StartListener(int port, string busyHint)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            throw new InvalidOperationException($"포트 {port}를 사용할 수 없습니다. {busyHint}");
        }
    }

    /// <summary>지정한 경로로 code/state가 오기를 최대 5분 기다린다.</summary>
    public static async Task<string> WaitForCodeAsync(TcpListener listener, string path, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(timeout.Token);
            if (requestLine == null)
            {
                continue;
            }

            // 요청 헤더는 읽어서 버린다.
            while (true)
            {
                string? header = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrEmpty(header))
                {
                    break;
                }
            }

            // "GET /callback?code=...&state=... HTTP/1.1"
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2 || !parts[1].StartsWith(path, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, "404 Not Found", "Not found");
                continue;
            }

            var query = ParseQuery(parts[1]);
            query.TryGetValue("error", out string? error);
            query.TryGetValue("code", out string? code);
            query.TryGetValue("state", out string? state);

            if (error != null)
            {
                await WriteResponseAsync(stream, "200 OK", "로그인이 취소되었습니다. 이 창을 닫아도 됩니다.");
                throw new InvalidOperationException("로그인 거부: " + error);
            }
            if (string.IsNullOrEmpty(code) || state != expectedState)
            {
                await WriteResponseAsync(stream, "400 Bad Request", "잘못된 응답입니다. 다시 시도하세요.");
                throw new InvalidOperationException("콜백 검증 실패 (state 불일치)");
            }

            await WriteResponseAsync(stream, "200 OK", "Usage Monitor 로그인 완료. 이 창을 닫아도 됩니다.");
            return code;
        }
    }

    public static string RandomUrlSafe(int byteCount)
    {
        return Base64Url(RandomNumberGenerator.GetBytes(byteCount));
    }

    public static string PkceChallenge(string verifier)
    {
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string message)
    {
        string html = "<!doctype html><meta charset=\"utf-8\"><title>Usage Monitor</title>"
                      + "<body style=\"font-family:sans-serif;padding:40px\"><h2>" + WebUtility.HtmlEncode(message) + "</h2></body>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(html);
        string head = "HTTP/1.1 " + status + "\r\n"
                      + "Content-Type: text/html; charset=utf-8\r\n"
                      + "Content-Length: " + bodyBytes.Length + "\r\n"
                      + "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>();
        int q = pathAndQuery.IndexOf('?');
        if (q < 0)
        {
            return result;
        }

        foreach (string pair in pathAndQuery.Substring(q + 1).Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair.Substring(0, eq);
            string value = eq < 0 ? "" : pair.Substring(eq + 1);
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
