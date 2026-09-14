[CmdletBinding()]
param(
    [switch]$IncludeSecrets
)

$ErrorActionPreference = 'Stop'

if (-not $IncludeSecrets) {
    throw 'This command prints OAuth secrets. Run again with -IncludeSecrets only on a trusted computer.'
}

if (-not ('UsageMonitorCredentialReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class UsageMonitorCredentialReader
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static string Read(string target)
    {
        IntPtr handle;
        if (!CredReadW(target, 1, 0, out handle)) return null;
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }
}
'@
}

function Convert-ExpiryToUnixSeconds {
    param($Value)
    if ($null -eq $Value) { return 0 }
    try { return [DateTimeOffset]::Parse($Value.ToString()).ToUnixTimeSeconds() }
    catch { return 0 }
}

function Get-JwtExpiry {
    param([string]$Token)
    if ([string]::IsNullOrWhiteSpace($Token)) { return 0 }
    $parts = $Token.Split('.')
    if ($parts.Count -lt 2) { return 0 }
    try {
        $payload = $parts[1].Replace('-', '+').Replace('_', '/')
        $payload += '=' * ((4 - ($payload.Length % 4)) % 4)
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
        return [long]$json.exp
    }
    catch { return 0 }
}

$claudeRaw = [UsageMonitorCredentialReader]::Read('UsageMonitor/ClaudeOAuth')
if ([string]::IsNullOrWhiteSpace($claudeRaw)) {
    throw 'UsageMonitor Claude credentials were not found. Complete Claude login in UsageMonitor first.'
}
$claude = $claudeRaw | ConvertFrom-Json

$codexRaw = [UsageMonitorCredentialReader]::Read('UsageMonitor/CodexOAuth')
if (-not [string]::IsNullOrWhiteSpace($codexRaw)) {
    $codex = $codexRaw | ConvertFrom-Json
    $codexAccess = [string]$codex.AccessToken
    $codexRefresh = [string]$codex.RefreshToken
    $codexAccount = [string]$codex.AccountId
    $codexExpiry = Convert-ExpiryToUnixSeconds $codex.ExpiresAt
}
else {
    $codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $authPath = Join-Path $codexHome 'auth.json'
    if (-not (Test-Path -LiteralPath $authPath)) {
        throw 'UsageMonitor and Codex auth.json credentials were not found.'
    }
    $auth = Get-Content -LiteralPath $authPath -Raw | ConvertFrom-Json
    $codexAccess = [string]$auth.tokens.access_token
    $codexRefresh = [string]$auth.tokens.refresh_token
    $codexAccount = [string]$auth.tokens.account_id
    $codexExpiry = Get-JwtExpiry $codexAccess
}

if ([string]::IsNullOrWhiteSpace($codexAccess) -or [string]::IsNullOrWhiteSpace($codexRefresh) -or
    [string]::IsNullOrWhiteSpace($codexAccount)) {
    throw 'Codex access token, refresh token, or account ID is missing.'
}

$bundle = [ordered]@{
    version = 1
    claude = [ordered]@{
        accessToken = [string]$claude.AccessToken
        refreshToken = [string]$claude.RefreshToken
        accountId = ''
        expiresAt = Convert-ExpiryToUnixSeconds $claude.ExpiresAt
    }
    codex = [ordered]@{
        accessToken = $codexAccess
        refreshToken = $codexRefresh
        accountId = $codexAccount
        expiresAt = $codexExpiry
    }
}

$bundle | ConvertTo-Json -Depth 4
