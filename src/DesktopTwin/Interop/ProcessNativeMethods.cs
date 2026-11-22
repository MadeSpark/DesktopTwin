using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopTwin.Interop;

/// <summary>
/// 进程相关的 Win32 调用。
///
/// 主要用途是结束 UU 的串流端 <c>GameViewerServer.exe</c>——它由 SYSTEM 服务拉起，
/// 属于高权限进程，普通管理员令牌默认拿不到它的句柄，需要先启用 <c>SeDebugPrivilege</c>。
/// </summary>
internal static class ProcessNativeMethods
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint Synchronize = 0x00100000;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int ErrorInvalidData = 13;
    private const int ErrorTimeout = 1460;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const string SeDebugPrivilege = "SeDebugPrivilege";

    /// <summary>
    /// 启用当前进程的 <c>SeDebugPrivilege</c>。失败不抛异常——拿不到就退回普通结束流程。
    /// </summary>
    /// <returns>成功启用返回 true。</returns>
    internal static bool TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SeDebugPrivilege, out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            return AdjustTokenPrivileges(
                token,
                disableAllPrivileges: false,
                ref privileges,
                bufferLength: 0,
                previousState: IntPtr.Zero,
                returnLength: IntPtr.Zero)
                && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>
    /// 结束指定进程。
    /// </summary>
    /// <param name="processId">进程标识。</param>
    /// <param name="errorCode">失败时的 Win32 错误码。</param>
    /// <returns>成功返回 true。</returns>
    internal static bool TryTerminateProcess(int processId, out int errorCode)
    {
        errorCode = 0;
        var handle = OpenProcess(ProcessTerminate, inheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (!TerminateProcess(handle, exitCode: 1))
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            return true;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>
    /// 在持有的进程句柄上复核创建时间、完整镜像路径和目标会话，再结束并等待同一进程。
    /// PID 可能在枚举与操作之间被复用，因此不能只凭 PID 直接调用 TerminateProcess。
    /// </summary>
    /// <param name="processId">枚举时记录的进程标识。</param>
    /// <param name="expectedSessionId">已捕获的子会话号。</param>
    /// <param name="expectedCreationTimeUtc">枚举时记录的进程创建时间（UTC FILETIME 刻度）。</param>
    /// <param name="expectedImagePath">枚举时确认过的完整镜像路径。</param>
    /// <param name="timeoutMilliseconds">等待该句柄对应进程退出的最长时间。</param>
    /// <param name="terminated">本次是否实际发出了终止请求；进程若已先退出则为 false。</param>
    /// <param name="errorCode">失败时的 Win32 错误码；身份不匹配使用 ERROR_INVALID_DATA。</param>
    /// <returns>身份完全一致且句柄对应进程已经退出时返回 true。</returns>
    internal static bool TryTerminateProcessExact(
        int processId,
        uint expectedSessionId,
        long expectedCreationTimeUtc,
        string expectedImagePath,
        int timeoutMilliseconds,
        out bool terminated,
        out int errorCode)
    {
        terminated = false;
        errorCode = 0;
        if (processId <= 0
            || expectedSessionId == 0
            || expectedCreationTimeUtc <= 0
            || string.IsNullOrWhiteSpace(expectedImagePath)
            || timeoutMilliseconds <= 0)
        {
            errorCode = ErrorInvalidData;
            return false;
        }

        var handle = OpenProcess(
            ProcessTerminate | ProcessQueryInformation | Synchronize,
            inheritHandle: false,
            processId);
        if (handle == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            if (ToFileTimeTicks(creation) != expectedCreationTimeUtc)
            {
                errorCode = ErrorInvalidData;
                return false;
            }

            var imagePath = QueryImagePath(handle);
            if (imagePath is null)
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            // MainModule 和 QueryFullProcessImageName 对同一文件可能分别返回短名、
            // 卷 GUID 或带 . 的完整路径。枚举阶段已统一为 Path.GetFullPath，
            // 终止阶段必须使用同一个比较边界，否则安全校验会变成永久误拒绝。
            if (!TryNormalizeImagePath(imagePath, out var normalizedImagePath)
                || !TryNormalizeImagePath(expectedImagePath, out var normalizedExpectedImagePath)
                || !string.Equals(
                    normalizedImagePath,
                    normalizedExpectedImagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                errorCode = ErrorInvalidData;
                return false;
            }

            if (!ProcessIdToSessionId((uint)processId, out var sessionId))
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            if (sessionId != expectedSessionId)
            {
                errorCode = ErrorInvalidData;
                return false;
            }

            if (!GetExitCodeProcess(handle, out var exitCode))
            {
                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }

            if (exitCode == StillActive)
            {
                if (!TerminateProcess(handle, exitCode: 1))
                {
                    errorCode = Marshal.GetLastPInvokeError();
                    return false;
                }

                terminated = true;
            }

            var waitResult = WaitForSingleObject(handle, (uint)timeoutMilliseconds);
            if (waitResult == WaitObject0)
            {
                return true;
            }

            errorCode = waitResult switch
            {
                WaitTimeout => ErrorTimeout,
                WaitFailed => ReadWaitFailureErrorCode(),
                _ => ErrorInvalidData
            };
            return false;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static long ToFileTimeTicks(FileTime value)
    {
        return ((long)value.HighDateTime << 32) | value.LowDateTime;
    }

    private static int ReadWaitFailureErrorCode()
    {
        var lastError = Marshal.GetLastPInvokeError();
        return lastError != 0 ? lastError : ErrorInvalidData;
    }

    /// <summary>
    /// 把进程镜像路径归一为完整路径。只做确定性的词法归一，不访问文件系统；
    /// SessionAgentService 的枚举与替换路径必须共用这一边界。
    /// </summary>
    internal static bool TryNormalizeImagePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidate = path.Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            normalizedPath = Path.GetFullPath(candidate);
            return Path.IsPathFullyQualified(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? QueryImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(capacity: 32768);
        var length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
        {
            return null;
        }

        return buffer.ToString(0, (int)length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
