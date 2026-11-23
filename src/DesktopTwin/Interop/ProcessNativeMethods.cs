using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    /// 进程生命周期内不可变的安全身份字段。镜像路径已经归一化。
    /// </summary>
    internal readonly record struct ProcessIdentitySnapshot(
        int ProcessId,
        uint SessionId,
        long CreationTimeUtc,
        string ImagePath)
    {
        internal bool IsComplete => ProcessId > 0
                                    && SessionId > 0
                                    && CreationTimeUtc > 0
                                    && !string.IsNullOrWhiteSpace(ImagePath);
    }

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
            // 卷 GUID 或目录联接别名。枚举与终止阶段必须共用同一个最终路径边界，
            // 否则安全校验会变成永久误拒绝，或在别名下误放行。
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

            // 该 API 只能按 PID 查询，但此时句柄已固定进程对象，创建时间和最终
            // 镜像路径也已与该对象核对；进程会话号在生命周期内不可变，因此这里
            // 只作为纵深校验，不会把终止目标切换到另一个 PID。
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

    /// <summary>
    /// 只读捕获进程身份快照：PID、会话号、创建时间和最终镜像路径。
    /// 不能只凭 PID 认定桌面外壳仍在运行，PID 可能在两次轮询之间被复用。
    /// </summary>
    internal static bool TryGetProcessIdentity(
        int processId,
        out ProcessIdentitySnapshot snapshot,
        out int errorCode)
    {
        snapshot = default;
        errorCode = 0;
        if (processId <= 0)
        {
            errorCode = ErrorInvalidData;
            return false;
        }

        var handle = OpenProcess(
            ProcessQueryInformation,
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

            var imagePath = QueryImagePath(handle);
            if (imagePath is null
                || !TryNormalizeImagePath(imagePath, out var normalizedImagePath))
            {
                errorCode = imagePath is null
                    ? Marshal.GetLastPInvokeError()
                    : ErrorInvalidData;
                return false;
            }

            // ProcessIdToSessionId 只能按 PID 查询；若进程恰好退出并复用 PID，
            // 查询结果可能属于另一个对象。紧跟查询前后都要求固定句柄仍处于活动
            // 状态，一旦原进程已退出就失败关闭，不能把会话号拼到旧句柄身份上。
            if (!GetExitCodeProcess(handle, out var beforeSessionExitCode)
                || beforeSessionExitCode != StillActive)
            {
                errorCode = beforeSessionExitCode != StillActive
                    ? ErrorInvalidData
                    : Marshal.GetLastPInvokeError();
                return false;
            }

            if (!ProcessIdToSessionId((uint)processId, out var sessionId)
                || sessionId == 0)
            {
                errorCode = sessionId == 0
                    ? ErrorInvalidData
                    : Marshal.GetLastPInvokeError();
                return false;
            }

            if (!GetExitCodeProcess(handle, out var afterSessionExitCode)
                || afterSessionExitCode != StillActive)
            {
                errorCode = afterSessionExitCode != StillActive
                    ? ErrorInvalidData
                    : Marshal.GetLastPInvokeError();
                return false;
            }

            snapshot = new ProcessIdentitySnapshot(
                processId,
                sessionId,
                ToFileTimeTicks(creation),
                normalizedImagePath);
            return true;
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
    /// 把进程镜像路径归一为完整路径。文件可读取时通过文件句柄解析到最终
    /// 路径，使 8.3 短名、卷 GUID、目录联接和符号链接别名得到同一结果；
    /// 文件尚不存在或无权打开时退回确定性的词法完整路径。SessionAgentService
    /// 的枚举、替换和启动项守卫必须共用这一边界。
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
            if (!Path.IsPathFullyQualified(normalizedPath))
            {
                return false;
            }

            if (TryGetFinalImagePath(normalizedPath, out var finalPath))
            {
                normalizedPath = finalPath;
            }

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

    private static bool TryGetFinalImagePath(string candidate, out string finalPath)
    {
        finalPath = string.Empty;
        try
        {
            using var handle = File.OpenHandle(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var capacity = 512;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    fileNameNormalized: 0);
                if (length == 0)
                {
                    return false;
                }

                if (length < (uint)buffer.Capacity)
                {
                    var resolved = buffer.ToString(0, (int)length);
                    finalPath = RemoveExtendedPathPrefix(resolved);
                    return Path.IsPathFullyQualified(finalPath);
                }

                capacity = checked((int)length + 1);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            // 文件不存在或当前令牌无权读取内容时仍可比较词法路径；
            // 调用方在无法证明路径相等时会失败关闭，绝不会放宽终止条件。
        }

        return false;
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(uncPrefix.Length);
        }

        // 只去掉本地盘符路径的 \\?\ 前缀；卷 GUID 和设备路径必须保留，
        // 否则会变成相对路径而丢失可比较性。
        return path.Length >= 7
               && path.StartsWith(@"\\?\", StringComparison.Ordinal)
               && char.IsAsciiLetter(path[4])
               && path[5] == ':'
               && path[6] == '\\'
            ? path.Substring(4)
            : path;
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
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint fileNameNormalized);

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
