using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

internal enum ObservedSessionDecision
{
    None,
    Reset,
    FailClosed
}

internal readonly record struct LoginCompletionState(uint SessionId, long CompletedAtTimestamp);

internal readonly record struct LoginCompletionUpdate(
    LoginCompletionState Completion,
    bool InvalidateObservedSession);

/// <summary>
/// 子会话启动程序守卫。
///
/// 第一道防线仍是把注册表 / 计划任务里的启动源临时关掉。杀毒软件、单实例程序
/// 或厂商服务可能在关停后仍把程序拉起来；这里只为**子会话**工作：
/// 登录阶段对未确认关闭的项按精确路径兜底，之后常驻拦截由会话 0 服务拉起的
/// 已登记启动程序副本，绝不枚举或终止主会话里的进程。用户登录后自己从
/// explorer.exe 启动同名程序不会被常驻阶段误杀。
/// </summary>
internal sealed class StartupGuardService : IDisposable
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const int MissingSessionScansBeforeReset = 4;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    // 登录通知到达后再保留两分钟的无条件兜底；如果收不到通知，从子会话
    // 第一次可见起保留五分钟。常驻阶段只认会话 0 服务父进程。
    private static readonly TimeSpan LoginSettledDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoginFallbackDuration = TimeSpan.FromMinutes(5);
    private static readonly string[] BlockedFileNames =
    [
        "explorer.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "regsvr32.exe",
        "taskhostw.exe",
        "dwm.exe",
        "winlogon.exe",
        "csrss.exe",
        "services.exe",
        "lsass.exe",
        "smss.exe",
        "rdpclip.exe"
    ];

    private readonly HashSet<string> _fallbackPaths;
    private readonly HashSet<string> _managedPaths;
    private readonly uint _mainSessionId;
    private readonly Func<uint?> _childSessionIdProvider;
    private readonly Func<uint, DateTime?> _childSessionLogonTimeProvider;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _monitorTask;
    private readonly Dictionary<int, DateTime> _failedProcesses = new();
    private readonly HashSet<string> _reportedFailurePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTime> _baselineProcesses = new();
    private readonly object _loginStateLock = new();
    private readonly DateTime _guardStartedUtc;
    private long _loginCompletedTimestamp;
    private uint _loginCompletedSessionId;
    private long _sessionFirstSeenTimestamp;
    private long _logonIdentityUnknownSinceTimestamp;
    private uint _observedSessionId;
    private DateTime? _observedSessionLogonTime;
    private int _missingSessionScans;
    private bool _loggedResidentPhase;
    private int _loginNotificationRefreshRequested;
    private bool _loggedUnreadablePath;
    private bool _loggedParentSnapshotFailure;
    private int _disposed;
    private int _intercepted;

    public StartupGuardService(
        IEnumerable<string> startupExecutables,
        IEnumerable<string> managedStartupExecutables,
        Func<uint?> childSessionIdProvider,
        Func<uint, DateTime?>? childSessionLogonTimeProvider = null,
        CancellationToken applicationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startupExecutables);
        ArgumentNullException.ThrowIfNull(managedStartupExecutables);
        ArgumentNullException.ThrowIfNull(childSessionIdProvider);

        _fallbackPaths = NormalizeTargets(startupExecutables);
        _managedPaths = NormalizeTargets(managedStartupExecutables);
        // 未确认项必须包含在常驻目标里；否则计划的登录窗口一过就会漏掉
        // 由服务持续重启的启动程序。
        _managedPaths.UnionWith(_fallbackPaths);
        _mainSessionId = (uint)Process.GetCurrentProcess().SessionId;
        _childSessionIdProvider = childSessionIdProvider;
        _childSessionLogonTimeProvider = childSessionLogonTimeProvider ?? (_ => null);
        _guardStartedUtc = DateTime.UtcNow;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);

        if (_managedPaths.Count == 0)
        {
            _monitorTask = Task.CompletedTask;
            return;
        }

        // 只在构造时记录「已经存在的子会话进程」。如果等到第一次扫描才记录，
        // 新会话里刚登录就启动的程序会被误当成基线而永远不被拦截。
        CaptureExistingChildProcesses();
        _monitorTask = Task.Run(Monitor);
    }

    /// <summary>本次已确认终止的子会话进程数。</summary>
    public int InterceptedCount => Volatile.Read(ref _intercepted);

    /// <summary>
    /// 已确认命中受管启动程序，但无法终止它。订阅方必须按失败关闭处理：
    /// 断开并注销子会话，恢复临时停用的启动项，而不是继续运行。
    /// </summary>
    public event Action<uint, string>? IsolationFailed;

    /// <summary>
    /// RDP 登录完成通知。收到通知后，未确认关停项再多做两分钟无条件兜底；
    /// 之后进入常驻阶段，只拦截会话 0 服务拉起的已登记启动程序。
    /// </summary>
    public void NotifyLoginCompleted()
    {
        if (TryGetChildSessionId() is not { } sessionId || sessionId == 0)
        {
            return;
        }

        var completedAtTimestamp = Stopwatch.GetTimestamp();
        lock (_loginStateLock)
        {
            var update = ApplyLoginCompletedNotification(
                new LoginCompletionState(_loginCompletedSessionId, _loginCompletedTimestamp),
                sessionId,
                completedAtTimestamp);
            _loginCompletedSessionId = update.Completion.SessionId;
            _loginCompletedTimestamp = update.Completion.CompletedAtTimestamp;
            if (update.InvalidateObservedSession)
            {
                // Monitor 是唯一会改基线和当前观察状态的一方；用请求位把
                // “下一轮重新捕获基线”串行化，避免通知线程和扫描线程同时清字典。
                Interlocked.Exchange(ref _loginNotificationRefreshRequested, 1);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            // 只有监控任务确实退出后才能释放 CTS。若它还在枚举进程，提前释放会让
            // WaitForNextScan 访问已释放的 WaitHandle，反而制造退出期异常。
            if (_monitorTask.Wait(TimeSpan.FromSeconds(2)))
            {
                _cancellation.Dispose();
            }
            else
            {
                AppLog.Warn("启动项守卫未在退出窗口内停止；等待监控结束后释放取消源。");
                _ = _monitorTask.ContinueWith(
                    _ => _cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (AggregateException)
        {
            // 监控线程的异常已经在 Monitor 里记录；退出不能被它拖住。
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// 归一化启动程序路径。接受当前还不存在、但启动项指向的 .exe，
    /// 以便拦截登录后才被下载或解压出来的延迟程序；跳过 Windows 系统目录和
    /// 通用启动器，避免把子会话的桌面外壳或系统服务误当成用户启动项。
    /// </summary>
    internal static HashSet<string> NormalizeTargets(IEnumerable<string> startupExecutables)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windowsDirectory = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var currentExecutable = Environment.ProcessPath;

        foreach (var candidate in startupExecutables)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException or SecurityException)
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase)
                || BlockedFileNames.Contains(Path.GetFileName(fullPath), StringComparer.OrdinalIgnoreCase)
                || (currentExecutable is not null
                    && string.Equals(fullPath, currentExecutable, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            targets.Add(fullPath);
        }

        return targets;
    }

    /// <summary>
    /// 判定一个进程是否必须被拦截。会话号检查放在路径匹配之前：
    /// 即使主会话里有同名同路径的程序，也绝不能碰。
    /// </summary>
    internal static bool ShouldTerminate(
        uint processSessionId,
        uint targetSessionId,
        uint mainSessionId,
        string? executablePath,
        IReadOnlySet<string> exactPaths)
    {
        if (targetSessionId == mainSessionId
            || processSessionId != targetSessionId
            || processSessionId == mainSessionId)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(executablePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException or SecurityException)
            {
                return false;
            }

            // 路径能读出来时只认精确路径，防止把同名但不同目录的程序误杀。
            return exactPaths.Contains(normalized);
        }

        // 读不到 MainModule 时无法证明完整路径。即使进程名相同也不能
        // 终止，否则可能误杀用户在同一子会话里手动打开的程序。调用方会
        // 记录告警，宁可漏拦，也不能按名称猜测。
        return false;
    }

    /// <summary>
    /// 判定当前观察到的会话是否仍是同一登录身份。登录时间任一方向未知时
    /// 宁可失败关闭，也不把旧基线沿用到可能复用的会话号上。两边都为 null
    /// 只允许在明确的登录兜底上限内等待；超过上限必须交给调用方失败关闭。
    /// </summary>
    internal static ObservedSessionDecision DecideObservedSession(
        uint observedSessionId,
        uint? childSessionId,
        int consecutiveMissingScans,
        DateTime? observedLogonTime,
        DateTime? childLogonTime,
        TimeSpan unknownLogonDuration,
        int requiredMissingScans = MissingSessionScansBeforeReset,
        TimeSpan? maxUnknownLogonDuration = null)
    {
        if (observedSessionId == 0)
        {
            return ObservedSessionDecision.None;
        }

        if (childSessionId is not { } currentSessionId || currentSessionId == 0)
        {
            return consecutiveMissingScans >= requiredMissingScans
                ? ObservedSessionDecision.Reset
                : ObservedSessionDecision.None;
        }

        if (currentSessionId != observedSessionId)
        {
            return ObservedSessionDecision.Reset;
        }

        if (observedLogonTime is { } oldLogon)
        {
            if (childLogonTime is not { } newLogon)
            {
                // 已经确认过登录身份，随后 WTS 又读不到时间：这可能是会话
                // 正在被替换。无法证明身份时立即失败关闭，不能继续扫描。
                return ObservedSessionDecision.FailClosed;
            }

            return oldLogon == newLogon
                ? ObservedSessionDecision.None
                : ObservedSessionDecision.Reset;
        }

        if (childLogonTime is not null)
        {
            // 旧会话身份未确认，现在读到了登录时间；按新观察重新捕获基线。
            return ObservedSessionDecision.Reset;
        }

        var limit = maxUnknownLogonDuration ?? LoginFallbackDuration;
        if (unknownLogonDuration < TimeSpan.Zero)
        {
            unknownLogonDuration = TimeSpan.Zero;
        }

        return unknownLogonDuration >= limit
            ? ObservedSessionDecision.FailClosed
            : ObservedSessionDecision.None;
    }

    /// <summary>
    /// 应用登录完成通知。时间戳必须无条件刷新，即使同一个会话号已经收到过
    /// 通知；刷新请求由监控线程在扫描循环内串行消费，不能从通知线程直接清
    /// 基线字典。首次观察之前没有旧状态可清，但仍要保留这次合法通知。
    /// </summary>
    internal static LoginCompletionUpdate ApplyLoginCompletedNotification(
        LoginCompletionState current,
        uint notifiedSessionId,
        long completedAtTimestamp)
    {
        if (notifiedSessionId == 0)
        {
            return new LoginCompletionUpdate(current, InvalidateObservedSession: false);
        }

        return new LoginCompletionUpdate(
            new LoginCompletionState(notifiedSessionId, completedAtTimestamp),
            InvalidateObservedSession: true);
    }

    /// <summary>
    /// 兼容旧的“仅按会话号/缺失次数判断”测试和调用方。登录身份判定必须使用
    /// <see cref="DecideObservedSession"/>；这个入口刻意保留旧的宽松 null 语义，
    /// 监控线程不再使用它。
    /// </summary>
    internal static bool ShouldResetObservedSession(
        uint observedSessionId,
        uint? childSessionId,
        int consecutiveMissingScans,
        int requiredMissingScans = MissingSessionScansBeforeReset,
        DateTime? observedLogonTime = null,
        DateTime? childLogonTime = null)
    {
        if (observedSessionId == 0)
        {
            return false;
        }

        if (childSessionId is not { } currentSessionId || currentSessionId == 0)
        {
            return consecutiveMissingScans >= requiredMissingScans;
        }

        // 会话号可能在旧会话退出后立刻被新会话复用。只有两边都读到了登录
        // 时间时才据此判断，单次查询失败不能清空守卫状态。
        return currentSessionId != observedSessionId
               || (observedLogonTime is { } oldLogon
                   && childLogonTime is { } newLogon
                   && oldLogon != newLogon);
    }

    /// <summary>
    /// 常驻阶段只拦截「可读精确路径」且父进程在会话 0 的副本。厂商服务
    /// 常驻在会话 0，却把用户界面进程创建到子会话；这正是禁用启动项后
    /// 仍出现重复程序的原因。用户自己从子会话启动的同路径程序父进程在
    /// 子会话，不会命中。
    /// </summary>
    internal static bool ShouldTerminateServiceSpawn(
        uint processSessionId,
        uint targetSessionId,
        uint mainSessionId,
        string? executablePath,
        uint? parentSessionId,
        DateTime? parentStartedAt,
        DateTime processStartedAt,
        IReadOnlySet<string> exactPaths)
    {
        if (targetSessionId == mainSessionId
            || processSessionId != targetSessionId
            || processSessionId == mainSessionId
            || parentSessionId != 0
            || parentStartedAt is null
            // 父 PID 可能被回收。服务父进程必须早于子进程创建，否则不能
            // 把孤儿子进程的旧 ParentProcessId 当成服务拉起证据。
            || parentStartedAt > processStartedAt
            || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            return exactPaths.Contains(Path.GetFullPath(executablePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or SecurityException)
        {
            return false;
        }
    }

    private void Monitor()
    {
        var loggedMainSession = uint.MaxValue;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var childSessionId = TryGetChildSessionId();
                if (childSessionId is not { } targetSessionId || targetSessionId == 0)
                {
                    _missingSessionScans++;
                    if (DecideObservedSession(
                            _observedSessionId,
                            childSessionId,
                            _missingSessionScans,
                            _observedSessionLogonTime,
                            childLogonTime: null,
                            unknownLogonDuration: TimeSpan.Zero) == ObservedSessionDecision.Reset)
                    {
                        AppLog.Info("子会话已持续离开，启动项守卫已重置会话状态。");
                        ResetObservedSession();
                    }

                    WaitForNextScan();
                    continue;
                }

                _missingSessionScans = 0;

                if (targetSessionId == _mainSessionId)
                {
                    if (loggedMainSession != targetSessionId)
                    {
                        AppLog.Warn("启动项守卫拒绝在启动程序所在的会话里运行。");
                        loggedMainSession = targetSessionId;
                    }

                    WaitForNextScan();
                    continue;
                }

                if (ConsumeLoginNotificationRefreshRequested() && _observedSessionId != 0)
                {
                    // 同一个会话号也可能对应一次新的登录。通知线程只写时间戳和
                    // 请求位，真正清基线在这里做，保证字典只由监控线程修改。
                    ResetObservedSession(preserveLoginCompletion: true);
                    AppLog.Info("收到新的登录完成通知，启动项守卫已重置旧会话状态。");
                }

                var childLogonTime = TryGetChildSessionLogonTime(targetSessionId);
                if (_observedSessionId != 0
                    && _observedSessionLogonTime is null
                    && _logonIdentityUnknownSinceTimestamp == 0)
                {
                    _logonIdentityUnknownSinceTimestamp = Stopwatch.GetTimestamp();
                }

                if (_observedSessionLogonTime is not null)
                {
                    _logonIdentityUnknownSinceTimestamp = 0;
                }

                var unknownLogonDuration = _logonIdentityUnknownSinceTimestamp == 0
                    ? TimeSpan.Zero
                    : Stopwatch.GetElapsedTime(_logonIdentityUnknownSinceTimestamp);
                var sessionDecision = DecideObservedSession(
                    _observedSessionId,
                    targetSessionId,
                    _missingSessionScans,
                    _observedSessionLogonTime,
                    childLogonTime,
                    unknownLogonDuration);

                if (sessionDecision == ObservedSessionDecision.FailClosed)
                {
                    const string identityFailureMessage =
                        "启动项守卫无法确认子会话的登录身份（登录时间未知或已发生不一致变化），" +
                        "已停止扫描并按隔离失败关闭。";
                    AppLog.Warn($"子会话 {targetSessionId} 登录身份无法确认，启动项守卫失败关闭。");
                    RaiseIsolationFailure(targetSessionId, identityFailureMessage);
                    return;
                }

                if (sessionDecision == ObservedSessionDecision.Reset)
                {
                    // 会话号可能被 Windows 复用。登录时间发生变化或从未知变为
                    // 已知时必须丢弃旧基线，否则新会话会被误判成已经常驻。
                    AppLog.Info("子会话登录身份已变化，启动项守卫已重置会话状态。");
                    ResetObservedSession();
                }

                if (_observedSessionId == 0)
                {
                    var firstSeenTimestamp = Stopwatch.GetTimestamp();
                    _observedSessionId = targetSessionId;
                    _observedSessionLogonTime = childLogonTime;
                    _missingSessionScans = 0;
                    _logonIdentityUnknownSinceTimestamp = childLogonTime is null
                        ? firstSeenTimestamp
                        : 0;
                    Interlocked.Exchange(ref _sessionFirstSeenTimestamp, firstSeenTimestamp);
                    // 登录通知可能属于上一个子会话，不能拿来缩短新会话的兜底窗口。
                    // 第一次观察到会话时若通知已经先到，则保留它。
                    ClearLoginCompletionIfSessionMismatch(targetSessionId);

                    _loggedResidentPhase = false;
                    CaptureExistingChildProcesses();
                    AppLog.Info(
                        $"启动项守卫已启动：仅监控子会话 {targetSessionId} 的 "
                        + $"{_managedPaths.Count} 个受管启动程序（其中 "
                        + $"{_fallbackPaths.Count} 个未确认项在登录阶段无条件兜底）。"
                        + "登录阶段结束后只拦截会话 0 服务拉起的副本。");
                }

                var allowDirectFallback = IsLoginFallbackPhase(targetSessionId);
                if (!allowDirectFallback && !_loggedResidentPhase)
                {
                    _loggedResidentPhase = true;
                    AppLog.Info("启动项守卫进入常驻阶段：只拦截会话 0 服务重新拉起的启动程序。");
                }

                Scan(targetSessionId, allowDirectFallback);
                WaitForNextScan();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (Exception exception)
        {
            AppLog.Warn("启动项守卫异常退出。", exception);
            var sessionId = _observedSessionId;
            if (sessionId == 0)
            {
                try
                {
                    sessionId = _childSessionIdProvider() ?? 0;
                }
                catch (Exception providerException) when (providerException is Win32Exception
                                                              or InvalidOperationException
                                                              or NotSupportedException)
                {
                    sessionId = 0;
                }
            }

            if (sessionId != 0)
            {
                RaiseIsolationFailure(
                    sessionId,
                    "启动项守卫异常退出，无法继续保证启动项隔离："
                    + exception.GetBaseException().Message);
            }
        }
        finally
        {
            if (_observedSessionId != 0)
            {
                AppLog.Info($"启动项守卫已结束：拦截 {InterceptedCount} 个进程。");
            }
        }
    }

    internal void ResetObservedSession()
    {
        ResetObservedSession(preserveLoginCompletion: false);
    }

    private void ResetObservedSession(bool preserveLoginCompletion)
    {
        _observedSessionId = 0;
        _observedSessionLogonTime = null;
        _missingSessionScans = 0;
        _logonIdentityUnknownSinceTimestamp = 0;
        _baselineProcesses.Clear();
        _failedProcesses.Clear();
        _reportedFailurePaths.Clear();
        _loggedResidentPhase = false;
        Interlocked.Exchange(ref _sessionFirstSeenTimestamp, 0);

        if (preserveLoginCompletion)
        {
            return;
        }

        lock (_loginStateLock)
        {
            // 如果通知刚好在重置期间到达，保留它；下一轮会消费请求位，
            // 由监控线程完成旧会话状态清理。普通重置仍会清空登录窗口。
            if (Volatile.Read(ref _loginNotificationRefreshRequested) == 0)
            {
                _loginCompletedSessionId = 0;
                _loginCompletedTimestamp = 0;
            }
        }
    }

    private bool IsLoginFallbackPhase(uint targetSessionId)
    {
        // 登录时间未知时绝不允许进入常驻阶段，即使登录完成通知已经到达。
        // 未确认窗口最多持续 LoginFallbackDuration，超限由 DecideObservedSession
        // 返回 FailClosed。
        if (_observedSessionLogonTime is null)
        {
            var unknownSince = _logonIdentityUnknownSinceTimestamp;
            return unknownSince == 0
                || Stopwatch.GetElapsedTime(unknownSince) < LoginFallbackDuration;
        }

        var loginCompletion = GetLoginCompletion();
        if (loginCompletion.CompletedAtTimestamp != 0
            && loginCompletion.SessionId == targetSessionId)
        {
            return Stopwatch.GetElapsedTime(loginCompletion.CompletedAtTimestamp) < LoginSettledDuration;
        }

        var sessionFirstSeen = Interlocked.Read(ref _sessionFirstSeenTimestamp);
        return sessionFirstSeen == 0
            || Stopwatch.GetElapsedTime(sessionFirstSeen) < LoginFallbackDuration;
    }

    private LoginCompletionState GetLoginCompletion()
    {
        lock (_loginStateLock)
        {
            return new LoginCompletionState(_loginCompletedSessionId, _loginCompletedTimestamp);
        }
    }

    private void ClearLoginCompletionIfSessionMismatch(uint targetSessionId)
    {
        lock (_loginStateLock)
        {
            if (_loginCompletedSessionId != targetSessionId)
            {
                _loginCompletedSessionId = 0;
                _loginCompletedTimestamp = 0;
            }
        }
    }

    private bool ConsumeLoginNotificationRefreshRequested()
    {
        return Interlocked.Exchange(ref _loginNotificationRefreshRequested, 0) != 0;
    }

    private void RaiseIsolationFailure(uint sessionId, string message)
    {
        if (sessionId == 0
            || Volatile.Read(ref _disposed) != 0
            || _cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            IsolationFailed?.Invoke(sessionId, message);
        }
        catch (Exception exception)
        {
            // 订阅方自己出错不能拖垮进程扫描；没有订阅方时由日志保留证据。
            AppLog.Error("启动项隔离失败事件的订阅方抛出异常。", exception);
        }
    }

    private void Scan(uint targetSessionId, bool allowDirectFallback)
    {
        var parentProcessIds = CaptureParentProcessIds();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            // 枚举失败可能是瞬时资源压力；跳过本轮，下一轮继续，不能让守卫永久退出。
            AppLog.Warn("启动项守卫枚举进程失败，本轮已跳过。", exception);
            return;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var sessionId = (uint)process.SessionId;
                    if (sessionId != targetSessionId || sessionId == _mainSessionId)
                    {
                        continue;
                    }

                    // 重连到已存在的子会话时，用户可能已经打开了目标程序。只处理
                    // 守卫启动之后才出现的进程，绝不把登录前就在运行的工作窗口杀掉。
                    DateTime startedAt;
                    try
                    {
                        startedAt = process.StartTime.ToUniversalTime();
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        // 读不到启动时间就无法证明它是新进程；宁可漏拦，不可误杀。
                        continue;
                    }

                    if (startedAt < _guardStartedUtc
                        || (_baselineProcesses.TryGetValue(process.Id, out var baselineStartedAt)
                            && startedAt == baselineStartedAt))
                    {
                        continue;
                    }

                    // 先拿到并缓存内核进程句柄。PID 可能在枚举和 Kill 之间被复用；
                    // 缓存句柄后，后续读取和终止都只针对这一轮看到的原进程。
                    try
                    {
                        _ = process.Handle;
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        // 拿不到稳定句柄就无法证明不会误杀，宁可漏拦。
                        continue;
                    }

                    // 句柄固定后再核对一次身份，防止枚举后 PID 已被回收。
                    try
                    {
                        if ((uint)process.SessionId != sessionId
                            || process.StartTime.ToUniversalTime() != startedAt)
                        {
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        continue;
                    }

                    string? executablePath = null;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        // 受保护进程可能拒绝读取模块信息，下面统一告警。
                    }

                    if (string.IsNullOrWhiteSpace(executablePath) && !_loggedUnreadablePath)
                    {
                        _loggedUnreadablePath = true;
                        AppLog.Warn(
                            "启动项守卫无法读取子会话进程的完整路径，已拒绝按进程名终止；"
                            + "该进程可能仍需人工核查：" + process.ProcessName);
                    }

                    var directFallbackMatch = allowDirectFallback
                        && ShouldTerminate(
                            sessionId,
                            targetSessionId,
                            _mainSessionId,
                            executablePath,
                            _fallbackPaths);
                    if (!directFallbackMatch)
                    {
                        var parentIdentity = TryGetParentProcessIdentity(
                            process.Id,
                            parentProcessIds);
                        if (!ShouldTerminateServiceSpawn(
                                sessionId,
                                targetSessionId,
                                _mainSessionId,
                                executablePath,
                                parentIdentity?.SessionId,
                                parentIdentity?.StartedAt,
                                startedAt,
                                _managedPaths))
                        {
                            continue;
                        }
                    }

                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    // 同一个受保护进程可能每 500 毫秒被重新枚举；只在启动时间
                    // 也一致时跳过重试。PID 被回收后时间会变化，不能永久拉黑。
                    if (_failedProcesses.TryGetValue(process.Id, out var failedStartedAt))
                    {
                        if (failedStartedAt == startedAt)
                        {
                            continue;
                        }

                        _failedProcesses.Remove(process.Id);
                    }

                    var displayPath = executablePath ?? process.ProcessName;
                    try
                    {
                        process.Kill();
                        if (!process.WaitForExit(1000))
                        {
                            throw new InvalidOperationException("进程未在 1 秒内退出。");
                        }

                        Interlocked.Increment(ref _intercepted);
                        AppLog.Info($"已拦截分身自启动：{displayPath} (PID {process.Id})");
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        _failedProcesses[process.Id] = startedAt;
                        var accessDenied = exception is Win32Exception { NativeErrorCode: 5 };
                        var hint = accessDenied
                            ? "；这通常是安全软件自保护拦截。请只在安全软件中放行 DesktopTwin 的精确程序 "
                              + $"{Environment.ProcessPath ?? "DesktopTwin.exe"}，"
                              + "不要放行被拦截的启动程序，也不要关闭自保护。"
                            : string.Empty;
                        var failureMessage =
                            $"拦截分身自启动失败：{displayPath} (PID {process.Id})："
                            + $"{exception.GetBaseException().Message}{hint}";
                        if (_reportedFailurePaths.Add(displayPath))
                        {
                            AppLog.Warn(failureMessage, exception);
                        }

                        // 受保护进程无法终止意味着子会话已经不符合隔离保证。
                        // 不能只缓存失败后继续扫描，否则 MCP 会继续控制一个
                        // 已经混入本机启动程序的桌面。
                        RaiseIsolationFailure(targetSessionId, failureMessage);
                    }
                }
                catch (Exception exception) when (exception is Win32Exception
                                                      or InvalidOperationException
                                                      or NotSupportedException)
                {
                    // 进程在枚举和读取之间退出是常态。
                }
            }
        }
    }

    private Dictionary<int, int> CaptureParentProcessIds()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            if (!_loggedParentSnapshotFailure)
            {
                _loggedParentSnapshotFailure = true;
                AppLog.Warn(
                    "启动项守卫无法创建进程快照，常驻阶段将只保留登录阶段兜底。",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return parents;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                if (!_loggedParentSnapshotFailure)
                {
                    _loggedParentSnapshotFailure = true;
                    AppLog.Warn(
                        "启动项守卫无法读取进程快照，常驻阶段将只保留登录阶段兜底。",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                return parents;
            }

            do
            {
                if (entry.ProcessId != 0)
                {
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }

                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));

            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static (uint SessionId, DateTime StartedAt)? TryGetParentProcessIdentity(
        int processId,
        IReadOnlyDictionary<int, int> parentProcessIds)
    {
        if (!parentProcessIds.TryGetValue(processId, out var parentProcessId)
            || parentProcessId <= 0
            || parentProcessId == processId)
        {
            return null;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            return ((uint)parent.SessionId, parent.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or Win32Exception)
        {
            // 父进程可能在枚举与查询之间退出；无法证明是服务拉起时不终止。
            return null;
        }
    }

    private void CaptureExistingChildProcesses()
    {
        try
        {
            if (TryGetChildSessionId() is not { } targetSessionId
                || targetSessionId == 0
                || targetSessionId == _mainSessionId)
            {
                return;
            }

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if ((uint)process.SessionId != targetSessionId)
                        {
                            continue;
                        }

                        // 构造函数记录守卫起点后，基线捕获仍要花一点时间。若一个
                        // 启动程序恰好在这个窗口里创建，不能因为枚举到了它就把
                        // 它误当作用户原有进程。只有明确早于守卫起点的进程才进入
                        // 基线；读不到启动时间的进程留给扫描阶段的时间过滤兜底。
                        var startedAt = process.StartTime.ToUniversalTime();
                        if (startedAt >= _guardStartedUtc)
                        {
                            continue;
                        }

                        _baselineProcesses[process.Id] = startedAt;
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        // 进程在枚举期间退出，或启动时间不可读。
                    }
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            // 读不到现有进程时仍有启动时间过滤兜底；宁可漏拦，不可误杀。
            AppLog.Warn("启动项守卫读取现有子会话进程失败。", exception);
        }
    }

    private uint? TryGetChildSessionId()
    {
        try
        {
            return _childSessionIdProvider();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception)
        {
            var message = "启动项守卫读取子会话失败，无法继续证明启动项隔离："
                          + exception.Message;
            AppLog.Warn(message, exception);

            // 查询失败不等于会话消失。只要这个守卫已经观察过子会话，就必须
            // 立即按隔离失败处理，绝不能重置值守状态后继续放行启动项。
            var observedSessionId = _observedSessionId;
            if (observedSessionId != 0)
            {
                RaiseIsolationFailure(observedSessionId, message);
            }

            return null;
        }
    }

    private DateTime? TryGetChildSessionLogonTime(uint sessionId)
    {
        try
        {
            return _childSessionLogonTimeProvider(sessionId);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or ArgumentException)
        {
            // 单次读取失败不代表会话消失，不能因此重置守卫。
            AppLog.Warn("启动项守卫读取子会话登录时间失败。", exception);
            return null;
        }
    }

    private void WaitForNextScan()
    {
        if (_cancellation.Token.WaitHandle.WaitOne(ScanInterval))
        {
            throw new OperationCanceledException(_cancellation.Token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
