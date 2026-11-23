using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using DesktopTwin.Interop;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 关停 / 恢复「开机自启动项」的结果。
/// </summary>
/// <param name="Success">是否已确认没有非例外启动项还能在分身里运行；无法确认时必须失败关闭。</param>
/// <param name="Message">给用户看的结论。</param>
/// <param name="Warnings">关不掉的项、以及需要注意的提示。</param>
/// <param name="StartupExecutables">本次未能确认关停的启动程序；登录阶段按路径直接兜底拦截。</param>
/// <param name="ManagedStartupExecutables">
/// 本次日志涉及的全部启动程序；常驻阶段只拦截由会话 0 服务拉起的副本，
/// 避免厂商服务绕过启动项开关后，在分身里长期留下重复程序。
/// </param>
/// <summary>
/// 启动项枚举快照；仅用于无副作用的隔离流程测试。
/// </summary>
internal sealed record StartupSnoozeSnapshot(
    List<StartupSnoozeRegistryEntry> Registry,
    List<StartupSnoozeTaskEntry> Tasks,
    List<StartupSnoozeUwpEntry> Uwp);

public sealed record StartupSnoozeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> StartupExecutables,
    IReadOnlyList<string> ManagedStartupExecutables)
{
    /// <summary>创建一个成功结果。</summary>
    public static StartupSnoozeResult Ok(
        string message,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? startupExecutables = null,
        IReadOnlyList<string>? managedStartupExecutables = null) =>
        new(
            true,
            message,
            warnings ?? Array.Empty<string>(),
            startupExecutables ?? Array.Empty<string>(),
            managedStartupExecutables ?? startupExecutables ?? Array.Empty<string>());

    /// <summary>创建一个失败结果。</summary>
    public static StartupSnoozeResult Fail(string message, IReadOnlyList<string>? warnings = null) =>
        new(
            false,
            message,
            warnings ?? Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
}

/// <summary>
/// 启动项隔离策略。旧模式保留用于读取和恢复旧日志；正常连接使用
/// <see cref="ExplorerRunPolicies"/>，让可见 Explorer 启动但跳过 Run/RunOnce。
/// </summary>
internal enum StartupIsolationMode
{
    FullRegistry,
    AlternateShell,
    ExplorerRunPolicies
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
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string RunApprovedPath = ApprovedRootPath + @"\Run";
    private const string Run32ApprovedPath = ApprovedRootPath + @"\Run32";
    private const string StartupFolderApprovedPath = ApprovedRootPath + @"\StartupFolder";
    private const string StartMenuPrograms = @"Microsoft\Windows\Start Menu\Programs";
    private const string TaskCacheTreePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
    private const string UwpRootPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string RestoreMutexName = @"Global\DesktopTwin.StartupSnooze.Restore";
    private const string JournalStoreKeyPath = @"SOFTWARE\DesktopTwin\StartupIsolation";
    private const string JournalValuePrefix = "Journal.";
    private const int MaxJournalBytes = 512 * 1024;
    private static readonly TimeSpan RestoreMutexTimeout = TimeSpan.FromSeconds(45);

    internal enum ChildSessionPresence
    {
        Present,
        Absent,
        Unknown
    }

    internal enum ScheduledTaskPresence
    {
        Present,
        Absent,
        Unknown
    }

    internal readonly record struct ScheduledTaskXmlQueryResult(
        ScheduledTaskPresence Presence,
        string? Xml)
    {
        public bool IsPresent => Presence == ScheduledTaskPresence.Present && Xml is not null;

        public bool IsAbsent => Presence == ScheduledTaskPresence.Absent;

        public bool IsUnknown => Presence == ScheduledTaskPresence.Unknown;

        public static ScheduledTaskXmlQueryResult Present(string xml) =>
            new(ScheduledTaskPresence.Present, xml);

        public static ScheduledTaskXmlQueryResult Absent =>
            new(ScheduledTaskPresence.Absent, null);

        public static ScheduledTaskXmlQueryResult Unknown =>
            new(ScheduledTaskPresence.Unknown, null);
    }

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly TimeSpan DefaultPresenceConfirmationDelay =
        TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly bool _useMachineJournal;
    private readonly string? _journalValueName;
    private readonly string? _legacyJournalPath;
    private readonly Func<ChildSessionPresence> _childSessionPresenceProvider;
    private readonly TimeSpan _presenceConfirmationDelay;
    private readonly Func<StartupIsolationMode, List<string>> _refusalReasonsProvider;
    private readonly Func<StartupSnoozeSnapshot>? _startupSnapshotProvider;
    private readonly ICurrentUserShellStore _shellStore;
    private readonly IExplorerRunPolicyStore _policyStore;
    private bool _appliedInThisInstance;

    /// <summary>
    /// 创建服务。
    /// </summary>
    /// <param name="directory">
    /// 测试用数据目录。生产环境传 <see langword="null"/>，关停日志写入仅管理员可写的
    /// <c>HKLM\SOFTWARE\DesktopTwin\StartupIsolation</c>，不读取用户可写的 AppData。
    /// </param>
    public StartupSnoozeService(string? directory = null)
        : this(directory, childSessionPresence: null)
    {
    }

    /// <summary>
    /// 创建可注入分身会话存在性查询的测试实例。生产环境必须走公开构造函数，
    /// 由 WTS 查询真实子会话；测试数据目录默认按“不存在”处理。
    /// </summary>
    internal StartupSnoozeService(
        string? directory,
        Func<ChildSessionPresence>? childSessionPresence,
        TimeSpan? presenceConfirmationDelay = null,
        Func<bool, List<string>>? refusalReasonsProvider = null,
        Func<StartupSnoozeSnapshot>? startupSnapshotProvider = null,
        ICurrentUserShellStore? shellStore = null,
        IExplorerRunPolicyStore? policyStore = null,
        Func<StartupIsolationMode, List<string>>? isolationRefusalReasonsProvider = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        _useMachineJournal = directory is null;
        _childSessionPresenceProvider = childSessionPresence
            ?? (_useMachineJournal
                ? QueryNativeChildSessionPresence
                : static () => ChildSessionPresence.Absent);
        _presenceConfirmationDelay = presenceConfirmationDelay ?? DefaultPresenceConfirmationDelay;
        _refusalReasonsProvider = isolationRefusalReasonsProvider
            ?? (refusalReasonsProvider is null
                ? GetRefusalReasons
                : mode => refusalReasonsProvider(mode == StartupIsolationMode.AlternateShell));
        _startupSnapshotProvider = startupSnapshotProvider;
        _shellStore = shellStore ?? new CurrentUserShellStore();
        _policyStore = policyStore ?? new ExplorerRunPolicyStore();
        _journalValueName = _useMachineJournal ? BuildJournalValueName() : null;
        _legacyJournalPath = _useMachineJournal
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopTwin",
                "startup-snooze.json")
            : null;
        JournalPath = _useMachineJournal
            ? @"HKLM\" + JournalStoreKeyPath + @"\" + _journalValueName
            : Path.Combine(Directory, "startup-snooze.json");
        ExceptionsPath = Path.Combine(Directory, "startup-snooze-exceptions.json");
    }

    /// <summary>测试数据目录；生产环境仅用于例外清单和诊断输出。</summary>
    public string Directory { get; }

    /// <summary>关停日志位置；生产环境是当前用户的 HKLM 注册表值。</summary>
    public string JournalPath { get; }

    /// <summary>例外清单路径（写入被系统拒绝的项，用户明确知情后可放行）。</summary>
    public string ExceptionsPath { get; }

    /// <summary>当前是否处于「已关停」状态（以日志存储中是否存在关停记录为准）。</summary>
    public bool IsSnoozed
    {
        get
        {
            if (!_useMachineJournal)
            {
                return File.Exists(JournalPath);
            }

            try
            {
                return ReadMachineJournalBytes() is not null;
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or SecurityException
                                                  or NotSupportedException)
            {
                // 读不出日志时按「有日志」处理，让恢复流程失败关闭，绝不能当作
                // “没有改动过启动项”而直接开始新一轮写入。
                AppLog.Warn("无法读取管理员级启动项关停日志。", exception);
                return true;
            }
        }
    }

    /// <summary>
    /// 上次退出时没来得及恢复（日志还在）——下次启动必须先恢复再连分身。
    /// </summary>
    public bool HasPendingJournal => IsSnoozed;

    /// <summary>
    /// 查询 WTS 中是否仍有子会话。查询失败不能等同于不存在。
    /// </summary>
    internal ChildSessionPresence QueryChildSessionPresence()
    {
        try
        {
            return _childSessionPresenceProvider();
        }
        catch (Exception exception)
        {
            AppLog.Warn("查询分身会话存在性失败，按未知处理并保留启动项关停日志。", exception);
            return ChildSessionPresence.Unknown;
        }
    }

    private static ChildSessionPresence QueryNativeChildSessionPresence()
    {
        if (!ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId))
        {
            return ChildSessionPresence.Unknown;
        }

        return sessionId.HasValue ? ChildSessionPresence.Present : ChildSessionPresence.Absent;
    }

    /// <summary>
    /// 恢复闸门要求的连续缺席次数。WTS 的单次查询可能刚好落在会话销毁或
    /// 新会话创建的过渡窗口里，恢复启动项前必须跨过这个窗口。
    /// </summary>
    internal const int ConfirmedAbsenceScans = 3;

    /// <summary>
    /// 查询子会话存在性；只有连续 <see cref="ConfirmedAbsenceScans"/> 次都返回
    /// <see cref="ChildSessionPresence.Absent"/> 才把结论视为“确认不存在”。
    /// Present/Unknown 立即返回，绝不继续等待或把后续 Absent 当成确认。
    /// </summary>
    private ChildSessionPresence QueryConfirmedChildSessionPresence()
    {
        for (var scan = 0; scan < ConfirmedAbsenceScans; scan++)
        {
            var presence = QueryChildSessionPresence();
            if (presence != ChildSessionPresence.Absent)
            {
                return presence;
            }

            if (scan + 1 < ConfirmedAbsenceScans && _presenceConfirmationDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_presenceConfirmationDelay);
            }
        }

        return ChildSessionPresence.Absent;
    }

    /// <summary>
    /// 新开分身、修改启动项前的闸门：任何已存在或无法确认的分身会话都必须
    /// 失败关闭。这里用单次严格探测配合写入前/后的复检；恢复路径另用三次确认。
    /// </summary>
    private StartupSnoozeResult? GetNewSessionIsolationBlockResult()
    {
        return QueryChildSessionPresence() switch
        {
            ChildSessionPresence.Present => StartupSnoozeResult.Fail(
                "检测到已有分身会话，但没有走旧会话接管流程，已拒绝修改启动项。"
                + "请先关闭已有分身，或使用可核对的关停日志接管它。"),
            ChildSessionPresence.Unknown => StartupSnoozeResult.Fail(
                "无法确认是否已有分身会话，已拒绝修改启动项。请关闭 DesktopTwin 后重试。"),
            ChildSessionPresence.Absent => null,
            _ => StartupSnoozeResult.Fail(
                "分身会话状态返回值无效，已拒绝修改启动项。请关闭 DesktopTwin 后重试。")
        };
    }

    /// <summary>
    /// 隐式恢复启动项前的统一闸门：只有连续确认没有子会话时才能恢复。
    /// </summary>
    internal StartupSnoozeResult? GetRestoreBlockResult()
    {
        return QueryConfirmedChildSessionPresence() switch
        {
            ChildSessionPresence.Present => StartupSnoozeResult.Fail(
                "检测到分身会话仍在运行，已保留启动项关停日志。"
                + "必须先注销该分身，确认会话结束后才能恢复启动项。"),
            ChildSessionPresence.Unknown => StartupSnoozeResult.Fail(
                "无法确认分身会话是否已经结束，已保留启动项关停日志。"
                + "请关闭 DesktopTwin 后稍后重试，确认没有分身会话后才能恢复启动项。"),
            ChildSessionPresence.Absent => null,
            _ => StartupSnoozeResult.Fail(
                "分身会话状态返回值无效，已保留启动项关停日志，拒绝恢复。")
        };
    }

    /// <summary>
    /// 关停本机所有能覆盖的第三方启动源。
    ///
    /// 这是“新开一个分身”的路径：已有子会话时不能把它当成没有会话而重新关停启动项，
    /// 否则会改到一个无法核对来源的旧会话下面。接管旧会话必须走专用方法。
    /// </summary>
    public StartupSnoozeResult EnsureSnoozed() => EnsureSnoozed(
        StartupIsolationMode.FullRegistry);

    /// <summary>
    /// 兼容旧调用的关停入口。<c>false</c> 表示旧的替代 Shell 模式，仅供
    /// 兼容测试与旧日志使用；正常可见桌面必须选择 Explorer Run/RunOnce
    /// 策略模式，让标准 Explorer 启动，同时只跳过 Run/RunOnce。
    /// </summary>
    internal StartupSnoozeResult EnsureSnoozed(bool isolateRegistryStartupEntries) =>
        EnsureSnoozed(isolateRegistryStartupEntries
            ? StartupIsolationMode.FullRegistry
            : StartupIsolationMode.AlternateShell);

    internal StartupSnoozeResult EnsureSnoozed(StartupIsolationMode mode)
    {
        lock (_gate)
        {
            // 会话探测和启动项写入必须在同一把跨进程恢复互斥体内完成。
            // 在锁外先探测、再排队拿锁，会给新会话在空档中出现的窗口。
            return WithRestoreMutex(() => EnsureSnoozedCore(
                adoptExistingJournal: false,
                requireAbsentSession: true,
                mode));
        }
    }

    /// <summary>
    /// 已有一个子会话时接管启动项隔离。只允许复用“已完成且当前状态仍与日志一致”
    /// 的关停日志；没有日志、日志未完成或状态不一致时都必须失败关闭，绝不能在这里恢复。
    /// </summary>
    internal StartupSnoozeResult EnsureSnoozedForExistingChildSession() =>
        EnsureSnoozedForExistingChildSession(StartupIsolationMode.FullRegistry);

    internal StartupSnoozeResult EnsureSnoozedForExistingChildSession(
        bool isolateRegistryStartupEntries) =>
        EnsureSnoozedForExistingChildSession(isolateRegistryStartupEntries
            ? StartupIsolationMode.FullRegistry
            : StartupIsolationMode.AlternateShell);

    internal StartupSnoozeResult EnsureSnoozedForExistingChildSession(
        StartupIsolationMode mode)
    {
        lock (_gate)
        {
            // 接管同样必须在互斥体内重新核对「会话仍在」和「日志仍在」。
            // 否则排队拿锁期间会话可能已经退出，旧日志也可能已被恢复。
            return WithRestoreMutex(() =>
            {
                if (QueryChildSessionPresence() != ChildSessionPresence.Present)
                {
                    return StartupSnoozeResult.Fail(
                        "无法确认已有分身会话仍在运行，已拒绝接管启动项隔离。");
                }

                if (!IsSnoozed)
                {
                    return StartupSnoozeResult.Fail(
                        "检测到已有分身会话，但没有可核对的启动项关停日志。"
                        + "为避免在未知会话下修改启动项，已拒绝连接；请先注销已有分身再重试。");
                }

                return EnsureSnoozedCore(
                    adoptExistingJournal: true,
                    requireAbsentSession: false,
                    mode);
            });
        }
    }

    private StartupSnoozeResult EnsureSnoozedCore(
        bool adoptExistingJournal,
        bool requireAbsentSession,
        StartupIsolationMode mode)
    {
        var isolateRegistryStartupEntries = mode == StartupIsolationMode.FullRegistry;
        var explorerPolicies = mode == StartupIsolationMode.ExplorerRunPolicies;
        var alternateShell = mode == StartupIsolationMode.AlternateShell;
        if (requireAbsentSession)
        {
            var presenceBlock = GetNewSessionIsolationBlockResult();
            if (presenceBlock is not null)
            {
                return presenceBlock;
            }
        }

        if (_legacyJournalPath is not null && File.Exists(_legacyJournalPath) && !IsSnoozed)
        {
            AppLog.Warn("发现旧版用户可写目录中的启动项关停日志，拒绝把它当作提权恢复指令：" + _legacyJournalPath);
            return StartupSnoozeResult.Fail(
                "发现旧版启动项关停日志，但其完整性无法验证，已拒绝连接。"
                + "请由管理员先核对并恢复当前启动项，再删除旧日志："
                + _legacyJournalPath);
        }

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
                // 旧会话仍存在时恢复半成品日志，会让启动项在被使用的会话下重新打开。
                // 这里必须保留日志，等会话结束后由显式恢复路径按原值收尾。
                if (adoptExistingJournal)
                {
                    AppLog.Warn("检测到已有分身会话，但上次启动项关停日志未完成；保留日志并拒绝接管。");
                    return StartupSnoozeResult.Fail(
                        "检测到已有分身会话，但上次启动项关停未完成。为避免在会话仍运行时重新打开启动项，"
                        + "已保留关停日志并拒绝连接；请先注销该分身，再重新启动 DesktopTwin。");
                }

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
                if (GetJournalIsolationMode(existing) != mode)
                {
                    if (adoptExistingJournal)
                    {
                        AppLog.Warn("已有分身会话，但关停日志的隔离模式与本次连接不一致；保留日志并拒绝接管。");
                        return StartupSnoozeResult.Fail(
                            "检测到已有分身会话，但启动项隔离模式与上次运行不一致。"
                            + "为避免在会话仍运行时改动启动项，已保留关停日志并拒绝连接；"
                            + "请先注销该分身，再重新启动 DesktopTwin。");
                    }

                    AppLog.Warn("关停日志的隔离模式与本次连接不一致，先恢复旧模式原值。");
                    var modeRecovery = RestoreCore();
                    if (!modeRecovery.Success)
                    {
                        return StartupSnoozeResult.Fail(
                            "启动项隔离模式已变化，且恢复旧模式失败：" + modeRecovery.Message,
                            modeRecovery.Warnings);
                    }

                    return EnsureSnoozedCore(
                        adoptExistingJournal: false,
                        requireAbsentSession: requireAbsentSession,
                        mode);
                }

                if (!_appliedInThisInstance && !adoptExistingJournal)
                {
                    return StartupSnoozeResult.Fail(
                        "发现上次运行留下的已完成关停日志。为避免把已失效的开关当成隔离成功，"
                        + "必须先按日志恢复原启动项，再重新关停。");
                }

                if (!IsCompletedJournalStillApplied(existing))
                {
                    // 接管旧会话时绝不能恢复：即使日志显示“已完成”，当前系统状态变了，
                    // 也可能意味着有启动程序已经在旧会话里跑起来。
                    if (adoptExistingJournal)
                    {
                        AppLog.Warn("已有分身会话，但已完成关停日志与当前系统状态不一致；保留日志并拒绝接管。");
                        return StartupSnoozeResult.Fail(
                            "检测到已有分身会话，但已完成关停日志与当前系统状态不一致。"
                            + "为避免在会话仍运行时重新打开启动项，已保留关停日志并拒绝连接；"
                            + "请先注销该分身，再重新启动 DesktopTwin。");
                    }

                    AppLog.Warn("已完成关停日志与当前系统状态不一致，先恢复原值后重新隔离。");
                    var recovery = RestoreCore();
                    if (!recovery.Success)
                    {
                        return StartupSnoozeResult.Fail(
                            "启动项状态已发生变化，且恢复原值失败：" + recovery.Message,
                            recovery.Warnings);
                    }
                }

                if (!_appliedInThisInstance)
                {
                    if (!adoptExistingJournal)
                    {
                        return StartupSnoozeResult.Fail(
                            "发现上次运行留下的已完成关停日志。为避免把已失效的开关当成隔离成功，"
                            + "必须先按日志恢复原启动项，再重新关停。");
                    }

                    _appliedInThisInstance = true;
                    AppLog.Info("检测到旧分身会话和仍匹配的已完成关停日志，已接管启动项隔离。");
                }

                if (!isolateRegistryStartupEntries)
                {
                    try
                    {
                        var additions = _startupSnapshotProvider is null
                            ? CollectRegistryGuardExecutables(existing.Warnings)
                            : _startupSnapshotProvider().Registry
                                .Select(ResolveGuardExecutable)
                                .Where(executable => !string.IsNullOrWhiteSpace(executable))
                                .Select(executable => executable!);
                        if (AppendGuardExecutables(existing.DeferredExecutables, additions))
                        {
                            // 新的启动项守卫目标只影响已在运行的子会话，
                            // 不改变任何启动项的审批值或恢复原值。
                            SaveJournal(existing);
                            AppLog.Info("已为已完成关停日志补充启动项守卫目标。");
                        }
                    }
                    catch (Exception exception) when (exception is SecurityException
                                                          or UnauthorizedAccessException
                                                          or IOException or InvalidOperationException
                                                          or ArgumentException or NotSupportedException)
                    {
                        AppLog.Warn("补充启动项守卫目标失败，拒绝在当前子会话继续连接。", exception);
                        return StartupSnoozeResult.Fail(
                            "无法补充启动项守卫目标，已拒绝连接：" + exception.Message);
                    }
                }

                return StartupSnoozeResult.Ok(
                    "启动项已处于关停状态。",
                    startupExecutables: NormalizeStartupExecutables(existing),
                    managedStartupExecutables: NormalizeManagedStartupExecutables(existing));
            }
        }

        var refusals = _refusalReasonsProvider(mode);
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
            var warnings = new List<string>();
            var blockedStartupItems = new List<string>();
            var exceptions = LoadExceptions();
            journal.Warnings = warnings;

            try
            {
                if (_startupSnapshotProvider is null)
                {
                    journal.Registry = isolateRegistryStartupEntries
                        ? CollectRegistryEntries(
                            warnings,
                            journal.DeferredExecutables,
                            exceptions,
                            blockedStartupItems)
                        : explorerPolicies
                            ? CollectRegistryEntries(
                                warnings,
                                journal.DeferredExecutables,
                                exceptions,
                                blockedStartupItems,
                                includeRunValues: false)
                            : [];
                    journal.Tasks = CollectScheduledTasks();
                    journal.Uwp = CollectUwpTasks();
                    if (!isolateRegistryStartupEntries)
                    {
                        // Explorer 策略和替代外壳都不会执行 Run/RunOnce 启动链；
                        // 但厂商服务仍可能在登录后直接拉起同一程序的辅助进程。
                        // 这里只读枚举启动项作为常驻守卫目标，不改第三方启动值。
                        AppendGuardExecutables(
                            journal.DeferredExecutables,
                            CollectRegistryGuardExecutables(warnings));
                    }
                }
                else
                {
                    var snapshot = _startupSnapshotProvider();
                    journal.Registry = isolateRegistryStartupEntries
                        ? snapshot.Registry
                        : explorerPolicies
                            ? snapshot.Registry
                                .Where(entry => string.Equals(
                                    entry.Kind,
                                    "StartupFolderItem",
                                    StringComparison.Ordinal))
                                .ToList()
                            : [];
                    journal.Tasks = snapshot.Tasks;
                    journal.Uwp = snapshot.Uwp;
                    if (!isolateRegistryStartupEntries)
                    {
                        AppendGuardExecutables(
                            journal.DeferredExecutables,
                            snapshot.Registry
                                .Select(ResolveGuardExecutable)
                                .Where(executable => !string.IsNullOrWhiteSpace(executable))
                                .Select(executable => executable!));
                    }
                }
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                AppLog.Warn("枚举启动项失败，未做任何修改。", exception);
                return StartupSnoozeResult.Fail("读取启动项失败，未做任何修改：" + exception.Message);
            }

            if (explorerPolicies)
            {
                try
                {
                    journal.ExplorerRunPolicies = CaptureExplorerRunPolicies();
                }
                catch (Exception exception) when (exception is SecurityException
                                                      or UnauthorizedAccessException
                                                      or IOException or InvalidOperationException
                                                      or ArgumentException or NotSupportedException)
                {
                    AppLog.Warn("读取 Explorer Run/RunOnce 策略失败，未做任何修改。", exception);
                    return StartupSnoozeResult.Fail(
                        "无法安全接管 Explorer 启动项策略，未做任何修改：" + exception.Message);
                }
            }

            // 替代 shell 模式必须先把当前用户 Shell 的原值写入日志，再改注册表。
            // 这里只接管当前用户 Winlogon，机器级 HKLM Winlogon 永不触碰；可见
            // Explorer 模式绝不修改 Shell，否则会再次得到黑屏桌面。
            if (alternateShell)
            {
                try
                {
                    journal.Shell = CaptureShellOverride();
                }
                catch (Exception exception) when (exception is SecurityException
                                                      or UnauthorizedAccessException
                                                      or IOException or InvalidOperationException
                                                      or ArgumentException or NotSupportedException)
                {
                    AppLog.Warn("读取当前用户 Winlogon Shell 失败，未做任何修改。", exception);
                    return StartupSnoozeResult.Fail(
                        "无法安全接管当前用户登录外壳，未做任何修改：" + exception.Message);
                }
            }

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

            DropUncoveredEntries(journal, warnings, blockedStartupItems, exceptions);
            DropExceptionEntries(journal, warnings, exceptions);
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

            if (requireAbsentSession)
            {
                // 枚举启动项可能耗时。日志已经保存，但还没有写任何系统状态；
                // 此时若子会话出现，必须保留日志并失败关闭，不能继续关停或连接。
                var preMutationBlock = GetNewSessionIsolationBlockResult();
                if (preMutationBlock is not null)
                {
                    AppLog.Warn("关停启动项前检测到子会话出现或状态未知，已保留关停日志并取消连接。");
                    return StartupSnoozeResult.Fail(
                        preMutationBlock.Message + " 启动项隔离尚未执行，关停日志已保留。",
                        warnings);
                }
            }

            if (journal.Registry.Count == 0
                && journal.Tasks.Count == 0
                && journal.Uwp.Count == 0
                && journal.ExplorerRunPolicies.Count == 0
                && journal.Shell is null)
            {
                if (blockedStartupItems.Count > 0)
                {
                    blockedStartupItems = blockedStartupItems
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var blockedReason = BuildBlockedReason(blockedStartupItems);
                    AppLog.Warn(blockedReason);
                    var blockedRollback = RestoreCore();
                    return StartupSnoozeResult.Fail(
                        blockedReason + "。启动项回滚：" + blockedRollback.Message,
                        warnings.Concat(blockedStartupItems).ToArray());
                }

                if (requireAbsentSession)
                {
                    // 空清单不写系统状态，但仍要在返回成功前做最后一次探测；
                    // 否则会话在枚举期间出现时，连接流程会把“没有启动项”误当成
                    // 可以安全继续。
                    var emptyJournalBlock = GetNewSessionIsolationBlockResult();
                    if (emptyJournalBlock is not null)
                    {
                        AppLog.Warn("空启动项清单完成前检测到子会话出现或状态未知，已取消连接。");
                        return StartupSnoozeResult.Fail(
                            emptyJournalBlock.Message + " 空关停日志已保留，未连接分身。",
                            warnings);
                    }
                }

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

                // 空清单同样由本次实例完成并落盘；否则同一实例的重试会把它
                // 误认成上次运行遗留的日志并拒绝继续。
                _appliedInThisInstance = true;
                return StartupSnoozeResult.Ok(
                    "没有需要关停的启动项。",
                    warnings,
                    NormalizeStartupExecutables(journal),
                    NormalizeManagedStartupExecutables(journal));
            }

            try
            {
                if (isolateRegistryStartupEntries)
                {
                    ApplyRegistryEntries(journal, warnings, blockedStartupItems);
                }

                ApplyScheduledTasks(journal, warnings, blockedStartupItems);
                ApplyUwpTasks(journal, warnings, blockedStartupItems);
                if (explorerPolicies)
                {
                    ApplyExplorerRunPolicies(journal);
                }

                // Shell 必须最后写入：前面的任务/UWP 开关失败时，登录外壳尚未
                // 被改掉，回滚范围更小；成功时它负责阻止子会话启动 Explorer。
                if (alternateShell)
                {
                    ApplyShellOverride(journal);
                }
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

            if (requireAbsentSession)
            {
                // 写入系统状态期间子会话可能刚刚出现。此时启动项可能已经在
                // 分身里被触发，不能再把隔离当成成功并继续连接；保留未完成
                // 日志（否则恢复时会丢失原始状态），等确认会话结束后再恢复。
                var postWriteBlock = GetNewSessionIsolationBlockResult();
                if (postWriteBlock is not null)
                {
                    journal.Warnings = warnings;
                    journal.Completed = false;
                    var persistProblem = string.Empty;
                    try
                    {
                        SaveJournal(journal);
                    }
                    catch (Exception exception) when (exception is IOException
                                                          or UnauthorizedAccessException
                                                          or System.Security.SecurityException)
                    {
                        persistProblem = " 此外，保存未完成关停日志失败：" + exception.Message;
                        AppLog.Error("保存子会话出现后的未完成关停日志失败。", exception);
                    }

                    AppLog.Warn(
                        "关停启动项期间检测到子会话出现或状态未知，已保留日志并取消连接。"
                        + persistProblem);
                    return StartupSnoozeResult.Fail(
                        postWriteBlock.Message
                        + " 启动项关停可能已经写入；未完成日志已保留，"
                        + "请在确认分身会话结束后再恢复启动项。" + persistProblem,
                        warnings);
                }
            }

            blockedStartupItems = blockedStartupItems
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blockedStartupItems.Count > 0)
            {
                // 只要还有一个非例外启动项可能进入子会话，就不能把“部分关停”
                // 当成隔离成功。继续连接会让单实例程序把主桌面窗口拉出来，
                // 因此先按日志回滚，再让连接流程失败关闭。
                var reason = BuildBlockedReason(blockedStartupItems);
                AppLog.Warn(reason);
                var rollback = RestoreCore();
                return StartupSnoozeResult.Fail(
                    reason + "。启动项回滚：" + rollback.Message,
                    warnings.Concat(blockedStartupItems).ToArray());
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
                journal.Shell is null
                    ? "已隔离启动项：注册表 {0} 项、Explorer Run/RunOnce 策略 {1} 项、计划任务 {2} 项、UWP {3} 项。"
                    : "已隔离启动项：注册表 {0} 项、Explorer Run/RunOnce 策略 {1} 项、计划任务 {2} 项、UWP {3} 项；已接管当前用户登录外壳。",
                journal.Registry.Count(entry => entry.Applied),
                journal.ExplorerRunPolicies.Count(entry => entry.WriteConfirmed),
                journal.Tasks.Count(entry => entry.ConfirmedDisabled),
                journal.Uwp.Count(entry => entry.WrittenState == 0));
            AppLog.Info(summary + (warnings.Count == 0 ? string.Empty : "；未能关停：" + string.Join("；", warnings)));
            _appliedInThisInstance = true;
            return StartupSnoozeResult.Ok(
                summary,
                warnings,
                NormalizeStartupExecutables(journal),
                NormalizeManagedStartupExecutables(journal));
    }

    /// <summary>
    /// 按日志把启动项原样恢复。
    /// </summary>
    public StartupSnoozeResult Restore()
    {
        lock (_gate)
        {
            return WithRestoreMutex(RestoreCore);
        }
    }

    /// <summary>
    /// 会话边界与恢复动作在同一个跨进程互斥体内完成：确认连续缺席后，
    /// 不允许新子会话在恢复前插入，避免“检查通过后会话出现、启动项却被重新打开”。
    /// </summary>
    internal StartupSnoozeResult RestoreIfChildAbsent()
    {
        lock (_gate)
        {
            return WithRestoreMutex(() => GetRestoreBlockResult() ?? RestoreCore());
        }
    }

    /// <summary>
    /// 所有会修改启动项或恢复日志的路径都必须经过这里。隔离与恢复共用同一把
    /// 跨进程互斥体，独立恢复助手才不会和主进程的同时写操作交错。
    /// </summary>
    private static StartupSnoozeResult WithRestoreMutex(Func<StartupSnoozeResult> action) =>
        WithRestoreMutex(action, message => StartupSnoozeResult.Fail(message));

    private static T WithRestoreMutex<T>(Func<T> action, Func<string, T> failureFactory)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, RestoreMutexName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or IOException
                                              or NotSupportedException)
        {
            return failureFactory("无法创建启动项恢复互斥体：" + exception.Message);
        }

        using (mutex)
        {
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(RestoreMutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个恢复进程在持锁时退出。日志和系统状态都按幂等
                    // 恢复处理，接管互斥体比永远等待更安全。
                    acquired = true;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or IOException
                                                  or NotSupportedException
                                                  or ObjectDisposedException)
            {
                return failureFactory("等待启动项恢复互斥体失败：" + exception.Message);
            }

            if (!acquired)
            {
                return failureFactory(
                    "另一个 DesktopTwin 恢复进程仍在操作启动项，已拒绝并发修改。");
            }

            try
            {
                return action();
            }
            finally
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 锁已经不归当前线程时不能让退出路径再次失败。
                }
            }
        }
    }

    private bool IsCompletedJournalStillApplied(StartupSnoozeJournal journal)
    {
        try
        {
            foreach (var entry in journal.Registry.Where(item => item.Applied))
            {
                if (string.IsNullOrWhiteSpace(entry.WrittenBase64))
                {
                    return false;
                }

                using var baseKey = RegistryKey.OpenBaseKey(
                    ParseHive(entry.Hive),
                    ParseView(entry.ApprovedView));
                using var key = baseKey.OpenSubKey(entry.KeyPath, writable: false);
                var current = ReadApprovalValue(
                    key,
                    entry.ValueName,
                    out var exists,
                    out var problem);
                if (problem is not null || !exists || current is null)
                {
                    return false;
                }

                if (!current.SequenceEqual(Convert.FromBase64String(entry.WrittenBase64)))
                {
                    return false;
                }
            }

            foreach (var task in journal.Tasks.Where(item => item.ConfirmedDisabled))
            {
                var query = QueryTaskXml(task.Relative);
                if (!query.IsPresent
                    || ScheduledTaskXml.GetEnabledState(query.Xml) != TaskEnabledState.Disabled
                    || !ScheduledTaskIdentityMatches(journal, task, query.Xml!))
                {
                    return false;
                }
            }

            foreach (var uwp in journal.Uwp.Where(item => item.WriteConfirmed && item.WrittenState == 0))
            {
                using var key = Registry.CurrentUser.OpenSubKey(uwp.KeyPath, writable: false);
                if (key?.GetValue("State", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    is not int state
                    || state != 0)
                {
                    return false;
                }
            }

            if (journal.ExplorerRunPolicies.Count > 0)
            {
                ValidateExplorerRunPolicies(journal.ExplorerRunPolicies);
                foreach (var policy in journal.ExplorerRunPolicies)
                {
                    if (!policy.WriteAttempted || !policy.WriteConfirmed)
                    {
                        return false;
                    }

                    var target = ExplorerRunPolicyTargets.Resolve(
                        policy.Hive,
                        policy.ValueName);
                    var current = _policyStore.Read(target);
                    if (!current.ValueExists
                        || current.Kind != RegistryValueKind.DWord
                        || current.Value != policy.WrittenValue)
                    {
                        return false;
                    }
                }
            }

            if (journal.Shell is not null
                && (!journal.Shell.WriteConfirmed
                    || !IsManagedShellValue(journal.Shell)))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException
                                              or InvalidOperationException
                                              or ArgumentException
                                              or FormatException
                                              or NotSupportedException)
        {
            AppLog.Warn("核对已完成关停日志的当前状态失败，将重新恢复并隔离。", exception);
            return false;
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

            var currentQuery = QueryTaskXml(task.Relative);
            if (!currentQuery.IsPresent)
            {
                if (currentQuery.IsUnknown)
                {
                    // 读取失败时必须保留日志，下一次启动或恢复仍会重试；
                    // 若误判为删除，这个任务会永久保持被关停状态。
                    problems.Add(task.Relative + "：无法读取或解析当前任务定义，未重新启用");
                    continue;
                }

                task.WriteAttempted = false;
                task.ConfirmedDisabled = false;
                journal.Tasks.Remove(task);
                restored++;
                continue;
            }

            var currentXml = currentQuery.Xml!;
            var currentState = ScheduledTaskXml.GetEnabledState(currentXml);
            if (currentState == TaskEnabledState.Unknown)
            {
                // Unknown 既不是启用也不是禁用：不能据此重新启用，也不能丢弃日志。
                problems.Add(task.Relative + "：启用状态无法确定，保留当前状态和恢复日志");
                continue;
            }

            if (!ScheduledTaskIdentityMatches(journal, task, currentXml))
            {
                // 任务已被重建、动作/主体/触发器/注册信息发生变化，或旧日志没有
                // 身份：都不是 DesktopTwin 关停的原任务，绝不能重新启用。
                warnings.Add(task.Relative + "：任务定义已变化或日志缺少身份，保留当前状态");
                AppLog.Warn("计划任务身份不匹配，拒绝重新启用：" + task.Relative);
                task.WriteAttempted = false;
                task.ConfirmedDisabled = false;
                journal.Tasks.Remove(task);
                preserved++;
                continue;
            }

            // 已经是启用状态时不要为了“执行恢复”再跑一次 schtasks：
            // 某些受保护任务即使状态正确也可能拒绝修改，直接回读确认即可。
            if (currentState == TaskEnabledState.Enabled)
            {
                task.WriteAttempted = false;
                task.ConfirmedDisabled = false;
                journal.Tasks.Remove(task);
                restored++;
                continue;
            }

            // 当前是禁用状态，但日志没有证明本次运行准备禁用这个任务：
            // 这是外部修改，不能为了恢复而重新启用。旧版日志没有写入标记，
            // 只有明确回读确认过禁用，才按旧版自己的修改恢复。
            if (!CanRestoreScheduledTask(journal, task))
            {
                warnings.Add(task.Relative + "：任务在 DesktopTwin 关停前已被外部禁用，保留当前状态");
                AppLog.Warn("计划任务被外部禁用，保留当前值：" + task.Relative);
                journal.Tasks.Remove(task);
                preserved++;
                continue;
            }

            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Enable");
            if (exitCode != 0)
            {
                // 用户在分身会话期间删除了任务时，任务已经不可能再启动，
                // 不应因此永久保留日志并让每次启动都报恢复失败。
                var presenceAfterFailure = GetScheduledTaskPresence(task.Relative);
                if (presenceAfterFailure == ScheduledTaskPresence.Absent)
                {
                    task.WriteAttempted = false;
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    restored++;
                    continue;
                }

                // 命令失败后仍可能已经生效；只有身份和开关都回读确认才清日志。
                var afterFailureQuery = QueryTaskXml(task.Relative);
                if (afterFailureQuery.IsAbsent)
                {
                    task.WriteAttempted = false;
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    restored++;
                    continue;
                }

                if (afterFailureQuery.IsUnknown)
                {
                    problems.Add(
                        task.Relative + "：命令失败且无法复核任务状态，保留恢复日志；" + output);
                    continue;
                }

                var afterFailureXml = afterFailureQuery.Xml!;
                if (!ScheduledTaskIdentityMatches(journal, task, afterFailureXml))
                {
                    warnings.Add(task.Relative + "：任务定义已变化，保留当前状态");
                    AppLog.Warn("计划任务身份不匹配，拒绝继续恢复：" + task.Relative);
                    task.WriteAttempted = false;
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    preserved++;
                    continue;
                }

                if (ScheduledTaskXml.GetEnabledState(afterFailureXml) == TaskEnabledState.Enabled)
                {
                    task.WriteAttempted = false;
                    task.ConfirmedDisabled = false;
                    journal.Tasks.Remove(task);
                    restored++;
                    continue;
                }

                problems.Add(task.Relative + "：" + output);
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: true, expectedIdentity: task.Identity))
            {
                problems.Add(task.Relative + "：启用后回读未确认");
                continue;
            }

            task.WriteAttempted = false;
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

        // Explorer 策略先于 Shell 恢复：任务/UWP 已恢复后，再撤掉 Run/RunOnce
        // 开关。可见桌面模式没有 Shell 记录，因此这里只操作四个精确 DWORD。
        foreach (var policy in journal.ExplorerRunPolicies.ToArray())
        {
            try
            {
                if (RestoreExplorerRunPolicy(policy))
                {
                    journal.ExplorerRunPolicies.Remove(policy);
                    restored++;
                }
                else
                {
                    journal.ExplorerRunPolicies.Remove(policy);
                    preserved++;
                    warnings.Add(
                        "Explorer 策略在恢复前已被外部修改，已保留当前值："
                        + policy.Hive + @"\" + policy.ValueName);
                }
            }
            catch (Exception exception) when (exception is SecurityException
                                                  or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                problems.Add("Explorer Run 策略：" + exception.Message);
            }
        }

        // Shell 最后恢复：先恢复 Run/任务/UWP，再让 Explorer 回到登录外壳，
        // 尽量缩短子会话仍存在时启动项重新可用的窗口。
        if (journal.Shell is not null)
        {
            try
            {
                RestoreShellOverride(journal.Shell);
                journal.Shell = null;
                restored++;
            }
            catch (Exception exception) when (exception is SecurityException
                                                  or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                problems.Add("当前用户 Winlogon Shell：" + exception.Message);
            }
        }

        if (problems.Count == 0)
        {
            try
            {
                DeleteJournalStorage();
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
        _appliedInThisInstance = false;
        if (warnings.Count > 0)
        {
            AppLog.Warn("恢复时保留了外部修改：" + string.Join("；", warnings));
        }

        return StartupSnoozeResult.Ok(message, warnings);
    }

    /// <summary>
    /// 启动时调用：上次没恢复完就补一次，返回给用户的提示（无需提示时为 null）。
    /// </summary>
    public StartupSnoozeResult? RecoverOnStartup()
    {
        lock (_gate)
        {
            if (_legacyJournalPath is not null && File.Exists(_legacyJournalPath) && !HasPendingJournal)
            {
                AppLog.Warn("发现无法验证完整性的旧版启动项关停日志，拒绝按其内容恢复：" + _legacyJournalPath);
                return StartupSnoozeResult.Fail(
                    "发现旧版启动项关停日志，但其完整性无法验证，已拒绝执行恢复。"
                    + "请由管理员先核对并恢复启动项，再删除旧日志："
                    + _legacyJournalPath);
            }

            if (!HasPendingJournal)
            {
                return null;
            }

            return WithRestoreMutex<StartupSnoozeResult?>(
                RecoverOnStartupCore,
                message => StartupSnoozeResult.Fail(message));
        }
    }

    private StartupSnoozeResult? RecoverOnStartupCore()
    {
        if (!IsSnoozed)
        {
            return null;
        }

        switch (QueryConfirmedChildSessionPresence())
        {
            case ChildSessionPresence.Present:
                // 崩溃恢复会刻意保留断线的子会话。此时恢复启动项，会让下次登录
                // 在该会话里重新拉起启动程序；日志必须原样保留给接管流程核对。
                AppLog.Info("检测到上次的分身会话仍未结束，保留启动项关停日志，等待连接时接管。");
                return null;

            case ChildSessionPresence.Unknown:
                AppLog.Warn("无法确认上次的分身会话是否已经结束，保留启动项关停日志并拒绝恢复。");
                return StartupSnoozeResult.Fail(
                    "无法确认上次的分身会话是否已经结束，已保留启动项关停日志，不会重新打开启动项。"
                    + "请稍后重试；若持续失败，请关闭 DesktopTwin 并检查系统会话状态。");

            case ChildSessionPresence.Absent:
                AppLog.Warn("确认没有分身会话，正在按上次未完成的日志恢复启动项。");
                return RestoreCore();

            default:
                AppLog.Warn("分身会话状态返回值无效，保留启动项关停日志并拒绝恢复。");
                return StartupSnoozeResult.Fail(
                    "分身会话状态返回值无效，已保留启动项关停日志，不会重新打开启动项。"
                    + "请关闭 DesktopTwin 后重试。");
        }
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

    /// <summary>
    /// Run 键的空值名就是注册表默认值 <c>(Default)</c>，Windows 同样会启动它，
    /// 因此只有 null 是非法/不可收集项，空字符串必须保留。
    /// </summary>
    internal static bool IsCollectableRunValueName(string? valueName) => valueName is not null;

    /// <summary>
    /// Policies\Explorer\Run 只要存在任何值（包括空名字的默认值）或子键，就属于
    /// 未覆盖的组策略启动面，必须阻断，不能因为值名是空字符串而漏掉。
    /// </summary>
    internal static bool HasPolicyStartupEntries(
        IReadOnlyCollection<string> valueNames,
        IReadOnlyCollection<string> subKeyNames) =>
        valueNames.Count > 0 || subKeyNames.Count > 0;

    private static List<StartupSnoozeRegistryEntry> CollectRegistryEntries(
        List<string> warnings,
        List<string> deferredExecutables,
        HashSet<string> exceptions,
        List<string> blockedStartupItems,
        bool includeRunValues = true)
    {
        var entries = new List<StartupSnoozeRegistryEntry>();

        if (includeRunValues)
        {
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
                // 空字符串是 Run 的默认值名，同样会被 Windows 启动；只过滤 null。
                if (!IsCollectableRunValueName(valueName))
                {
                    continue;
                }

                var commandLine = Convert.ToString(
                    runKey.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    CultureInfo.InvariantCulture);

                var approvalPath = FindApprovalPath(target, valueName);
                var originalBytes = ReadApprovalBytes(
                    target.Hive,
                    target.ApprovedView,
                    approvalPath,
                    valueName,
                    out var exists,
                    out var approvalProblem);

                if (exists && approvalProblem is not null)
                {
                    // 非 REG_BINARY 或读不到值类型时，绝不能按“原值不存在”处理：
                    // 否则后续写入会覆盖它，恢复时还会把原本存在的值删掉。
                    RecordUnsupportedApproval(
                        warnings,
                        deferredExecutables,
                        blockedStartupItems,
                        exceptions,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}|{1}|{2}",
                            target.HiveName,
                            target.ApprovedView,
                            valueName),
                        "RunValue",
                        valueName,
                        commandLine,
                        approvalProblem);
                    continue;
                }

                // 已经关掉的不要重复关：恢复时也就不用动它。
                if (exists)
                {
                    var approvalState = StartupApproval.Classify(originalBytes);
                    if (approvalState is StartupApprovalState.Disabled
                        or StartupApprovalState.DisabledLocked)
                    {
                        continue;
                    }

                    // 0x08 是策略锁定启用，未知值也不能猜测。若强行写 0x03，
                    // 既可能被系统回滚，也可能破坏组策略/安全软件维护的状态。
                    if (approvalState != StartupApprovalState.Enabled)
                    {
                        RecordUnsupportedApproval(
                            warnings,
                            deferredExecutables,
                            blockedStartupItems,
                            exceptions,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}|{1}|{2}",
                                target.HiveName,
                                target.ApprovedView,
                                valueName),
                            "RunValue",
                            valueName,
                            commandLine,
                            approvalState == StartupApprovalState.EnabledLocked
                                ? "启动项被策略锁定为启用，无法安全关闭"
                                : "启动项审批值状态未知，拒绝覆盖");
                        continue;
                    }
                }

                // 没有审批值时，只有「已证明目标不存在」才能跳过：Windows 本就不会
                // 启动失效项，写开关反而会把它变成启用。路径不可验证/解析失败属于
                // 「无法证明不存在」，必须阻断，绝不能静默跳过。
                if (!exists)
                {
                    var presence = ProveExecutablePresence(commandLine);
                    if (presence == ExecutablePresence.Missing)
                    {
                        continue;
                    }

                    if (presence == ExecutablePresence.Unknown)
                    {
                        RecordUnsupportedApproval(
                            warnings,
                            deferredExecutables,
                            blockedStartupItems,
                            exceptions,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}|{1}|{2}",
                                target.HiveName,
                                target.ApprovedView,
                                valueName),
                            "RunValue",
                            valueName,
                            commandLine,
                            "无法证明启动程序是否存在（命令解析失败或路径不可验证）");
                        continue;
                    }
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
        }

        entries.AddRange(CollectStartupFolderEntries(
            warnings,
            deferredExecutables,
            exceptions,
            blockedStartupItems));
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
        out bool exists,
        out string? problem)
    {
        exists = false;
        problem = null;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return ReadApprovalValue(key, valueName, out exists, out problem);
    }

    private static byte[]? ReadApprovalValue(
        RegistryKey? key,
        string valueName,
        out bool exists,
        out string? problem)
    {
        exists = false;
        problem = null;
        if (key is null
            || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        exists = true;
        try
        {
            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return ClassifyApprovalValue(kind, raw, out problem);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException
                                              or ArgumentException)
        {
            problem = "审批值读取失败：" + exception.Message;
            return null;
        }
    }

    private static byte[]? ClassifyApprovalValue(
        RegistryValueKind kind,
        object? raw,
        out string? problem)
    {
        problem = null;
        if (kind != RegistryValueKind.Binary)
        {
            problem = "审批值不是 REG_BINARY";
            return null;
        }

        if (raw is byte[] bytes)
        {
            return bytes;
        }

        problem = "审批值读取结果为空";
        return null;
    }

    private static bool IsWrittenValue(byte[]? current, byte[]? written)
    {
        return current is not null && written is not null && current.SequenceEqual(written);
    }

    private static void RecordUnsupportedApproval(
        List<string> warnings,
        List<string> deferredExecutables,
        List<string> blockedStartupItems,
        HashSet<string> exceptions,
        string key,
        string kind,
        string valueName,
        string? commandLine,
        string problem)
    {
        if (exceptions.Contains(key))
        {
            // 例外项本来就要留在子会话里启动；审批值格式异常也不能
            // 反过来把它加入阻断清单或守卫名单。
            warnings.Add(valueName + "：已按例外清单保持启用（审批值无法处理：" + problem + "）");
            return;
        }

        warnings.Add(valueName + "：" + problem + "，保持原值");
        AppLog.Warn("启动项审批值无法安全处理：" + valueName + " -> " + problem);

        var executable = ResolveGuardExecutable(kind, commandLine);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            deferredExecutables.Add(executable);
        }
        else
        {
            AppLog.Warn("启动项审批值无法安全处理，且无法解析出兜底程序：" + valueName);
        }

        // 例外清单允许该启动项保持原样。其余审批值无法读写的项都必须
        // 阻止连接，不能只靠事后守卫把已经启动的主桌面单实例程序杀回去。
        if (!exceptions.Contains(key))
        {
            blockedStartupItems.Add(
                valueName
                + (string.IsNullOrWhiteSpace(executable)
                    ? "（" + commandLine + "）"
                    : "（" + executable + "）"));
        }
    }

    /// <summary>
    /// 已确认会由会话 0 厂商服务额外拉起的产品组件。只登记启动器同目录下的
    /// 固定相对路径，绝不按进程名、父进程名或安装目录前缀猜测。新增条目必须
    /// 先有实际子会话进程和归属证据；例如联想的 MessagingPlugin.exe 属于系统
    /// 协同服务，不是当前用户启动项，不能混入此表。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> ServiceSpawnCompanionFiles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["GameViewer.exe"] =
            [
                System.IO.Path.Combine("bin", "GameViewerServer.exe"),
                System.IO.Path.Combine("bin", "crashpad_handler.exe")
            ]
        };

    /// <summary>
    /// 替代外壳模式下只读收集启动项指向的精确 EXE。这里刻意不读取
    /// StartupApproved、不探测写权限、不生成待恢复条目；审批值即使被策略锁定
    /// 或无法识别，也只能影响 Explorer 启动链，不能因此拒绝整个分身连接。
    /// 收集结果只用于拦截会话 0 服务绕过外壳后在子会话里拉起的副本。
    /// </summary>
    private static List<string> CollectRegistryGuardExecutables(List<string> warnings)
    {
        var executables = new List<string>();
        var runKeyPaths = new[]
        {
            RunKeyPath,
            RunOnceKeyPath,
            RunOnceKeyPath + @"\Setup",
            @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run"
        };

        foreach (var target in RunTargets())
        {
            foreach (var runKeyPath in runKeyPaths)
            {
                using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.RunView);
                using var runKey = baseKey.OpenSubKey(runKeyPath, writable: false);
                if (runKey is null)
                {
                    continue;
                }

                foreach (var valueName in runKey.GetValueNames())
                {
                    if (!IsCollectableRunValueName(valueName))
                    {
                        continue;
                    }

                    var commandLine = Convert.ToString(
                        runKey.GetValue(
                            valueName,
                            string.Empty,
                            RegistryValueOptions.DoNotExpandEnvironmentNames),
                        CultureInfo.InvariantCulture);
                    AddGuardExecutable(
                        executables,
                        warnings,
                        "RunValue",
                        commandLine,
                        target.HiveName + @"\" + runKeyPath + @"\" + valueName);
                }
            }
        }

        foreach (var folder in StartupFolders())
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

                AddGuardExecutable(
                    executables,
                    warnings,
                    "StartupFolderItem",
                    file,
                    folder.HiveName + @"\" + StartupFolderApprovedPath + @"\" + fileName);
            }
        }

        return executables;
    }

    private static (string Path, RegistryHive Hive, string HiveName)[] StartupFolders() => new[]
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

    private static void AddGuardExecutable(
        List<string> executables,
        List<string> warnings,
        string kind,
        string? commandLine,
        string description)
    {
        var executable = ResolveGuardExecutable(kind, commandLine);
        if (string.IsNullOrWhiteSpace(executable))
        {
            var warning = description + "：无法解析出精确 EXE，无法加入服务拉起的副本守卫。";
            if (!warnings.Contains(warning, StringComparer.Ordinal))
            {
                warnings.Add(warning);
                AppLog.Warn(warning);
            }

            return;
        }

        executables.Add(executable);
        AppendGuardExecutables(
            executables,
            GetServiceSpawnCompanionExecutables(executable));
    }

    /// <summary>
    /// 返回启动器所对应厂商服务的精确伙伴程序。仅使用固定相对路径，不枚举
    /// 安装目录；路径不存在时仍然保留，以便拦截登录后才释放的组件。
    /// </summary>
    internal static IReadOnlyList<string> GetServiceSpawnCompanionExecutables(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Array.Empty<string>();
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executable.Trim().Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or SecurityException)
        {
            AppLog.Warn("无法解析启动项伙伴程序目录。", exception);
            return Array.Empty<string>();
        }

        if (!ServiceSpawnCompanionFiles.TryGetValue(Path.GetFileName(fullPath), out var relativePaths))
        {
            return Array.Empty<string>();
        }

        var productDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(productDirectory))
        {
            return Array.Empty<string>();
        }

        var companions = new List<string>(relativePaths.Length);
        foreach (var relativePath in relativePaths)
        {
            try
            {
                companions.Add(Path.GetFullPath(Path.Combine(productDirectory, relativePath)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException or SecurityException)
            {
                AppLog.Warn(
                    "无法解析启动项伙伴程序路径：" + fullPath + " -> " + relativePath,
                    exception);
            }
        }

        return companions;
    }

    /// <summary>追加去重后的精确可执行文件；返回是否真的新增了目标。</summary>
    private static bool AppendGuardExecutables(List<string> target, IEnumerable<string> additions)
    {
        var known = new HashSet<string>(
            target.Where(executable => !string.IsNullOrWhiteSpace(executable)),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition))
            {
                continue;
            }

            var executable = addition.Trim();
            if (known.Add(executable))
            {
                target.Add(executable);
                changed = true;
            }
        }

        return changed;
    }

    private static List<StartupSnoozeRegistryEntry> CollectStartupFolderEntries(
        List<string> warnings,
        List<string> deferredExecutables,
        HashSet<string> exceptions,
        List<string> blockedStartupItems)
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

                var bytes = ReadFolderApprovalBytes(
                    folder.Hive,
                    fileName,
                    out var exists,
                    out var approvalProblem);
                if (exists && approvalProblem is not null)
                {
                    RecordUnsupportedApproval(
                        warnings,
                        deferredExecutables,
                        blockedStartupItems,
                        exceptions,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}|{1}|{2}",
                            folder.HiveName,
                            "Registry64",
                            fileName),
                        "StartupFolderItem",
                        fileName,
                        file,
                        approvalProblem);
                    continue;
                }

                if (exists)
                {
                    var approvalState = StartupApproval.Classify(bytes);
                    if (approvalState is StartupApprovalState.Disabled
                        or StartupApprovalState.DisabledLocked)
                    {
                        continue;
                    }

                    if (approvalState != StartupApprovalState.Enabled)
                    {
                        RecordUnsupportedApproval(
                            warnings,
                            deferredExecutables,
                            blockedStartupItems,
                            exceptions,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}|{1}|{2}",
                                folder.HiveName,
                                "Registry64",
                                fileName),
                            "StartupFolderItem",
                            fileName,
                            file,
                            approvalState == StartupApprovalState.EnabledLocked
                                ? "启动文件夹项被策略锁定为启用，无法安全关闭"
                                : "启动文件夹项审批值状态未知，拒绝覆盖");
                        continue;
                    }
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

    private static byte[]? ReadFolderApprovalBytes(
        RegistryHive hive,
        string valueName,
        out bool exists,
        out string? problem)
    {
        exists = false;
        problem = null;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(StartupFolderApprovedPath, writable: false);
        return ReadApprovalValue(key, valueName, out exists, out problem);
    }

    private static List<StartupSnoozeTaskEntry> CollectScheduledTasks()
    {
        var entries = new List<StartupSnoozeTaskEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var tree = baseKey.OpenSubKey(TaskCacheTreePath, writable: false);
        if (tree is null)
        {
            // 读不到计划任务树就无法证明没有登录启动任务。此时继续连接会
            // 让任务栏 / 托盘程序在分身里重复启动，因此必须按隔离失败处理。
            throw new InvalidOperationException(
                "无法读取计划任务注册表，无法确认登录启动任务是否已关停。");
        }

        Collect(tree, string.Empty, depth: 0);
        return entries;

        void Collect(RegistryKey node, string relative, int depth)
        {
            if (depth > 32)
            {
                // 正常任务路径远达不到这个深度；超过它说明注册表结构异常，
                // 不能静默跳过可能存在的登录启动任务。
                throw new InvalidOperationException("计划任务树嵌套过深，拒绝在覆盖不完整时继续。");
            }

            var isTask = node.GetValue("Id") is not null;
            if (isTask && !IsMicrosoftSystemTaskPath(relative))
            {
                var query = QueryTaskXml(relative);
                // TaskCache 里可能残留已删除任务的 Id。只有定义文件已在受保护
                // 路径上被确认删除时才会得到 Absent；此时它不可能在登录时启动，
                // 忽略该节点但仍继续遍历子键。
                if (!query.IsAbsent)
                {
                    if (!query.IsPresent)
                    {
                        // TaskCache 声称任务存在，但定义文件状态无法确认时就读
                        // 不到 XML，也就无法判断它是否会在登录/会话切换时启动
                        // 程序。静默跳过会把重复启动留给分身，因此按「无法完整
                        // 隔离」拒绝继续。
                        throw new InvalidOperationException($"无法读取计划任务 XML：{relative}");
                    }

                    var xml = query.Xml!;
                    var identity = ScheduledTaskXml.GetIdentity(xml);
                    if (identity is null)
                    {
                        // 读到了字符串但解析失败时，不能把任务当成“未启用”跳过。
                        throw new InvalidOperationException($"无法解析计划任务 XML：{relative}");
                    }

                    switch (ScheduledTaskIsolation.Classify(xml))
                    {
                        case ScheduledTaskIsolationDecision.Block:
                            // Settings/Enabled 缺失以外的未知值、空值、重复元素或
                            // XML 语义异常都无法证明任务是否会在登录时启动。这里
                            // 必须失败关闭，不能把 Unknown 当成「已禁用」跳过。
                            throw new InvalidOperationException(
                                $"无法确定计划任务的启用状态：{relative}");

                        case ScheduledTaskIsolationDecision.Isolate:
                        {
                            var trimmed = relative.TrimStart('\\');
                            var slash = trimmed.LastIndexOf('\\');
                            entries.Add(new StartupSnoozeTaskEntry
                            {
                                Relative = relative,
                                TaskName = slash < 0 ? trimmed : trimmed.Substring(slash + 1),
                                TaskPath = slash < 0 ? "\\" : "\\" + trimmed.Substring(0, slash) + "\\",
                                Identity = identity,
                                Executable = ScheduledTaskXml.GetExecutable(xml),
                                OriginalEnabled = true
                            });
                            break;
                        }
                    }
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

    /// <summary>
    /// Windows 自带计划任务所在的显式文件夹清单。只有这些文件夹下的任务才被
    /// 视为系统任务而排除，不能按 <c>\Microsoft\</c> 前缀一刀切：Office、
    /// Microsoft.Office.Desktop、Microsoft.YourPhone 及任意用户安装的
    /// Microsoft.* 软件同样使用该命名空间，必须照常隔离。未列出的路径一律
    /// 不排除（失败关闭），宁可多关一个用户任务，也不能漏掉登录启动项。
    /// </summary>
    private static readonly string[] WindowsSystemTaskFolders =
    {
        @"\Microsoft\Windows\AppID",
        @"\Microsoft\Windows\Application Experience",
        @"\Microsoft\Windows\ApplicationData",
        @"\Microsoft\Windows\AppxDeploymentClient",
        @"\Microsoft\Windows\Autochk",
        @"\Microsoft\Windows\BitLocker",
        @"\Microsoft\Windows\Bluetooth",
        @"\Microsoft\Windows\CertificateServicesClient",
        @"\Microsoft\Windows\Chkdsk",
        @"\Microsoft\Windows\CloudExperienceHost",
        @"\Microsoft\Windows\CloudRestore",
        @"\Microsoft\Windows\Customer Experience Improvement Program",
        @"\Microsoft\Windows\Data Integrity Scan",
        @"\Microsoft\Windows\Defrag",
        @"\Microsoft\Windows\Device Information",
        @"\Microsoft\Windows\Device Setup",
        // 设备注册 COM 任务由系统在用户登录时调用，不是用户启动程序；
        // 其任务 ACL 只允许 SYSTEM 修改，隔离它只会让子会话连接失败。
        @"\Microsoft\Windows\DeviceDirectoryClient",
        @"\Microsoft\Windows\Diagnosis",
        @"\Microsoft\Windows\DiskCleanup",
        @"\Microsoft\Windows\DiskDiagnostic",
        @"\Microsoft\Windows\Feedback",
        @"\Microsoft\Windows\File Classification Infrastructure",
        @"\Microsoft\Windows\FileHistory",
        @"\Microsoft\Windows\Flighting",
        @"\Microsoft\Windows\Input",
        @"\Microsoft\Windows\InstallService",
        @"\Microsoft\Windows\LanguageComponentsInstaller",
        @"\Microsoft\Windows\Location",
        @"\Microsoft\Windows\Maintenance",
        @"\Microsoft\Windows\Management",
        @"\Microsoft\Windows\MapUpdate",
        @"\Microsoft\Windows\MemoryDiagnostic",
        @"\Microsoft\Windows\Mobile Broadband Accounts",
        @"\Microsoft\Windows\MUI",
        @"\Microsoft\Windows\NetTrace",
        @"\Microsoft\Windows\Network Accessibility",
        @"\Microsoft\Windows\NlaSvc",
        @"\Microsoft\Windows\PI",
        @"\Microsoft\Windows\Plug and Play",
        @"\Microsoft\Windows\Policy",
        @"\Microsoft\Windows\Power Efficiency Diagnostics",
        @"\Microsoft\Windows\Recovery",
        @"\Microsoft\Windows\Registry",
        @"\Microsoft\Windows\Servicing",
        @"\Microsoft\Windows\SettingSync",
        @"\Microsoft\Windows\Shell",
        @"\Microsoft\Windows\Software Inventory Logging",
        @"\Microsoft\Windows\SoftwareProtectionPlatform",
        @"\Microsoft\Windows\SpacePort",
        @"\Microsoft\Windows\Sysmain",
        @"\Microsoft\Windows\SystemRestore",
        @"\Microsoft\Windows\Task Manager",
        @"\Microsoft\Windows\Time Synchronization",
        @"\Microsoft\Windows\TPM",
        @"\Microsoft\Windows\UNP",
        @"\Microsoft\Windows\UpdateOrchestrator",
        @"\Microsoft\Windows\UPnP",
        @"\Microsoft\Windows\User Profile Service",
        @"\Microsoft\Windows\WDI",
        @"\Microsoft\Windows\Windows Defender",
        @"\Microsoft\Windows\Windows Error Reporting",
        @"\Microsoft\Windows\Windows Filtering Platform",
        @"\Microsoft\Windows\Windows Media Sharing",
        @"\Microsoft\Windows\Windows Update",
        @"\Microsoft\Windows\WindowsBackup",
        @"\Microsoft\Windows\WindowsUpdate",
        @"\Microsoft\Windows\Wininet",
        @"\Microsoft\Windows\WlanSvc",
        @"\Microsoft\Windows\Work Folders",
        @"\Microsoft\Windows\Workplace Join",
        @"\Microsoft\Windows\WS",
        @"\Microsoft\Windows\WwanSvc",
        @"\Microsoft\Windows\XblGameSave",
        @"\Microsoft\Windows\Xbox"
    };

    /// <summary>
    /// 判断是否为不能关停的 Windows 系统任务。只认可显式清单里的 Windows
    /// 系统任务文件夹，以及根目录下 Explorer 用于阻止提权启动的
    /// <c>\CreateExplorerShellUnelevatedTask</c>。Office、Microsoft.YourPhone、
    /// Microsoft.Office.Desktop 等用户软件任务不在清单内，必须照常隔离。
    /// </summary>
    internal static bool IsMicrosoftSystemTaskPath(string relative)
    {
        if (string.Equals(
                relative,
                @"\CreateExplorerShellUnelevatedTask",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var folder in WindowsSystemTaskFolders)
        {
            if (relative.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 只有同时具备「原状态为启用」「写入前已记日志」「严格回读确认已禁用」
    /// 「版本 3 稳定身份」的证据，才允许恢复时重新启用。仅有 WriteAttempted
    /// （写成败未知）或旧版日志都必须保持禁用，绝不能把外部关闭误当成本程序写入。
    /// </summary>
    private static bool CanRestoreScheduledTask(
        StartupSnoozeJournal journal,
        StartupSnoozeTaskEntry task) =>
        journal.Version >= 3
        && task.OriginalEnabled
        && task.WriteAttempted
        && task.ConfirmedDisabled
        && ScheduledTaskXml.IsValidIdentity(task.Identity);

    /// <summary>
    /// 只有版本 3 日志且记录的身份与当前 XML 完全一致时，才允许把任务视为
    /// DesktopTwin 关停过的原任务。旧日志没有身份，必须失败关闭。
    /// </summary>
    private static bool ScheduledTaskIdentityMatches(
        StartupSnoozeJournal journal,
        StartupSnoozeTaskEntry task,
        string currentXml)
    {
        if (journal.Version < 3 || !ScheduledTaskXml.IsValidIdentity(task.Identity))
        {
            return false;
        }

        var currentIdentity = ScheduledTaskXml.GetIdentity(currentXml);
        return currentIdentity is not null
               && string.Equals(task.Identity, currentIdentity, StringComparison.Ordinal);
    }

    private static ScheduledTaskPresence GetScheduledTaskPresence(string relative)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var tree = baseKey.OpenSubKey(TaskCacheTreePath, writable: false);
            if (tree is null)
            {
                return ScheduledTaskPresence.Unknown;
            }

            using var task = tree.OpenSubKey(relative.TrimStart('\\'), writable: false);
            if (task is null)
            {
                return ScheduledTaskPresence.Absent;
            }

            // TaskCache 中每个任务都带 Id。同名键存在却缺少 Id 属于异常缓存，
            // 不能据此删除恢复日志。
            return task.GetValue("Id") is not null
                ? ScheduledTaskPresence.Present
                : ScheduledTaskPresence.Unknown;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("无法确认计划任务是否仍存在：" + relative, exception);
            return ScheduledTaskPresence.Unknown;
        }
    }

    internal static ScheduledTaskXmlQueryResult InterpretFailedTaskXmlQuery(
        ScheduledTaskPresence presence,
        bool definitionFileConfirmedMissing = false) =>
        presence == ScheduledTaskPresence.Absent
        || (presence == ScheduledTaskPresence.Present && definitionFileConfirmedMissing)
            ? ScheduledTaskXmlQueryResult.Absent
            : ScheduledTaskXmlQueryResult.Unknown;

    /// <summary>
    /// TaskCache 偶尔会留住已经被删除的任务（例如系统升级后的内置任务）。
    /// 只有从受保护的任务目录根开始逐级确认路径中没有重解析点、且最终定义文件
    /// 明确不存在时，才允许把这种缓存残留当成“任务不存在”。读不到目录、
    /// 路径异常或发现重解析点时一律返回 <see langword="false"/>，继续失败关闭。
    /// </summary>
    internal static bool IsTaskDefinitionFileConfirmedMissing(
        string relativeTaskPath,
        string? taskRoot = null)
    {
        if (string.IsNullOrWhiteSpace(relativeTaskPath))
        {
            return false;
        }

        var trimmed = relativeTaskPath.Trim().TrimStart('\\');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var parts = trimmed.Split('\\', StringSplitOptions.None);
        var invalidChars = Path.GetInvalidFileNameChars();
        if (parts.Any(part =>
                part.Length == 0
                || part == "."
                || part == ".."
                || part.IndexOfAny(invalidChars) >= 0))
        {
            return false;
        }

        var root = Path.GetFullPath(
            taskRoot ?? Path.Combine(Environment.SystemDirectory, "Tasks"));
        try
        {
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                current = Path.Combine(current, parts[i]);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            var definitionPath = Path.Combine(current, parts[^1]);
            try
            {
                _ = File.GetAttributes(definitionPath);
                return false;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // 中间目录本身不存在时，定义文件必然不存在；上面的逐级检查已经
            // 排除了已存在目录中的重解析点。
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            AppLog.Warn("无法确认计划任务定义文件是否存在：" + relativeTaskPath, exception);
            return false;
        }
    }

    internal static ScheduledTaskXmlQueryResult ParseTaskXmlQueryOutput(string? output)
    {
        var trimmed = output?.Trim().TrimStart('\uFEFF');
        // schtasks 的成功输出声称 XML 是 UTF-16，实际却按 OEM 代码页写字节。
        // ScheduledTaskXml 会安全移除声明并用禁用 DTD 的严格解析器校验；
        // 空输出、截断、错误命名空间或格式错误都按“读不到定义”处理。
        return trimmed is not null && ScheduledTaskXml.GetIdentity(trimmed) is not null
            ? ScheduledTaskXmlQueryResult.Present(trimmed)
            : ScheduledTaskXmlQueryResult.Unknown;
    }

    private static ScheduledTaskXmlQueryResult QueryTaskXml(string relative)
    {
        var (exitCode, output) = RunSchtasks("/Query", "/XML", "ONE", "/TN", relative);
        if (exitCode != 0)
        {
            var presence = GetScheduledTaskPresence(relative);
            var definitionMissing = presence == ScheduledTaskPresence.Present
                                    && IsTaskDefinitionFileConfirmedMissing(relative);
            AppLog.Warn(
                $"查询计划任务定义失败，TaskCache 状态={presence}，定义文件"
                + (definitionMissing ? "确认缺失" : "状态未知")
                + "：" + relative + "；" + output);
            return InterpretFailedTaskXmlQuery(presence, definitionMissing);
        }

        return ParseTaskXmlQueryOutput(output);
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
                && UwpStartupState.IsEnabled(state))
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

    private static string BuildBlockedReason(IReadOnlyList<string> blockedStartupItems)
    {
        var application = Environment.ProcessPath ?? "DesktopTwin.exe";
        return "启动项隔离不完整，已取消连接："
            + string.Join("；", blockedStartupItems)
            + "。如果系统或安全软件拦截，请只允许精确程序 "
            + application
            + " 修改启动项和结束分身内启动程序；不要放行被拦截的启动程序，也不要关闭安全软件自保护。";
    }

    private void ApplyRegistryEntries(
        StartupSnoozeJournal journal,
        List<string> warnings,
        List<string> blockedStartupItems)
    {
        var disabledBytes = StartupApproval.CreateDisabled(DateTime.UtcNow);
        var disabledBase64 = Convert.ToBase64String(disabledBytes);
        foreach (var entry in journal.Registry)
        {
            try
            {
                if (entry.OriginalExists
                    && !StartupApproval.CanDisable(Convert.FromBase64String(
                        entry.OriginalBase64
                        ?? throw new InvalidDataException("关停日志缺少原始注册表值"))))
                {
                    throw new InvalidOperationException(
                        "启动项审批值已不是可安全关闭的用户启用状态");
                }

                using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));

                // 枚举和真正写入之间可能隔了一段时间。先比对收集时的快照，
                // 外部已经改过的值绝不覆盖；该启动项保持启用并由子会话守卫兜底。
                using var existingKey = baseKey.OpenSubKey(entry.KeyPath, writable: false);
                var snapshotProblem = CompareRegistrySnapshot(existingKey, entry);
                if (snapshotProblem is not null)
                {
                    warnings.Add(entry.ValueName + "：" + snapshotProblem + "，保持原值");
                    AppLog.Warn("启动项在关停前已发生变化，跳过写入：" + entry.ValueName + " -> " + snapshotProblem);
                    if (!IsRegistryEntryDisabledNow(entry))
                    {
                        blockedStartupItems.Add(entry.ValueName + "（外部修改后仍可能启动）");
                    }
                    continue;
                }

                using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("无法创建键 " + entry.KeyPath);

                // 取得可写句柄后、日志标记和实际 SetValue 之前再核对一次，
                // 尽量缩小外部程序在这个窗口内修改启动项的机会。
                snapshotProblem = CompareRegistrySnapshot(key, entry);
                if (snapshotProblem is not null)
                {
                    warnings.Add(entry.ValueName + "：" + snapshotProblem + "，保持原值");
                    AppLog.Warn("启动项在关停前已发生变化，跳过写入：" + entry.ValueName + " -> " + snapshotProblem);
                    if (!IsRegistryEntryDisabledNow(entry))
                    {
                        blockedStartupItems.Add(entry.ValueName + "（外部修改后仍可能启动）");
                    }
                    continue;
                }

                // 先把「准备写入的值」落盘，再改系统。若进程恰好在这两步之间崩溃，
                // 恢复逻辑也能认出当前值是不是桌面分身写的，而不是盲目覆盖。
                entry.WrittenBase64 = disabledBase64;
                entry.Applied = false;
                SaveJournal(journal);

                key.SetValue(entry.ValueName, disabledBytes, RegistryValueKind.Binary);

                // SetValue 不抛异常不等于系统真的接受了写入：自保护驱动可能静默
                // 回滚。必须回读确认状态确实为关闭（含策略锁定关闭），否则该启动项
                // 继续保持启用，要在子会话里用守卫兜底。
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
                blockedStartupItems.Add(entry.ValueName + "（" + exception.Message + "）");
                TryRestoreRegistryEntryQuietly(entry);
            }
        }
    }

    private static bool IsRegistryEntryDisabledNow(StartupSnoozeRegistryEntry entry)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
            using var key = baseKey.OpenSubKey(entry.KeyPath, writable: false);
            var current = ReadApprovalValue(key, entry.ValueName, out var exists, out var problem);
            return IsApprovalConfirmedDisabled(exists, problem, current);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException
                                              or ArgumentException)
        {
            AppLog.Warn("无法确认启动项是否仍处于关闭状态：" + entry.ValueName, exception);
            return false;
        }
    }

    internal static bool IsApprovalConfirmedDisabled(
        bool exists,
        string? problem,
        byte[]? current) =>
        problem is null && exists && StartupApproval.IsDisabled(current);

    private static string? CompareRegistrySnapshot(
        RegistryKey? key,
        StartupSnoozeRegistryEntry entry)
    {
        var current = ReadApprovalValue(
            key,
            entry.ValueName,
            out var exists,
            out var problem);
        if (problem is not null)
        {
            return problem;
        }

        if (!entry.OriginalExists)
        {
            return exists ? "原值本不存在，但当前出现了同名值" : null;
        }

        if (!exists)
        {
            return "原值已不存在";
        }

        var original = Convert.FromBase64String(
            entry.OriginalBase64 ?? throw new InvalidDataException("关停日志缺少原始注册表值"));
        return current is not null && current.SequenceEqual(original)
            ? null
            : "原值已被外部修改";
    }

    private void ApplyScheduledTasks(
        StartupSnoozeJournal journal,
        List<string> warnings,
        List<string> blockedStartupItems)
    {
        foreach (var task in journal.Tasks)
        {
            var snapshotQuery = QueryTaskXml(task.Relative);
            if (!snapshotQuery.IsPresent)
            {
                task.WriteAttempted = false;
                if (snapshotQuery.IsUnknown)
                {
                    // 查询失败与任务不存在必须区分。把失败当成不存在会跳过
                    // 关停，让任务继续在分身登录时执行。
                    throw new InvalidOperationException(
                        "无法读取或解析计划任务的当前定义：" + task.Relative);
                }

                warnings.Add(task.Relative + "：任务在关停前已不存在，保持原状");
                AppLog.Warn("计划任务在关停前已不存在，跳过写入：" + task.Relative);
                continue;
            }

            var snapshotXml = snapshotQuery.Xml!;
            var snapshotState = ScheduledTaskXml.GetEnabledState(snapshotXml);
            if (snapshotState == TaskEnabledState.Disabled)
            {
                task.WriteAttempted = false;
                warnings.Add(task.Relative + "：任务在关停前已被外部禁用，保持原状");
                AppLog.Warn("计划任务在关停前已被外部禁用，跳过写入：" + task.Relative);
                continue;
            }

            if (snapshotState == TaskEnabledState.Unknown)
            {
                // 未知状态既不能当成启用去写，也不能当成禁用而跳过；只能阻断。
                throw new InvalidOperationException(
                    "计划任务的启用状态无法确定，拒绝在覆盖不完整时继续：" + task.Relative);
            }

            if (!ScheduledTaskIdentityMatches(journal, task, snapshotXml))
            {
                task.WriteAttempted = false;
                warnings.Add(task.Relative + "：任务定义在关停前已变化，保持原状");
                AppLog.Warn("计划任务定义在关停前已变化，拒绝写入：" + task.Relative);
                blockedStartupItems.Add(task.Relative + "（任务定义在关停前已变化）");
                continue;
            }

            // SaveJournal 会刷盘，可能给外部程序留下几百毫秒的窗口。写盘后
            // 在真正执行 /Disable 前再读一次定义；若任务刚被用户或应用禁用，
            // 绝不能把它记成 DesktopTwin 的改动，否则恢复时会错误地重新启用。
            var beforeDisableQuery = QueryTaskXml(task.Relative);
            if (!beforeDisableQuery.IsPresent)
            {
                if (beforeDisableQuery.IsAbsent)
                {
                    task.WriteAttempted = false;
                    warnings.Add(task.Relative + "：任务在关停前已不存在，保持原状");
                    AppLog.Warn("计划任务在关停前已不存在，跳过写入：" + task.Relative);
                    continue;
                }

                throw new InvalidOperationException(
                    "无法在关停前复核计划任务定义：" + task.Relative);
            }

            var beforeDisableXml = beforeDisableQuery.Xml!;
            var beforeDisableState = ScheduledTaskXml.GetEnabledState(beforeDisableXml);
            if (beforeDisableState == TaskEnabledState.Disabled)
            {
                task.WriteAttempted = false;
                warnings.Add(task.Relative + "：任务在关停前已被外部禁用，保持原状");
                AppLog.Warn("计划任务在关停前已被外部禁用，跳过写入：" + task.Relative);
                continue;
            }

            if (beforeDisableState == TaskEnabledState.Unknown)
            {
                throw new InvalidOperationException(
                    "计划任务的启用状态无法确定，拒绝在覆盖不完整时继续：" + task.Relative);
            }

            if (!ScheduledTaskIdentityMatches(journal, task, beforeDisableXml))
            {
                task.WriteAttempted = false;
                warnings.Add(task.Relative + "：任务定义在关停前已变化，保持原状");
                AppLog.Warn("计划任务定义在关停前已变化，拒绝写入：" + task.Relative);
                blockedStartupItems.Add(task.Relative + "（任务定义在关停前已变化）");
                continue;
            }

            task.WriteAttempted = true;
            SaveJournal(journal);
            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Disable");
            if (exitCode != 0)
            {
                // schtasks 可能在改动生效后仍返回失败（超时、被安全软件拦截输出、
                // 退出码异常）。直接清掉 WriteAttempted 会让回滚跳过这条任务，
                // 启动项被永远留在禁用状态。这里必须重新查询：只有身份仍匹配
                // 且 Settings/Enabled 明确为 false 时才保留所有权；查询 Unknown
                // 绝不能当作“安全未启用”，只能清标记并让本轮失败关闭。
                var postFailureQuery = QueryTaskXml(task.Relative);
                if (postFailureQuery.IsPresent
                    && ScheduledTaskXml.IsDisabled(postFailureQuery.Xml)
                    && ScheduledTaskIdentityMatches(journal, task, postFailureQuery.Xml!))
                {
                    task.ConfirmedDisabled = false;
                    task.WriteAttempted = true;
                    SaveJournal(journal);
                    warnings.Add(task.Relative + "：" + output + "（已复核为禁用，保留恢复日志）");
                    AppLog.Warn(
                        "禁用计划任务命令失败但状态已确认禁用：" + task.Relative + " -> " + output);
                    blockedStartupItems.Add(task.Relative + "（" + output + "）");
                    continue;
                }

                task.WriteAttempted = false;
                task.ConfirmedDisabled = false;
                SaveJournal(journal);
                warnings.Add(task.Relative + "：" + output);
                AppLog.Warn("禁用计划任务失败：" + task.Relative + " -> " + output);
                blockedStartupItems.Add(task.Relative + "（" + output + "）");
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: false, expectedIdentity: task.Identity))
            {
                warnings.Add(task.Relative + "：禁用后回读未确认");
                blockedStartupItems.Add(task.Relative + "（禁用后回读未确认）");
                continue;
            }

            task.ConfirmedDisabled = true;
            SaveJournal(journal);
        }
    }

    private void ApplyUwpTasks(
        StartupSnoozeJournal journal,
        List<string> warnings,
        List<string> blockedStartupItems)
    {
        foreach (var uwp in journal.Uwp)
        {
            try
            {
                var snapshotProblem = CompareUwpSnapshot(uwp);
                if (snapshotProblem is not null)
                {
                    warnings.Add(uwp.Task + "：" + snapshotProblem + "，保持原值");
                    AppLog.Warn("UWP 启动项在关停前已发生变化，跳过写入：" + uwp.Task + " -> " + snapshotProblem);
                    if (!IsUwpEntryDisabledNow(uwp))
                    {
                        blockedStartupItems.Add(uwp.Task + "（外部修改后仍可能启动）");
                    }
                    continue;
                }

                using var key = Registry.CurrentUser.OpenSubKey(uwp.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("键不存在");

                snapshotProblem = CompareUwpSnapshot(uwp, key);
                if (snapshotProblem is not null)
                {
                    warnings.Add(uwp.Task + "：" + snapshotProblem + "，保持原值");
                    AppLog.Warn("UWP 启动项在关停前已发生变化，跳过写入：" + uwp.Task + " -> " + snapshotProblem);
                    if (!IsUwpEntryDisabledNow(uwp))
                    {
                        blockedStartupItems.Add(uwp.Task + "（外部修改后仍可能启动）");
                    }
                    continue;
                }

                // 与注册表项相同：先记录准备写入的状态，崩溃后恢复逻辑才能判断
                // 当前 State=0 是本次关停造成的，还是用户/应用自己改的。
                uwp.WrittenState = 0;
                SaveJournal(journal);
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
                uwp.WriteConfirmed = true;
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                warnings.Add(uwp.Task + "：" + exception.Message);
                AppLog.Warn("关闭 UWP 启动任务失败：" + uwp.Task, exception);
                blockedStartupItems.Add(uwp.Task + "（" + exception.Message + "）");
                TryRestoreUwpEntryQuietly(uwp);
            }
        }
    }

    private static bool IsUwpEntryDisabledNow(StartupSnoozeUwpEntry entry)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: false);
            if (key is null)
            {
                return true;
            }

            var current = key.GetValue(
                "State",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return IsUwpStateDisabled(current);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法确认 UWP 启动项是否仍处于关闭状态：" + entry.Task, exception);
            return false;
        }
    }

    internal static bool IsUwpStateDisabled(object? current) =>
        current is int state && state == 0;

    private static string? CompareUwpSnapshot(
        StartupSnoozeUwpEntry entry,
        RegistryKey? key = null)
    {
        using var ownedKey = key is null
            ? Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: false)
            : null;
        key ??= ownedKey;
        if (key is null)
        {
            return "启动项键已不存在";
        }

        var current = key.GetValue(
            "State",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return current is int state && state == entry.OriginalState
            ? null
            : "State 已不是收集时的原值";
    }

    private static void RestoreRegistryEntry(StartupSnoozeRegistryEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));

        // 只有「原值」和「我们写进去的关闭值」可以覆盖。WrittenBase64 是写入前
        // 持久化的精确标记；不能把任意 0x03 值都当成自己的，否则会覆盖用户在
        // DesktopTwin 运行期间手动关闭启动项留下的时间戳。
        var original = entry.OriginalExists
            ? Convert.FromBase64String(
                entry.OriginalBase64 ?? throw new InvalidDataException("关停日志缺少原始注册表值"))
            : null;
        var written = entry.WrittenBase64 is null
            ? null
            : Convert.FromBase64String(entry.WrittenBase64);

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

            if (!IsWrittenValue(current, written))
            {
                throw new StartupStateChangedException("当前启动值不是 DesktopTwin 写入的值，未覆盖外部修改");
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

            if (!IsWrittenValue(currentNow, written))
            {
                throw new StartupStateChangedException("当前启动值不是 DesktopTwin 写入的值，未覆盖外部修改");
            }

            deleteKey.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            if (deleteKey.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            {
                throw new InvalidOperationException("删除后回读仍存在");
            }

            return;
        }

        if (current is not null && original is not null && current.SequenceEqual(original))
        {
            return;
        }

        if (current is null)
        {
            throw new StartupStateChangedException("原启动值已被其他程序删除，未重新创建");
        }

        if (!IsWrittenValue(current, written))
        {
            throw new StartupStateChangedException("当前启动值不是 DesktopTwin 写入的值，未覆盖外部修改");
        }

        using var writeKey = baseKey.OpenSubKey(entry.KeyPath, writable: true)
            ?? throw new StartupStateChangedException("原启动项所在的注册表键已被删除，未重新创建");

        // 写入前再次核对，防止在取得可写句柄期间外部程序已经把值改掉。
        var writableCurrent = writeKey.GetValue(
            entry.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        if (writableCurrent is not null && original is not null && writableCurrent.SequenceEqual(original))
        {
            return;
        }

        if (writableCurrent is null)
        {
            throw new StartupStateChangedException("原启动值已被其他程序删除，未重新创建");
        }

        if (!IsWrittenValue(writableCurrent, written))
        {
            throw new StartupStateChangedException("当前启动值不是 DesktopTwin 写入的值，未覆盖外部修改");
        }

        writeKey.SetValue(entry.ValueName, original!, RegistryValueKind.Binary);
        var restored = writeKey.GetValue(
            entry.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        if (restored is null || original is null || !restored.SequenceEqual(original))
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

        if (!CanRestoreUwpState(state, entry))
        {
            throw new StartupStateChangedException("当前 UWP State 不是 DesktopTwin 写入的值，未覆盖外部修改");
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

        if (writableCurrent is not int writableStateToChange
            || !CanRestoreUwpState(writableStateToChange, entry))
        {
            throw new StartupStateChangedException("当前 UWP State 不是 DesktopTwin 写入的值，未覆盖外部修改");
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

    /// <summary>
    /// 只有「严格回读确认过本程序写入的 0」「原状态确实是启用」「当前值仍是本程序
    /// 写入的 0」三项同时成立，才允许恢复成原值。仅有 WrittenState=0（写入失败或
    /// 旧日志）时必须保持禁用并保留日志，绝不能把外部关闭误当成本程序写入。
    /// </summary>
    internal static bool CanRestoreUwpState(int state, StartupSnoozeUwpEntry entry)
    {
        return state == 0
               && entry.WriteConfirmed
               && entry.WrittenState == 0
               && UwpStartupState.IsEnabled(entry.OriginalState);
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

    // ===== Explorer Run/RunOnce 策略 =====

    private List<StartupSnoozeExplorerRunPolicyEntry> CaptureExplorerRunPolicies()
    {
        var entries = new List<StartupSnoozeExplorerRunPolicyEntry>();
        foreach (var target in _policyStore.Targets)
        {
            var canonical = ExplorerRunPolicyTargets.Resolve(target.Hive, target.ValueName);
            var snapshot = _policyStore.Read(canonical);
            if (snapshot.ValueExists
                && (snapshot.Kind != RegistryValueKind.DWord || snapshot.Value is null))
            {
                throw new InvalidOperationException(
                    "Explorer 策略值已存在但不是 REG_DWORD，拒绝覆盖：" + canonical.DisplayPath);
            }

            entries.Add(new StartupSnoozeExplorerRunPolicyEntry
            {
                Hive = canonical.Hive,
                ValueName = canonical.ValueName,
                OriginalKeyExists = snapshot.KeyExists,
                OriginalExists = snapshot.ValueExists,
                OriginalValue = snapshot.Value,
                WrittenValue = 1
            });
        }

        ValidateExplorerRunPolicies(entries);
        return entries;
    }

    /// <summary>
    /// 四个值全部采集并校验后才允许进入写入阶段。日志先记录“准备写入”，
    /// 再写 DWORD 1 并严格回读；任何一步失败都会走统一回滚。
    /// </summary>
    private void ApplyExplorerRunPolicies(StartupSnoozeJournal journal)
    {
        foreach (var entry in journal.ExplorerRunPolicies)
        {
            var target = ExplorerRunPolicyTargets.Resolve(entry.Hive, entry.ValueName);
            EnsureOriginalExplorerRunPolicyBeforeWrite(entry, target, "写入前");

            entry.WriteAttempted = true;
            entry.WriteConfirmed = false;
            SaveJournal(journal);

            EnsureOriginalExplorerRunPolicyBeforeWrite(entry, target, "日志落盘后");
            _policyStore.WriteDisabledPolicy(target);
            var written = _policyStore.Read(target);
            if (!written.ValueExists
                || written.Kind != RegistryValueKind.DWord
                || written.Value != entry.WrittenValue)
            {
                throw new InvalidOperationException(
                    "写入 Explorer 策略后回读不一致：" + target.DisplayPath);
            }

            entry.WriteConfirmed = true;
            SaveJournal(journal);
        }
    }

    private void EnsureOriginalExplorerRunPolicyBeforeWrite(
        StartupSnoozeExplorerRunPolicyEntry entry,
        ExplorerRunPolicyTarget target,
        string stage)
    {
        var current = _policyStore.Read(target);
        if (!ExplorerRunPolicyMatchesOriginal(entry, current))
        {
            throw new StartupStateChangedException(
                stage + " Explorer 策略已被外部修改：" + target.DisplayPath);
        }
    }

    private bool RestoreExplorerRunPolicy(StartupSnoozeExplorerRunPolicyEntry entry)
    {
        var target = ExplorerRunPolicyTargets.Resolve(entry.Hive, entry.ValueName);
        var current = _policyStore.Read(target);
        if (ExplorerRunPolicyMatchesOriginal(entry, current))
        {
            return true;
        }

        if (!entry.WriteAttempted && !entry.WriteConfirmed)
        {
            throw new InvalidOperationException(
                "Explorer 策略没有本程序的写入记录，拒绝恢复或覆盖当前值："
                + target.DisplayPath);
        }

        if (!ExplorerRunPolicyMatchesWritten(entry, current))
        {
            // 保留第三方写入的值，不能为了“清理”而覆盖用户或管理员的策略。
            return false;
        }

        if (entry.OriginalExists)
        {
            if (entry.OriginalValue is null)
            {
                throw new InvalidDataException(
                    "关停日志中的 Explorer 策略原值无效：" + target.DisplayPath);
            }

            _policyStore.RestorePolicyValue(target, entry.OriginalValue.Value);
            var restored = _policyStore.Read(target);
            if (!ExplorerRunPolicyMatchesOriginal(entry, restored))
            {
                throw new InvalidOperationException(
                    "恢复 Explorer 策略后回读不一致：" + target.DisplayPath);
            }
        }
        else
        {
            _policyStore.DeleteDisabledPolicy(target);
            var restored = _policyStore.Read(target);
            if (restored.ValueExists)
            {
                throw new InvalidOperationException(
                    "删除 Explorer 策略后回读仍存在：" + target.DisplayPath);
            }
        }

        return true;
    }

    private static bool ExplorerRunPolicyMatchesOriginal(
        StartupSnoozeExplorerRunPolicyEntry entry,
        ExplorerRunPolicySnapshot snapshot) =>
        entry.OriginalExists == snapshot.ValueExists
        && (!entry.OriginalExists
            || (snapshot.Kind == RegistryValueKind.DWord
                && snapshot.Value == entry.OriginalValue));

    private static bool ExplorerRunPolicyMatchesWritten(
        StartupSnoozeExplorerRunPolicyEntry entry,
        ExplorerRunPolicySnapshot snapshot) =>
        snapshot.ValueExists
        && snapshot.Kind == RegistryValueKind.DWord
        && snapshot.Value == entry.WrittenValue;

    internal static void ValidateExplorerRunPolicies(
        IReadOnlyCollection<StartupSnoozeExplorerRunPolicyEntry> policies)
    {
        if (policies.Count != 0 && policies.Count != ExplorerRunPolicyTargets.All.Count)
        {
            throw new InvalidDataException("Explorer 策略日志必须记录完整的四个开关。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in policies)
        {
            var target = ExplorerRunPolicyTargets.Resolve(entry.Hive, entry.ValueName);
            if (!seen.Add(target.Hive + @"\" + target.ValueName))
            {
                throw new InvalidDataException("Explorer 策略日志包含重复开关。");
            }

            if (entry.WrittenValue != 1)
            {
                throw new InvalidDataException("Explorer 策略日志的写入值必须为 DWORD 1。");
            }

            if (entry.WriteConfirmed && !entry.WriteAttempted)
            {
                throw new InvalidDataException("Explorer 策略日志缺少写入前记录。");
            }

            if (entry.OriginalExists != entry.OriginalValue.HasValue)
            {
                throw new InvalidDataException("Explorer 策略日志的原值存在标记与内容不一致。");
            }

            if (entry.OriginalExists && !entry.OriginalKeyExists)
            {
                throw new InvalidDataException("Explorer 策略日志的原值与键存在标记不一致。");
            }
        }

        if (seen.Count != policies.Count)
        {
            throw new InvalidDataException("Explorer 策略日志包含未知开关。");
        }
    }

    private static StartupIsolationMode GetJournalIsolationMode(
        StartupSnoozeJournal journal)
    {
        if (journal.ExplorerRunPolicies.Count > 0 && journal.Shell is not null)
        {
            throw new InvalidDataException("关停日志不能同时使用 Explorer 策略和替代 Shell 模式。");
        }

        if (journal.ExplorerRunPolicies.Count > 0)
        {
            return StartupIsolationMode.ExplorerRunPolicies;
        }

        return journal.Shell is not null
            ? StartupIsolationMode.AlternateShell
            : StartupIsolationMode.FullRegistry;
    }

    // ===== 当前用户 Winlogon Shell =====

    /// <summary>
    /// 读取并校验当前用户 Shell，生成可崩溃恢复的接管记录。只允许接管
    /// “不存在/空值”或标准 <c>explorer.exe</c>。没有日志独立证明所有权时，
    /// 即使值恰好等于本程序门卫路径也必须拒绝；任何第三方 Shell 均不覆盖。
    /// </summary>
    private StartupSnoozeShellEntry CaptureShellOverride()
    {
        var snapshot = _shellStore.Read();
        if (snapshot.Exists)
        {
            if (snapshot.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                throw new InvalidDataException("当前用户 Winlogon Shell 不是字符串类型。");
            }

            if (snapshot.Value is null || snapshot.Value.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("当前用户 Winlogon Shell 的值无法安全读取。");
            }

            if (!IsStandardShellValue(snapshot.Value))
            {
                throw new InvalidOperationException(
                    "当前用户 Winlogon Shell 已被其它程序设置，拒绝覆盖：" + snapshot.Value);
            }
        }

        var managed = GetManagedShellPath();
        return new StartupSnoozeShellEntry
        {
            KeyPath = _shellStore.KeyPath,
            ValueName = _shellStore.ValueName,
            OriginalExists = snapshot.Exists,
            OriginalKind = (int)snapshot.Kind,
            OriginalValue = snapshot.Value,
            ManagedValue = managed
        };
    }

    /// <summary>
    /// 在日志里记录“准备写入”后，才允许改 HKCU Winlogon Shell；写完必须
    /// 严格回读 REG_SZ 和完整路径。典型 Shell 值没有空格，但仍使用完整路径，
    /// 避免依赖 PATH 或当前目录。
    /// </summary>
    private void ApplyShellOverride(StartupSnoozeJournal journal)
    {
        var entry = journal.Shell;
        if (entry is null)
        {
            return;
        }

        if (entry.WriteConfirmed)
        {
            if (!IsManagedShellValue(entry))
            {
                throw new InvalidOperationException("已完成日志中的 Winlogon Shell 与接管值不一致。");
            }

            return;
        }

        // 从采集到真正写入之间会经历启动项枚举和日志落盘。写入前必须重新
        // 对比原值，防止期间被第三方 Shell 接管后仍被本程序覆盖。
        EnsureOriginalShellBeforeWrite(entry, "写入前");

        entry.WriteAttempted = true;
        entry.WriteConfirmed = false;
        SaveJournal(journal);

        // 日志落盘本身也可能给外部程序留下修改 Shell 的窗口；紧贴写入前
        // 再做一次精确比较。Windows 注册表没有 compare-and-swap，但任何能
        // 观察到的外部变更都会在这里失败关闭。
        EnsureOriginalShellBeforeWrite(entry, "日志落盘后");

        _shellStore.Write(entry.ManagedValue, RegistryValueKind.String);
        var written = _shellStore.Read();
        if (!written.Exists
            || written.Kind != RegistryValueKind.String
            || !string.Equals(written.Value, entry.ManagedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("写入当前用户 Winlogon Shell 后回读不一致。");
        }

        entry.WriteConfirmed = true;
        SaveJournal(journal);
    }

    /// <summary>
    /// 最后恢复当前用户 Shell。只有“原值完全一致”或“仍是本日志写入的门卫值”
    /// 两种情况允许继续；外部修改时保留日志并失败关闭，绝不覆盖用户的设置。
    /// </summary>
    private void RestoreShellOverride(StartupSnoozeShellEntry entry)
    {
        var current = _shellStore.Read();
        if (IsOriginalShellValue(entry, current))
        {
            return;
        }

        if (!entry.WriteAttempted && !entry.WriteConfirmed)
        {
            throw new InvalidOperationException(
                "Winlogon Shell 没有本程序的写入记录，拒绝恢复或覆盖当前值。");
        }

        if (!IsManagedShellValue(entry, current))
        {
            throw new InvalidOperationException(
                "当前用户 Winlogon Shell 已被外部修改，未覆盖；关停日志已保留。");
        }

        if (entry.OriginalExists)
        {
            if (entry.OriginalValue is null
                || entry.OriginalKind is not ((int)RegistryValueKind.String
                    or (int)RegistryValueKind.ExpandString))
            {
                throw new InvalidDataException("关停日志中的原始 Winlogon Shell 无效。");
            }

            _shellStore.Write(
                entry.OriginalValue,
                (RegistryValueKind)entry.OriginalKind);
            var restored = _shellStore.Read();
            if (!IsOriginalShellValue(entry, restored))
            {
                throw new InvalidOperationException("恢复当前用户 Winlogon Shell 后回读不一致。");
            }

            return;
        }

        _shellStore.Delete();
        var removed = _shellStore.Read();
        if (!IsOriginalShellValue(entry, removed))
        {
            throw new InvalidOperationException("删除当前用户 Winlogon Shell 后回读不一致。");
        }
    }

    private void EnsureOriginalShellBeforeWrite(
        StartupSnoozeShellEntry entry,
        string phase)
    {
        var current = _shellStore.Read();
        if (!IsOriginalShellValue(entry, current))
        {
            throw new InvalidOperationException(
                "当前用户 Winlogon Shell 在接管" + phase + "已被外部修改，未覆盖；关停日志已保留。");
        }
    }

    private static bool IsOriginalShellValue(
        StartupSnoozeShellEntry entry,
        CurrentUserShellSnapshot snapshot)
    {
        if (!entry.OriginalExists)
        {
            return !snapshot.Exists;
        }

        return snapshot.Exists
               && snapshot.Kind == (RegistryValueKind)entry.OriginalKind
               && string.Equals(snapshot.Value, entry.OriginalValue, StringComparison.Ordinal);
    }

    private bool IsManagedShellValue(StartupSnoozeShellEntry entry) =>
        IsManagedShellValue(entry, _shellStore.Read());

    private static bool IsManagedShellValue(
        StartupSnoozeShellEntry entry,
        CurrentUserShellSnapshot snapshot) =>
        snapshot.Exists
        && snapshot.Kind == RegistryValueKind.String
        && string.Equals(snapshot.Value, entry.ManagedValue, StringComparison.Ordinal);

    private static string GetManagedShellPath()
    {
        var path = Path.GetFullPath(SessionAgentService.DefaultShellGatePath);
        if (!string.Equals(
                Path.GetFileName(path),
                SessionAgentService.ShellGateFileName,
                StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(path))
        {
            throw new InvalidOperationException("外壳门卫路径不是受控的绝对路径。");
        }

        return path;
    }

    private static bool IsStandardShellValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }
        else if (candidate.Contains('"'))
        {
            return false;
        }

        if (string.Equals(candidate, "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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
                "explorer.exe");
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

    // ===== 拒绝条件 =====

    private List<string> GetRefusalReasons(StartupIsolationMode mode)
    {
        var reasons = new List<string>();
        var ignoreExplorerRunSources = mode == StartupIsolationMode.AlternateShell;

        // 可见 Explorer 模式下，四个 Run/RunOnce 策略只覆盖标准值；策略
        // Run、RunOnceEx 与 RunOnce\Setup 等其它外壳启动源没有等价开关，
        // 必须像全注册表模式一样在连接前拒绝。只有替代 Shell 不启动 Explorer，
        // 才不检查这些来源。
        if (!ignoreExplorerRunSources)
        {
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
                // 空字符串是 RunOnce 的默认值名，和普通值名一样可能承载启动命令，
                // 不能跳过；只过滤理论上的 null。
                if (name is null)
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
                // 默认值（名字为空字符串）同样属于策略启动项，必须阻断。
                if (policies is not null
                    && HasPolicyStartupEntries(
                        policies.GetValueNames(),
                        policies.GetSubKeyNames()))
                {
                    reasons.Add("组策略启动项 " + hive + "|" + view + @"\Policies\Explorer\Run");
                }
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

            if (winlogon.GetValueNames().Any(name =>
                    string.Equals(name, "Shell", StringComparison.OrdinalIgnoreCase)))
            {
                var shellKind = winlogon.GetValueKind("Shell");
                var shellRaw = winlogon.GetValue(
                    "Shell",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (shellKind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
                    || shellRaw is not string shell)
                {
                    reasons.Add("Winlogon Shell 类型或值无效");
                }
                else if (!IsStandardShellValue(shell)
                         && !(hive == RegistryHive.CurrentUser
                              && mode == StartupIsolationMode.AlternateShell
                              && string.Equals(
                                  shell.Trim(),
                                  GetManagedShellPath(),
                                  StringComparison.Ordinal)))
                {
                    reasons.Add("Winlogon Shell 被改动");
                }
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
    private static string? ResolveGuardExecutable(StartupSnoozeRegistryEntry entry) =>
        ResolveGuardExecutable(entry.Kind, entry.CommandLine);

    private static string? ResolveGuardExecutable(StartupSnoozeTaskEntry entry) =>
        ShortcutTargetResolver.ResolveExecutable(
            RunOnceClassifier.ParseExecutable(entry.Executable));

    private static string? ResolveGuardExecutable(string kind, string? commandLine)
    {
        var candidate = string.Equals(kind, "StartupFolderItem", StringComparison.OrdinalIgnoreCase)
            ? commandLine
            : RunOnceClassifier.ParseExecutable(commandLine);

        return ShortcutTargetResolver.ResolveExecutable(candidate);
    }

    private static void DropUncoveredEntries(
        StartupSnoozeJournal journal,
        List<string> warnings,
        List<string> blockedStartupItems,
        HashSet<string> exceptions)
    {
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
                warnings.Add(entry.ValueName + "：系统拒绝写入（" + problem + "），无法安全隔离，已取消连接");
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

                blockedStartupItems.Add(
                    entry.ValueName
                    + (string.IsNullOrWhiteSpace(executable) ? "（" + entry.CommandLine + "）" : "（" + executable + "）"));
                continue;
            }

            kept.Add(entry);
        }

        journal.Registry = kept;
    }

    private static void DropExceptionEntries(
        StartupSnoozeJournal journal,
        List<string> warnings,
        HashSet<string> exceptions)
    {
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
            var probeValue = StartupApproval.CreateDisabled(DateTime.UtcNow);
            Exception? failure = null;
            try
            {
                key.SetValue(probeName, probeValue, RegistryValueKind.Binary);

                // 安全软件或注册表虚拟化可能让 SetValue 无异常返回但实际没有落盘。
                // 必须按唯一值名回读精确字节，删除后也要确认没有留下探测值。
                if (key.GetValue(
                        probeName,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] written
                    || !written.SequenceEqual(probeValue))
                {
                    failure = new InvalidOperationException("临时探测值写入后回读不一致");
                }
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
                    if (key.GetValue(
                            probeName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
                    {
                        failure ??= new InvalidOperationException("临时探测值删除后回读仍存在");
                    }
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

    private static bool WaitForTaskEnabled(
        string relative,
        bool expected,
        string? expectedIdentity)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var query = QueryTaskXml(relative);
            var state = query.IsPresent
                ? ScheduledTaskXml.GetEnabledState(query.Xml)
                : TaskEnabledState.Unknown;
            if (query.IsPresent
                && ((expected && state == TaskEnabledState.Enabled)
                    || (!expected && state == TaskEnabledState.Disabled))
                && ScheduledTaskXml.IsValidIdentity(expectedIdentity)
                && string.Equals(
                    ScheduledTaskXml.GetIdentity(query.Xml),
                    expectedIdentity,
                    StringComparison.Ordinal))
            {
                return true;
            }

            System.Threading.Thread.Sleep(300);
        }

        return false;
    }

    private static Encoding GetOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            AppLog.Warn("无法解析 schtasks 的 OEM 编码，暂按 UTF-8 读取。", exception);
            return Encoding.UTF8;
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        try
        {
            var oemEncoding = GetOemEncoding();
            // 绝不能按 PATH 裸命令解析 schtasks：子会话或被篡改的环境可以把
            // fake schtasks.exe 放进目录并劫持「关停/恢复」命令。System32 的
            // 绝对路径是唯一可信位置；读环境变量只为定位系统目录，不作为命令名。
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                return (-1, "无法读取 SystemRoot 环境变量");
            }

            var schtasks = Path.Combine(systemRoot, "System32", "schtasks.exe");
            if (!File.Exists(schtasks))
            {
                return (-1, "System32 中不存在 schtasks.exe");
            }

            var startInfo = new ProcessStartInfo(schtasks)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oemEncoding,
                StandardErrorEncoding = oemEncoding
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
            var output = standardOutput.GetAwaiter().GetResult().Trim();
            var error = standardError.GetAwaiter().GetResult().Trim();
            return (process.ExitCode, process.ExitCode == 0
                ? output
                : (output + " " + error).Trim());
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
        var json = JsonSerializer.Serialize(journal, JournalJsonOptions);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetBytes(json);
        if (bytes.Length == 0 || bytes.Length > MaxJournalBytes)
        {
            throw new IOException("启动项关停日志大小超出安全上限。");
        }

        if (_useMachineJournal)
        {
            WriteMachineJournalBytes(bytes);
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
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
            {
                stream.Write(bytes, 0, bytes.Length);
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
        var bytes = ReadJournalStorage();
        if (bytes.Length == 0 || bytes.Length > MaxJournalBytes)
        {
            throw new InvalidDataException("启动项关停日志大小超出安全上限。");
        }

        var json = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
        var journal = JsonSerializer.Deserialize<StartupSnoozeJournal>(json)
                      ?? throw new InvalidOperationException("关停日志内容为空。");
        ValidateJournal(journal);
        return journal;
    }

    private byte[] ReadJournalStorage()
    {
        if (_useMachineJournal)
        {
            return ReadMachineJournalBytes()
                   ?? throw new FileNotFoundException("找不到启动项关停日志。", JournalPath);
        }

        return File.ReadAllBytes(JournalPath);
    }

    private void DeleteJournalStorage()
    {
        if (!_useMachineJournal)
        {
            File.Delete(JournalPath);
            return;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(JournalStoreKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        key.DeleteValue(_journalValueName!, throwOnMissingValue: false);
        key.Flush();
    }

    private void WriteMachineJournalBytes(byte[] bytes)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(JournalStoreKeyPath, writable: true)
            ?? throw new IOException("无法创建管理员级启动项日志存储。");
        key.SetValue(_journalValueName!, bytes, RegistryValueKind.Binary);
        key.Flush();
    }

    private byte[]? ReadMachineJournalBytes()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(JournalStoreKeyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var value = key.GetValue(
            _journalValueName!,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }

        return value as byte[]
            ?? throw new InvalidDataException("管理员级启动项日志存储的值类型无效。");
    }

    private static string BuildJournalValueName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("无法取得当前用户 SID，不能安全定位启动项关停日志。");
        }

        return JournalValuePrefix + sid;
    }

    /// <summary>
    /// 生产日志位于仅管理员可写的 HKLM，测试使用临时目录。无论存储位置如何，
    /// 都不能信任日志中的注册表路径、任务名是否只指向启动项；否则篡改日志就能
    /// 借恢复流程写 HKLM/任务计划。
    /// </summary>
    internal static void ValidateJournal(StartupSnoozeJournal journal)
    {
        // 版本 1 没有计划任务写入标记；版本 2 没有任务身份。两者仍可读取，
        // 但计划任务恢复会因缺少身份而失败关闭；版本 3 起身份是必需字段；
        // 版本 4 增加当前用户 Winlogon Shell 的精确接管记录；版本 5 增加
        // Explorer Run/RunOnce 策略，同时仍能恢复版本 1-4。
        if (journal.Version is < 1 or > 5)
        {
            throw new InvalidDataException("不支持的启动项关停日志版本：" + journal.Version);
        }

        if (journal.Registry is null || journal.Tasks is null || journal.Uwp is null
            || journal.ExplorerRunPolicies is null
            || journal.Warnings is null || journal.DeferredExecutables is null)
        {
            throw new InvalidDataException("启动项关停日志缺少必要的列表。");
        }

        if (journal.Version < 5 && journal.ExplorerRunPolicies.Count > 0)
        {
            throw new InvalidDataException("旧版启动项关停日志不能包含 Explorer 策略记录。");
        }

        if (journal.ExplorerRunPolicies.Count > 0 && journal.Shell is not null)
        {
            throw new InvalidDataException("启动项关停日志不能同时包含 Explorer 策略和替代 Shell。");
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
            ValidateTaskEntry(journal.Version, task);
        }

        foreach (var uwp in journal.Uwp)
        {
            ValidateUwpEntry(uwp);
        }

        if (journal.ExplorerRunPolicies.Count > 0)
        {
            ValidateExplorerRunPolicies(journal.ExplorerRunPolicies);
            if (journal.Completed
                && journal.ExplorerRunPolicies.Any(policy => !policy.WriteConfirmed))
            {
                throw new InvalidDataException("已完成的 Explorer 策略日志缺少写入确认。");
            }

            if (journal.Version < 5)
            {
                throw new InvalidDataException("旧版启动项关停日志不能包含 Explorer 策略记录。");
            }
        }

        if (journal.Shell is not null)
        {
            ValidateShellEntry(journal.Shell);
        }

        foreach (var executable in journal.DeferredExecutables)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.Length > 32_768
                || executable.IndexOf('\0') >= 0
                || !Path.IsPathFullyQualified(executable)
                || executable.StartsWith(@"\\?\", StringComparison.Ordinal)
                || executable.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw new InvalidDataException("启动项关停日志包含无效的启动程序路径（必须是完整文件路径）。");
            }
        }
    }

    private static void ValidateShellEntry(StartupSnoozeShellEntry? entry)
    {
        if (entry is null)
        {
            throw new InvalidDataException("启动项关停日志包含空的 Winlogon Shell 记录。");
        }

        if (!string.Equals(
                entry.KeyPath,
                CurrentUserShellStore.WinlogonKeyPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.ValueName, "Shell", StringComparison.Ordinal))
        {
            throw new InvalidDataException("启动项关停日志中的 Winlogon Shell 位置无效。");
        }

        if (entry.OriginalExists
            && entry.OriginalKind is not ((int)RegistryValueKind.String
                or (int)RegistryValueKind.ExpandString))
        {
            throw new InvalidDataException("启动项关停日志中的 Winlogon Shell 原始类型无效。");
        }

        if (entry.OriginalExists)
        {
            if (entry.OriginalValue is null
                || entry.OriginalValue.IndexOf('\0') >= 0
                || !IsStandardShellValue(entry.OriginalValue))
            {
                throw new InvalidDataException(
                    "启动项关停日志中的 Winlogon Shell 原始值不是可接管的系统资源管理器外壳。");
            }
        }
        else if (entry.OriginalValue is not null)
        {
            throw new InvalidDataException("Winlogon Shell 原始值存在性与值不一致。");
        }

        if (entry.WriteConfirmed && !entry.WriteAttempted)
        {
            throw new InvalidDataException("Winlogon Shell 写入状态不一致。");
        }

        if (string.IsNullOrWhiteSpace(entry.ManagedValue)
            || entry.ManagedValue.IndexOf('\0') >= 0
            || !Path.IsPathFullyQualified(entry.ManagedValue)
            || entry.ManagedValue.StartsWith(@"\\?\", StringComparison.Ordinal)
            || entry.ManagedValue.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(entry.ManagedValue),
                SessionAgentService.ShellGateFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("启动项关停日志中的 Winlogon Shell 接管路径无效。");
        }

        string expected;
        string actual;
        try
        {
            expected = Path.GetFullPath(SessionAgentService.DefaultShellGatePath);
            actual = Path.GetFullPath(entry.ManagedValue);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            throw new InvalidDataException("无法解析 Winlogon Shell 接管路径。", exception);
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Winlogon Shell 接管路径不是本程序的门卫路径。");
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

        // 默认值名是合法的空字符串，必须允许它通过；只拒绝 null 和超长/含 NUL。
        if (entry.ValueName is null || entry.ValueName.Length > 1024
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

    private static void ValidateTaskEntry(int journalVersion, StartupSnoozeTaskEntry? task)
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

        if (IsMicrosoftSystemTaskPath(task.Relative))
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

        if (journalVersion >= 3 && !ScheduledTaskXml.IsValidIdentity(task.Identity))
        {
            throw new InvalidDataException("启动项关停日志缺少有效的计划任务身份。");
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

        if (!UwpStartupState.IsEnabled(uwp.OriginalState))
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
            // 启动文件夹里的项是 .lnk，必须解析到真实 EXE 才能与进程路径精确匹配。
            executables.Add(ResolveGuardExecutable(entry));
        }

        foreach (var entry in journal.Tasks.Where(task => !task.ConfirmedDisabled))
        {
            executables.Add(ResolveGuardExecutable(entry));
        }

        executables.AddRange(journal.DeferredExecutables);

        return NormalizeExecutableList(executables);
    }

    /// <summary>
    /// 返回日志中所有可解析的启动程序，供常驻守卫拦截由会话 0 服务
    /// 在子会话里重新拉起的副本。这里包含已确认关闭的项，因为禁用注册表
    /// 或计划任务并不能阻止部分厂商服务直接调用会话内程序。
    /// </summary>
    private static IReadOnlyList<string> NormalizeManagedStartupExecutables(StartupSnoozeJournal journal)
    {
        var executables = new List<string?>();
        foreach (var entry in journal.Registry)
        {
            executables.Add(ResolveGuardExecutable(entry));
        }

        foreach (var entry in journal.Tasks)
        {
            executables.Add(ResolveGuardExecutable(entry));
        }

        executables.AddRange(journal.DeferredExecutables);
        return NormalizeExecutableList(executables);
    }

    private static IReadOnlyList<string> NormalizeExecutableList(IEnumerable<string?> executables) =>
        executables
            .Select(NormalizeExecutablePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    /// <summary>启动命令行指向的可执行文件是否存在：存在 / 已证明不存在 / 无法证明。</summary>
    internal enum ExecutablePresence
    {
        Unknown,
        Exists,
        Missing
    }

    /// <summary>
    /// 判断启动命令行指向的程序是否存在。只使用可离线验证的信息（绝对路径或
    /// PATH 目录里的文件），不执行任何东西。无法证明时返回 Unknown，调用方必须
    /// 失败关闭，不能把「查不到」当成「不存在」。
    /// </summary>
    internal static ExecutablePresence ProveExecutablePresence(string? commandLine) =>
        ProveExecutablePresence(
            commandLine,
            Environment.GetEnvironmentVariable("PATH"),
            File.Exists);

    /// <summary>可注入 PATH 与文件探测的纯函数版本，供离线测试使用。</summary>
    internal static ExecutablePresence ProveExecutablePresence(
        string? commandLine,
        string? pathVariable,
        Func<string, bool> fileExists)
    {
        // 未成对的引号无法确定第一个参数在哪里结束，不能猜成裸命令再去 PATH 里
        // 查一个被剥掉引号的名字，否则解析失败会被误判为「已证明不存在」。
        if (commandLine is null || commandLine.Count(character => character == '"') % 2 != 0)
        {
            return ExecutablePresence.Unknown;
        }

        var executable = RunOnceClassifier.ParseExecutable(commandLine);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return ExecutablePresence.Unknown;
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(executable.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return ExecutablePresence.Unknown;
        }

        if (expanded.Length == 0)
        {
            return ExecutablePresence.Unknown;
        }

        // "C:foo" 是盘符相对路径，"\foo" 是当前盘根相对路径，都依赖进程当前目录，
        // 不能当成绝对路径直接下存在/不存在结论。
        if ((expanded.Length >= 2 && char.IsLetter(expanded[0]) && expanded[1] == ':'
                && (expanded.Length == 2 || (expanded[2] != '\\' && expanded[2] != '/')))
            || (expanded[0] == '\\' && (expanded.Length == 1 || expanded[1] != '\\'))
            || expanded[0] == '/')
        {
            return ExecutablePresence.Unknown;
        }

        if (Path.IsPathRooted(expanded))
        {
            return SafeFileExists(expanded, fileExists)
                ? ExecutablePresence.Exists
                : ExecutablePresence.Missing;
        }

        // 带目录分隔符却不是根路径（如 "bin\app.exe"）会相对当前目录解析，
        // 无法确定真实位置，必须失败关闭。
        if (expanded.IndexOf('\\') >= 0
            || expanded.IndexOf('/') >= 0
            || expanded.IndexOf(':') >= 0)
        {
            return ExecutablePresence.Unknown;
        }

        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            // 空 PATH 不能证明「找不到」，只能证明「没找」。
            return ExecutablePresence.Unknown;
        }

        var extensions = Path.HasExtension(expanded)
            ? new[] { string.Empty }
            : new[] { string.Empty, ".exe", ".com", ".bat", ".cmd" };

        foreach (var rawDirectory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            if (!Path.IsPathRooted(directory))
            {
                // 相对 PATH 目录依赖进程当前目录，同样无法静态证明。
                return ExecutablePresence.Unknown;
            }

            foreach (var extension in extensions)
            {
                var candidate = SafeCombine(directory, expanded + extension);
                if (candidate is null)
                {
                    return ExecutablePresence.Unknown;
                }

                if (SafeFileExists(candidate, fileExists))
                {
                    return ExecutablePresence.Exists;
                }
            }
        }

        return ExecutablePresence.Missing;
    }

    private static bool SafeFileExists(string path, Func<string, bool> fileExists)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or IOException
                                              or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static string? SafeCombine(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return null;
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
