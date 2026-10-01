using System.Runtime.InteropServices;

namespace Clockwork.Overlay;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor, rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength, dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                 ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
}

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const uint GW_OWNER = 4;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const nint WS_EX_TRANSPARENT = 0x20,
                       WS_EX_TOOLWINDOW = 0x80,
                       WS_EX_LAYERED = 0x80000,
                       WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        nint systemInformation,
        int systemInformationLength,
        out int returnLength);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    // x64 layout documented for SYSTEM_PROCESS_INFORMATION.
    private const int CreateTimeOffset = 32;
    private const int ProcessNameOffset = 56;
    private const int ProcessIdOffset = 80;
    private const int WorkingSetOffset = 144;

    public static bool TryGetForegroundFullscreenCandidate(out nint hwnd, out int pid, out RECT monitor)
    {
        hwnd = 0;
        pid = 0;
        monitor = default;

        try
        {
            hwnd = GetForegroundWindow();
            if (hwnd == 0)
                return false;

            GetWindowThreadProcessId(hwnd, out var rawPid);
            if (rawPid == 0 || rawPid == (uint)Environment.ProcessId)
                return false;
            pid = (int)rawPid;

            var monitorHandle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitorHandle == 0)
                return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitorHandle, ref mi) || !GetWindowRect(hwnd, out var windowRect))
                return false;

            monitor = mi.rcMonitor;
            return CoversMonitor(windowRect, monitor);
        }
        catch
        {
            hwnd = 0;
            pid = 0;
            monitor = default;
            return false;
        }
    }

    public static bool IsWindowForProcess(nint hwnd, int pid)
    {
        if (hwnd == 0 || pid <= 0 || !IsWindow(hwnd))
            return false;

        GetWindowThreadProcessId(hwnd, out var ownerPid);
        return ownerPid == (uint)pid;
    }

    public static bool TryGetClientAreaScreenRect(nint hWnd, out RECT screenRect)
    {
        screenRect = default;
        if (hWnd == 0)
            return false;

        try
        {
            if (!GetClientRect(hWnd, out var clientRect))
                return false;

            var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
            var bottomRight = new POINT { X = clientRect.Right, Y = clientRect.Bottom };
            if (!ClientToScreen(hWnd, ref topLeft) || !ClientToScreen(hWnd, ref bottomRight))
                return false;

            screenRect = new RECT
            {
                Left = topLeft.X,
                Top = topLeft.Y,
                Right = bottomRight.X,
                Bottom = bottomRight.Y,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetMainWindowForProcess(int pid, nint cachedWindow, out nint window)
    {
        window = 0;

        if (IsWindowForProcess(cachedWindow, pid) && GetWindow(cachedWindow, GW_OWNER) == 0)
        {
            window = cachedWindow;
            return true;
        }

        try
        {
            nint bestVisible = 0;
            nint bestAny = 0;
            long bestArea = -1;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == 0)
                    return true;

                GetWindowThreadProcessId(hwnd, out var rawPid);
                if (rawPid != (uint)pid || GetWindow(hwnd, GW_OWNER) != 0)
                    return true;

                if (bestAny == 0)
                    bestAny = hwnd;

                if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    long area = 0;
                    if (GetClientRect(hwnd, out var rect))
                        area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);

                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestVisible = hwnd;
                    }
                }

                return true;
            }, 0);

            window = bestVisible != 0 ? bestVisible : bestAny;
            return window != 0;
        }
        catch
        {
            window = 0;
            return false;
        }
    }

    public static (double usedGb, double totalGb, double percent)? GetRamUsage()
    {
        try
        {
            var memory = new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };

            if (!GlobalMemoryStatusEx(ref memory) || memory.ullTotalPhys == 0)
                return null;

            var totalGb = memory.ullTotalPhys / 1073741824.0;
            var usedGb = (memory.ullTotalPhys - memory.ullAvailPhys) / 1073741824.0;
            return (usedGb, totalGb, memory.dwMemoryLoad);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static bool CoversMonitor(RECT window, RECT monitor) =>
        window.Left <= monitor.Left &&
        window.Top <= monitor.Top &&
        window.Right >= monitor.Right &&
        window.Bottom >= monitor.Bottom;

    internal sealed class ProcessSnapshotTable : IDisposable
    {
        private nint _buffer;
        private int _capacity = 256 * 1024;
        private bool _disposed;

        private readonly Dictionary<int, ProcessSnapshot> _requested = new();

        public ProcessSnapshotTable()
        {
            _buffer = Marshal.AllocHGlobal(_capacity);
        }

        public bool Refresh(int requestedPid, out ProcessSnapshot snapshot)
        {
            snapshot = default;
            if (requestedPid <= 0 || _disposed)
                return false;

            _requested.Clear();
            _requested[requestedPid] = default;

            if (!QuerySnapshotTable())
                return false;

            return _requested.TryGetValue(requestedPid, out snapshot) && !string.IsNullOrWhiteSpace(snapshot.Name);
        }

        private bool QuerySnapshotTable()
        {
            while (true)
            {
                var status = NtQuerySystemInformation(
                    SystemProcessInformation,
                    _buffer,
                    _capacity,
                    out var returnedLength);

                if (status == 0)
                    break;

                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall)
                {
                    var nextCapacity = Math.Max(_capacity * 2, returnedLength > 0 ? returnedLength + 64 * 1024 : 0);
                    if (nextCapacity <= _capacity || nextCapacity > 256 * 1024 * 1024)
                        return false;

                    Marshal.FreeHGlobal(_buffer);
                    _capacity = nextCapacity;
                    _buffer = Marshal.AllocHGlobal(_capacity);
                    continue;
                }

                return false;
            }

            ParseSnapshotTable();
            return true;
        }

        private void ParseSnapshotTable()
        {
            var baseAddress = _buffer;
            var offset = 0;

            while (offset + ProcessNameOffset < _capacity)
            {
                var entry = IntPtr.Add(baseAddress, offset);
                var nextOffset = unchecked((uint)Marshal.ReadInt32(entry, 0));

                var pid = unchecked((int)Marshal.ReadInt64(entry, ProcessIdOffset));
                if (pid > 0 && _requested.ContainsKey(pid))
                {
                    var name = ReadUnicodeString(IntPtr.Add(entry, ProcessNameOffset));
                    var workingSet = ReadSizeT(entry, WorkingSetOffset);
                    var createTime = Marshal.ReadInt64(entry, CreateTimeOffset);
                    _requested[pid] = new ProcessSnapshot(name ?? string.Empty, workingSet, createTime);
                }

                if (nextOffset == 0)
                    break;

                if (nextOffset > int.MaxValue - offset)
                    break;

                offset += (int)nextOffset;
            }
        }

        private static long? ReadSizeT(nint entry, int offset)
        {
            try
            {
                var value = Marshal.ReadInt64(entry, offset);
                return value >= 0 ? value : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadUnicodeString(nint entry)
        {
            try
            {
                var length = (ushort)Marshal.ReadInt16(entry, 0);
                var buffer = Marshal.ReadIntPtr(entry, 8);
                if (length == 0 || buffer == 0)
                    return string.Empty;

                return Marshal.PtrToStringUni(buffer, length / 2);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
            }
            GC.SuppressFinalize(this);
        }
    }
}

internal readonly record struct ProcessSnapshot(string Name, long? WorkingSetBytes, long CreateTime);

internal readonly record struct GameInfo(
    int Pid,
    string Name,
    long? WorkingSetBytes,
    long CreateTime,
    RECT Monitor,
    nint WindowHandle,
    bool IsFullscreen,
    bool IsForeground,
    bool IsWindowVisible,
    bool IsMinimized);

internal static class GameDetector
{
    public static bool TryGet(Win32.ProcessSnapshotTable snapshots, out GameInfo game)
    {
        game = default;

        // Cheap rejection first: only a fullscreen foreground window can start a session.
        // The process-table query happens only after this passes.
        if (!Win32.TryGetForegroundFullscreenCandidate(out var hwnd, out var pid, out var monitor))
            return false;

        if (pid <= 0 || pid == Environment.ProcessId)
            return false;

        if (!snapshots.Refresh(pid, out var process))
            return false;

        var name = Path.GetFileNameWithoutExtension(process.Name);
        if (string.IsNullOrWhiteSpace(name) || IsExecutableBlacklisted(name))
            return false;

        game = new GameInfo(
            pid,
            name,
            process.WorkingSetBytes,
            process.CreateTime,
            monitor,
            hwnd,
            true,
            Win32.GetForegroundWindow() == hwnd,
            Win32.IsWindowVisible(hwnd),
            Win32.IsIconic(hwnd));
        return true;
    }

    public static bool TryRefreshTracked(
        Win32.ProcessSnapshotTable snapshots,
        GameInfo tracked,
        out GameInfo game)
    {
        game = default;

        if (!snapshots.Refresh(tracked.Pid, out var process))
            return false;

        var name = Path.GetFileNameWithoutExtension(process.Name);
        if (!string.Equals(name, tracked.Name, StringComparison.OrdinalIgnoreCase) ||
            (tracked.CreateTime != 0 && process.CreateTime != 0 && process.CreateTime != tracked.CreateTime))
            return false; // PID was recycled.

        if (IsExecutableBlacklisted(name))
            return false;

        var hwnd = tracked.WindowHandle;
        Win32.TryGetMainWindowForProcess(tracked.Pid, hwnd, out var refreshedHwnd);
        hwnd = refreshedHwnd != 0 ? refreshedHwnd : hwnd;

        return UpdateWindowState(tracked with
        {
            Name = name,
            WorkingSetBytes = process.WorkingSetBytes,
            CreateTime = process.CreateTime,
            WindowHandle = hwnd,
        }, out game);
    }

    public static bool TryRefreshTrackedWindowOnly(GameInfo tracked, out GameInfo game)
    {
        game = default;
        var hwnd = tracked.WindowHandle;
        Win32.TryGetMainWindowForProcess(tracked.Pid, hwnd, out var refreshedHwnd);
        hwnd = refreshedHwnd != 0 ? refreshedHwnd : hwnd;

        if (hwnd == 0)
            return false;

        return UpdateWindowState(tracked with { WindowHandle = hwnd }, out game);
    }

    public static bool IsExecutableBlacklisted(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;

        var normalized = Path.GetFileNameWithoutExtension(executableName.Trim());
        return !string.IsNullOrWhiteSpace(normalized)
            && IgnoredApplications.ExecutableNames.Contains(normalized);
    }

    private static bool UpdateWindowState(GameInfo tracked, out GameInfo game)
    {
        game = tracked;
        var hwnd = tracked.WindowHandle;
        if (hwnd == 0 || !Win32.IsWindow(hwnd))
            return false;

        var monitor = tracked.Monitor;
        var monitorHandle = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitorHandle != 0)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitorHandle, ref mi))
                monitor = mi.rcMonitor;
        }

        var isFullscreen = tracked.IsFullscreen;
        if (Win32.GetWindowRect(hwnd, out var windowRect))
            isFullscreen = CoversMonitor(windowRect, monitor);

        game = tracked with
        {
            Monitor = monitor,
            IsFullscreen = isFullscreen,
            IsForeground = Win32.GetForegroundWindow() == hwnd,
            IsWindowVisible = Win32.IsWindowVisible(hwnd),
            IsMinimized = Win32.IsIconic(hwnd),
        };
        return true;
    }

    private static bool CoversMonitor(RECT window, RECT monitor) =>
        window.Left <= monitor.Left &&
        window.Top <= monitor.Top &&
        window.Right >= monitor.Right &&
        window.Bottom >= monitor.Bottom;
}
