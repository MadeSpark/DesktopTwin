using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 子会话登录阶段的启动程序兜底守卫。
///
/// 第一道防线仍是把注册表 / 计划任务里的启动源临时关掉。杀毒软件、单实例程序
/// 或厂商服务可能在关停后仍把程序拉起来；这里只在**刚登录的子会话**里、按启动项
/// 的精确路径拦截一小段时间，绝不枚举或终止主会话里的进程。
/// </summary>
internal sealed class StartupGuardService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GuardDuration = TimeSpan.FromMinutes(5);
    // 登录后两分钟仍可能有延后启动的软件；绝对上限仍是五分钟。只对确实
    // 未能关停的项启用守卫，因此延长窗口不会影响正常被关掉的启动项。
    private static readonly TimeSpan LoginSettledDuration = TimeSpan.FromMinutes(2);
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

    private readonly HashSet<string> _targetPaths;
    private readonly HashSet<string> _targetNames;
    private readonly uint _mainSessionId;
    private readonly Func<uint?> _childSessionIdProvider;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _monitorTask;
    private readonly HashSet<int> _failedProcessIds = new();
    private readonly Dictionary<int, DateTime> _baselineProcesses = new();
    private readonly DateTime _guardStartedUtc;
    private long _loginCompletedTimestamp;
    private int _disposed;
    private int _intercepted;

    public StartupGuardService(
        IEnumerable<string> startupExecutables,
        Func<uint?> childSessionIdProvider,
        CancellationToken applicationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startupExecutables);
        ArgumentNullException.ThrowIfNull(childSessionIdProvider);

        _targetPaths = NormalizeTargets(startupExecutables);
        _targetNames = _targetPaths
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _mainSessionId = (uint)Process.GetCurrentProcess().SessionId;
        _childSessionIdProvider = childSessionIdProvider;
        _guardStartedUtc = DateTime.UtcNow;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);

        if (_targetPaths.Count == 0)
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
    /// RDP 登录完成通知。登录后启动项通常在一分钟内趋于稳定；收到通知后
    /// 再守 60 秒即可，不必让兜底守卫一直跑到绝对上限。
    /// </summary>
    public void NotifyLoginCompleted()
    {
        Interlocked.CompareExchange(ref _loginCompletedTimestamp, Stopwatch.GetTimestamp(), 0);
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
    /// 归一化启动程序路径。只接受确实存在的 .exe；跳过 Windows 系统目录和
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
                || !File.Exists(fullPath)
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
        string processName,
        IReadOnlySet<string> exactPaths,
        IReadOnlySet<string> targetNames)
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

        // 受保护进程有时拒绝读取 MainModule。此时才退回进程名，
        // 并仍然要求它属于刚登录的子会话。
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(processName);
        return !string.IsNullOrWhiteSpace(nameWithoutExtension)
            && targetNames.Contains(nameWithoutExtension);
    }

    private void Monitor()
    {
        var loggedSession = uint.MaxValue;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var childSessionId = TryGetChildSessionId();
                if (childSessionId is not { } targetSessionId || targetSessionId == 0)
                {
                    WaitForNextScan();
                    continue;
                }

                if (targetSessionId == _mainSessionId)
                {
                    if (loggedSession != targetSessionId)
                    {
                        AppLog.Warn("启动项守卫拒绝在启动程序所在的会话里运行。");
                        loggedSession = targetSessionId;
                    }

                    WaitForNextScan();
                    continue;
                }

                // 持续时间从子会话第一次可见时开始；进程启动时间仍以构造函数
                // 记录的时刻为界，确保登录前已存在的程序不会被误杀。
                var guardStartedAt = Stopwatch.GetTimestamp();
                AppLog.Info(
                    $"启动项守卫已启动：仅监控子会话 {targetSessionId} 的 "
                    + $"{_targetPaths.Count} 个启动程序，登录完成后再守 "
                    + $"{LoginSettledDuration.TotalSeconds:0} 秒（绝对上限 "
                    + $"{GuardDuration.TotalSeconds:0} 秒）。");

                while (!_cancellation.IsCancellationRequested)
                {
                    if (TryGetChildSessionId() is { } currentSessionId
                        && currentSessionId != 0
                        && currentSessionId != _mainSessionId)
                    {
                        Scan(currentSessionId);
                    }

                    if (Stopwatch.GetElapsedTime(guardStartedAt) >= GuardDuration)
                    {
                        break;
                    }

                    var loginCompletedAt = Interlocked.Read(ref _loginCompletedTimestamp);
                    if (loginCompletedAt != 0
                        && Stopwatch.GetElapsedTime(loginCompletedAt) >= LoginSettledDuration)
                    {
                        break;
                    }

                    WaitForNextScan();
                }

                AppLog.Info(
                    $"启动项守卫已结束：拦截 {InterceptedCount} 个进程。");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (Exception exception)
        {
            AppLog.Warn("启动项守卫异常退出。", exception);
        }
    }

    private void Scan(uint targetSessionId)
    {
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
                        // 受保护进程可能拒绝读取模块信息，后面会按进程名兜底。
                    }

                    if (!ShouldTerminate(
                            sessionId,
                            targetSessionId,
                            _mainSessionId,
                            executablePath,
                            process.ProcessName,
                            _targetPaths,
                            _targetNames))
                    {
                        continue;
                    }

                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    var displayPath = executablePath ?? process.ProcessName;
                    AppLog.Info(
                        $"已拦截分身自启动：{displayPath} (PID {process.Id})");

                    try
                    {
                        process.Kill();
                        Interlocked.Increment(ref _intercepted);
                    }
                    catch (Exception exception) when (exception is Win32Exception
                                                          or InvalidOperationException
                                                          or NotSupportedException)
                    {
                        if (_failedProcessIds.Add(process.Id))
                        {
                            AppLog.Warn(
                                $"拦截分身自启动失败：{displayPath} (PID {process.Id})",
                                exception);
                        }
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
            AppLog.Warn("启动项守卫读取子会话失败。", exception);
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
}