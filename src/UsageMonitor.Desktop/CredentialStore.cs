using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UsageMonitor;

/// <summary>모니터가 직접 로그인해 받은 Claude OAuth 토큰.</summary>
public sealed class ClaudeTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>모니터가 직접 로그인해 받은 Codex(ChatGPT) OAuth 토큰.</summary>
public sealed class CodexTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccountId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Windows 자격 증명 관리자(Credential Manager)에 토큰을 저장한다.
/// 설정 파일이나 로그에는 토큰을 절대 쓰지 않는다.
/// </summary>
public static class CredentialStore
{
    private const string ClaudeTarget = "UsageMonitor/ClaudeOAuth";
    private const string CodexTarget = "UsageMonitor/CodexOAuth";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static ClaudeTokens? LoadClaudeTokens() => Load<ClaudeTokens>(ClaudeTarget);
    public static void SaveClaudeTokens(ClaudeTokens tokens) => Save(ClaudeTarget, tokens);
    public static void DeleteClaudeTokens() => Delete(ClaudeTarget);

    public static CodexTokens? LoadCodexTokens() => Load<CodexTokens>(CodexTarget);
    public static void SaveCodexTokens(CodexTokens tokens) => Save(CodexTarget, tokens);
    public static void DeleteCodexTokens() => Delete(CodexTarget);

    private static T? Load<T>(string target) where T : class
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out IntPtr handle))
        {
            return null;
        }

        try
        {
            CREDENTIAL cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            byte[] blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(blob));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CredFree(handle);
        }
    }

    private static void Save<T>(string target, T value)
    {
        byte[] blob = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(target);
        IntPtr userName = Marshal.StringToCoTaskMemUni("oauth");
        IntPtr blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };

            if (!CredWriteW(ref cred, 0))
            {
                throw new InvalidOperationException("자격 증명 저장 실패 (오류 " + Marshal.GetLastWin32Error() + ")");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userName);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    private static void Delete(string target)
    {
        if (!CredDeleteW(target, CredTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException("자격 증명 삭제 실패 (오류 " + error + ")");
            }
        }
    }
}
