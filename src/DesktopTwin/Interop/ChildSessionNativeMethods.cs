using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using DesktopTwin.Services;
using Microsoft.Win32;

namespace DesktopTwin.Interop;

internal enum RdpInputWindowApplyResult
{
    Applied,
    WindowMissing,
    Failed
}

internal static class ChildSessionNativeMethods
{
    private const int DefaultRdpPort = 3389;
    private const int ErrorNotFound = 1168;
    private const string RdpTcpRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string TermServiceParametersRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string RdpWrapperLibraryName = "rdpwrap.dll";
    private const uint NoChildSessionId = uint.MaxValue;
    private static readonly IntPtr CurrentServerHandle = IntPtr.Zero;
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> RdpInputWindows = new();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr serverHandle,
        uint sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumChildWindowCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumLength);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(
        IntPtr window,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    internal static IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    internal static void EnableChildSessions()
    {
        if (!WTSEnableChildSessions(true))
        {
            throw CreateLastWin32Exception("无法启用 RDP Child Session");
        }
    }

    internal static int GetConfiguredRdpPort()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var rdpTcpKey = localMachine.OpenSubKey(RdpTcpRegistryPath);
            var configuredPort = rdpTcpKey?.GetValue("PortNumber");
            return configuredPort is int port and > 0 and <= ushort.MaxValue
                ? port
                : DefaultRdpPort;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return DefaultRdpPort;
        }
    }

    internal static bool IsRdpWrapperEnabled()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var termServiceParametersKey =
                localMachine.OpenSubKey(TermServiceParametersRegistryPath);
            var serviceLibraryPath =
                termServiceParametersKey?.GetValue("ServiceDll") as string;
            return serviceLibraryPath?.Contains(
                RdpWrapperLibraryName,
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }

    internal static bool IsChildSessionsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
        {
            throw CreateLastWin32Exception("无法读取 RDP Child Session 状态");
        }

        return enabled;
    }

    /// <summary>
    /// 关闭 Windows 的 Child Sessions 功能。
    ///
    /// 该开关是系统级持久设置，关闭后本机不再支持创建第二个桌面会话。
    /// 只有在用户明确要求「恢复系统原状」时才调用。
    /// </summary>
    internal static void DisableChildSessions()
    {
        if (!WTSEnableChildSessions(false))
        {
            throw CreateLastWin32Exception("无法关闭 RDP Child Session");
        }
    }

    internal static uint? TryGetChildSessionId()
    {
        return TryQueryChildSessionId(out var sessionId) ? sessionId : null;
    }

    /// <summary>
    /// 区分「确认没有子会话」和「查询本身失败」。前者返回 true 且 sessionId 为 null；
    /// 后者返回 false。恢复、连接和守卫必须使用这个三态结果，不能把 WTS 查询故障
    /// 当成会话已经消失。
    /// </summary>
    internal static bool TryQueryChildSessionId(out uint? sessionId)
    {
        var succeeded = WTSGetChildSessionId(out var value);
        var error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return InterpretChildSessionQuery(succeeded, value, error, out sessionId);
    }

    /// <summary>
    /// 解释一次 WTS Child Session 查询。只有查询成功，或明确返回
    /// <c>ERROR_NOT_FOUND</c>，才能证明「没有子会话」；其他错误一律为未知。
    /// </summary>
    internal static bool InterpretChildSessionQuery(
        bool succeeded,
        uint value,
        int win32Error,
        out uint? sessionId)
    {
        sessionId = null;
        if (succeeded)
        {
            // 官方契约：没有子会话时返回 (ULONG)-1。会话 0 不是可用的交互
            // 子会话；若 API 异常地成功返回 0，只能视为未知，绝不能让
            // 守卫把 Session 0 当成目标会话。
            if (value == NoChildSessionId)
            {
                return true;
            }

            if (value == 0)
            {
                return false;
            }

            sessionId = value;
            return true;
        }

        // 没有子会话是本机正常状态，WTS 用 ERROR_NOT_FOUND 表示。
        return win32Error == ErrorNotFound;
    }

    /// <summary>
    /// 查询当前子会话；WTS 查询异常时抛出，而不是伪装成「没有会话」。
    /// </summary>
    internal static uint? GetChildSessionIdStrict()
    {
        if (WTSGetChildSessionId(out var sessionId))
        {
            if (sessionId == NoChildSessionId)
            {
                return null;
            }

            if (sessionId == 0)
            {
                throw new Win32Exception(
                    0,
                    "WTSGetChildSessionId 成功返回会话 0；该值不是可用的交互子会话，已失败关闭。");
            }

            return sessionId;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return null;
        }

        throw new Win32Exception(
            error,
            $"无法取得 RDP Child Session ID（Win32 错误 {error}）");
    }

    /// <summary>
    /// 等待指定会话真正消失。注销启动项隔离必须等 RDP 会话结束后再放开，
    /// 否则「会话断开/注销」触发的计划任务仍可能在日志恢复后启动。
    /// </summary>
    internal static bool WaitForSessionExit(uint sessionId, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        var missingScans = 0;
        while (true)
        {
            if (!TryGetSessionState(sessionId, out var state))
            {
                // WTS 查询失败不能证明会话已消失。中断计数，等待查询恢复或超时。
                missingScans = 0;
            }
            else if (state < 0)
            {
                // 必须连续三次“查询成功且不存在”才认定会话已消失。
                if (++missingScans >= 3)
                {
                    return true;
                }
            }
            else
            {
                missingScans = 0;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            System.Threading.Thread.Sleep(250);
        }
    }

    /// <summary>
    /// 枚举本机全部会话。
    ///
    /// 失败时返回空数组而不是抛异常：调用方（UU 视角切换）在切换过程中会被别的进程
    /// 反复改变会话状态，查询失败是常态，不该让一次查询失败打断整个切换流程。
    /// </summary>
    internal static IReadOnlyList<SessionInfo> EnumerateSessions()
    {
        return TryEnumerateSessions(out var sessions) ? sessions : [];
    }

    /// <summary>
    /// 三态枚举本机会话。返回 false 表示 WTS 查询失败，调用方不得把空结果
    /// 当成「本机没有任何会话」。
    /// </summary>
    private static bool TryEnumerateSessions(out IReadOnlyList<SessionInfo> result)
    {
        result = Array.Empty<SessionInfo>();
        if (!WTSEnumerateSessionsW(CurrentServerHandle, 0, 1, out var buffer, out var count))
        {
            return false;
        }

        if (buffer == IntPtr.Zero)
        {
            return count == 0;
        }

        var sessions = new List<SessionInfo>(count);
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WtsSessionInfo>(
                    buffer + (index * size));

                // 会话号大于 65535 的是伪会话（例如 Services），不参与判断。
                if (item.SessionId > ushort.MaxValue)
                {
                    continue;
                }

                sessions.Add(new SessionInfo(
                    (uint)item.SessionId,
                    item.WinStationName == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUni(item.WinStationName) ?? string.Empty,
                    item.State,
                    QuerySessionUserName((uint)item.SessionId)));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        result = sessions;
        return true;
    }

    /// <summary>
    /// 取「真正接着显示器」的那个会话。
    ///
    /// 先按 WinStation 名找 Console：<c>WTSGetActiveConsoleSessionId</c> 在会话切换的瞬间
    /// 会短暂返回别的值（甚至 0xFFFFFFFF），只靠它会认错会话。
    /// </summary>
    internal static uint? TryGetConsoleSessionId()
    {
        foreach (var session in EnumerateSessions())
        {
            if (string.Equals(session.WinStationName, "Console", StringComparison.OrdinalIgnoreCase))
            {
                return session.SessionId;
            }
        }

        return null;
    }

    /// <summary>
    /// 查询会话的连接状态。会话不存在时返回 -1。
    /// </summary>
    internal static int GetSessionState(uint sessionId)
    {
        return TryGetSessionState(sessionId, out var state) ? state : -1;
    }

    /// <summary>
    /// 三态查询会话状态：返回 false 表示 WTS 查询失败，true 且状态为 -1 表示
    /// 已确认会话不存在。
    /// </summary>
    internal static bool TryGetSessionState(uint sessionId, out int state)
    {
        state = -1;
        if (!TryEnumerateSessions(out var sessions))
        {
            return false;
        }

        foreach (var session in sessions)
        {
            if (session.SessionId == sessionId)
            {
                state = session.State;
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// 断开一个会话但不注销它：桌面上的程序继续运行，会话进入 Disconnected 状态。
    ///
    /// 这是 UU 视角切换「去整台电脑」的关键一步——UU 的被控端由 SYSTEM 服务拉起，
    /// 只会挑会话号最大的已登录桌面，把分身断掉它就只能落到 Console。
    /// </summary>
    internal static bool DisconnectSession(uint sessionId, bool wait = false)
    {
        return WTSDisconnectSession(CurrentServerHandle, sessionId, wait);
    }

    /// <summary>
    /// 会话状态码是否为「已连接（Active）」，即桌面上真的有人。
    /// </summary>
    internal static bool IsSessionActive(uint sessionId) => GetSessionState(sessionId) == 0;

    /// <summary>
    /// 读子会话本次登录的时间。WTS 可能会复用同一个会话号；登录时间是区分
    /// 「旧会话消失后又以同号出现」的稳定身份，取不到时返回 null。
    ///
    /// WTS_INFO_CLASS.WTSLogonTime（18）在受支持的 Windows 上固定返回失败并
    /// 设置 ERROR_NOT_SUPPORTED。必须查询 WTSSessionInfoEx（25），从
    /// WTSINFOEX_LEVEL1_W 的 LogonTime 字段读取 64 位 FILETIME。
    /// </summary>
    internal static DateTime? TryGetSessionLogonTime(uint sessionId)
    {
        const int sessionInfoExClass = 25;
        const int logonTimeEndOffset = 176; // 8 字节头 + 结构体偏移 160 + 8 字节值

        if (!WTSQuerySessionInformationW(
                CurrentServerHandle,
                sessionId,
                sessionInfoExClass,
                out var buffer,
                out var bytesReturned)
            || buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (bytesReturned < logonTimeEndOffset)
            {
                return null;
            }

            var levelAndLogonTime = new byte[logonTimeEndOffset];
            Marshal.Copy(buffer, levelAndLogonTime, 0, levelAndLogonTime.Length);
            return ParseSessionLogonTime(levelAndLogonTime);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// 从 WTSINFOEXW 的原始字节解析登录时间。单独拆出来便于用固定布局测试，
    /// 避免测试依赖当前会话是否真的有人登录。
    /// </summary>
    internal static DateTime? ParseSessionLogonTime(ReadOnlySpan<byte> sessionInfo)
    {
        const int logonTimeOffset = 168;

        if (sessionInfo.Length < logonTimeOffset + sizeof(long)
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                sessionInfo.Slice(0, sizeof(uint))) != 1)
        {
            return null;
        }

        var fileTime = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
            sessionInfo.Slice(logonTimeOffset, sizeof(long)));
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读会话的登录用户名。取不到时返回空串。
    /// </summary>
    private static string QuerySessionUserName(uint sessionId)
    {
        const int userNameInfoClass = 5;

        if (!WTSQuerySessionInformationW(
                CurrentServerHandle,
                sessionId,
                userNameInfoClass,
                out var buffer,
                out _)
            || buffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static uint? TerminateChildSession()
    {
        return TerminateChildSession(expectedSessionId: null, wait: true);
    }

    internal static uint? TerminateChildSession(bool wait)
    {
        return TerminateChildSession(expectedSessionId: null, wait: wait);
    }

    /// <summary>
    /// 仅当当前 Child Session 与预期标识一致时注销会话，避免旧界面操作误伤新会话。
    /// </summary>
    /// <param name="expectedSessionId">用户请求关闭的 Child Session 标识。</param>
    internal static uint? TerminateChildSession(uint expectedSessionId)
    {
        return TerminateChildSession(expectedSessionId, wait: true);
    }

    /// <summary>
    /// 仅当当前 Child Session 与预期标识一致时注销会话，并指定是否等待注销完成。
    /// 退出流程用 <c>wait: false</c>，避免阻塞进程关闭。
    /// </summary>
    /// <param name="expectedSessionId">需要匹配的会话标识。</param>
    /// <param name="wait">是否等待注销操作完成。</param>
    internal static uint? TerminateChildSession(uint expectedSessionId, bool wait)
    {
        return TerminateChildSession((uint?)expectedSessionId, wait);
    }

    /// <summary>
    /// 查询并注销当前 Child Session，可选校验预期会话标识。
    /// </summary>
    /// <param name="expectedSessionId">需要匹配的会话标识；为空时注销当前 Child Session。</param>
    /// <param name="wait">是否等待注销操作完成。</param>
    private static uint? TerminateChildSession(uint? expectedSessionId, bool wait)
    {
        if (!WTSGetChildSessionId(out var childSessionId))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(
                error,
                $"无法取得 RDP Child Session ID（Win32 错误 {error}）");
        }

        if (childSessionId == NoChildSessionId)
        {
            return null;
        }

        if (expectedSessionId is { } expected && childSessionId != expected)
        {
            throw new InvalidOperationException(
                $"桌面分身会话已从 {expected} 变更为 {childSessionId}，已取消注销操作。");
        }

        if (!WTSLogoffSession(CurrentServerHandle, childSessionId, wait))
        {
            throw CreateLastWin32Exception($"无法注销 Child Session {childSessionId}");
        }

        return childSessionId;
    }

    internal static bool TryFocusRdpInputWindow(IntPtr rdpHostWindow)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow, out _);
        if (inputWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = SetFocus(inputWindow);
        return GetFocus() == inputWindow;
    }

    internal static bool IsRdpInputWindowFocused(IntPtr rdpHostWindow)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow, out _);
        return inputWindow != IntPtr.Zero && GetFocus() == inputWindow;
    }

    /// <summary>
    /// 禁用或恢复 RDP 的输入窗口，并在 IsWindow 仍有效的前提下读回真实状态。
    /// EnableWindow 的返回值表示“此前是否已禁用”，不能用来判断本次是否成功，
    /// 因此调用方必须依据返回值决定是否继续连接。
    /// </summary>
    internal static RdpInputWindowApplyResult SetRdpInputWindowEnabled(
        IntPtr rdpHostWindow,
        bool enabled)
    {
        var inputWindow = FindRdpInputWindow(rdpHostWindow, out var fromCache);
        if (inputWindow == IntPtr.Zero)
        {
            AppLog.Warn(
                $"SetRdpInputWindowEnabled: enabled={enabled}，未找到 Input Capture Window（host=0x{rdpHostWindow:X}）。");
            return RdpInputWindowApplyResult.WindowMissing;
        }

        var beforeEnabled = IsWindowEnabled(inputWindow);
        _ = EnableWindow(inputWindow, enabled);
        var windowStillExists = IsWindow(inputWindow);
        var afterEnabled = windowStillExists && IsWindowEnabled(inputWindow);
        AppLog.Info(
            $"SetRdpInputWindowEnabled: enabled={enabled}，inputWindow=0x{inputWindow:X}，"
            + $"来源={(fromCache ? "缓存" : "枚举")}，实际状态 before={beforeEnabled} "
            + $"after={afterEnabled} exists={windowStillExists}。");

        if (!windowStillExists)
        {
            RdpInputWindows.TryRemove(rdpHostWindow, out _);
            return RdpInputWindowApplyResult.Failed;
        }

        return afterEnabled == enabled
            ? RdpInputWindowApplyResult.Applied
            : RdpInputWindowApplyResult.Failed;
    }

    internal static void ClearRdpInputWindowCache(IntPtr rdpHostWindow)
    {
        RdpInputWindows.TryRemove(rdpHostWindow, out _);
    }

    private static IntPtr FindRdpInputWindow(IntPtr rdpHostWindow, out bool fromCache)
    {
        fromCache = false;
        if (rdpHostWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (RdpInputWindows.TryGetValue(rdpHostWindow, out var cachedInputWindow))
        {
            if (IsWindow(cachedInputWindow) && IsChild(rdpHostWindow, cachedInputWindow))
            {
                fromCache = true;
                return cachedInputWindow;
            }

            RdpInputWindows.TryRemove(rdpHostWindow, out _);
        }

        var inputWindow = IntPtr.Zero;
        EnumChildWindowCallback callback = (window, _) =>
        {
            const int windowTextCapacity = 256;
            var windowText = new StringBuilder(windowTextCapacity);
            _ = GetWindowText(window, windowText, windowText.Capacity);
            if (!string.Equals(
                    windowText.ToString(),
                    "Input Capture Window",
                    StringComparison.Ordinal))
            {
                return true;
            }

            inputWindow = window;
            return false;
        };

        _ = EnumChildWindows(rdpHostWindow, callback, IntPtr.Zero);
        if (inputWindow != IntPtr.Zero)
        {
            RdpInputWindows[rdpHostWindow] = inputWindow;
        }
        return inputWindow;
    }

    private static Win32Exception CreateLastWin32Exception(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation}（Win32 错误 {error}）");
    }

    private delegate bool EnumChildWindowCallback(IntPtr window, IntPtr parameter);

    /// <summary>
    /// <c>WTS_SESSION_INFO</c> 的托管映射。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        internal int SessionId;
        internal IntPtr WinStationName;
        internal int State;
    }
}

/// <summary>
/// 一个 Windows 会话的快照。
/// </summary>
/// <param name="SessionId">会话号。</param>
/// <param name="WinStationName">WinStation 名，Console 表示接显示器的那个会话。</param>
/// <param name="State">连接状态码，0 表示 Active。</param>
/// <param name="UserName">登录用户名，未登录时为空串。</param>
internal readonly record struct SessionInfo(
    uint SessionId,
    string WinStationName,
    int State,
    string UserName);
