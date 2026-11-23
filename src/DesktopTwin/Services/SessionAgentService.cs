using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 分身内的热键代理：释放、安装并拉起「分身端」。
///
/// 为什么需要它：焦点在分身画面里时，RDP 控件会把组合键转发进远程会话，
/// Console 会话这边的 <c>RegisterHotKey</c> 收不到 <c>WM_HOTKEY</c>，
/// Raw Input 的注册又会被 RDP 控件按「每进程每设备类一个窗口」的规则顶掉。
/// 唯一的干净做法是**在分身会话里也跑一个监听**——那个会话里没有 RDP 客户端，
/// 没有钩子冲突，热键注册一定成功。
///
/// 为什么是原生 exe 而不是 PowerShell 脚本（0.2.3 的教训）：
/// 1) 控制台窗口。powershell.exe 是控制台子系统程序，任务计划程序拉起它必然建一个控制台窗口；
///    用户机器的默认终端是 Windows Terminal，窗口归 WindowsTerminal.exe 所有，
///    <c>-WindowStyle Hidden</c> 与 <c>ShowWindow</c> 都藏不掉——分身桌面上会常驻一个空的
///    PowerShell 窗口。WinExe 不分配控制台，从根上消除这个问题。
/// 2) 启动速度。PowerShell 冷启动 + Add-Type 要 7~15 秒，主程序会把「正在启动」误判成
///    「不在线」而补拉新代理，新代理再杀旧代理——自噬循环，热键永远不稳。
/// 3) 没有脚本文件、执行策略、缓存文件争用这些坑。
///
/// 通信方式照搬社区脚本验证过的做法：分身端命中热键后写一个带时间戳的
/// <c>request.json</c>，主端轮询它。文件通信不需要任何 IPC 权限。
/// </summary>
internal sealed class SessionAgentService : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "DesktopTwinSessionAgent";
    internal const string AgentFileName = "DesktopTwin.Agent.exe";
    internal const string ShellGateFileName = "DesktopTwin.ShellGate.exe";
    private const string AgentResourceName = "DesktopTwin.Agent.exe";
    private const string RequestFileName = "uu-switch-request.json";
    private const string HeartbeatFileName = "session-agent-heartbeat.txt";
    private const string AgentLogFileName = "session-agent.log";

    /// <summary>
    /// 请求文件超过这个年龄就当作过期，避免关机前残留的旧请求在下次启动时被误执行。
    /// </summary>
    private static readonly TimeSpan RequestMaxAge = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 「拉起分身端之后」的启动宽限期。
    ///
    /// 分身端冷启动要看进程创建和登录完成，给它一段宽限再判定「不在线」：
    /// 判早了就会补拉新代理，而旧代理正在启动——两个进程互抢热键和互斥体，
    /// 结果一个稳定的都没有。
    /// </summary>
    private static readonly TimeSpan LaunchGracePeriod = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 心跳超过这个年龄就当代理卡住了。代理每 2 秒写一次，容错 15 次。
    /// </summary>
    private static readonly TimeSpan HeartbeatFreshWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 心跳最多每 2 秒读一次：状态轮询是 500ms 一次，每次开文件既浪费又增加冲突概率。
    /// </summary>
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 连续读不到心跳多少次才认定离线。单次失败只记日志、按「未知」跳过。
    /// </summary>
    private const int HeartbeatReadFailureLimit = 3;

    /// <summary>
    /// 进程探测结果的缓存时长。枚举进程约 1~3ms，没必要每 500ms 做一次。
    /// </summary>
    private static readonly TimeSpan AgentProbeInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 终止旧代理后确认其退出的上限。超时必须失败关闭，不能再补拉一个
    /// 抢不到单实例互斥体的重复进程。
    /// </summary>
    private const int AgentExitTimeoutMilliseconds = 5_000;

    /// <summary>
    /// 分身端进程探测结果。Absent 只代表当前会话内没有匹配项；
    /// Conflict 和 Unknown 都必须失败关闭，不能触发一次重复启动。
    /// </summary>
    internal enum AgentProcessPresence
    {
        Absent,
        Matching,
        Conflict,
        Unknown
    }

    /// <summary>
    /// 一次启动/复用/终止周期内唯一可信的子会话身份：会话号 + 该次登录时间。
    ///
    /// 会话号会在旧会话退出后被系统复用，只比会话号会把新会话当成旧会话。
    /// 登录时间读不到时身份不完整，调用方必须失败关闭，不能退化成「按会话号操作」。
    /// </summary>
    internal readonly record struct ChildSessionIdentity(uint SessionId, DateTime LogonTimeUtc)
    {
        internal bool IsComplete => SessionId > 0 && LogonTimeUtc != default;
    }

    private readonly string _dataDirectory;
    private readonly string _agentDirectory;
    private readonly string _legacyAgentPath;
    private readonly bool _protectAgentDirectory;
    private readonly bool _manageLegacyDefaultInstallation;
    private readonly ISessionAgentStartupRegistration _startupRegistration;

    /// <summary>
    /// 串行化「检查、替换、下发」整段流程，避免状态轮询在旧进程枚举或等待
    /// 退出期间重入并下发第二次启动任务。
    /// </summary>
    private readonly object _agentStartSync = new();

    /// <summary>
    /// 串行化代理文件、启动项和参数配置的整段变更。启动预检现在在线程池执行，
    /// 状态轮询仍可能在 UI 线程补拉代理；两者若同时替换文件，会出现「新文件已落地、
    /// 旧进程仍被当成同参数复用」或反向的发布窗口。
    ///
    /// 锁顺序固定为「安装锁 → 启动锁」。所有涉及 UI Dispatcher 的轮询启停都放在
    /// 安装锁之外，避免后台安装同步等待 UI、而 UI 又等待安装锁造成死锁。
    /// </summary>
    private readonly object _agentInstallSync = new();
    private volatile bool _pollingDesired;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pollTimer;
    private DateTime _lastRequestSeen = DateTime.MinValue;
    private long _lastLaunchTickCount = -1;
    private string? _hotkeyArgument;
    private string? _controlPipeArgument;

    /// <summary>连续读心跳失败的次数；成功一次就清零。</summary>
    private int _consecutiveHeartbeatReadFailures;

    /// <summary>心跳读取结果缓存（2 秒）。</summary>
    private long _lastHeartbeatCheckTicks = -100_000;
    private bool _lastHeartbeatFresh;

    /// <summary>进程探测结果缓存（3 秒）。</summary>
    private long _lastAgentProbeTicks = -100_000;
    private bool _lastAgentProbeResult;
    private volatile bool _disposed;

    /// <summary>
    /// 卸载阶段需要删除的持久自启项。生产实现直接操作任务计划程序和当前用户
    /// Run；测试通过假实现验证失败关闭顺序，绝不能触碰真实启动项。
    /// </summary>
    internal interface ISessionAgentStartupRegistration
    {
        bool TryDeleteLogonTask(out string error);

        void DeleteRunValue(string? expectedCommandLine);
    }

    /// <summary>
    /// 隔离期间停用功能时，对当前子会话中精确匹配的代理执行的一次收尾操作。
    /// 测试注入固定结果，绝不能触碰真实进程。
    /// </summary>
    internal readonly record struct AgentStopResult(bool Success, string Error);

    private readonly Func<AgentStopResult> _stopIsolatedAgent;
    private readonly string _shellGatePath;

    /// <summary>
    /// 生产自启项实现。所有真实注册表/任务计划操作都集中在这里，测试注入假实现后
    /// 不会触碰当前用户的启动状态。
    /// </summary>
    private sealed class WindowsSessionAgentStartupRegistration
        : ISessionAgentStartupRegistration
    {
        private readonly string _agentPath;
        private readonly string _legacyAgentPath;

        internal WindowsSessionAgentStartupRegistration(string agentPath, string legacyAgentPath)
        {
            _agentPath = agentPath;
            _legacyAgentPath = legacyAgentPath;
        }

        public bool TryDeleteLogonTask(out string error) =>
            ChildSessionProcessLauncher.TryDeleteSessionAgentLogonTask(out error);

        public void DeleteRunValue(string? expectedCommandLine) =>
            DeleteManagedRunValue(_agentPath, _legacyAgentPath, expectedCommandLine);
    }

    public SessionAgentService(
        Dispatcher dispatcher,
        string? dataDirectory = null,
        string? agentDirectory = null,
        bool? protectAgentDirectory = null,
        bool? manageLegacyDefaultInstallation = null,
        string? legacyAgentPath = null,
        ISessionAgentStartupRegistration? startupRegistration = null,
        Func<AgentStopResult>? stopIsolatedAgent = null,
        string? shellGatePath = null)
    {
        _dispatcher = dispatcher;
        _stopIsolatedAgent = stopIsolatedAgent ?? StopAgentForStartupIsolation;
        _shellGatePath = shellGatePath ?? DefaultShellGatePath;
        _dataDirectory = dataDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopTwin");

        // 生产环境把可执行代理放到仅管理员可写的 ProgramData 目录；请求/心跳仍留在
        // 当前用户的 AppData，分身端按交互用户身份读写它们。测试传入 dataDirectory
        // 时默认仍复用该目录，保持每个测试实例完全隔离。
        _protectAgentDirectory = protectAgentDirectory
            ?? (dataDirectory is null && agentDirectory is null);
        _agentDirectory = agentDirectory
            ?? (dataDirectory ?? DefaultAgentDirectory);
        _legacyAgentPath = legacyAgentPath ?? LegacyAgentPath;

        // 只有真正的生产布局才允许触碰旧版 AppData 代理。测试即使显式要求
        // 加固目录，也绝不能清理当前用户实际使用的旧文件或 Run 值。
        _manageLegacyDefaultInstallation = manageLegacyDefaultInstallation
            ?? (dataDirectory is null && agentDirectory is null);
        _startupRegistration = startupRegistration
            ?? new WindowsSessionAgentStartupRegistration(AgentPath, _legacyAgentPath);
    }

    /// <summary>
    /// 分身端请求切换。总在 UI 线程触发。
    /// </summary>
    public event EventHandler? SwitchRequested;

    /// <summary>
    /// 分身端 exe 的落地路径。
    /// </summary>
    public string AgentPath => Path.Combine(_agentDirectory, AgentFileName);

    /// <summary>
    /// 生产环境代理目录：<c>%ProgramData%\DesktopTwin\Agent</c>。该目录只允许
    /// Administrators/SYSTEM 写入，普通用户仅有读取和执行权限，避免提升权限的主程序
    /// 被诱导把用户可写路径上的文件当作代理执行。
    /// </summary>
    internal static string DefaultAgentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DesktopTwin",
        "Agent");

    /// <summary>
    /// 默认安装位置下的分身端路径。启动项扫描与启动项守卫都必须精确排除它，
    /// 否则守卫会把显式拉起的控制通道当成用户启动程序终止。
    /// </summary>
    internal static string DefaultAgentPath => Path.Combine(DefaultAgentDirectory, AgentFileName);

    /// <summary>
    /// 替代资源管理器的隐藏外壳门卫。与分身端使用同一份内嵌字节，
    /// 仅通过 --shell-gate 参数切换行为；独立文件名可避免进程探测把两者
    /// 误认为同一个分身端。
    /// </summary>
    internal static string DefaultShellGatePath => Path.Combine(
        DefaultAgentDirectory,
        ShellGateFileName);

    /// <summary>
    /// 旧版本把代理释放到当前用户的 AppData。升级时必须迁移 Run 值并清理该
    /// 文件，否则启动项会继续执行用户可写目录中的旧代理。
    /// </summary>
    internal static string LegacyAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopTwin",
        AgentFileName);

    /// <summary>
    /// 判断一条启动命令是否精确指向旧版代理。旧版始终给路径加双引号，但这里
    /// 也接受无参数的无引号路径；只做目标路径匹配，避免把同名前缀的其它程序当成代理。
    /// </summary>
    internal static bool IsLegacyAgentCommand(string? commandLine, string legacyAgentPath) =>
        IsAgentCommand(commandLine, legacyAgentPath);

    /// <summary>
    /// 判断 Run 值是否精确指向指定的代理可执行文件。命令允许携带任意参数，但路径
    /// 结束后必须是空白或闭合引号；反引号、同名前缀、powershell 包装和残缺引号
    /// 一律不算本产品所有，删除时必须失败关闭。
    /// </summary>
    internal static bool IsAgentCommand(string? commandLine, string agentPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(agentPath))
        {
            return false;
        }

        string normalizedAgent;
        try
        {
            normalizedAgent = Path.GetFullPath(agentPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandLine).TrimStart();
        var quoted = expanded.StartsWith('"');
        var pathText = quoted ? expanded[1..] : expanded;
        if (!pathText.StartsWith(normalizedAgent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nextIndex = normalizedAgent.Length;
        if (quoted)
        {
            // 开头有双引号时，路径结束后必须有配对的结束引号；不能把
            // ""...DesktopTwin.Agent.exe" 这种残缺命令误认成本产品启动项。
            return nextIndex < pathText.Length
                && pathText[nextIndex] == '"'
                && (nextIndex + 1 == pathText.Length || char.IsWhiteSpace(pathText[nextIndex + 1]));
        }

        return nextIndex == pathText.Length || char.IsWhiteSpace(pathText[nextIndex]);
    }

    /// <summary>
    /// Run 值只有在命令被证明指向当前或旧版代理时才允许删除。用于卸载前的
    /// 最后一道校验，避免同名但属于其它程序的 Run 值被误删。
    /// </summary>
    internal static bool IsManagedAgentRunValue(
        string? commandLine,
        string agentPath,
        string legacyAgentPath) =>
        IsAgentCommand(commandLine, agentPath)
        || IsAgentCommand(commandLine, legacyAgentPath);

    /// <summary>
    /// 自启项的同步决策。正常安装先注册当前用户登录任务，再删除旧 HKCU Run；
    /// 隔离期间完全冻结自启态：不新建、不改写、不删除任务、Run 值或旧版代理文件。
    /// </summary>
    internal readonly record struct StartupSynchronizationPlan(
        bool CreateOrUpdateLoginTask,
        bool RemoveRunValue,
        bool DeleteLegacyAgentFile);

    internal static StartupSynchronizationPlan PlanStartupSynchronization(
        bool updateStartupEntry,
        bool isManagedRun,
        bool manageLegacyDefaultInstallation)
    {
        // 隔离期间（updateStartupEntry=false）必须保持启动态与关停日志完全一致：
        // 不新建或更新登录任务、不改写或删除 Run 值、也不删除旧版代理文件。
        // 否则日志记录的原始命令会与实际状态分叉，恢复后可能指向不存在的程序。
        return new StartupSynchronizationPlan(
            CreateOrUpdateLoginTask: updateStartupEntry && manageLegacyDefaultInstallation,
            RemoveRunValue: updateStartupEntry
                && manageLegacyDefaultInstallation
                && isManagedRun,
            DeleteLegacyAgentFile: updateStartupEntry && manageLegacyDefaultInstallation);
    }

    /// <summary>
    /// 判断一个路径是否正是指向默认安装位置的分身端。可读取时按文件句柄解析
    /// 最终路径，防止 8.3 短名、卷 GUID 或目录联接别名绕过排除；不接受仅
    /// 文件名相同或位于相邻目录的程序。
    /// </summary>
    internal static bool IsDefaultAgentPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            return ProcessNativeMethods.TryNormalizeImagePath(
                       Path.GetFullPath(expanded),
                       out var candidatePath)
                   && ProcessNativeMethods.TryNormalizeImagePath(
                       Path.GetFullPath(DefaultAgentPath),
                       out var defaultPath)
                   && string.Equals(candidatePath, defaultPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 枚举当前子会话中的外壳门卫。只认会话号、登录时间与完整镜像路径，
    /// 绝不按进程名或模糊路径放行。更新过程中仍运行的旧文件允许 .old 别名，
    /// 但别名必须来自受保护的固定安装目录。
    /// </summary>
    internal static bool TryConfirmShellGateProcess(out string error)
    {
        error = string.Empty;
        if (!TryCaptureChildSessionIdentity(out var identity, out error))
        {
            return false;
        }

        if (!TryConfirmChildSessionIdentity(identity, out error))
        {
            return false;
        }

        var presence = ProbeShellGateProcess(identity);
        if (presence != AgentProcessPresence.Matching)
        {
            error = presence switch
            {
                AgentProcessPresence.Absent => "子会话中没有已确认的外壳门卫进程。",
                AgentProcessPresence.Conflict => "子会话中存在同名但路径不符的外壳门卫进程，拒绝继续。",
                _ => "无法确认子会话外壳门卫进程的身份。"
            };
            return false;
        }

        if (!TryConfirmChildSessionIdentity(identity, out error))
        {
            return false;
        }

        return true;
    }

    private static AgentProcessPresence ProbeShellGateProcess(ChildSessionIdentity identity)
    {
        if (!identity.IsComplete)
        {
            return AgentProcessPresence.Unknown;
        }

        var processName = Path.GetFileNameWithoutExtension(ShellGateFileName);
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            AppLog.Warn("无法枚举外壳门卫候选进程，无法确认其存在。", exception);
            return AgentProcessPresence.Unknown;
        }

        var identities = new List<(int? SessionId, string? ImagePath)>();
        try
        {
            foreach (var process in candidates)
            {
                int? processSessionId = null;
                try
                {
                    processSessionId = process.SessionId;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    identities.Add((null, null));
                    continue;
                }

                if (processSessionId <= 0)
                {
                    identities.Add((processSessionId, null));
                    continue;
                }

                if ((uint)processSessionId.Value != identity.SessionId)
                {
                    continue;
                }

                string? processPath = null;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    // 分类器会把身份不完整的候选判为 Unknown。
                }

                identities.Add((processSessionId, processPath));
            }
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }

        return ClassifyAgentProcessPresence(
            DefaultShellGatePath,
            identity,
            identities,
            [DefaultShellGatePath + ".old"]);
    }

    /// <summary>
    /// 请求文件路径。
    /// </summary>
    public string RequestPath => Path.Combine(_dataDirectory, RequestFileName);

    /// <summary>
    /// 心跳文件路径。
    /// </summary>
    public string HeartbeatPath => Path.Combine(_dataDirectory, HeartbeatFileName);

    /// <summary>
    /// 释放并验证替代资源管理器的外壳门卫。该动作与热键、MCP 是否启用无关：
    /// 只要主程序可能连接分身，RDP 的 StartProgram 就必须指向一个已验证的本地程序。
    /// </summary>
    internal static bool TryEnsureShellGateInstalled(out string gatePath, out string error)
    {
        gatePath = string.Empty;
        error = string.Empty;

        try
        {
            var directory = Path.GetFullPath(DefaultAgentDirectory);
            var parent = Directory.GetParent(directory)?.FullName
                ?? throw new IOException("外壳门卫目录不能是卷根目录，拒绝创建。");
            EnsureProtectedDirectory(parent);
            EnsureProtectedDirectory(directory);

            var target = Path.GetFullPath(DefaultShellGatePath);
            var outcome = WriteExecutableIfChanged(target, "外壳门卫", protect: true);
            if (outcome == AgentWriteOutcome.FailedRolledBack)
            {
                throw new IOException("外壳门卫更新失败，目标路径上仍是旧版本。");
            }

            ValidateProtectedFile(target);
            var assembly = typeof(SessionAgentService).Assembly;
            var resourceName = ResolveAgentResourceName(assembly);
            using var expectedStream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(Locale.Get("Agent.EmbeddedMissing"));
            var expected = new byte[expectedStream.Length];
            var offset = 0;
            while (offset < expected.Length)
            {
                var read = expectedStream.Read(expected, offset, expected.Length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            var actual = File.ReadAllBytes(target);
            if (offset != expected.Length
                || actual.Length != expected.Length
                || !BytesEqual(actual, expected))
            {
                throw new IOException("外壳门卫与当前内嵌版本不一致，拒绝使用。");
            }

            gatePath = target;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException
                                              or COMException
                                              or InvalidOperationException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            error = "外壳门卫安装失败：" + exception.Message;
            AppLog.Warn(error, exception);
            return false;
        }
    }

    /// <summary>
    /// 安装（或更新）分身端：释放 exe、按需注册登录任务并开始轮询请求。
    /// </summary>
    /// <param name="hotkey">当前热键组合，形如 <c>Ctrl+Alt+U</c>；为空表示不注册热键。</param>
    /// <param name="controlPipe">MCP 控制管道名；为空表示不开放控制通道。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="updateStartupEntry">
    /// 是否允许修改自启项。启动项隔离或子会话状态未知时必须为 false，
    /// 否则会把刚关停的分身端自启项重新打开，破坏隔离日志与系统状态的一致性。
    /// </param>
    public bool EnsureInstalled(
        string? hotkey,
        string? controlPipe,
        out string error,
        bool updateStartupEntry = true)
    {
        bool installed;
        bool pollingReady;
        lock (_agentInstallSync)
        {
            installed = EnsureInstalledCore(
                hotkey,
                controlPipe,
                out error,
                updateStartupEntry,
                out pollingReady);
        }

        // ReconcilePolling 会切回 UI 线程；必须在释放安装锁后调用。
        if (pollingReady)
        {
            ReconcilePolling();
        }

        return installed;
    }

    private bool EnsureInstalledCore(
        string? hotkey,
        string? controlPipe,
        out string error,
        bool updateStartupEntry,
        out bool pollingReady)
    {
        pollingReady = false;
        error = string.Empty;
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(hotkey) && string.IsNullOrWhiteSpace(controlPipe))
        {
            throw new ArgumentException("至少需要热键或控制管道中的一项。");
        }

        var nextHotkey = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey;
        var nextControlPipe = string.IsNullOrWhiteSpace(controlPipe) ? null : controlPipe;
        if (!string.Equals(_hotkeyArgument, nextHotkey, StringComparison.Ordinal)
            || !string.Equals(_controlPipeArgument, nextControlPipe, StringComparison.Ordinal))
        {
            // 即使启动项隔离禁止写自启项，当前会话里的旧代理仍必须换成新参数。
            NeedsRestart = true;
        }

        _hotkeyArgument = nextHotkey;
        _controlPipeArgument = nextControlPipe;

        string? updateError = null;

        try
        {
            EnsureAgentDirectory();
            Directory.CreateDirectory(_dataDirectory);
            switch (WriteAgentIfChanged())
            {
                case AgentWriteOutcome.Updated:
                    // 代理换了，正在跑的还是旧版（或者压根没在跑），下次连上时要重启一次。
                    NeedsRestart = true;
                    break;

                case AgentWriteOutcome.FailedRolledBack:
                    updateError = Locale.Get("Agent.Error.UpdateRolledBack");
                    AppLog.Warn(updateError);
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            error = Locale.Format("Agent.Error.ReleaseFailed", exception.Message);
            AppLog.Warn(error, exception);
            return false;
        }

        try
        {
            var currentRun = ReadRunKey();
            var isManagedRun = _manageLegacyDefaultInstallation
                && IsManagedAgentRunValue(currentRun, AgentPath, _legacyAgentPath);
            var arguments = BuildAgentArguments();
            var startupPlan = PlanStartupSynchronization(
                updateStartupEntry,
                isManagedRun,
                _manageLegacyDefaultInstallation);

            if (startupPlan.CreateOrUpdateLoginTask)
            {
                // 必须先让持久任务注册成功，后面才能删除旧的 Run 值。
                // 任务失败时保留原 Run，用户至少不会失去原有的自启能力。
                ChildSessionProcessLauncher.CreateOrUpdateSessionAgentLogonTask(
                    AgentPath,
                    arguments);
            }

            if (startupPlan.RemoveRunValue)
            {
                // 任务已注册成功，删除旧 Run 才能避免同一登录点被两个自启项触发。
                // 传入刚才校验过的精确值；若值在检查后被改写，删除会失败关闭。
                _startupRegistration.DeleteRunValue(currentRun);
            }

            if (startupPlan.DeleteLegacyAgentFile)
            {
                TryDelete(_legacyAgentPath);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException
                                              or COMException
                                              or InvalidOperationException)
        {
            error = Locale.Format("Agent.Error.RunKeyFailed", exception.Message);
            AppLog.Warn(error, exception);
            return false;
        }

        _pollingDesired = true;
        pollingReady = true;

        if (updateError is not null)
        {
            // 旧代理可用，所以流程走完、轮询也开着；但这次更新没成功，不能报成功。
            error = updateError;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在分身会话里立刻拉起一份分身端。
    ///
    /// 自启项只在下次登录时生效，用户当前就开着分身，所以必须主动启动一次。
    /// 用任务计划程序以「有限权限 + 交互令牌」启动：代理清单是 asInvoker，
    /// 不会弹 UAC。
    /// </summary>
    /// <returns>是否已下发启动任务。</returns>
    public bool TryStartInChildSession()
    {
        ThrowIfDisposed();

        // 整个「确认会话 -> 找旧代理 -> 等退出 -> 下发任务」流程必须串行。
        // 先拿安装锁，再拿启动锁，确保不会在代理文件/启动项正在替换时执行旧参数。
        // 全局锁顺序固定为「安装锁 → 启动锁」。
        lock (_agentInstallSync)
        lock (_agentStartSync)
        {
            // 整个周期只认这一次捕获的 sessionId + logonTime。会话号可能被系统
            // 复用，任何后续探测、终止和启动都必须基于同一身份；身份变化立即失败关闭。
            if (!TryCaptureChildSessionIdentity(out var identity, out var identityError))
            {
                AppLog.Warn(identityError);
                return false;
            }

            // 只往「已经登录完成」的会话里拉：RDP 刚连上、桌面还没起来时下发任务，
            // 任务计划程序会把进程起在一个没有交互桌面的会话里，进程活不下来，
            // 却会让主程序以为「已经拉起过」。
            if (!ChildSessionNativeMethods.TryGetSessionState(identity.SessionId, out var sessionState))
            {
                AppLog.Warn(
                    $"无法确认分身会话 {DescribeIdentity(identity)} 的连接状态，本次不下发分身端启动任务。");
                return false;
            }

            if (sessionState != 0)
            {
                AppLog.Info(
                    $"分身会话 {DescribeIdentity(identity)} 还没进入 Active 状态"
                    + $"（当前状态 {sessionState}），等登录完成后再拉起分身端。");
                return false;
            }

            if (!File.Exists(AgentPath))
            {
                AppLog.Warn($"分身端文件不存在：{AgentPath}，跳过拉起。");
                return false;
            }

            if (NeedsRestart)
            {
                // 参数或代理文件变过：不能相信 HasLiveAgentProcess 的缓存，必须重新
                // 枚举当前子会话。只有精确匹配 AgentPath 的旧进程才允许被终止；
                // 任何身份/路径不可读或不匹配都失败关闭，不能再拉起重复进程。
                if (!TryStopExistingAgentProcess(identity, out var foundExisting))
                {
                    return false;
                }

                if (!foundExisting)
                {
                    AppLog.Info("未发现需要替换的旧分身端，准备下发一次启动任务。");
                }
            }
            else
            {
                // 探测前先确认身份：不能拿旧快照去枚举一个已经被重建的同号会话，
                // 否则会把新会话里的代理当成旧会话的代理复用或跳过。
                if (!TryConfirmChildSessionIdentity(identity, out var probeIdentityError))
                {
                    AppLog.Warn(probeIdentityError + " 本次不下发分身端启动任务。");
                    return false;
                }

                // 不能复用 HasLiveAgentProcess 的 3 秒缓存：缓存里的 true 可能来自
                // 同会话中路径相同的旧进程，而缓存过期前进程可能已经被替换。这里重新
                // 枚举一次，并且只有「已捕获的会话 + 完整镜像路径」都匹配才算已在运行。
                var presence = ProbeAgentProcess(identity);
                if (presence == AgentProcessPresence.Matching)
                {
                    // 同参数代理已在运行，继续复用；只有 NeedsRestart 才进入替换流程。
                    AppLog.Info("分身端已在运行，跳过本次拉起。");
                    return false;
                }

                if (presence == AgentProcessPresence.Conflict)
                {
                    AppLog.Warn(
                        "当前子会话存在同名但镜像路径不匹配的进程；为避免误杀或重复启动，"
                        + "本次不下发分身端启动任务。");
                    return false;
                }

                if (presence == AgentProcessPresence.Unknown)
                {
                    AppLog.Warn(
                        "无法确认当前子会话中是否存在目标分身端；为避免重复启动，"
                        + "本次不下发启动任务。");
                    return false;
                }
            }

            // 启动前最后一次确认：捕获身份之后会话可能已经重建或复用。
            // 不确认就下发任务，可能把代理起进已经消失的旧会话。
            if (!TryConfirmChildSessionIdentity(identity, out var launchIdentityError))
            {
                AppLog.Warn(launchIdentityError + " 本次不下发分身端启动任务。");
                return false;
            }

            var ok = ChildSessionProcessLauncher.RunInSession(
                identity.SessionId,
                identity.LogonTimeUtc,
                AgentPath,
                BuildManagedAgentArguments(identity),
                taskNamePrefix: "DesktopTwin-SessionAgent-");

            // 启动器在登记任务前复核会话号与登录时间；分身端进程启动后还会
            // 自校验一次身份，避免会话号在两次检查之间被系统复用。这里再复核
            // 一次，确保本次启动结果确实属于最初捕获的会话。
            // 身份已经变化时不能把结果当作成功：清掉 NeedsRestart 或进入启动宽限期
            // 会让下一次轮询漏掉新会话，从而漏启动。
            if (ok && !TryConfirmChildSessionIdentity(identity, out var postLaunchIdentityError))
            {
                AppLog.Warn(
                    "分身端启动任务已下发，但完成后无法再次确认原会话身份："
                    + postLaunchIdentityError);
                return false;
            }

            AppLog.Info(ok
                ? Locale.Format("Agent.Started", identity.SessionId)
                : Locale.Format("Agent.StartFailed", identity.SessionId));

            if (ok)
            {
                NeedsRestart = false;
                // 进入启动宽限期：这段时间内即使没有心跳也按「在线」处理。
                _lastLaunchTickCount = Environment.TickCount64;
            }

            return ok;
        }
    }

    /// <summary>
    /// 分身端文件更新过、但还在用旧版本跑（或压根没跑起来）。
    ///
    /// 用来避免「每次连接状态变化都重启分身端」：那样会陷入
    /// 「新进程杀旧进程、旧进程还没退出、新进程抢不到互斥体又退出」的循环，
    /// 结果一个稳定的分身端都没有，热键彻底失灵。
    /// </summary>
    public bool NeedsRestart
    {
        get => _needsRestart;
        private set => _needsRestart = value;
    }

    private volatile bool _needsRestart;

    /// <summary>
    /// 分身端是否在线。
    ///
    /// 两道判断，各管一件事：
    /// 1. 先看**进程**在不在（同会话）——它回答「代理有没有被启动或被收掉」；
    /// 2. 进程在再看**心跳**——它回答「代理还活着在干活，还是卡住了」。
    ///
    /// 只看心跳会把「读文件失败」误判成掉线（历史上一天误报 60 多次）；
    /// 只看进程又发现不了卡死，所以两者缺一不可。
    /// </summary>
    public bool IsAgentOnline()
    {
        // 启动宽限：刚拉起就等心跳会把还没完成启动的代理误判成「不在线」，
        // 触发又一次拉起——新代理会杀掉正在启动的旧代理，形成自噬循环。
        if (_lastLaunchTickCount >= 0)
        {
            var sinceLaunch = TimeSpan.FromMilliseconds(
                unchecked(Environment.TickCount64 - _lastLaunchTickCount));
            if (sinceLaunch >= TimeSpan.Zero && sinceLaunch < LaunchGracePeriod)
            {
                return true;
            }
        }

        if (!HasLiveAgentProcess())
        {
            return false;
        }

        return IsHeartbeatFresh();
    }

    /// <summary>
    /// 分身会话里是否已经有精确匹配 <see cref="AgentPath"/> 的分身端进程。
    /// </summary>
    /// <remarks>
    /// 不能靠互斥体判断：代理用的是 <c>Local\</c> 命名空间，会话内可见，
    /// 主程序所在的父会话根本打不开它。改成 <c>Global\</c> 又会让多个分身会话互相打架。
    ///
    /// 必须同时按会话、完整镜像路径过滤：控制台登录时自启项也会跑一次代理，
    /// 那个进程会立刻退出；同会话里还可能有同名旧文件、手工复制的 exe。只看
    /// 进程名会把它们当成「分身端还活着」，从而永远不下发真正的启动任务。
    /// </remarks>
    public bool HasLiveAgentProcess()
    {
        var now = Environment.TickCount64;
        if (now - _lastAgentProbeTicks < (long)AgentProbeInterval.TotalMilliseconds)
        {
            return _lastAgentProbeResult;
        }

        _lastAgentProbeTicks = now;
        if (!TryCaptureChildSessionIdentity(out var identity, out _)
            || !TryConfirmChildSessionIdentity(identity, out _))
        {
            // 身份不完整或已变化时既不能假定在线，也不能让上一次缓存的 true 继续生效。
            _lastAgentProbeResult = false;
            return false;
        }

        _lastAgentProbeResult = ProbeAgentProcess(identity) == AgentProcessPresence.Matching;
        return _lastAgentProbeResult;
    }

    /// <summary>
    /// 只使用调用方捕获并确认过的会话身份枚举候选进程，绝不自行查询 WTS、
    /// 也绝不把目标换成「当前查到」的另一个会话。身份不完整时返回 Unknown 失败关闭。
    /// </summary>
    private AgentProcessPresence ProbeAgentProcess(ChildSessionIdentity identity)
    {
        if (!identity.IsComplete)
        {
            return AgentProcessPresence.Unknown;
        }

        var processName = Path.GetFileNameWithoutExtension(AgentFileName);
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            AppLog.Warn("无法枚举分身端候选进程，无法确认在线状态。", exception);
            return AgentProcessPresence.Unknown;
        }

        var identities = new List<(int? SessionId, string? ImagePath)>();
        try
        {
            foreach (var process in candidates)
            {
                int? processSessionId = null;
                try
                {
                    processSessionId = process.SessionId;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    // 身份不可读的候选必须标记为未知，不能静默当作「不存在」。
                    identities.Add((null, null));
                    continue;
                }

                if (processSessionId <= 0)
                {
                    identities.Add((processSessionId, null));
                    continue;
                }

                if ((uint)processSessionId.Value != identity.SessionId)
                {
                    continue;
                }

                string? processPath = null;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    // 目标会话内的候选身份不完整，分类器会返回 Unknown 并失败关闭。
                }

                identities.Add((processSessionId, processPath));
            }
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }

        return ClassifyAgentProcessPresence(AgentPath, identity, identities);
    }

    /// <summary>
    /// 终止当前子会话里精确匹配 <see cref="AgentPath"/> 的旧代理，并等待其退出。
    ///
    /// 这个流程刻意不复用 <see cref="HasLiveAgentProcess"/> 的 3 秒缓存：替换参数
    /// 或代理文件时，决策必须基于本次重新枚举。任何会阻止安全判断的情况都返回
    /// false，让调用方失败关闭，而不是再拉起一个抢不到单实例互斥体的进程。
    /// </summary>
    /// <param name="identity">本次周期已捕获、且每次关键操作前都会复核的会话身份。</param>
    /// <param name="foundExisting">是否发现并尝试终止了至少一个精确匹配的旧代理。</param>
    private bool TryStopExistingAgentProcess(
        ChildSessionIdentity identity,
        out bool foundExisting)
    {
        foundExisting = false;

        if (!identity.IsComplete)
        {
            AppLog.Warn(
                "已捕获的分身会话身份不完整，无法安全识别旧分身端；本次不下发启动任务。");
            return false;
        }

        // 进入枚举前先确认：拿旧快照去枚举同号的新会话，可能误杀新会话里的代理。
        if (!TryConfirmChildSessionIdentity(identity, out var stopIdentityError))
        {
            AppLog.Warn(stopIdentityError + " 本次不终止旧分身端，也不下发启动任务。");
            return false;
        }

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AgentFileName));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            AppLog.Warn("无法枚举分身端候选进程，本次不下发启动任务。", exception);
            return false;
        }

        var matching = new List<(Process Process, long CreationTimeUtc)>();
        try
        {
            foreach (var process in candidates)
            {
                int processSessionId;
                try
                {
                    processSessionId = process.SessionId;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    AppLog.Warn(
                        $"无法读取分身端候选进程 {process.Id} 的会话身份，为避免误杀或重复启动，本次不下发启动任务。",
                        exception);
                    return false;
                }

                // 主会话（或任何其他会话）里的同名进程都不是本次替换目标，
                // 也绝不允许被终止。只有目标子会话内的候选才继续读取镜像路径。
                if ((uint)processSessionId != identity.SessionId)
                {
                    continue;
                }

                string? processPath;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    AppLog.Warn(
                        $"无法读取分身会话 {DescribeIdentity(identity)} 内候选进程 {process.Id} 的完整路径，"
                        + "为避免误杀或重复启动，本次不下发启动任务。",
                        exception);
                    return false;
                }

                if (!IsMatchingAgentProcess(AgentPath, identity, processSessionId, processPath))
                {
                    // 同名但不是 AgentPath（例如 .old 退役文件或另一个目录的副本）
                    // 仍可能持有同名单实例互斥体。不杀它，但也绝不并发拉起新代理。
                    AppLog.Warn(
                        $"分身会话 {DescribeIdentity(identity)} 内的同名进程 {process.Id} "
                        + $"不匹配代理完整路径：期望 {AgentPath}，实际 {processPath ?? "<无法读取>"}。"
                        + "本次不下发启动任务。");
                    return false;
                }

                long creationTimeUtc;
                try
                {
                    creationTimeUtc = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception
                                                      or NotSupportedException
                                                      or System.Security.SecurityException)
                {
                    AppLog.Warn(
                        $"无法读取分身会话 {DescribeIdentity(identity)} 内候选进程 {process.Id} 的创建时间，"
                        + "无法在 PID 复用时保证只终止原进程，本次不下发启动任务。",
                        exception);
                    return false;
                }

                matching.Add((process, creationTimeUtc));
            }

            if (matching.Count == 0)
            {
                return true;
            }

            // 枚举进程和读镜像路径都可能耗时；真正动刀前再复核一次身份。
            // 旧会话已经注销、同号会话已经重建时，绝不终止新会话里的代理。
            if (!TryConfirmChildSessionIdentity(identity, out var killIdentityError))
            {
                AppLog.Warn(killIdentityError + " 本次不终止旧分身端，也不下发启动任务。");
                return false;
            }

            foundExisting = true;
            var started = Environment.TickCount64;
            foreach (var target in matching)
            {
                var remaining = AgentExitTimeoutMilliseconds
                    - (int)Math.Min(
                        int.MaxValue,
                        Environment.TickCount64 - started);
                if (remaining <= 0)
                {
                    AppLog.Warn(
                        $"等待旧分身端进程 {target.Process.Id} 退出超时（{AgentExitTimeoutMilliseconds} 毫秒），"
                        + "本次不下发启动任务。");
                    return false;
                }

                // 在同一个进程句柄上复核创建时间、镜像路径和会话号，再终止并等待。
                // 这样即使 PID 在两次检查之间被复用，也绝不会向新进程发出终止请求。
                if (!ProcessNativeMethods.TryTerminateProcessExact(
                        target.Process.Id,
                        identity.SessionId,
                        target.CreationTimeUtc,
                        AgentPath,
                        remaining,
                        out _,
                        out var errorCode))
                {
                    AppLog.Warn(
                        $"终止或等待旧分身端进程 {target.Process.Id} 失败（Win32 {errorCode}），"
                        + "本次不下发启动任务。");
                    return false;
                }
            }

            AppLog.Info(
                $"已在 {DescribeIdentity(identity)} 终止 {matching.Count} 个旧分身端进程，并确认其退出。");
            return true;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 判断一个进程是否是本次可替换的分身端。
    ///
    /// 纯函数，不枚举进程、不访问文件。只有同时满足以下不变量才返回 true：
    /// 捕获的会话身份完整且会话号为正数；进程会话与捕获会话号相同；期望路径和
    /// 实际镜像路径都是规范化后的完整路径；两者按 Windows 大小写不敏感语义完全相等。
    /// 任何空/无效路径或身份都拒绝。
    /// </summary>
    internal static bool IsMatchingAgentProcess(
        string? expectedAgentPath,
        ChildSessionIdentity identity,
        int? processSessionId,
        string? processImagePath)
    {
        if (!identity.IsComplete || processSessionId is null || processSessionId <= 0)
        {
            return false;
        }

        if ((uint)processSessionId.Value != identity.SessionId)
        {
            return false;
        }

        return ProcessNativeMethods.TryNormalizeImagePath(expectedAgentPath, out var expectedPath)
               && ProcessNativeMethods.TryNormalizeImagePath(processImagePath, out var actualPath)
               && string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 对一次进程枚举做三态分类。其他会话的候选被忽略；当前会话里路径精确
    /// 相等才算 Matching。存在同会话同名但路径不同的进程是 Conflict；任何
    /// 无法读取会话或镜像路径的候选、以及无效的期望路径都是 Unknown。匹配
    /// 优先于未知，未知优先于冲突——只要存在一份可信的精确匹配就可以安全复用。
    /// </summary>
    internal static AgentProcessPresence ClassifyAgentProcessPresence(
        string? expectedAgentPath,
        ChildSessionIdentity identity,
        IEnumerable<(int? SessionId, string? ImagePath)> candidates,
        IReadOnlyCollection<string>? additionalExpectedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!identity.IsComplete
            || !ProcessNativeMethods.TryNormalizeImagePath(expectedAgentPath, out var expectedPath))
        {
            return AgentProcessPresence.Unknown;
        }

        var expectedPaths = new List<string> { expectedPath };
        if (additionalExpectedPaths is not null)
        {
            foreach (var path in additionalExpectedPaths)
            {
                if (!ProcessNativeMethods.TryNormalizeImagePath(path, out var normalized))
                {
                    return AgentProcessPresence.Unknown;
                }

                expectedPaths.Add(normalized);
            }
        }

        var hasConflict = false;
        var hasUnknown = false;
        foreach (var candidate in candidates)
        {
            if (candidate.SessionId is null || candidate.SessionId <= 0)
            {
                hasUnknown = true;
                continue;
            }

            if ((uint)candidate.SessionId.Value != identity.SessionId)
            {
                continue;
            }

            if (!ProcessNativeMethods.TryNormalizeImagePath(candidate.ImagePath, out var actualPath))
            {
                hasUnknown = true;
                continue;
            }

            if (expectedPaths.Exists(path =>
                    string.Equals(path, actualPath, StringComparison.OrdinalIgnoreCase)))
            {
                return AgentProcessPresence.Matching;
            }

            hasConflict = true;
        }

        if (hasUnknown)
        {
            return AgentProcessPresence.Unknown;
        }

        return hasConflict ? AgentProcessPresence.Conflict : AgentProcessPresence.Absent;
    }

    /// <summary>
    /// 捕获本次周期唯一可信的子会话身份。会话不存在、WTS 查询失败或读不到登录时间
    /// 都返回 false；拿不到完整身份时必须失败关闭，不能退化成「只按会话号启动」。
    /// </summary>
    internal static bool TryCaptureChildSessionIdentity(
        out ChildSessionIdentity identity,
        out string error)
    {
        if (!ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId))
        {
            return TryBindChildSessionIdentity(
                querySucceeded: false,
                queriedSessionId: null,
                logonTimeUtc: null,
                out identity,
                out error);
        }

        var logonTime = sessionId is { } id
            ? ChildSessionNativeMethods.TryGetSessionLogonTime(id)
            : null;
        return TryBindChildSessionIdentity(
            querySucceeded: true,
            queriedSessionId: sessionId,
            logonTimeUtc: logonTime,
            out identity,
            out error);
    }

    /// <summary>
    /// 把一次 WTS 三态查询结果绑定成会话身份。纯函数，不访问 WTS，
    /// 便于普通单元测试覆盖「查询失败 / 没有会话 / 登录时间不可读」边界。
    /// </summary>
    internal static bool TryBindChildSessionIdentity(
        bool querySucceeded,
        uint? queriedSessionId,
        DateTime? logonTimeUtc,
        out ChildSessionIdentity identity,
        out string error)
    {
        identity = default;

        if (!querySucceeded)
        {
            error = "无法查询 RDP Child Session 身份，无法建立稳定的会话身份；"
                    + "本次不下发分身端启动任务。";
            return false;
        }

        if (queriedSessionId is not { } sessionId)
        {
            error = "分身会话尚未创建，分身端会在下次登录时由自启项拉起。";
            return false;
        }

        if (sessionId == 0)
        {
            error = $"WTS 返回了无效的分身会话号 {sessionId}，无法建立稳定的会话身份；"
                    + "本次不下发分身端启动任务。";
            return false;
        }

        if (logonTimeUtc is not { } logonTime || logonTime == default)
        {
            error = $"无法确认分身会话 {sessionId} 的登录时间，无法区分会话重建或会话号复用；"
                    + "本次不下发分身端启动任务。";
            return false;
        }

        identity = new ChildSessionIdentity(sessionId, logonTime);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 重新确认当前子会话仍是捕获时的那一个。任何查询失败、会话消失、会话号变化
    /// 或登录时间不可读/变化都返回 false，由调用方失败关闭。
    /// </summary>
    internal static bool TryConfirmChildSessionIdentity(
        ChildSessionIdentity expected,
        out string error)
    {
        if (!ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId))
        {
            return TryValidateChildSessionIdentity(
                expected,
                currentQuerySucceeded: false,
                currentSessionId: null,
                currentLogonTimeUtc: null,
                out error);
        }

        var currentLogonTime = sessionId is { } id
            ? ChildSessionNativeMethods.TryGetSessionLogonTime(id)
            : null;
        return TryValidateChildSessionIdentity(
            expected,
            currentQuerySucceeded: true,
            currentSessionId: sessionId,
            currentLogonTimeUtc: currentLogonTime,
            out error);
    }

    /// <summary>
    /// 纯身份校验边界：只比较已捕获身份与重新查询到的结果，不访问 WTS。
    /// 登录时间不可读时失败关闭；绝不在会话号相同但登录时间不同的情况下继续。
    /// </summary>
    internal static bool TryValidateChildSessionIdentity(
        ChildSessionIdentity expected,
        bool currentQuerySucceeded,
        uint? currentSessionId,
        DateTime? currentLogonTimeUtc,
        out string error)
    {
        if (!expected.IsComplete)
        {
            error = "已捕获的分身会话身份不完整（会话 "
                    + $"{expected.SessionId}，登录时间 {FormatLogonTime(expected.LogonTimeUtc)}），"
                    + "本次操作失败关闭。";
            return false;
        }

        if (!currentQuerySucceeded)
        {
            error = "无法重新确认分身会话身份（WTS Child Session 查询失败），本次操作失败关闭。";
            return false;
        }

        if (currentSessionId is not { } sessionId)
        {
            error = $"分身会话已消失（捕获时 {DescribeIdentity(expected)}），本次操作失败关闭。";
            return false;
        }

        if (sessionId != expected.SessionId)
        {
            error = $"分身会话已从 {DescribeIdentity(expected)} 变为会话 {sessionId}，"
                    + "本次操作失败关闭。";
            return false;
        }

        if (currentLogonTimeUtc is not { } logonTime || logonTime == default)
        {
            error = $"无法确认分身会话 {sessionId} 的登录时间，无法排除会话重建或会话号复用；"
                    + "本次操作失败关闭。";
            return false;
        }

        if (logonTime.Ticks != expected.LogonTimeUtc.Ticks)
        {
            error = $"分身会话 {sessionId} 已重建或会话号被复用（登录时间从 "
                    + $"{FormatLogonTime(expected.LogonTimeUtc)} 变为 {FormatLogonTime(logonTime)}），"
                    + "本次操作失败关闭。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string DescribeIdentity(ChildSessionIdentity identity) =>
        $"会话 {identity.SessionId}（登录时间 {FormatLogonTime(identity.LogonTimeUtc)}）";

    private static string FormatLogonTime(DateTime logonTimeUtc) =>
        logonTimeUtc == default ? "<未知>" : logonTimeUtc.ToString("O");

    /// <summary>
    /// 只看心跳文件判断分身端是否还在写心跳（不含启动宽限）。
    ///
    /// 读文件时会撞上代理正在写：两边都用 <see cref="FileShare.ReadWrite"/> 打开就不会互斥。
    /// 即便这样仍可能失败（杀软扫描等），因此**单次失败不算掉线**：
    /// 连续 <see cref="HeartbeatReadFailureLimit"/> 次读不到才认，中间那次按「未知」跳过。
    /// </summary>
    private bool IsHeartbeatFresh()
    {
        var now = Environment.TickCount64;
        if (now - _lastHeartbeatCheckTicks < (long)HeartbeatCheckInterval.TotalMilliseconds)
        {
            return _lastHeartbeatFresh;
        }

        _lastHeartbeatCheckTicks = now;
        _lastHeartbeatFresh = ProbeHeartbeat();
        return _lastHeartbeatFresh;
    }

    private bool ProbeHeartbeat()
    {
        long unixSeconds;
        try
        {
            using var stream = new FileStream(
                HeartbeatPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (!long.TryParse(reader.ReadToEnd().Trim(), out unixSeconds))
            {
                _consecutiveHeartbeatReadFailures = 0;
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _consecutiveHeartbeatReadFailures++;

            // 单次冲突不当掉线：这一轮按「未知」处理，留给下一轮再确认。
            AppLog.Info($"读取分身端心跳失败（第 {_consecutiveHeartbeatReadFailures} 次）：{exception.Message}");
            return _consecutiveHeartbeatReadFailures < HeartbeatReadFailureLimit;
        }

        _consecutiveHeartbeatReadFailures = 0;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return age >= TimeSpan.Zero && age < HeartbeatFreshWindow;
    }

    /// <summary>
    /// 启动项隔离期间停用热键/MCP 时，只停止当前子会话中精确匹配的代理。
    /// 不创建、不删除、不改写任何启动项或代理文件；当前根本没有子会话时是安全的空操作。
    /// WTS 查询失败、会话身份不完整或进程身份不可证明时必须失败关闭。
    /// </summary>
    private AgentStopResult StopAgentForStartupIsolation()
    {
        lock (_agentStartSync)
        {
            if (!ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId))
            {
                return new AgentStopResult(
                    false,
                    "无法查询 RDP Child Session，不能安全确认是否仍有旧分身端在运行。");
            }

            if (sessionId is null)
            {
                return new AgentStopResult(true, string.Empty);
            }

            var childSessionId = sessionId.Value;
            var logonTimeUtc = ChildSessionNativeMethods.TryGetSessionLogonTime(childSessionId);
            if (!TryBindChildSessionIdentity(
                    querySucceeded: true,
                    queriedSessionId: childSessionId,
                    logonTimeUtc: logonTimeUtc,
                    out var identity,
                    out var identityError))
            {
                return new AgentStopResult(false, identityError);
            }

            if (!TryStopExistingAgentProcess(identity, out _))
            {
                return new AgentStopResult(
                    false,
                    $"未能安全停止分身会话 {DescribeIdentity(identity)} 中的旧代理。");
            }

            // 下次启动判断必须重新枚举，不能沿用停用前缓存的「在线」结果。
            _lastAgentProbeTicks = -100_000;
            _lastAgentProbeResult = false;
            _lastLaunchTickCount = -1;
            return new AgentStopResult(true, string.Empty);
        }
    }

    /// <summary>
    /// 卸载分身端：删登录任务与旧版 Run、停轮询、删代理与日志。
    /// </summary>
    /// <param name="removeStartupEntry">
    /// 启动项隔离期间为 false。此时任务和 Run 值已由隔离日志关停，不能删除、改写
    /// 或删除被它引用的代理文件，否则恢复日志时无法还原原来的自启状态；但必须停止
    /// 当前子会话中仍在运行的旧代理，避免已停用的热键/MCP 继续生效。
    /// </param>
    /// <param name="removeShellGate">
    /// 仅在确认没有子会话、启动项也已恢复正常时允许删除替代资源管理器门卫。
    /// 隔离期间 removeStartupEntry 为 false 时该参数不生效，门卫文件必须保留供
    /// 当前 RDP 连接和后续恢复使用。
    /// </param>
    /// <returns>
    /// 启动项已确认删除且代理文件清理完成时返回 true。任务或 Run 删除失败时
    /// 返回 false，并保留代理文件，避免留下一个指向已删除程序的登录任务。
    /// </returns>
    public bool Uninstall(bool removeStartupEntry = true, bool removeShellGate = true)
    {
        bool uninstalled;
        lock (_agentInstallSync)
        {
            _pollingDesired = false;
            uninstalled = UninstallCore(removeStartupEntry, removeShellGate);
        }

        ReconcilePolling();
        return uninstalled;
    }

    private bool UninstallCore(bool removeStartupEntry, bool removeShellGate)
    {
        if (!removeStartupEntry)
        {
            var stopResult = _stopIsolatedAgent();
            if (!stopResult.Success)
            {
                AppLog.Warn($"隔离期间停用分身端失败，保留全部启动项和代理文件：{stopResult.Error}");
                return false;
            }

            return true;
        }

        if (_manageLegacyDefaultInstallation)
        {
            if (!_startupRegistration.TryDeleteLogonTask(out var taskError))
            {
                AppLog.Warn($"删除分身端登录任务失败，保留代理文件：{taskError}");
                return false;
            }

            try
            {
                // 卸载时没有先前读取值；生产实现会在同一个键句柄里重新读取并验证
                // 命令目标，foreign/伪造的同名 Run 值一律拒绝删除。
                _startupRegistration.DeleteRunValue(expectedCommandLine: null);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or System.Security.SecurityException
                                                  or IOException
                                                  or InvalidDataException
                                                  or InvalidOperationException)
            {
                // Run 仍可能在下次登录时拉起代理，因此不能在删除失败后把目标文件删掉。
                AppLog.Warn("删除分身端旧版 Run 自启项失败，保留代理文件。", exception);
                return false;
            }
        }

        var agentDeleted = TryDeleteStrict(AgentPath, "分身端代理文件");
        var legacyDeleted = !_manageLegacyDefaultInstallation
            || TryDeleteStrict(_legacyAgentPath, "旧版分身端代理文件");
        if (!agentDeleted || !legacyDeleted)
        {
            return false;
        }

        TryDelete(RequestPath);
        TryDelete(HeartbeatPath);
        TryDelete(Path.Combine(_dataDirectory, AgentLogFileName));

        if (removeShellGate && _manageLegacyDefaultInstallation)
        {
            // 只有正常卸载路径允许移除门卫及其更新别名；此时启动任务和 Run
            // 均已删除，不再有迟到的子会话可引用 StartProgram。
            if (!TryDeleteStrict(_shellGatePath, "外壳门卫文件")
                || !TryDeleteStrict(_shellGatePath + ".old", "旧版外壳门卫文件"))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollingDesired = false;
        ReconcilePolling();
    }

    /// <summary>
    /// 把「是否需要轮询」的最终状态发布到 UI Dispatcher。安装和卸载可能在线程池
    /// 交错完成，不能直接按调用先后 Start/Stop；回调在 UI 线程重新读取最新意愿，
    /// 以最后一次安装/卸载为准，避免卸载后又被较慢的安装回调重新拉起计时器。
    /// </summary>
    private void ReconcilePolling()
    {
        RunOnDispatcher(() =>
        {
            if (_disposed || !_pollingDesired)
            {
                StopPollingCore();
                return;
            }

            StartPollingCore();
        });
    }

    private void StartPollingCore()
    {
        if (_disposed || _pollTimer is not null)
        {
            return;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 切换本身要断开、重连、等 UU 重新挑会话，好几秒起步，
            // 这里没必要做毫秒级响应；1.5 秒一次足够，也更省电。
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPollingCore()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
    }

    /// <summary>
    /// 启动阶段会把代理的文件/注册表/计划任务操作搬到线程池；DispatcherTimer
    /// 仍只能在创建它的 UI Dispatcher 上启停。这里异步排队而不阻塞线程池：
    /// UI 线程可能正等待同一段安装锁，同步 Invoke 会形成互相等待；异步回调执行时
    /// 会重新读取最新的轮询意愿，因此连续安装/卸载仍以最后一次状态为准。
    /// </summary>
    private void RunOnDispatcher(Action action)
    {
        if (IsDispatcherShuttingDown())
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException) when (IsDispatcherShuttingDown())
        {
            // 后台安装刚好跨过窗口关闭：没有 UI 可承载计时器，停止轮询即可。
        }
    }

    private bool IsDispatcherShuttingDown() =>
        _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished;

    private void OnPollTick(object? sender, EventArgs e)
    {
        // Dispatcher 停止轮询的操作是异步排队的；在它执行前可能还排着一次
        // 已经触发的 Tick。卸载/释放后必须让它立即失效。
        if (_disposed || !_pollingDesired || !File.Exists(RequestPath))
        {
            return;
        }

        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(RequestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // 同一个文件只处理一次：分身端每次命中都会重写它，修改时间随之推进。
        if (modified <= _lastRequestSeen)
        {
            return;
        }

        _lastRequestSeen = modified;

        var age = DateTime.UtcNow - modified;
        if (age > RequestMaxAge || age < TimeSpan.FromSeconds(-5))
        {
            // 过期请求（关机前残留）或时钟异常，忽略。
            AppLog.Info($"忽略过期的分身端切换请求（{age.TotalSeconds:N0} 秒前）。");
            return;
        }

        AppLog.Info("收到分身端的热键请求，开始切换。");
        _dispatcher.BeginInvoke(() => SwitchRequested?.Invoke(this, EventArgs.Empty));
    }

    private string BuildAgentArguments()
    {
        var process = Process.GetCurrentProcess();
        return BuildAgentArguments(
            _hotkeyArgument,
            _controlPipeArgument,
            process.SessionId);
    }

    /// <summary>
    /// 显式下发启动任务时，把宿主捕获的会话身份一并交给分身端。
    /// 自启项不携带这组参数，因为它必须能在下一次登录时启动；只有宿主主动
    /// 拉起的代理才需要证明自己仍运行在预期的那个会话里。
    /// </summary>
    private string BuildManagedAgentArguments(ChildSessionIdentity identity)
    {
        if (!identity.IsComplete)
        {
            throw new ArgumentException("显式启动分身端需要完整的会话登录身份。", nameof(identity));
        }

        var process = Process.GetCurrentProcess();
        return BuildAgentArguments(
            _hotkeyArgument,
            _controlPipeArgument,
            process.SessionId,
            identity.SessionId,
            identity.LogonTimeUtc);
    }

    internal static string BuildAgentArguments(
        string? hotkey,
        string? controlPipe,
        int hostSessionId = 0,
        uint? expectedSessionId = null,
        DateTime? expectedLogonTimeUtc = null)
    {
        if (!string.IsNullOrWhiteSpace(controlPipe) && hostSessionId <= 0)
        {
            throw new ArgumentException("控制通道必须携带正整数宿主会话身份。", nameof(hostSessionId));
        }

        if (expectedSessionId.HasValue != expectedLogonTimeUtc.HasValue)
        {
            throw new ArgumentException("会话身份必须同时包含会话号与登录时间。");
        }

        if (expectedSessionId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSessionId), "会话号必须为正整数。");
        }

        if (expectedLogonTimeUtc is { } logonTime && logonTime == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedLogonTimeUtc),
                "登录时间必须为有效的 UTC 时间。");
        }

        var builder = new StringBuilder();
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            builder.Append("--no-hotkey");
        }
        else
        {
            builder.Append("--hotkey \"").Append(hotkey).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(controlPipe))
        {
            builder.Append(" --control-pipe \"").Append(controlPipe).Append('"');
            builder.Append(" --host-session ").Append(hostSessionId);
        }

        if (expectedSessionId is { } sessionId && expectedLogonTimeUtc is { } expectedLogon)
        {
            builder.Append(" --expected-session ").Append(
                sessionId.ToString(CultureInfo.InvariantCulture));
            builder.Append(" --expected-logon-time ").Append(
                expectedLogon.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string? ReadRunKey()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) as string;
    }

    /// <summary>
    /// 把内嵌的分身端 exe 释放到数据目录；内容有变化才重写。
    /// </summary>
    /// <returns>是否真的写了新文件。调用方据此决定要不要重启分身端。</returns>
    /// <summary>
    /// 释放内嵌分身端的结果。
    ///
    /// 把「替换失败但已回滚」单独列出来：这种情况下路径上是一个**能用的旧代理**，
    /// 轮询要照常开着，只是要如实告诉用户「这次没更新成」。
    /// </summary>
    private enum AgentWriteOutcome
    {
        Unchanged,
        Updated,
        FailedRolledBack
    }

    private void EnsureAgentDirectory()
    {
        if (!_protectAgentDirectory)
        {
            Directory.CreateDirectory(_agentDirectory);
            return;
        }

        // 生产目录是 %ProgramData%\DesktopTwin\Agent，父目录必须一并加固；
        // 测试使用隔离目录时也只保护它的直接父级，绝不触碰 ProgramData。
        var root = Directory.GetParent(_agentDirectory)?.FullName
            ?? throw new IOException("代理目录不能是卷根目录，拒绝创建。");
        EnsureProtectedDirectory(root);
        EnsureProtectedDirectory(_agentDirectory);
    }

    /// <summary>
    /// 以原子方式创建（或重新加固）管理员专用目录。普通用户只拿到读取/遍历
    /// 权限，不能预置文件、替换目录联接，也不能在提升权限的释放过程中改写代理。
    /// 已存在目录只在所有者可信时才重新设置 DACL；所有者不可信时直接失败关闭。
    /// </summary>
    private static void EnsureProtectedDirectory(string path)
    {
        var exists = Directory.Exists(path);
        if (exists)
        {
            ValidateTrustedOwner(path, isDirectory: true);
            ValidateNotReparsePoint(path, isDirectory: true);
            new DirectoryInfo(path).SetAccessControl(BuildDirectorySecurity(setOwner: false));
        }
        else
        {
            // DirectoryInfo.Create(DirectorySecurity) 在创建时就带上 DACL，
            // 不存在“先创建、后收紧”的可利用窗口。
            new DirectoryInfo(path).Create(BuildDirectorySecurity(setOwner: true));
        }

        ValidateProtectedDirectory(path);
    }

    internal static DirectorySecurity BuildDirectorySecurity(bool setOwner)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        if (setOwner)
        {
            security.SetOwner(new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null));
        }

        return security;
    }

    internal static FileSecurity BuildFileSecurity(bool setOwner)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));

        if (setOwner)
        {
            security.SetOwner(new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null));
        }

        return security;
    }

    private static void ProtectAgentFile(string path)
    {
        // 先确认不是重解析点，再把所有者改为 Administrators。文件所有者即使
        // 没有显式写权限也能改 DACL，所以不能保留创建者（普通用户）作为所有者。
        ValidateNotReparsePoint(path, isDirectory: false);
        new FileInfo(path).SetAccessControl(BuildFileSecurity(setOwner: true));
        ValidateProtectedFile(path);
    }

    private static void ValidateProtectedDirectory(string path)
    {
        ValidateTrustedOwner(path, isDirectory: true);
        ValidateNotReparsePoint(path, isDirectory: true);
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
        {
            throw new IOException("代理目录的 DACL 仍受父目录继承控制，拒绝使用。");
        }

        ValidateAccessRules(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)));
    }

    private static void ValidateProtectedFile(string path)
    {
        ValidateTrustedOwner(path, isDirectory: false);
        ValidateNotReparsePoint(path, isDirectory: false);
        var security = new FileSecurity(path, AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
        {
            throw new IOException("代理文件的 DACL 仍受父目录继承控制，拒绝使用。");
        }

        ValidateAccessRules(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)));
    }

    internal static void ValidateAccessRules(AuthorizationRuleCollection rules)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const FileSystemRights writeRights =
            FileSystemRights.Write
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        var systemRights = (FileSystemRights)0;
        var administratorRights = (FileSystemRights)0;
        var userRights = (FileSystemRights)0;

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                throw new IOException("代理路径包含拒绝规则，拒绝使用。");
            }

            // 只继承给子对象的 ACE 不作用于当前目录/文件。把它计入必需权限
            // 会让一个实际不可访问的 DACL 被误判为安全。
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                throw new IOException("代理路径包含仅继承规则，拒绝使用。");
            }

            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (identity.Equals(administrators))
            {
                administratorRights |= rule.FileSystemRights;
                continue;
            }

            if (identity.Equals(system))
            {
                systemRights |= rule.FileSystemRights;
                continue;
            }

            if (identity.Equals(users))
            {
                if ((rule.FileSystemRights & writeRights) != 0)
                {
                    throw new IOException("代理路径授予普通用户写入权限，拒绝使用。");
                }

                userRights |= rule.FileSystemRights;
                continue;
            }

            throw new IOException("代理路径包含非管理员身份，拒绝使用。");
        }

        // 只拒绝“多余权限”还不够：空 DACL 或缺少管理员完全控制的 DACL
        // 会让后续替换代理失败，也可能把读取权限误当成可执行权限。这里要求
        // 三个最小允许 ACE 的实际权限位都存在。
        if ((systemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
        {
            throw new IOException("代理路径缺少 SYSTEM 完全控制权限，拒绝使用。");
        }

        if ((administratorRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
        {
            throw new IOException("代理路径缺少 Administrators 完全控制权限，拒绝使用。");
        }

        if ((userRights & FileSystemRights.ReadAndExecute) != FileSystemRights.ReadAndExecute)
        {
            throw new IOException("代理路径缺少 Builtin Users 读取/执行权限，拒绝使用。");
        }
    }

    internal static void ValidateTrustedOwner(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileSecurity(path, AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null
            || (!owner.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
                && !owner.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))))
        {
            throw new IOException("代理路径的所有者不是 Administrators/SYSTEM，拒绝使用。");
        }
    }

    internal static void ValidateNotReparsePoint(string path, bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var current = isDirectory
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath).Directory;

        // 只检查最后一级不够：普通用户无法改写受保护的 ProgramData 子目录，
        // 但仍要把祖先目录联接/符号链接一并拒绝，避免路径在检查后被重定向。
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"代理路径的祖先目录是重解析点，拒绝使用：{current.FullName}");
            }

            current = current.Parent;
        }

        var attributes = isDirectory
            ? new DirectoryInfo(fullPath).Attributes
            : File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("代理路径是重解析点，拒绝使用。");
        }
    }

    private AgentWriteOutcome WriteAgentIfChanged() =>
        WriteExecutableIfChanged(AgentPath, "分身端", _protectAgentDirectory);

    /// <summary>
    /// 原子释放内嵌可执行文件。分身端与外壳门卫共享同一份字节和同一套
    /// 失败关闭规则；调用方只有在返回 Unchanged/Updated 后才能使用目标路径。
    /// </summary>
    private static AgentWriteOutcome WriteExecutableIfChanged(
        string targetPath,
        string displayName,
        bool protect)
    {
        var assembly = typeof(SessionAgentService).Assembly;
        var resourceName = ResolveAgentResourceName(assembly);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(Locale.Get("Agent.EmbeddedMissing"));

        var buffer = new byte[stream.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        // 内容没变就不重写：重写会推进修改时间，分身端据此判断「自己是不是旧版本」，
        // 平白让所有代理互杀一轮。
        if (File.Exists(targetPath))
        {
            // 每次复用前重新验证并加固 DACL，防止旧版本或外部工具留下可写 ACL。
            if (protect)
            {
                ProtectAgentFile(targetPath);
            }
            try
            {
                var existing = File.ReadAllBytes(targetPath);
                if (existing.Length == buffer.Length && BytesEqual(existing, buffer))
                {
                    return AgentWriteOutcome.Unchanged;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 读不到就当变了，重写一份。
            }
        }

        // 先写临时文件，再把旧文件「改名让位」，最后把新文件搬到正式名字上。
        //
        // 不能直接覆盖或删除旧文件：正在运行的程序把自己的 exe 映射住了，
        // 删除/覆盖都会失败（Device or resource busy）。但 Windows 允许**重命名**
        // 正在运行的可执行文件——旧进程继续用改名后的文件跑，新文件占回原名字。
        var tempPath = targetPath + ".new";
        var retiredPath = targetPath + ".old";
        DeleteFileIfExists(tempPath);
        // 先创建空文件并加固，再写入内容；不能先写完整可执行文件再收紧 ACL。
        using (File.Create(tempPath))
        {
        }

        if (protect)
        {
            ProtectAgentFile(tempPath);
        }

        File.WriteAllBytes(tempPath, buffer);
        if (protect)
        {
            // 写入后再次以磁盘对象为准核对，防止临时路径在两步之间被替换。
            ValidateProtectedFile(tempPath);
        }

        if (File.Exists(targetPath))
        {
            TryDelete(retiredPath);
            try
            {
                File.Move(targetPath, retiredPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 连改名都不行（极少见）：留着旧程序继续跑，下次启动再换。
                AppLog.Warn($"旧{displayName}无法改名让位，本次沿用旧版本。", exception);
                TryDelete(tempPath);
                return AgentWriteOutcome.Unchanged;
            }
        }

        try
        {
            File.Move(tempPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 旧文件刚才已经被改名让位，这里再失败，targetPath 上就什么都没有了。
            AppLog.Warn($"释放新{displayName}失败，回滚到旧版本。", exception);

            try
            {
                if (!File.Exists(targetPath) && File.Exists(retiredPath))
                {
                    File.Move(retiredPath, targetPath);
                }
            }
            catch (Exception rollbackException) when (rollbackException is IOException
                                                          or UnauthorizedAccessException)
            {
                AppLog.Error($"回滚旧{displayName}失败，可能无法启动。", rollbackException);
            }

            TryDelete(tempPath);

            if (!File.Exists(targetPath))
            {
                // 回滚也没能留下可执行文件：这次安装是失败的，不能当成「内容没变」。
                throw new IOException(
                    $"{displayName}安装失败：目标路径没有可用的可执行文件。", exception);
            }

            // 回滚成功说明旧版本还在、还能用；但这次替换确实失败了，必须如实上报。
            return AgentWriteOutcome.FailedRolledBack;
        }

        // 移动完成后重新核对正式文件，防止替换过程中目标被外部改写或 ACL 被放宽。
        if (protect)
        {
            ValidateProtectedFile(targetPath);
        }

        // 旧文件此刻可能还被旧进程占着，删不掉也无所谓：下次启动还会再试一次。
        TryDelete(retiredPath);

        AppLog.Info($"{displayName}已释放：{targetPath}");
        return AgentWriteOutcome.Updated;
    }

    /// <summary>
    /// 找到内嵌分身端的资源名。
    ///
    /// 正常情况下 `LogicalName` 会把它固定成 <c>DesktopTwin.Agent.exe</c>；
    /// 但 MSBuild 的资源命名步骤一旦早于条目添加（比如构建目标挂错了位置），
    /// 名字就会退回默认的「命名空间 + 路径」形式。这里按后缀兜一层，
    /// 免得因为一个打包细节让整个程序起不来——上一次就是这么炸的。
    /// </summary>
    private static string ResolveAgentResourceName(System.Reflection.Assembly assembly)
    {
        if (assembly.GetManifestResourceInfo(AgentResourceName) is not null)
        {
            return AgentResourceName;
        }

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(AgentResourceName, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"分身端资源名是 {name}，不是预期的 {AgentResourceName}（已按后缀匹配）。");
                return name;
            }
        }

        throw new InvalidOperationException(
            "程序内没有找到内嵌的分身端（DesktopTwin.Agent.exe）。这是打包错误，请重新安装。"
            + "现有资源：" + string.Join(", ", assembly.GetManifestResourceNames()));
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 删除本产品拥有的 HKCU Run 值。键不存在或值已不存在都视为成功；
    /// 同名但无法证明指向当前/旧版代理的命令、非字符串值、以及读取后又被改写的
    /// 值都必须失败关闭，绝不能按名称盲删用户或其它程序的数据。
    /// </summary>
    private static void DeleteManagedRunValue(
        string agentPath,
        string legacyAgentPath,
        string? expectedCommandLine)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        var currentValue = key.GetValue(RunValueName);
        if (currentValue is null)
        {
            return;
        }

        if (currentValue is not string currentCommandLine)
        {
            throw new InvalidDataException(
                $"拒绝删除非字符串的 {RunValueName} Run 值。");
        }

        if (expectedCommandLine is not null
            && !string.Equals(
                currentCommandLine,
                expectedCommandLine,
                StringComparison.Ordinal))
        {
            throw new IOException(
                $"{RunValueName} Run 值在检查后已改变，拒绝删除。");
        }

        if (!IsManagedAgentRunValue(currentCommandLine, agentPath, legacyAgentPath))
        {
            throw new InvalidDataException(
                $"拒绝删除不属于本产品的 {RunValueName} Run 值。");
        }

        key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 删除普通文件；不存在视为成功。重解析点、目录或删除失败都必须让调用方
    /// 失败关闭，不能因为 File.Exists 对悬空链接返回 false 就继续写入并跟随链接。
    /// </summary>
    private static void DeleteFileIfExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"拒绝删除重解析点：{path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException($"期望文件但发现目录：{path}");
            }

            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // 文件不存在，正常的新建路径。
        }
        catch (DirectoryNotFoundException)
        {
            // 父目录不存在，由后续写入明确失败。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            DeleteFileIfExists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"删除文件失败：{path}", exception);
        }
    }

    private static bool TryDeleteStrict(string path, string description)
    {
        try
        {
            DeleteFileIfExists(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"删除{description}失败：{path}", exception);
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SessionAgentService));
        }
    }
}
