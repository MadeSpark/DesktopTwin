using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DesktopTwin.Interop;

namespace DesktopTwin.Services;

/// <summary>
/// 独立恢复进程。主进程退出等待超时或恢复失败时启动它；它先等主进程真正退出，
/// 再通过恢复互斥体接管磁盘上的日志，避免退出时限截断注册表/计划任务恢复。
/// </summary>
internal static class StartupRecoveryCommand
{
    private const string RestoreArgument = "--restore-startup-journal";
    private const int ParentExitTimeoutMilliseconds = 120_000;
    private const int ChildSessionExitTimeoutMilliseconds = 120_000;
    private const int SingleInstanceWaitMilliseconds = 5_000;
    private const int ConfirmedAbsenceScans = 3;
    private const int RestoreAttempts = 8;

    // 必须与 App.xaml.cs 的 SingleInstanceMutexName 保持一致。
    private const string SingleInstanceMutexName = @"Global\DesktopTwin.SingleInstance";

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0
            || !string.Equals(args[0], RestoreArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((args.Length != 2 && args.Length != 3)
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid)
            || parentPid <= 0
            || parentPid == Environment.ProcessId)
        {
            AppLog.Warn("启动项恢复命令行参数无效。");
            exitCode = 2;
            return true;
        }

        // 第三个参数是父进程启动时间。PID 可能很快被复用，仅等 PID 退出会
        // 误把另一个长命进程当成父进程；旧的两参数形式仍兼容。
        long parentStartTicks = 0;
        if (args.Length == 3
            && (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentStartTicks)
                || parentStartTicks < 0))
        {
            AppLog.Warn("启动项恢复命令行的父进程启动时间无效。");
            exitCode = 2;
            return true;
        }

        try
        {
            exitCode = Run(parentPid, parentStartTicks);
        }
        catch (Exception exception)
        {
            // 恢复助手在主窗口和全局异常钩子初始化之前运行。这里必须把
            // 未预期异常落盘为非零退出，不能让进程静默消失、只留下事件日志。
            AppLog.Error("独立启动项恢复助手发生未处理异常。", exception);
            exitCode = 1;
        }

        return true;
    }

    /// <summary>
    /// 启动一个不显示窗口、与主进程同权限的恢复助手。只应在主进程即将退出、
    /// 且进程内恢复没有按时成功时调用。
    /// </summary>
    public static bool TryLaunch(int parentPid)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            AppLog.Warn("无法确定 DesktopTwin 可执行文件路径，未能启动恢复助手。");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(RestoreArgument);
            startInfo.ArgumentList.Add(parentPid.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(GetProcessStartTicks(parentPid).ToString(CultureInfo.InvariantCulture));

            var process = Process.Start(startInfo);
            if (process is null)
            {
                AppLog.Warn("启动项恢复助手未能创建。");
                return false;
            }

            AppLog.Info($"已启动独立启动项恢复助手，PID {process.Id}，等待主进程 {parentPid} 退出。");
            process.Dispose();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or System.Security.SecurityException)
        {
            AppLog.Warn("启动独立启动项恢复助手失败。", exception);
            return false;
        }
    }

    private static int Run(int parentPid, long parentStartTicks)
    {
        AppLog.Info($"独立启动项恢复助手启动，等待主进程 {parentPid} 退出。");

        if (!WaitForParentExit(parentPid, parentStartTicks))
        {
            // 主进程还活着，进程内恢复线程可能仍在持锁写启动项。互斥体只能
            // 把两次恢复串行起来，不能证明两套恢复逻辑交错执行是安全的。
            // 因此助手的硬边界是「父进程退出后才动手」；等待超时就保留日志，
            // 交给下一次启动的 RecoverOnStartup 继续处理。
            AppLog.Warn("主进程未在限时内退出，恢复助手退出并保留日志供下次启动恢复。");
            return 1;
        }

        // 与主实例共用同一个生命周期锁。助手持有它期间，新实例不能启动；
        // 若新实例已经先启动，助手不碰它刚收集的日志，留给新实例自行恢复。
        var instanceMutex = TryAcquireSingleInstance();
        if (instanceMutex is null)
        {
            AppLog.Warn("另一个 DesktopTwin 实例已在运行，恢复助手退出并保留启动项日志。");
            return 1;
        }

        try
        {
            if (!WaitForNoChildSession())
            {
                // 会话仍在使用启动项隔离日志时恢复，等同于在主程序仍尝试连接期间
                // 重新打开自启动。保留日志，由下一次启动恢复流程继续收敛。
                AppLog.Warn("分身会话未在限时内结束，恢复助手退出并保留启动项日志。");
                return 1;
            }

            return RestoreJournal();
        }
        finally
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 句柄已失效或所有权异常时不再尝试第二次释放，进程退出会兜底。
            }
            finally
            {
                instanceMutex.Dispose();
            }
        }
    }

    private static int RestoreJournal()
    {
        var service = new StartupSnoozeService();
        for (var attempt = 1; attempt <= RestoreAttempts; attempt++)
        {
            StartupSnoozeResult result;
            try
            {
                result = service.Restore();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"独立恢复启动项第 {attempt} 次尝试异常。", exception);
                result = StartupSnoozeResult.Fail(exception.GetBaseException().Message);
            }

            if (result.Success)
            {
                AppLog.Info("独立启动项恢复完成：" + result.Message);
                return 0;
            }

            AppLog.Warn($"独立恢复启动项第 {attempt} 次失败：" + result.Message);
            if (attempt == RestoreAttempts)
            {
                break;
            }

            // 让用户/系统有机会结束后台写入者，同时避免密集重试触发安全软件。
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(15, attempt * 2)));
        }

        AppLog.Warn("独立启动项恢复未完成，日志已保留供下次启动继续恢复。");
        return 1;
    }

    private static bool WaitForNoChildSession()
    {
        var deadline = Environment.TickCount64 + ChildSessionExitTimeoutMilliseconds;
        var missingScans = 0;
        var loggedSession = false;
        var loggedQueryFailure = false;
        while (true)
        {
            if (!ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId))
            {
                // WTS 查询失败不代表会话消失。重置“确认不存在”计数并保留日志，
                // 超时后由调用方保持启动项隔离，等待下次启动继续恢复。
                missingScans = 0;
                if (!loggedQueryFailure)
                {
                    AppLog.Warn("查询分身会话是否存在失败，暂不恢复启动项。");
                    loggedQueryFailure = true;
                }
            }
            else if (sessionId is null)
            {
                // 与原生等待一样要求连续确认，避免一次 WTS 查询抖动就误放开启动项。
                if (++missingScans >= ConfirmedAbsenceScans)
                {
                    return true;
                }
            }
            else
            {
                loggedQueryFailure = false;
                if (!loggedSession)
                {
                    AppLog.Info($"等待分身会话 {sessionId.Value} 结束后再恢复启动项。");
                    loggedSession = true;
                }

                missingScans = 0;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(500);
        }
    }

    private static Mutex? TryAcquireSingleInstance()
    {
        var deadline = Environment.TickCount64 + SingleInstanceWaitMilliseconds;
        while (true)
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
                try
                {
                    if (mutex.WaitOne(0))
                    {
                        return mutex;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // 上一个实例异常结束时所有权会以 abandoned 形式转交；
                    // 此时仍由本助手持有，可以安全恢复。
                    return mutex;
                }

                mutex.Dispose();
                mutex = null;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or System.IO.IOException
                                                  or System.Security.SecurityException
                                                  or System.Threading.WaitHandleCannotBeOpenedException)
            {
                mutex?.Dispose();
                AppLog.Warn("恢复助手无法获取 DesktopTwin 单实例锁，保留启动项日志。", exception);
                return null;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(200);
        }
    }

    private static long GetProcessStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static bool WaitForParentExit(int parentPid, long expectedStartTicks)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            if (expectedStartTicks > 0
                && parent.StartTime.ToUniversalTime().Ticks != expectedStartTicks)
            {
                // 原 PID 已退出并被另一个进程复用，不能被它继续拖延。
                AppLog.Warn($"父进程 PID {parentPid} 已被复用，继续恢复启动项。");
                return true;
            }

            if (parent.HasExited)
            {
                return true;
            }

            if (!parent.WaitForExit(ParentExitTimeoutMilliseconds))
            {
                return false;
            }

            AppLog.Info($"主进程 {parentPid} 已退出，开始独立恢复启动项。");
            return true;
        }
        catch (ArgumentException)
        {
            // 主进程已经退出，句柄都来不及打开。
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            // 无法核对身份时不强行宣称已退出，但仍会受恢复互斥体保护尝试一次。
            AppLog.Warn("查询主进程退出状态失败，将在互斥体保护下尝试恢复。", exception);
            return false;
        }
    }
}
