using Microsoft.Win32;

namespace PathEcho.Platform.Windows.Startup;

public sealed class StartupRegistrationService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PathEcho";

    public bool IsEnabled(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        var actual = key?.GetValue(ValueName) as string;
        return string.Equals(actual, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" --background";
}
