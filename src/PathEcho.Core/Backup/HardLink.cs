using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PathEcho.Core.Backup;

internal static class HardLink
{
    public static bool TryCreate(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (CreateHardLink(linkPath, existingPath, IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is 1 or 5 or 17 or 50 or 87)
        {
            return false;
        }

        throw new Win32Exception(error, $"无法创建备份硬链接：{linkPath}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
