using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PathEcho.Core.Restore;

namespace PathEcho.Platform.Windows.Restore;

public sealed class RestartManagerOccupancyService : IFileOccupancyService
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxAppName = 255;
    private const int MaxServiceName = 63;

    public Task<IReadOnlyList<OccupiedFile>> FindAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existingFiles = paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existingFiles.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<OccupiedFile>>(Array.Empty<OccupiedFile>());
        }

        var sessionKey = new StringBuilder(33);
        var result = RmStartSession(out var session, 0, sessionKey);
        ThrowIfFailed(result, "启动文件占用检查");
        try
        {
            result = RmRegisterResources(
                session,
                (uint)existingFiles.Length,
                existingFiles,
                0,
                null,
                0,
                null);
            ThrowIfFailed(result, "注册待恢复文件");

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = 0;
            result = RmGetList(session, out needed, ref count, null, ref rebootReasons);
            if (result == ErrorSuccess && needed == 0)
            {
                return Task.FromResult<IReadOnlyList<OccupiedFile>>(Array.Empty<OccupiedFile>());
            }

            if (result != ErrorMoreData)
            {
                ThrowIfFailed(result, "读取占用进程数量");
            }

            var processInfo = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, processInfo, ref rebootReasons);
            ThrowIfFailed(result, "读取占用进程");
            var processes = processInfo.Take((int)count).Select(ToLockingProcess).ToArray();
            return Task.FromResult<IReadOnlyList<OccupiedFile>>(
                processes.Length == 0
                    ? Array.Empty<OccupiedFile>()
                    : new[] { new OccupiedFile("恢复范围", processes) });
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    public async Task TerminateAsync(
        IReadOnlyList<LockingProcess> processes,
        CancellationToken cancellationToken = default)
    {
        foreach (var processInfo in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!processInfo.CanTerminate)
            {
                throw new InvalidOperationException(
                    $"不能结束进程 {processInfo.Name} ({processInfo.ProcessId})：{processInfo.ReasonCannotTerminate}");
            }

            using var process = Process.GetProcessById(processInfo.ProcessId);
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (actualStart.ToFileTime() != processInfo.StartedAtUtc.ToFileTime())
            {
                throw new InvalidOperationException($"进程 {processInfo.ProcessId} 的身份已变化，已取消结束操作。");
            }

            if (!TryConfirmNonCritical(process, out var reason))
            {
                throw new InvalidOperationException(
                    $"不能结束进程 {processInfo.Name} ({processInfo.ProcessId})：{reason}");
            }

            if (process.CloseMainWindow())
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                catch (TimeoutException)
                {
                }
            }

            process.Kill(true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static LockingProcess ToLockingProcess(RmProcessInfo info)
    {
        var pid = unchecked((int)info.Process.ProcessId);
        var startedAt = DateTimeOffset.FromFileTime(info.Process.ProcessStartTime.ToLong());
        var name = string.IsNullOrWhiteSpace(info.AppName) ? $"PID {pid}" : info.AppName;
        if (pid is 0 or 4 || pid == Environment.ProcessId)
        {
            return new LockingProcess(pid, startedAt, name, false, "系统或 PathEcho 自身进程");
        }

        if (info.ApplicationType is RmAppType.Service or RmAppType.Critical)
        {
            return new LockingProcess(pid, startedAt, name, false, "服务或系统关键进程");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return TryConfirmNonCritical(process, out var reason)
                ? new LockingProcess(pid, startedAt, name, true)
                : new LockingProcess(pid, startedAt, name, false, reason);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return new LockingProcess(pid, startedAt, name, false, "无法重新确认进程身份");
        }
    }

    private static bool TryConfirmNonCritical(Process process, out string? reason)
    {
        if (process.Id is 0 or 4 || process.Id == Environment.ProcessId)
        {
            reason = "系统或 PathEcho 自身进程";
            return false;
        }

        if (!IsProcessCritical(process.SafeHandle.DangerousGetHandle(), out var critical))
        {
            reason = $"无法确认是否为关键进程，Win32 错误 {Marshal.GetLastWin32Error()}";
            return false;
        }

        reason = critical ? "系统关键进程" : null;
        return !critical;
    }

    private static void ThrowIfFailed(int result, string action)
    {
        if (result != ErrorSuccess)
        {
            throw new Win32Exception(result, $"{action}失败");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint LowDateTime;
        public readonly uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RmUniqueProcess
    {
        public readonly uint ProcessId;
        public readonly NativeFileTime ProcessStartTime;
    }

    private enum RmAppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceName + 1)]
        public string ServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[]? fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RmProcessInfo[]? affectedApps,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}
