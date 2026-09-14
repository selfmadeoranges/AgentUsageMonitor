using Microsoft.Win32;

namespace UsageMonitor;

/// <summary>HKCU\...\Run 에 실행 파일을 등록/해제한다.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageMonitor";

    public static void Apply(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (exe == null)
            {
                return;
            }
            key.SetValue(ValueName, "\"" + exe + "\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
