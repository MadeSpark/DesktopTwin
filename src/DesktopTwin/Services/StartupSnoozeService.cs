using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 关停 / 恢复「开机自启动项」的结果。
/// </summary>
/// <param name="Success">流程是否走完（个别项关不掉不算失败，会出现在 <see cref="Warnings"/> 里）。</param>
/// <param name="Message">给用户看的结论。</param>
/// <param name="Warnings">关不掉的项、以及需要注意的提示。</param>
/// <param name="StartupExecutables">本次涉及的启动程序；子会话里只用于登录后的短时兜底拦截。</param>
public sealed record StartupSnoozeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> StartupExecutables)
{
    /// <summary>创建一个成功结果。</summary>
    public static StartupSnoozeResult Ok(
        string message,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? startupExecutables = null) =>
        new(
            true,
            message,
            warnings ?? Array.Empty<string>(),
            startupExecutables ?? Array.Empty<string>());

    /// <summary>创建一个失败结果。</summary>
    public static StartupSnoozeResult Fail(string message, IReadOnlyList<string>? warnings = null) =>
        new(false, message, warnings ?? Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// 在分身连接期间临时关掉本机的开机自启动项，断开后再按字节原样恢复。
///
/// 为什么需要它：子会话用同一个账户登录，Windows 会在子会话里把用户和公共启动项
/// 再跑一遍；单实例软件（Steam、Clash 等）收到协议或 IPC 调用后会把窗口拉回主桌面，
/// 于是「分身里启动」变成「主桌面又冒出来一堆窗口」。
///
/// 正常路径只动 Windows 自己的开关，不碰文件、不碰权限。只有系统拒绝关闭、
/// 登录后仍然在子会话里拉起了同一程序时，才由仅限子会话的短时守卫兜底。
/// <list type="bullet">
/// <item><c>Explorer\StartupApproved</c> 的二进制值（Run / Run32 / StartupFolder）</item>
/// <item>计划任务的启用状态（<c>schtasks /Change</c>）</item>
/// <item>UWP 启动任务的 <c>State</c> 值</item>
/// </list>
///
/// 先写日志再改系统，日志里存的是原值字节；中途崩溃时下次启动会照着日志恢复。
/// </summary>
public sealed class StartupSnoozeService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string RunApprovedPath = ApprovedRootPath + @"\Run";
    private const string Run32ApprovedPath = ApprovedRootPath + @"\Run32";
    private const string StartupFolderApprovedPath = ApprovedRootPath + @"\StartupFolder";
    private const string StartMenuPrograms = @"Microsoft\Windows\Start Menu\Programs";
    private const string TaskCacheTreePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
    private const string UwpRootPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    /// <summary>
    /// 创建服务。
    /// </summary>
    /// <param name="directory">数据目录，默认 <c>%AppData%\DesktopTwin</c>；测试里传临时目录。</param>
    public StartupSnoozeService(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        JournalPath = Path.Combine(Directory, "startup-snooze.json");
        ExceptionsPath = Path.Combine(Directory, "startup-snooze-exceptions.json");
    }

    /// <summary>数据目录。</summary>
    public string Directory { get; }

    /// <summary>关停日志路径。</summary>
    public string JournalPath { get; }

    /// <summary>例外清单路径（写入被系统拒绝的项，用户明确知情后可放行）。</summary>
    public string ExceptionsPath { get; }

    /// <summary>当前是否处于「已关停」状态（以日志文件是否存在为准）。</summary>
    public bool IsSnoozed => File.Exists(JournalPath);

    /// <summary>
    /// 上次退出时没来得及恢复（日志还在）——下次启动必须先恢复再连分身。
    /// </summary>
    public bool HasPendingJournal => IsSnoozed;

    /// <summary>
    /// 关停本机所有能覆盖的第三方启动源。
    /// </summary>
    public StartupSnoozeResult EnsureSnoozed()
    {
        lock (_gate)
        {
            if (IsSnoozed)
            {
                // 已有关停日志时不能悄悄把它当成空清单继续连接：日志损坏意味着
                // 不知道哪些启动项曾被改过，只有明确失败才能避免把用户启动项留在关闭状态。
                StartupSnoozeJournal existing;
                try
                {
                    existing = LoadJournal();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or JsonException or InvalidOperationException
                                                      or NotSupportedException or InvalidDataException
                                                      or SecurityException or ArgumentException)
                {
                    AppLog.Warn("已有关停日志无法读取，拒绝在状态不明时启动分身。", exception);
                    return StartupSnoozeResult.Fail(
                        "已有关停日志但无法读取，无法确认启动项状态；请先修复日志或重新启动 DesktopTwin：" +
                        exception.Message);
                }

                if (!existing.Completed)
                {
                    // 上次在「日志已写、系统状态可能改到一半」时中断。先恢复原始状态，
                    // 再按本次流程重新关停；绝不能把半成品日志当成已经完成。
                    AppLog.Warn("发现上次未完成的启动项关停日志，先恢复原始状态。");
                    var recovery = RestoreCore();
                    if (!recovery.Success)
                    {
                        return StartupSnoozeResult.Fail(
                            "上次关停启动项未完成，且恢复原始状态失败：" + recovery.Message);
                    }
                }
                else
                {
                    return StartupSnoozeResult.Ok(
                        "启动项已处于关停状态。",
                        startupExecutables: NormalizeStartupExecutables(existing));
                }
            }

            var refusals = GetRefusalReasons();
            if (refusals.Count > 0)
            {
                AppLog.Warn("存在未覆盖的启动源，已拒绝关停：" + string.Join("；", refusals));
                return StartupSnoozeResult.Fail(
                    "存在无法安全覆盖的启动源，已拒绝关停启动项：" + string.Join("；", refusals),
                    refusals);
            }

            var journal = new StartupSnoozeJournal
            {
                CreatedAt = DateTimeOffset.Now,
                Machine = Environment.MachineName,
                User = Environment.UserName
            };

            try
            {
                journal.Registry = CollectRegistryEntries();
                journal.Tasks = CollectScheduledTasks();
                journal.Uwp = CollectUwpTasks();
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                AppLog.Warn("枚举启动项失败，未做任何修改。", exception);
                return StartupSnoozeResult.Fail("读取启动项失败，未做任何修改：" + exception.Message);
            }

            var warnings = new List<string>();
            journal.Warnings = warnings;

            // 先落盘再探测/改系统：探测本身也可能触发杀软或留下临时键值。
            // 日志先于任何系统写入存在，崩溃后才有完整的原始状态可恢复。
            try
            {
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                AppLog.Warn("保存启动项关停日志失败，未做任何修改。", exception);
                return StartupSnoozeResult.Fail("保存启动项关停日志失败，未做任何修改：" + exception.Message);
            }

            DropUncoveredEntries(journal, warnings);
            DropExceptionEntries(journal, warnings);
            journal.Warnings = warnings;

            // 过滤后的日志也必须在写入任何系统状态前落盘。
            try
            {
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                AppLog.Warn("保存启动项关停日志失败，未做任何修改。", exception);
                return StartupSnoozeResult.Fail("保存启动项关停日志失败，未做任何修改：" + exception.Message);
            }

            if (journal.Registry.Count == 0 && journal.Tasks.Count == 0 && journal.Uwp.Count == 0)
            {
                // 没有可关的东西也要落一份日志：否则「已关停」状态无法表示，
                // 断开时也就不会走恢复流程。
                journal.Completed = true;
                try
                {
                    SaveJournal(journal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or System.Security.SecurityException)
                {
                    // 此时还没有修改任何启动项，最坏只是留下一个不完整日志；
                    // 下次启动会先按它恢复（空清单是无操作），不能继续连接。
                    AppLog.Warn("保存空的启动项关停日志失败。", exception);
                    return StartupSnoozeResult.Fail(
                        "保存启动项关停日志失败，已取消启动：" + exception.Message,
                        warnings);
                }

                return StartupSnoozeResult.Ok(
                    "没有需要关停的启动项。",
                    warnings,
                    NormalizeStartupExecutables(journal));
            }

            try
            {
                ApplyRegistryEntries(journal, warnings);
                ApplyScheduledTasks(journal, warnings);
                ApplyUwpTasks(journal, warnings);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                AppLog.Warn("关停启动项时出错，正在回滚。", exception);
                var rollback = RestoreCore();
                return StartupSnoozeResult.Fail(
                    "关停启动项失败，已回滚：" + exception.Message + "；" + rollback.Message,
                    warnings);
            }

            journal.Warnings = warnings;
            journal.Completed = true;
            try
            {
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                // 系统状态已经改过。日志落盘失败后不能把「已关停」当成成功，
                // 必须立即按磁盘上的原始快照回滚；回滚本身也要如实上报。
                AppLog.Warn("保存已完成的启动项关停日志失败，正在回滚。", exception);
                var rollback = RestoreCore();
                return StartupSnoozeResult.Fail(
                    "保存启动项关停日志失败，已尝试回滚：" + exception.Message + "；" + rollback.Message,
                    warnings);
            }

            var summary = string.Format(
                CultureInfo.CurrentCulture,
                "已关停启动项：注册表 {0} 项、计划任务 {1} 项、UWP {2} 项。",
                journal.Registry.Count(entry => entry.Applied),
                journal.Tasks.Count(entry => entry.ConfirmedDisabled),
                journal.Uwp.Count(entry => entry.WrittenState == 0));
            AppLog.Info(summary + (warnings.Count == 0 ? string.Empty : "；未能关停：" + string.Join("；", warnings)));
            return StartupSnoozeResult.Ok(
                summary,
                warnings,
                NormalizeStartupExecutables(journal));
        }
    }

    /// <summary>
    /// 按日志把启动项原样恢复。
    /// </summary>
    public StartupSnoozeResult Restore()
    {
        lock (_gate)
        {
            return RestoreCore();
        }
    }

    private StartupSnoozeResult RestoreCore()
    {
        if (!IsSnoozed)
        {
            return StartupSnoozeResult.Ok("没有需要恢复的启动项。");
        }

        StartupSnoozeJournal journal;
        try
        {
            journal = LoadJournal();
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException or InvalidOperationException
                                              or NotSupportedException or InvalidDataException
                                              or SecurityException or ArgumentException)
        {
            AppLog.Warn("读取关停日志失败，未能恢复启动项。", exception);
            return StartupSnoozeResult.Fail("读取关停日志失败：" + exception.Message);
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        var restored = 0;
        var preserved = 0;

        // 恢复必须对日志里的每一条原始状态幂等执行，而不能只看 Applied：
        // 系统值可能已经写入，但进程在 SaveJournal 记录标记前崩溃。
        // 反向遍历是为了在确认某项已被外部修改后，把它从日志里安全移除，
        // 避免下次启动继续拿旧快照覆盖用户的新选择。
        for (var index = journal.Registry.Count - 1; index >= 0; index--)
        {
            var entry = journal.Registry[index];
            try
            {
                RestoreRegistryEntry(entry);
                entry.Applied = false;
                entry.WrittenBase64 = null;
                journal.Registry.RemoveAt(index);
                restored++;
            }
            catch (StartupStateChangedException exception)
            {
                var conflictMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\\{1}：{2}",
                    entry.Hive,
                    entry.ValueName,
                    exception.Message);
                warnings.Add(conflictMessage);
                AppLog.Warn("启动项已被外部修改，保留当前值：" + conflictMessage);
                journal.Registry.RemoveAt(index);
                preserved++;
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or ArgumentException or FormatException
                                                  or InvalidOperationException or NotSupportedException)
            {
                problems.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\\{1}：{2}",
                    entry.Hive,
                    entry.ValueName,
                    exception.Message));
            }
        }

        foreach (var task in journal.Tasks.ToArray())
        {
            if (!task.OriginalEnabled)
            {
                journal.Tasks.Remove(task);
                continue;
            }

            // 已经是启用状态时不要为了“执行恢复”再跑一次 schtasks：
            // 某些受保护任务即使状态正确也可能拒绝修改，直接回读确认即可。
            if (WaitForTaskEnabled(task.Relative, expected: true))
            {
                task.ConfirmedDisabled = false;
                journal.Tasks.Remove(task);
                restored++;
                continue;
            }

            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Enable");
            if (exitCode != 0)
            {
                // 用户在分身会话期间删除了任务时，任务已经不可能再启动，
                // 不应因此永久保留日志并让每次启动都报恢复失败。
                if (!ScheduledTaskExists(task.Relative))
                {
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    restored++;
                    continue;
                }

                if (WaitForTaskEnabled(task.Relative, expected: true))
                {
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    restored++;
                    continue;
                }

                problems.Add(task.Relative + "：" + output);
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: true))
            {
                problems.Add(task.Relative + "：启用后回读未确认");
                continue;
            }

            task.ConfirmedDisabled = false;
            journal.Tasks.Remove(task);
            restored++;
        }

        for (var index = journal.Uwp.Count - 1; index >= 0; index--)
        {
            var uwp = journal.Uwp[index];
            try
            {
                RestoreUwpEntry(uwp);
                uwp.WrittenState = uwp.OriginalState;
                journal.Uwp.RemoveAt(index);
                restored++;
            }
            catch (StartupStateChangedException exception)
            {
                var conflictMessage = uwp.KeyPath + "：" + exception.Message;
                warnings.Add(conflictMessage);
                AppLog.Warn("UWP 启动项已被外部修改，保留当前值：" + conflictMessage);
                journal.Uwp.RemoveAt(index);
                preserved++;
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                problems.Add(uwp.KeyPath + "：" + exception.Message);
            }
        }

        if (problems.Count == 0)
        {
            try
            {
                File.Delete(JournalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or SecurityException or NotSupportedException
                                                  or ArgumentException)
            {
                problems.Add("删除日志失败：" + exception.Message);
            }
        }
        else
        {
            // 已经恢复的项把标记清掉，避免下次重复恢复时又去写一遍。
            try
            {
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or SecurityException or NotSupportedException
                                                  or ArgumentException)
            {
                AppLog.Warn("保存恢复进度失败。", exception);
            }
        }

        if (problems.Count > 0)
        {
            AppLog.Warn("恢复启动项未全部成功：" + string.Join("；", problems));
            return StartupSnoozeResult.Fail(
                "恢复启动项未全部成功：" + string.Join("；", problems),
                warnings.Concat(problems).ToArray());
        }

        var message = string.Format(
            CultureInfo.CurrentCulture,
            preserved == 0 ? "已恢复 {0} 个启动项。" : "已恢复 {0} 个启动项，保留 {1} 项外部修改。",
            restored,
            preserved);
        AppLog.Info(message);
        if (warnings.Count > 0)
        {
            AppLog.Warn("恢复时保留了外部修改：" + string.Join("；", warnings));
        }

        return StartupSnoozeResult.Ok(message, warnings);
    }

    /// <summary>
    /// 启动时调用：上次没恢复完就补一次，返回给用户的提示（无需提示时为 null）。
    /// </summary>
    public string? RecoverOnStartup()
    {
        if (!HasPendingJournal)
        {
            return null;
        }

        AppLog.Warn("发现上次未恢复的启动项关停日志，正在恢复。");
        var result = Restore();
        return result.Success
            ? "上次退出时未能恢复开机启动项，已按日志自动恢复：" + result.Message
            : "上次关停的开机启动项未能全部恢复，请检查：" + result.Message;
    }

    // ===== 枚举 =====

    private sealed record RunTarget(
        RegistryHive Hive,
        RegistryView RunView,
        RegistryView ApprovedView,
        string HiveName,
        string[] ApprovedPaths);

    private static IReadOnlyList<RunTarget> RunTargets() => new[]
    {
        new RunTarget(RegistryHive.CurrentUser, RegistryView.Registry64, RegistryView.Registry64,
            "CurrentUser", new[] { ApprovedRootPath + @"\Run" }),
        new RunTarget(RegistryHive.CurrentUser, RegistryView.Registry32, RegistryView.Registry64,
            "CurrentUser", new[] { ApprovedRootPath + @"\Run32" }),
        new RunTarget(RegistryHive.LocalMachine, RegistryView.Registry64, RegistryView.Registry64,
            "LocalMachine", new[] { ApprovedRootPath + @"\Run" }),
        // 32 位 Run 值从 WOW6432Node 读取，但 StartupApproved 整棵子树也被 WOW64
        // 重定向；关闭项必须写在 64 位视图的 Run32 下，否则写进的是不存在的镜像。
        new RunTarget(RegistryHive.LocalMachine, RegistryView.Registry32, RegistryView.Registry64,
            "LocalMachine", new[] { ApprovedRootPath + @"\Run32" })
    };

    private static List<StartupSnoozeRegistryEntry> CollectRegistryEntries()
    {
        var entries = new List<StartupSnoozeRegistryEntry>();

        foreach (var target in RunTargets())
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.RunView);
            using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: false);
            if (runKey is null)
            {
                continue;
            }

            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                var commandLine = Convert.ToString(
                    runKey.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    CultureInfo.InvariantCulture);

                var approvalPath = FindApprovalPath(target, valueName);
                var originalBytes = ReadApprovalBytes(
                    target.Hive, target.ApprovedView, approvalPath, valueName, out var exists);

                // 已经关掉的不要重复关：恢复时也就不用动它。
                if (exists && StartupApproval.IsDisabled(originalBytes))
                {
                    continue;
                }

                // 命令行的可执行文件不存在时 Windows 本来就不会启动它（启动项「已失效」），
                // 这种情况不需要写开关，否则反而会把它变成「启用」。
                if (!exists && !ExecutableExists(commandLine))
                {
                    continue;
                }

                entries.Add(new StartupSnoozeRegistryEntry
                {
                    Kind = "RunValue",
                    Hive = target.HiveName,
                    RunView = target.RunView.ToString(),
                    ApprovedView = target.ApprovedView.ToString(),
                    KeyPath = approvalPath,
                    ValueName = valueName,
                    CommandLine = commandLine,
                    OriginalExists = exists,
                    OriginalBase64 = exists && originalBytes is not null
                        ? Convert.ToBase64String(originalBytes)
                        : null
                });
            }
        }

        entries.AddRange(CollectStartupFolderEntries());
        return entries;
    }

    private static string FindApprovalPath(RunTarget target, string valueName)
    {
        foreach (var path in target.ApprovedPaths)
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.ApprovedView);
            using var key = baseKey.OpenSubKey(path, writable: false);
            if (key is not null && key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return target.ApprovedPaths[0];
    }

    private static byte[]? ReadApprovalBytes(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName,
        out bool exists)
    {
        exists = false;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] raw)
        {
            exists = true;
            return raw;
        }

        return null;
    }

    private static List<StartupSnoozeRegistryEntry> CollectStartupFolderEntries()
    {
        var entries = new List<StartupSnoozeRegistryEntry>();
        var folders = new (string Path, RegistryHive Hive, string HiveName)[]
        {
            (System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                StartMenuPrograms,
                "Startup"), RegistryHive.CurrentUser, "CurrentUser"),
            (System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StartMenuPrograms,
                "Startup"), RegistryHive.LocalMachine, "LocalMachine")
        };

        foreach (var folder in folders)
        {
            if (!System.IO.Directory.Exists(folder.Path))
            {
                continue;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(folder.Path))
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bytes = ReadFolderApprovalBytes(folder.Hive, fileName, out var exists);
                if (exists && StartupApproval.IsDisabled(bytes))
                {
                    continue;
                }

                entries.Add(new StartupSnoozeRegistryEntry
                {
                    Kind = "StartupFolderItem",
                    Hive = folder.HiveName,
                    RunView = "Registry64",
                    ApprovedView = "Registry64",
                    KeyPath = StartupFolderApprovedPath,
                    ValueName = fileName,
                    CommandLine = file,
                    OriginalExists = exists,
                    OriginalBase64 = exists && bytes is not null ? Convert.ToBase64String(bytes) : null
                });
            }
        }

        return entries;
    }

    private static byte[]? ReadFolderApprovalBytes(RegistryHive hive, string valueName, out bool exists)
    {
        exists = false;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(StartupFolderApprovedPath, writable: false);
        if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] raw)
        {
            exists = true;
            return raw;
        }

        return null;
    }

    private static List<StartupSnoozeTaskEntry> CollectScheduledTasks()
    {
        var entries = new List<StartupSnoozeTaskEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var tree = baseKey.OpenSubKey(TaskCacheTreePath, writable: false);
        if (tree is null)
        {
            return entries;
        }

        Collect(tree, string.Empty, depth: 0);
        return entries;

        void Collect(RegistryKey node, string relative, int depth)
        {
            if (depth > 6)
            {
                return;
            }

            var isTask = node.GetValue("Id") is not null;
            if (isTask && !relative.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                var xml = QueryTaskXml(relative);
                if (xml is not null && ScheduledTaskXml.IsEnabled(xml) && ScheduledTaskXml.HasSessionStartTrigger(xml))
                {
                    var trimmed = relative.TrimStart('\\');
                    var slash = trimmed.LastIndexOf('\\');
                    entries.Add(new StartupSnoozeTaskEntry
                    {
                        Relative = relative,
                        TaskName = slash < 0 ? trimmed : trimmed.Substring(slash + 1),
                        TaskPath = slash < 0 ? "\\" : "\\" + trimmed.Substring(0, slash) + "\\",
                        Executable = ScheduledTaskXml.GetExecutable(xml),
                        OriginalEnabled = true
                    });
                }
            }

            foreach (var childName in node.GetSubKeyNames())
            {
                using var child = node.OpenSubKey(childName, writable: false);
                if (child is not null)
                {
                    Collect(child, relative + "\\" + childName, depth + 1);
                }
            }
        }
    }

    private static bool ScheduledTaskExists(string relative)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var tree = baseKey.OpenSubKey(TaskCacheTreePath, writable: false);
            using var task = tree?.OpenSubKey(relative.TrimStart('\\'), writable: false);
            return task?.GetValue("Id") is not null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or ArgumentException or NotSupportedException)
        {
            // 读不到 TaskCache 时按“仍存在”处理，避免把恢复失败误判成任务已删除。
            AppLog.Warn("无法确认计划任务是否仍存在：" + relative, exception);
            return true;
        }
    }

    private static string? QueryTaskXml(string relative)
    {
        var (exitCode, output) = RunSchtasks("/Query", "/XML", "ONE", "/TN", relative);
        return exitCode == 0 && output.Contains('<') ? output : null;
    }

    private static List<StartupSnoozeUwpEntry> CollectUwpTasks()
    {
        var entries = new List<StartupSnoozeUwpEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var root = baseKey.OpenSubKey(UwpRootPath, writable: false);
        if (root is null)
        {
            return entries;
        }

        foreach (var family in root.GetSubKeyNames())
        {
            if (family.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                || family.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var packageKey = root.OpenSubKey(family, writable: false);
            if (packageKey is null)
            {
                continue;
            }

            Walk(packageKey, family, string.Empty, depth: 0);
        }

        return entries;

        void Walk(RegistryKey node, string family, string relative, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (node.GetValue("State", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int state
                && state == UwpStartupState.EnabledConfirmed)
            {
                entries.Add(new StartupSnoozeUwpEntry
                {
                    KeyPath = UwpRootPath + "\\" + family + (relative.Length == 0 ? string.Empty : "\\" + relative),
                    PackageFamilyName = family,
                    Task = relative.Length == 0 ? family : relative.Substring(relative.LastIndexOf('\\') + 1),
                    OriginalState = state,
                    WrittenState = state
                });
            }

            foreach (var childName in node.GetSubKeyNames())
            {
                using var child = node.OpenSubKey(childName, writable: false);
                if (child is not null)
                {
                    Walk(child, family, relative.Length == 0 ? childName : relative + "\\" + childName, depth + 1);
                }
            }
        }
    }

    // ===== 写入 =====

    private void ApplyRegistryEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var disabledBytes = StartupApproval.CreateDisabled(DateTime.UtcNow);
        var disabledBase64 = Convert.ToBase64String(disabledBytes);
        foreach (var entry in journal.Registry)
        {
            try
            {
                // 先把「准备写入的值」落盘，再改系统。若进程恰好在这两步之间崩溃，
                // 恢复逻辑也能认出当前值是不是桌面分身写的，而不是盲目覆盖。
                entry.WrittenBase64 = disabledBase64;
                entry.Applied = false;
                SaveJournal(journal);

                using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
                using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("无法创建键 " + entry.KeyPath);

                key.SetValue(entry.ValueName, disabledBytes, RegistryValueKind.Binary);

                // SetValue 不抛异常不等于系统真的接受了写入：自保护驱动可能静默
                // 回滚。必须回读确认低半字节是 0x03，否则该启动项继续保持启用，
                // 要在子会话里用守卫兜底。
                var written = key.GetValue(
                    entry.ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                if (!StartupApproval.IsDisabled(written))
                {
                    throw new InvalidOperationException("写入后回读未确认已关闭");
                }

                entry.WrittenBase64 = Convert.ToBase64String(written!);
                entry.Applied = true;
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException)
            {
                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}：{1}",
                    entry.ValueName,
                    exception.Message);
                warnings.Add(message);
                AppLog.Warn("无法关闭启动项 " + entry.ValueName, exception);
                TryRestoreRegistryEntryQuietly(entry);
            }
        }
    }

    private void ApplyScheduledTasks(StartupSnoozeJournal journal, List<string> warnings)
    {
        foreach (var task in journal.Tasks)
        {
            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Disable");
            if (exitCode != 0)
            {
                warnings.Add(task.Relative + "：" + output);
                AppLog.Warn("禁用计划任务失败：" + task.Relative + " -> " + output);
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: false))
            {
                warnings.Add(task.Relative + "：禁用后回读未确认");
                continue;
            }

            task.ConfirmedDisabled = true;
            SaveJournal(journal);
        }
    }

    private void ApplyUwpTasks(StartupSnoozeJournal journal, List<string> warnings)
    {
        foreach (var uwp in journal.Uwp)
        {
            try
            {
                // 与注册表项相同：先记录准备写入的状态，崩溃后恢复逻辑才能判断
                // 当前 State=0 是本次关停造成的，还是用户/应用自己改的。
                uwp.WrittenState = 0;
                SaveJournal(journal);

                using var key = Registry.CurrentUser.OpenSubKey(uwp.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("键不存在");
                key.SetValue("State", 0, RegistryValueKind.DWord);
                var written = key.GetValue(
                    "State",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (written is not int state || state != 0)
                {
                    throw new InvalidOperationException("写入后回读未确认已关闭");
                }

                uwp.WrittenState = 0;
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                warnings.Add(uwp.Task + "：" + exception.Message);
                AppLog.Warn("关闭 UWP 启动任务失败：" + uwp.Task, exception);
                TryRestoreUwpEntryQuietly(uwp);
            }
        }
    }

    private static void RestoreRegistryEntry(StartupSnoozeRegistryEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));

        // 只有「原值」和「我们写进去的关闭值」可以覆盖。当前值若是另一种
        // 启用状态，说明用户或别的程序已经改过它，恢复时不能把外部修改吃掉。
        var original = entry.OriginalExists
            ? Convert.FromBase64String(
                entry.OriginalBase64 ?? throw new InvalidDataException("关停日志缺少原始注册表值"))
            : null;

        // 先只读判断是否真的需要写入。受保护的启动项常常允许读取但拒绝写；
        // 已经处于原始状态时不能因为“打不开可写句柄”而误报恢复失败。
        using var readKey = baseKey.OpenSubKey(entry.KeyPath, writable: false);
        if (readKey is null)
        {
            if (!entry.OriginalExists)
            {
                // 原值本来不存在，键也不存在时已经恢复。
                return;
            }

            throw new StartupStateChangedException("原启动项所在的注册表键已被删除，未重新创建");
        }

        var current = readKey.GetValue(
            entry.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];

        if (!entry.OriginalExists)
        {
            // 原值本来不存在：当前为空就已经恢复；只有仍是关闭值时才删除。
            // 不要为了删除而创建空键，也不要删掉后来由用户新建的启动项。
            if (current is null)
            {
                return;
            }

            if (!StartupApproval.IsDisabled(current))
            {
                throw new StartupStateChangedException("当前启动值已被其他程序改为启用状态，未覆盖外部修改");
            }

            using var deleteKey = baseKey.OpenSubKey(entry.KeyPath, writable: true);
            if (deleteKey is null)
            {
                // 读和写之间键被卸载/删除，启动值已经不存在。
                return;
            }

            // 打开可写句柄后重新确认当前值，避免把刚刚被外部改成的启用值删掉。
            var currentNow = deleteKey.GetValue(
                entry.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
            if (currentNow is null)
            {
                return;
            }

            if (!StartupApproval.IsDisabled(currentNow))
            {
                throw new StartupStateChangedException("当前启动值已被其他程序改为启用状态，未覆盖外部修改");
            }

            deleteKey.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            if (deleteKey.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            {
                throw new InvalidOperationException("删除后回读仍存在");
            }

            return;
        }

        if (current is not null && current.SequenceEqual(original!))
        {
            return;
        }

        if (current is null)
        {
            throw new StartupStateChangedException("原启动值已被其他程序删除，未重新创建");
        }

        if (!StartupApproval.IsDisabled(current))
        {
            throw new StartupStateChangedException("当前启动值已被其他程序改为启用状态，未覆盖外部修改");
        }

        using var writeKey = baseKey.OpenSubKey(entry.KeyPath, writable: true)
            ?? throw new StartupStateChangedException("原启动项所在的注册表键已被删除，未重新创建");

        // 写入前再次核对，防止在取得可写句柄期间外部程序已经把值改掉。
        var writableCurrent = writeKey.GetValue(
            entry.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        if (writableCurrent is not null && writableCurrent.SequenceEqual(original!))
        {
            return;
        }

        if (writableCurrent is null)
        {
            throw new StartupStateChangedException("原启动值已被其他程序删除，未重新创建");
        }

        if (!StartupApproval.IsDisabled(writableCurrent))
        {
            throw new StartupStateChangedException("当前启动值已被其他程序改为启用状态，未覆盖外部修改");
        }

        writeKey.SetValue(entry.ValueName, original!, RegistryValueKind.Binary);
        var restored = writeKey.GetValue(
            entry.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        if (restored is null || !restored.SequenceEqual(original!))
        {
            throw new InvalidOperationException("恢复后回读与原始字节不一致");
        }
    }

    private static void TryRestoreRegistryEntryQuietly(StartupSnoozeRegistryEntry entry)
    {
        try
        {
            RestoreRegistryEntry(entry);
            entry.Applied = false;
            entry.WrittenBase64 = null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or InvalidOperationException
                                              or ArgumentException or FormatException or NotSupportedException)
        {
            AppLog.Warn("回滚启动项失败：" + entry.ValueName, exception);
        }
    }

    private static void RestoreUwpEntry(StartupSnoozeUwpEntry entry)
    {
        // 与注册表启动项一样，先只读判断。应用已经被卸载或本来就是原始状态时，
        // 不应因为受保护键拒绝写句柄而永久保留日志。
        using var readKey = Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: false);
        if (readKey is null)
        {
            // 应用已被卸载，键不存在时没有可恢复的启动状态。
            return;
        }

        var current = readKey.GetValue(
            "State",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is not int state)
        {
            throw new StartupStateChangedException("当前 UWP State 已不存在或类型已变化，未覆盖外部修改");
        }

        if (state == entry.OriginalState)
        {
            return;
        }

        if (state != 0)
        {
            throw new StartupStateChangedException("当前 UWP State 已被其他程序修改，未覆盖外部修改");
        }

        using var writeKey = Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: true);
        if (writeKey is null)
        {
            // 读和写之间应用被卸载。
            return;
        }

        // 写入前重新核对，不能用旧快照覆盖刚刚发生的外部修改。
        var writableCurrent = writeKey.GetValue(
            "State",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (writableCurrent is int writableState && writableState == entry.OriginalState)
        {
            return;
        }

        if (writableCurrent is not int writableStateToChange || writableStateToChange != 0)
        {
            throw new StartupStateChangedException("当前 UWP State 已被其他程序修改，未覆盖外部修改");
        }

        writeKey.SetValue("State", entry.OriginalState, RegistryValueKind.DWord);
        var restored = writeKey.GetValue(
            "State",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (restored is not int restoredState || restoredState != entry.OriginalState)
        {
            throw new InvalidOperationException("恢复后回读与原始 State 不一致");
        }
    }

    private static void TryRestoreUwpEntryQuietly(StartupSnoozeUwpEntry entry)
    {
        try
        {
            RestoreUwpEntry(entry);
            entry.WrittenState = entry.OriginalState;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or InvalidOperationException
                                              or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("回滚 UWP 启动任务失败：" + entry.Task, exception);
        }
    }

    // ===== 拒绝条件 =====

    private List<string> GetRefusalReasons()
    {
        var reasons = new List<string>();

        var runOnceTargets = new (RegistryHive Hive, RegistryView View, string Path)[]
        {
            // Registry32 本身已经重定向到 WOW6432Node；再手写 WOW6432Node 会变成
            // WOW6432Node\WOW6432Node，看起来查过了，实际漏掉真正的 32 位 RunOnce。
            (RegistryHive.CurrentUser, RegistryView.Registry64, RunKeyPath + "Once"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, RunKeyPath + "Once"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, RunKeyPath + "OnceEx"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, RunKeyPath + "OnceEx"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, RunKeyPath + "Once"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, RunKeyPath + "Once"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, RunKeyPath + "OnceEx"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, RunKeyPath + "OnceEx")
        };

        var allowedRoots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeWebView\Application"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeUpdate"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\EdgeWebView\Application")
        };

        foreach (var target in runOnceTargets)
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
            using var key = baseKey.OpenSubKey(target.Path, writable: false);
            if (key is null)
            {
                continue;
            }

            // RunOnceEx 的真实命令通常放在子键里，只检查根值会漏掉它；
            // RunOnce 下出现子键也不是我们能安全覆盖的标准形态，一律拒绝。
            if (key.GetSubKeyNames().Length > 0)
            {
                reasons.Add("RunOnce 子项 " + target.Hive + "|" + target.View + "|" + target.Path);
                continue;
            }

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var commandLine = Convert.ToString(
                    key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    CultureInfo.InvariantCulture);
                var executable = RunOnceClassifier.ParseExecutable(commandLine);
                if (RunOnceClassifier.IsAllowed(
                        name,
                        commandLine,
                        executable is not null && File.Exists(executable),
                        executable,
                        allowedRoots))
                {
                    AppLog.Info("放行微软 Edge 清理项：" + name + " -> " + commandLine);
                    continue;
                }

                reasons.Add("RunOnce 项 " + name);
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var policies = baseKey.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run");
                if (policies is not null
                    && (policies.GetValueNames().Any(name => !string.IsNullOrWhiteSpace(name))
                        || policies.GetSubKeyNames().Length > 0))
                {
                    reasons.Add("组策略启动项 " + hive + "|" + view + @"\Policies\Explorer\Run");
                }
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var winlogon = baseKey.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: false);
            if (winlogon is null)
            {
                continue;
            }

            var userinit = Convert.ToString(winlogon.GetValue("Userinit"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(userinit) && !IsStandardUserinit(userinit))
            {
                reasons.Add("Winlogon Userinit 被改动");
            }

            var shell = Convert.ToString(winlogon.GetValue("Shell"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(shell)
                && !string.Equals(shell.Trim(), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Winlogon Shell 被改动");
            }
        }

        return reasons;
    }

    private static bool IsStandardUserinit(string value)
    {
        var candidate = value.Trim().TrimEnd(',', ' ').Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }
        else if (candidate.Contains('"'))
        {
            return false;
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate);
        if (!Path.IsPathRooted(candidate))
        {
            return false;
        }

        try
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "userinit.exe");
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从一条无法关停的启动项里取兜底守卫要匹配的 EXE。启动文件夹里的 .lnk
    /// 不能直接拿快捷方式路径去匹配进程，必须解析到它指向的程序；解析失败时
    /// 宁可不加入守卫，也不能按快捷方式名称误杀别的进程。
    /// </summary>
    private static string? ResolveGuardExecutable(StartupSnoozeRegistryEntry entry)
    {
        string? candidate;
        if (string.Equals(entry.Kind, "StartupFolderItem", StringComparison.OrdinalIgnoreCase))
        {
            candidate = entry.CommandLine;
        }
        else
        {
            candidate = RunOnceClassifier.ParseExecutable(entry.CommandLine);
        }

        return ShortcutTargetResolver.ResolveExecutable(candidate);
    }

    private void DropUncoveredEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var exceptions = LoadExceptions();
        var kept = new List<StartupSnoozeRegistryEntry>();

        foreach (var entry in journal.Registry)
        {
            var key = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                entry.Hive,
                entry.ApprovedView,
                entry.ValueName);

            if (exceptions.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add(entry.ValueName + "：已按例外清单保持启用");
                continue;
            }

            var problem = ProbeWritable(entry);
            if (problem is not null)
            {
                warnings.Add(entry.ValueName + "：系统拒绝写入（" + problem + "），仍会在分身里启动");
                AppLog.Warn("启动项写入被系统拒绝：" + key + " -> " + problem);

                // 日志里要留下精确程序，子会话登录后的短时守卫才能把它挡在分身内。
                var executable = ResolveGuardExecutable(entry);
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    journal.DeferredExecutables.Add(executable);
                }
                else
                {
                    AppLog.Warn("启动项被系统拒绝写入，且无法解析出兜底程序：" + entry.ValueName);
                }

                continue;
            }

            kept.Add(entry);
        }

        journal.Registry = kept;
    }

    private void DropExceptionEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var exceptions = LoadExceptions();
        if (!journal.Tasks.Any(task => exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var task in journal.Tasks.Where(task =>
                     exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase)))
        {
            warnings.Add(task.Relative + "：已按例外清单保持启用");
        }

        journal.Tasks = journal.Tasks
            .Where(task => !exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string? ProbeWritable(StartupSnoozeRegistryEntry entry)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
            using var key = baseKey.OpenSubKey(entry.KeyPath, writable: true);
            if (key is null)
            {
                // 原值不存在、键也不存在时不要为了探测创建空键；真正的写入
                // 会由 ApplyRegistryEntries 再做一次，失败原因会进入警告清单。
                return entry.OriginalExists ? "无法打开键" : null;
            }

            // 只写一个唯一的临时值，绝不触碰真实启动值的名称或原值。
            // 即使进程在这里崩溃，日志已经在探测前落盘，真实启动项也没有被修改。
            var probeName = "DesktopTwinProbe_" + Guid.NewGuid().ToString("N");
            Exception? failure = null;
            try
            {
                key.SetValue(probeName, StartupApproval.CreateDisabled(DateTime.UtcNow), RegistryValueKind.Binary);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    key.DeleteValue(probeName, throwOnMissingValue: false);
                }
                catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                      or IOException or InvalidOperationException
                                                      or ArgumentException)
                {
                    failure ??= exception;
                }
            }

            return failure?.Message;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or InvalidOperationException
                                              or ArgumentException)
        {
            return exception.Message;
        }
    }

    private HashSet<string> LoadExceptions()
    {
        try
        {
            if (!File.Exists(ExceptionsPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ExceptionsPath, Encoding.UTF8));
            return new HashSet<string>(keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            AppLog.Warn("读取启动项例外清单失败，按空清单处理。", exception);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ===== 计划任务 =====

    private static bool WaitForTaskEnabled(string relative, bool expected)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var xml = QueryTaskXml(relative);
            if (xml is not null && ScheduledTaskXml.IsEnabled(xml) == expected)
            {
                return true;
            }

            System.Threading.Thread.Sleep(300);
        }

        return false;
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "无法启动 schtasks.exe");
            }

            // 必须同时异步读取两个管道，否则 schtasks 输出较多时会先写满缓冲区，
            // 子进程阻塞在写、父进程阻塞在 ReadToEnd，30 秒超时永远没有机会生效。
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                        or System.ComponentModel.Win32Exception
                                                        or NotSupportedException)
                {
                    AppLog.Warn("终止超时的 schtasks 进程失败。", exception);
                }

                return (-1, "schtasks 超时");
            }

            process.WaitForExit();
            return (process.ExitCode, (standardOutput.GetAwaiter().GetResult() + " "
                                      + standardError.GetAwaiter().GetResult()).Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            return (-1, exception.Message);
        }
    }

    // ===== 日志 =====

    private void SaveJournal(StartupSnoozeJournal journal)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(journal, JournalJsonOptions);
        var temporary = JournalPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       bufferSize: 16 * 1024,
                       leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // 同目录 rename 是原子的；日志内容必须在替换正式文件前真正落盘，
            // 否则断电后可能留下截断的 JSON，启动项就再也无法自动恢复。
            File.Move(temporary, JournalPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or SecurityException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                AppLog.Warn("清理启动项关停日志临时文件失败。", exception);
            }
        }
    }

    private StartupSnoozeJournal LoadJournal()
    {
        var json = File.ReadAllText(JournalPath, Encoding.UTF8);
        var journal = JsonSerializer.Deserialize<StartupSnoozeJournal>(json)
                      ?? throw new InvalidOperationException("关停日志内容为空。");
        ValidateJournal(journal);
        return journal;
    }

    /// <summary>
    /// 日志位于当前用户可写的 AppData，而恢复进程是提权进程。不能信任其中的
    /// 注册表路径、任务名是否只指向启动项；否则篡改日志就能借恢复流程写 HKLM/任务计划。
    /// </summary>
    internal static void ValidateJournal(StartupSnoozeJournal journal)
    {
        if (journal.Version != 1)
        {
            throw new InvalidDataException("不支持的启动项关停日志版本：" + journal.Version);
        }

        if (journal.Registry is null || journal.Tasks is null || journal.Uwp is null
            || journal.Warnings is null || journal.DeferredExecutables is null)
        {
            throw new InvalidDataException("启动项关停日志缺少必要的列表。");
        }

        if ((!string.IsNullOrEmpty(journal.Machine)
             && !string.Equals(journal.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(journal.User)
                && !string.Equals(journal.User, Environment.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("启动项关停日志不属于当前计算机或当前用户。");
        }

        if (journal.Registry.Count > 10_000 || journal.Tasks.Count > 10_000
            || journal.Uwp.Count > 10_000 || journal.DeferredExecutables.Count > 10_000)
        {
            throw new InvalidDataException("启动项关停日志条目过多。");
        }

        foreach (var entry in journal.Registry)
        {
            ValidateRegistryEntry(entry);
        }

        foreach (var task in journal.Tasks)
        {
            ValidateTaskEntry(task);
        }

        foreach (var uwp in journal.Uwp)
        {
            ValidateUwpEntry(uwp);
        }

        foreach (var executable in journal.DeferredExecutables)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.Length > 32_768
                || executable.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("启动项关停日志包含无效的启动程序路径。");
            }
        }
    }

    private static void ValidateRegistryEntry(StartupSnoozeRegistryEntry? entry)
    {
        if (entry is null)
        {
            throw new InvalidDataException("启动项关停日志包含空的注册表项。");
        }

        if (entry.Hive is not ("CurrentUser" or "LocalMachine"))
        {
            throw new InvalidDataException("启动项关停日志包含未知的注册表配置单元：" + entry.Hive);
        }

        if (entry.RunView is not ("Registry32" or "Registry64"))
        {
            throw new InvalidDataException("启动项关停日志包含未知的 Run 视图：" + entry.RunView);
        }

        if (!string.Equals(entry.ApprovedView, "Registry64", StringComparison.Ordinal))
        {
            throw new InvalidDataException("启动项关停日志包含未知的注册表视图：" + entry.ApprovedView);
        }

        var isRun = string.Equals(entry.KeyPath, RunApprovedPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.KeyPath, Run32ApprovedPath, StringComparison.OrdinalIgnoreCase);
        var isStartupFolder = string.Equals(
            entry.KeyPath,
            StartupFolderApprovedPath,
            StringComparison.OrdinalIgnoreCase);
        if (entry.Kind == "RunValue" && !isRun)
        {
            throw new InvalidDataException("启动项关停日志的 Run 项路径不在 StartupApproved\\Run 下。");
        }

        if (entry.Kind == "StartupFolderItem" && !isStartupFolder)
        {
            throw new InvalidDataException("启动项关停日志的启动文件夹项路径不正确。");
        }

        if (entry.Kind is not ("RunValue" or "StartupFolderItem") || (!isRun && !isStartupFolder))
        {
            throw new InvalidDataException("启动项关停日志包含不允许的注册表路径。");
        }

        if (string.IsNullOrWhiteSpace(entry.ValueName) || entry.ValueName.Length > 1024
            || entry.ValueName.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("启动项关停日志包含无效的注册表值名。");
        }

        if (entry.CommandLine is { Length: > 32_768 })
        {
            throw new InvalidDataException("启动项关停日志的命令行过长。");
        }

        if (entry.OriginalExists)
        {
            ValidateBase64(entry.OriginalBase64, "原始注册表值");
        }

        if (entry.WrittenBase64 is not null)
        {
            ValidateBase64(entry.WrittenBase64, "写入的注册表值");
        }
    }

    private static void ValidateBase64(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 * 1024)
        {
            throw new InvalidDataException("启动项关停日志的" + field + "无效。");
        }

        try
        {
            var raw = Convert.FromBase64String(text);
            if (raw.Length == 0 || raw.Length > 16 * 1024)
            {
                throw new InvalidDataException("启动项关停日志的" + field + "长度无效。");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("启动项关停日志的" + field + "不是 Base64。", exception);
        }
    }

    private static void ValidateTaskEntry(StartupSnoozeTaskEntry? task)
    {
        if (task is null)
        {
            throw new InvalidDataException("启动项关停日志包含空的计划任务。");
        }

        if (string.IsNullOrWhiteSpace(task.Relative) || task.Relative.Length > 1024
            || !task.Relative.StartsWith('\\')
            || task.Relative.IndexOfAny(new[] { '\0', '\r', '\n', '*', '?' }) >= 0)
        {
            throw new InvalidDataException("启动项关停日志包含无效的计划任务名。");
        }

        if (string.Equals(task.Relative, @"\Microsoft", StringComparison.OrdinalIgnoreCase)
            || task.Relative.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("启动项关停日志不能指向 Microsoft 系统任务。");
        }

        foreach (var segment in task.Relative.Split('\\').Skip(1))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new InvalidDataException("启动项关停日志包含非法的计划任务路径。");
            }
        }

        if (task.Executable is { Length: > 32_768 }
            || task.Executable?.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
        {
            throw new InvalidDataException("启动项关停日志包含无效的任务程序路径。");
        }
    }

    private static void ValidateUwpEntry(StartupSnoozeUwpEntry? uwp)
    {
        if (uwp is null)
        {
            throw new InvalidDataException("启动项关停日志包含空的 UWP 启动项。");
        }

        var prefix = UwpRootPath + "\\";
        if (string.IsNullOrWhiteSpace(uwp.KeyPath) || uwp.KeyPath.Length > 2048
            || uwp.KeyPath.IndexOf('\0') >= 0
            || !uwp.KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("启动项关停日志包含无效的 UWP 键路径。");
        }

        foreach (var segment in uwp.KeyPath[prefix.Length..].Split('\\'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new InvalidDataException("启动项关停日志包含非法的 UWP 键路径。");
            }
        }

        if (string.IsNullOrWhiteSpace(uwp.Task) || uwp.Task.Length > 1024
            || uwp.Task.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("启动项关停日志包含无效的 UWP 任务名。");
        }

        if (uwp.OriginalState != UwpStartupState.EnabledConfirmed)
        {
            throw new InvalidDataException("启动项关停日志包含无效的 UWP 原始状态。");
        }
    }

    // ===== 辅助 =====

    private static RegistryHive ParseHive(string hive) => hive switch
    {
        "CurrentUser" => RegistryHive.CurrentUser,
        "LocalMachine" => RegistryHive.LocalMachine,
        _ => throw new InvalidDataException("未知的注册表配置单元：" + hive)
    };

    private static RegistryView ParseView(string view) => view switch
    {
        "Registry32" => RegistryView.Registry32,
        "Registry64" => RegistryView.Registry64,
        _ => throw new InvalidDataException("未知的注册表视图：" + view)
    };

    private static IReadOnlyList<string> NormalizeStartupExecutables(StartupSnoozeJournal journal)
    {
        var executables = new List<string?>();
        // 只守卫「没有确认关掉」的项：成功关闭的启动源本来就不会在子会话里
        // 启动，把它们也列为目标会误杀用户登录后手动打开的同名程序。
        foreach (var entry in journal.Registry.Where(entry => !entry.Applied))
        {
            executables.Add(RunOnceClassifier.ParseExecutable(entry.CommandLine));
        }

        foreach (var entry in journal.Tasks.Where(task => !task.ConfirmedDisabled))
        {
            executables.Add(RunOnceClassifier.ParseExecutable(entry.Executable));
        }

        executables.AddRange(journal.DeferredExecutables);

        return executables
            .Select(NormalizeExecutablePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parsed = RunOnceClassifier.ParseExecutable(path);
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(parsed.Trim());
            return Path.IsPathRooted(expanded) ? expanded : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    private static bool ExecutableExists(string? commandLine)
    {
        var executable = RunOnceClassifier.ParseExecutable(commandLine);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            return File.Exists(Environment.ExpandEnvironmentVariables(executable));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 恢复时发现当前值已经被用户或其他程序改写；这不是失败，而是必须保留的外部状态。
    /// </summary>
    private sealed class StartupStateChangedException : InvalidOperationException
    {
        public StartupStateChangedException(string message)
            : base(message)
        {
        }
    }
}
