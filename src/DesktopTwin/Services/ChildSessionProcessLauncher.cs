using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 把目标程序启动到指定的分身会话中。
///
/// 为什么不能直接 <see cref="System.Diagnostics.Process.Start(string)"/>：
/// 进程只能在自己所属的 Windows 会话里创建窗口，跨会话启动必须借道「任务计划程序」——
/// 以交互令牌注册一个一次性任务，再用 RunEx 指定目标会话号运行，最后立刻删除任务。
/// </summary>
public static class ChildSessionProcessLauncher
{
    /// <summary>
    /// 临时计划任务的前缀。启动后会被删除，异常退出时可能在系统里残留。
    /// </summary>
    private const string TaskNamePrefix = "DesktopTwin-SessionLaunch-";

    /// <summary>
    /// 分身端代理使用的临时计划任务前缀。
    /// </summary>
    private const string SessionAgentTaskNamePrefix = "DesktopTwin-SessionAgent-";

    /// <summary>
    /// 分身端代理的持久登录任务名。
    ///
    /// 这个名字故意不以临时任务前缀结尾，避免 <see cref="SweepOrphanTasks()"/>
    /// 把用户登录后仍应保留的任务当成异常残留删掉。
    /// </summary>
    internal const string SessionAgentStartupTaskName = "DesktopTwin-SessionAgent-Autostart";

    /// <summary>
    /// 持久登录任务的固定描述。创建、覆盖和删除都必须用同一份元数据校验，
    /// 防止同名但来源不同的任务被误改或误删。
    /// </summary>
    internal const string SessionAgentStartupTaskDescription = "DesktopTwin 分身端登录自启";

    /// <summary>
    /// 本程序注册任务时写入的作者。创建和清扫都必须使用这一份常量。
    /// </summary>
    internal const string TaskAuthor = "DesktopTwin";

    /// <summary>
    /// 本程序注册临时任务时写入的描述前缀。创建和清扫都必须使用这一份常量。
    /// </summary>
    internal const string TaskDescriptionPrefix = "临时启动 ";

    /// <summary>
    /// 临时任务描述中文件名与会话号之间的分隔符。
    /// </summary>
    internal const string TaskDescriptionSessionSeparator = " 到分身会话 ";

    /// <summary>
    /// 临时任务的延迟清理时间：要留够「任务真正拉起进程」的余量。
    /// </summary>
    private static readonly TimeSpan TaskCleanupDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 用户级 COM 注册所在的根路径。
    ///
    /// 与 <see cref="TaskSchedulerClsid"/> 同理，只写一次。守卫要拼它去查 HKCU，
    /// 测试要拼它去验证「这个位置真的存在」——如果测试用自己的字面量拼路径，
    /// 生产把 <c>Classes</c> 写成 <c>Class</c> 时测试照样绿，守卫永远不触发，
    /// 那种测试比没有测试更糟：它给出虚假的安全感。
    /// </summary>
    internal const string UserClassesPrefix = @"Software\Classes";

    /// <summary>
    /// <c>Schedule.Service</c> 的 COM 类标识。
    ///
    /// 只在这里写一次：守卫要拿它去查 HKCU 覆盖，报错要拿它告诉用户查哪个键，
    /// HKLM 校验要拿它比对 ProgID 指向。三处各写一份字面量的话，
    /// 改漏一处就会出现「检查的是 A、报给用户的是 B」，而两边都不报错。
    /// </summary>
    internal const string TaskSchedulerClsid = "{0f87369f-a4e5-4cfc-bd3e-73e6154572dd}";

    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskTriggerLogon = 9;
    private const int TaskInstancesIgnoreNew = 2;

    /// <summary>
    /// 以最高权限运行（管理员）。
    /// </summary>
    private const int TaskRunLevelHighest = 1;

    /// <summary>
    /// 以当前用户的普通（未提权）权限运行。
    /// </summary>
    private const int TaskRunLevelLimited = 0;

    /// <summary>
    /// 使用指定的会话号运行任务。
    /// </summary>
    private const int TaskRunUseSessionId = 0x4;

    /// <summary>
    /// 在指定会话中启动目标程序。
    /// </summary>
    /// <param name="childSessionId">目标分身会话号。</param>
    /// <param name="executablePath">目标程序完整路径。</param>
    /// <param name="arguments">命令行参数。</param>
    /// <param name="runAsAdministrator">是否以管理员权限运行。</param>
    public static Task LaunchAsync(
        uint childSessionId,
        string executablePath,
        string arguments,
        bool runAsAdministrator)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var workingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        return Task.Run(() => LaunchWithTemporaryTask(
            childSessionId,
            fullPath,
            arguments ?? string.Empty,
            workingDirectory,
            runAsAdministrator));
    }

    /// <summary>
    /// 校验目标程序路径：必须是本地驱动器上的绝对路径，存在且为 .exe。
    ///
    /// 这里刻意拒绝 UNC 路径与设备路径（<c>\\?\</c>、<c>\\.\</c>）：
    /// 本程序以管理员权限运行，允许它拉起网络共享上的可执行文件，
    /// 等于把提权能力交给共享目录的所有者。
    /// </summary>
    public static string ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("目标程序路径不能为空。", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);

        if (!Path.IsPathFullyQualified(fullPath)
            || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "只允许启动本地驱动器上的程序，不接受网络路径或设备路径。",
                nameof(executablePath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只允许启动 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

    /// <summary>
    /// 可注入的任务计划程序根目录视图。
    ///
    /// 生产实现只负责 COM 读写；所有“该不该删”的判定都在不接触 COM 的
    /// <see cref="SweepOrphanTasks(IOrphanTaskFolder)"/> 里，测试可以安全地注入假实现。
    /// </summary>
    internal interface IOrphanTaskFolder
    {
        IReadOnlyList<string> GetTaskNames();

        bool TryGetRegistrationInfo(string taskName, out string? author, out string? description);

        void DeleteTask(string taskName);
    }

    /// <summary>
    /// 清扫上一次异常退出遗留的临时计划任务。
    ///
    /// 正常流程会在启动后立刻删除任务；如果进程在注册与删除之间被结束，任务就会留在系统里。
    /// 这些残留任务带的是本程序的身份与目标程序路径，必须在下次启动时清掉。
    /// </summary>
    public static void SweepOrphanTasks()
    {
        object? schedulerObject = null;
        object? rootFolderObject = null;
        try
        {
            // 清扫同样会 CoCreateInstance("Schedule.Service")，而且是在启动时立刻执行，
            // 不校验就等于给了一条不经过 LaunchWithTemporaryTask 的劫持通道。
            // 这里只查 HKCU（fail-closed）；HKLM 那层是兜底，读不到也没必要阻止清扫。
            EnsureNoUserClassOverride();

            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return;
            }

            schedulerObject = Activator.CreateInstance(schedulerType);
            if (schedulerObject is null)
            {
                return;
            }

            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            rootFolderObject = scheduler.GetFolder("\\");
            if (rootFolderObject is null)
            {
                return;
            }

            SweepOrphanTasks(new ComOrphanTaskFolder(rootFolderObject));
        }
        catch (InvalidOperationException exception)
        {
            // 守卫判定不可信（或注册表读不到）。清扫只是清理垃圾，没必要因此让程序起不来，
            // 但要留下记录：真正启动程序时同一个守卫会再次拦下。
            AppLog.Warn("跳过残留计划任务清扫：COM 注册未通过校验。", exception);
        }
        catch (Exception exception)
        {
            // 清扫是启动时的尽力而为操作：COM 不可用、权限被拒或任务已被并发删除，
            // 都不能让进程崩掉，更不能在不确定来源时删除任务。
            AppLog.Warn("清扫残留计划任务失败，忽略。", exception);
        }
        finally
        {
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>
    /// 不接触真实任务计划程序的清扫核心。任何读取或删除失败都只跳过当前候选。
    /// </summary>
    internal static void SweepOrphanTasks(IOrphanTaskFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string[] taskNames;
        try
        {
            taskNames = [.. folder.GetTaskNames()];
        }
        catch (Exception exception)
        {
            AppLog.Warn("枚举残留计划任务失败，跳过清扫。", exception);
            return;
        }

        foreach (var taskName in taskNames)
        {
            if (!IsManagedTemporaryTaskName(taskName))
            {
                continue;
            }

            bool metadataRead;
            string? author;
            string? description;
            try
            {
                metadataRead = folder.TryGetRegistrationInfo(taskName, out author, out description);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"读取临时计划任务 {taskName} 的注册信息失败，跳过清扫。", exception);
                continue;
            }

            if (!metadataRead)
            {
                AppLog.Warn($"临时计划任务 {taskName} 的注册信息不可读取，跳过清扫。");
                continue;
            }

            if (!HasExpectedTaskRegistrationInfo(author, description))
            {
                AppLog.Warn($"临时计划任务 {taskName} 的作者或描述不是 DesktopTwin 写入的，跳过清扫。");
                continue;
            }

            try
            {
                folder.DeleteTask(taskName);
                AppLog.Info($"已清理残留的临时计划任务 {taskName}");
            }
            catch (Exception exception)
            {
                AppLog.Warn($"清理残留计划任务 {taskName} 失败。", exception);
            }
        }
    }

    /// <summary>
    /// 创建或更新当前用户的交互式登录任务。
    ///
    /// 该任务只在用户登录时触发，使用交互令牌、普通权限，不保存密码，也不注册开机触发器。
    /// 任务名固定，因此重复安装是幂等的；覆盖前会先校验同名任务的作者与描述，
    /// 避免把用户或其他程序创建的同名任务误当成自己的。
    /// </summary>
    internal static void CreateOrUpdateSessionAgentLogonTask(string executablePath, string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var fullPath = ValidateExecutablePath(executablePath);
        var workingDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("分身端代理没有可用的工作目录。", nameof(executablePath));
        }

        var accountName = GetCurrentUserAccountName();
        EnsureTaskSchedulerIsTrusted();

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? existingTaskObject = null;
        object? existingDefinitionObject = null;
        object? existingInfoObject = null;
        object? taskDefinitionObject = null;
        object? registrationInfoObject = null;
        object? settingsObject = null;
        object? principalObject = null;
        object? triggersObject = null;
        object? actionsObject = null;
        object? triggerObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;

            // 同名任务可能是用户或其他程序创建的。覆盖之前必须确认来源；
            // 读不到元数据时一律拒绝，不能拿 TASK_CREATE_OR_UPDATE 去赌。
            try
            {
                existingTaskObject = rootFolder.GetTask(SessionAgentStartupTaskName);
            }
            catch (Exception exception) when (IsTaskNotFound(exception))
            {
                existingTaskObject = null;
            }

            if (existingTaskObject is not null)
            {
                dynamic existingTask = existingTaskObject;
                existingDefinitionObject = existingTask.Definition;
                dynamic existingDefinition = existingDefinitionObject;
                existingInfoObject = existingDefinition.RegistrationInfo;
                dynamic existingInfo = existingInfoObject;
                if (!HasExpectedSessionAgentTaskRegistrationInfo(
                        existingInfo.Author as string,
                        existingInfo.Description as string))
                {
                    throw new InvalidOperationException(
                        $"计划任务 {SessionAgentStartupTaskName} 已存在，但不是 DesktopTwin 创建的同名任务，拒绝覆盖。");
                }
            }

            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject;

            registrationInfoObject = taskDefinition.RegistrationInfo;
            dynamic registrationInfo = registrationInfoObject;
            registrationInfo.Author = TaskAuthor;
            registrationInfo.Description = SessionAgentStartupTaskDescription;

            settingsObject = taskDefinition.Settings;
            dynamic settings = settingsObject;
            settings.Enabled = true;
            settings.Hidden = true;
            settings.AllowDemandStart = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.MultipleInstances = TaskInstancesIgnoreNew;
            settings.RunOnlyIfIdle = false;

            principalObject = taskDefinition.Principal;
            dynamic principal = principalObject;
            principal.UserId = accountName;
            principal.LogonType = TaskLogonInteractiveToken;
            principal.RunLevel = TaskRunLevelLimited;

            triggersObject = taskDefinition.Triggers;
            dynamic triggers = triggersObject;
            triggerObject = triggers.Create(TaskTriggerLogon);
            dynamic trigger = triggerObject;
            trigger.UserId = accountName;
            trigger.Enabled = true;

            actionsObject = taskDefinition.Actions;
            dynamic actions = actionsObject;
            actionObject = actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = fullPath;
            action.Arguments = arguments;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                SessionAgentStartupTaskName,
                taskDefinition,
                TaskCreateOrUpdate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);

            if (registeredTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回已注册的登录任务。");
            }
        }
        finally
        {
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(triggerObject);
            ReleaseComObject(actionsObject);
            ReleaseComObject(triggersObject);
            ReleaseComObject(principalObject);
            ReleaseComObject(settingsObject);
            ReleaseComObject(registrationInfoObject);
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(existingInfoObject);
            ReleaseComObject(existingDefinitionObject);
            ReleaseComObject(existingTaskObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>
    /// 删除本程序的持久登录任务。任务不存在视为成功；同名但来源不明的任务拒绝删除。
    /// </summary>
    /// <returns>任务已不存在或已成功删除时返回 true。</returns>
    internal static bool TryDeleteSessionAgentLogonTask(out string error)
    {
        error = string.Empty;
        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskObject = null;
        object? definitionObject = null;
        object? registrationInfoObject = null;

        try
        {
            EnsureTaskSchedulerIsTrusted();

            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                error = "当前 Windows 未提供任务计划程序 COM 服务。";
                return false;
            }

            schedulerObject = Activator.CreateInstance(schedulerType);
            if (schedulerObject is null)
            {
                error = "无法创建任务计划程序 COM 对象。";
                return false;
            }

            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;

            try
            {
                taskObject = rootFolder.GetTask(SessionAgentStartupTaskName);
            }
            catch (Exception exception) when (IsTaskNotFound(exception))
            {
                return true;
            }

            if (taskObject is null)
            {
                error = "任务计划程序返回了空的登录任务对象。";
                return false;
            }

            dynamic task = taskObject;
            definitionObject = task.Definition;
            dynamic definition = definitionObject;
            registrationInfoObject = definition.RegistrationInfo;
            dynamic registrationInfo = registrationInfoObject;
            if (!HasExpectedSessionAgentTaskRegistrationInfo(
                    registrationInfo.Author as string,
                    registrationInfo.Description as string))
            {
                error = $"计划任务 {SessionAgentStartupTaskName} 的来源不是 DesktopTwin，拒绝删除。";
                return false;
            }

            rootFolder.DeleteTask(SessionAgentStartupTaskName, 0);
            return true;
        }
        catch (Exception exception) when (exception is COMException
                                              or InvalidOperationException
                                              or UnauthorizedAccessException
                                              or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                              or InvalidCastException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            ReleaseComObject(registrationInfoObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(taskObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>
    /// 持久登录任务的作者与描述必须同时精确匹配。作者单独匹配不够：
    /// 其他程序可以同样写 DesktopTwin，但不会知道这段固定描述。
    /// </summary>
    internal static bool HasExpectedSessionAgentTaskRegistrationInfo(
        string? author,
        string? description)
    {
        return string.Equals(author, TaskAuthor, StringComparison.Ordinal)
            && string.Equals(description, SessionAgentStartupTaskDescription, StringComparison.Ordinal);
    }

    /// <summary>
    /// 任务计划程序对「任务不存在」返回两种常见 HRESULT，部分包装层则抛文件异常。
    /// 只把这两种情况当作不存在，其他错误继续向上抛，避免误删失败被当成成功。
    /// </summary>
    private static bool IsTaskNotFound(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return true;
        }

        return exception is COMException comException
            && (comException.HResult == unchecked((int)0x8004130F)
                || comException.HResult == unchecked((int)0x80070002));
    }

    /// <summary>
    /// 取当前交互用户的账户名。任务创建和删除都必须绑定同一个身份。
    /// </summary>
    private static string GetCurrentUserAccountName()
    {
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var accountName = currentIdentity.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException("无法读取当前用户身份，不能注册分身端登录任务。");
        }

        return accountName;
    }

    /// <summary>
    /// 只接受本程序创建的两类任务名，且后缀必须恰好是 32 位十六进制 GUID。
    /// 名称大小写与 GUID 大小写均按 Windows 任务计划程序的实际语义处理：
    /// 前缀必须精确，GUID 的 a-f 允许大小写。
    /// </summary>
    internal static bool IsManagedTemporaryTaskName(string? taskName)
    {
        if (taskName is null)
        {
            return false;
        }

        ReadOnlySpan<char> suffix;
        if (taskName.StartsWith(TaskNamePrefix, StringComparison.Ordinal))
        {
            suffix = taskName.AsSpan(TaskNamePrefix.Length);
        }
        else if (taskName.StartsWith(SessionAgentTaskNamePrefix, StringComparison.Ordinal))
        {
            suffix = taskName.AsSpan(SessionAgentTaskNamePrefix.Length);
        }
        else
        {
            return false;
        }

        if (suffix.Length != 32)
        {
            return false;
        }

        foreach (var character in suffix)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 校验注册信息是否由本程序写入，且描述符合创建时使用的固定格式。
    /// 空字段、未知作者或任意自定义描述一律返回 false。
    /// </summary>
    internal static bool HasExpectedTaskRegistrationInfo(string? author, string? description)
    {
        if (!string.Equals(author, TaskAuthor, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(description)
            || !description.StartsWith(TaskDescriptionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = description.LastIndexOf(
            TaskDescriptionSessionSeparator,
            StringComparison.Ordinal);
        if (separatorIndex <= TaskDescriptionPrefix.Length)
        {
            return false;
        }

        var sessionIdText = description.AsSpan(
            separatorIndex + TaskDescriptionSessionSeparator.Length);
        return uint.TryParse(
            sessionIdText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);
    }

    /// <summary>
    /// 生成创建任务时写入的固定描述，供创建与清扫共用同一格式。
    /// </summary>
    internal static string BuildTaskDescription(string executablePath, uint childSessionId)
    {
        return TaskDescriptionPrefix
            + Path.GetFileName(executablePath)
            + TaskDescriptionSessionSeparator
            + childSessionId.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class ComOrphanTaskFolder : IOrphanTaskFolder
    {
        private readonly object _rootFolderObject;

        public ComOrphanTaskFolder(object rootFolderObject)
        {
            _rootFolderObject = rootFolderObject;
        }

        public IReadOnlyList<string> GetTaskNames()
        {
            return EnumerateTaskNames(_rootFolderObject);
        }

        public bool TryGetRegistrationInfo(
            string taskName,
            out string? author,
            out string? description)
        {
            object? taskObject = null;
            object? definitionObject = null;
            object? registrationInfoObject = null;
            try
            {
                dynamic rootFolder = _rootFolderObject;
                taskObject = rootFolder.GetTask(taskName);
                if (taskObject is null)
                {
                    author = null;
                    description = null;
                    return false;
                }

                dynamic task = taskObject;
                definitionObject = task.Definition;
                dynamic definition = definitionObject;
                registrationInfoObject = definition.RegistrationInfo;
                dynamic registrationInfo = registrationInfoObject;
                author = registrationInfo.Author as string;
                description = registrationInfo.Description as string;
                return true;
            }
            finally
            {
                ReleaseComObject(registrationInfoObject);
                ReleaseComObject(definitionObject);
                ReleaseComObject(taskObject);
            }
        }

        public void DeleteTask(string taskName)
        {
            dynamic rootFolder = _rootFolderObject;
            rootFolder.DeleteTask(taskName, 0);
        }
    }

    private static string[] EnumerateTaskNames(dynamic rootFolder)
    {
        object? taskCollection = null;
        var names = new List<string>();

        try
        {
            taskCollection = rootFolder.GetTasks(1);
            dynamic tasks = taskCollection;
            var count = (int)tasks.Count;

            for (var index = 1; index <= count; index++)
            {
                object? taskObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject;
                    names.Add((string)task.Name);
                }
                finally
                {
                    // 每个 task 都是独立的 RCW，不清掉会一直堆到 GC 才回收。
                    ReleaseComObject(taskObject);
                }
            }

            return [.. names];
        }
        catch (Exception exception) when (exception is COMException
                                              or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                              or InvalidCastException)
        {
            // 枚举失败时返回已经拿到的部分；每个名字随后还要经过精确名称与元数据双重校验。
            return [.. names];
        }
        finally
        {
            ReleaseComObject(taskCollection);
        }
    }

    /// <summary>
    /// 在创建 <c>Schedule.Service</c> 的 COM 对象之前校验注册表，避免被本地提权。
    ///
    /// 本程序以管理员权限运行，一旦 <c>CoCreateInstance</c> 被引到攻击者的 DLL，
    /// 那个 DLL 就会在管理员上下文里加载。检查分两层，两层的失败策略刻意不同：
    /// 1. HKCU 覆盖检查 fail-closed。CoCreateInstance 解析 CLSID 时 HKCU 优先于 HKLM，
    ///    而 HKCU 是普通权限进程就能写的——这是唯一现实的劫持入口，读不到也必须拒绝。
    /// 2. HKLM 注册检查 fail-open。改 HKLM 已经需要管理员权限，这一层只做兜底；
    ///    读不到时放行并记日志，避免策略收紧的机器上功能整体不可用。
    /// </summary>
    internal static void EnsureTaskSchedulerIsTrusted()
    {
        EnsureNoUserClassOverride();
        EnsureMachineRegistrationIsSystemOwned();
    }

    /// <summary>
    /// 拒绝 HKCU 下对 <c>Schedule.Service</c> 的 COM 注册覆盖。
    ///
    /// 读不到注册表时同样拒绝：既然无法确认是否被覆盖，就不该以管理员权限创建该 COM 对象。
    /// 攻击者也可以用拒绝读取权限的方式让本检查抛异常，fail-open 会正好被这一手绕过。
    ///
    /// 可见性为 internal 而非 private，是为了让测试能直接调这个真实入口做烟囱验证。
    /// 判定本身在 <see cref="ThrowIfOverridePresent"/> 里，那个方法是纯函数、不碰注册表。
    /// </summary>
    internal static void EnsureNoUserClassOverride()
    {
        bool clsidOverride;
        bool progIdOverride;

        try
        {
            using var userClasses = Registry.CurrentUser.OpenSubKey(
                $@"{UserClassesPrefix}\CLSID\{TaskSchedulerClsid}");
            clsidOverride = userClasses is not null;

            using var userProgId = Registry.CurrentUser.OpenSubKey(
                $@"{UserClassesPrefix}\Schedule.Service");
            progIdOverride = userProgId is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法读取 HKCU 的 COM 注册，按「可能已被覆盖」处理。", exception);
            throw new InvalidOperationException(
                "无法读取当前用户的 COM 注册信息，无法确认任务计划程序未被劫持，已中止在分身中启动程序。",
                exception);
        }

        ThrowIfOverridePresent(clsidOverride, progIdOverride);
    }

    /// <summary>
    /// 根据 HKCU 覆盖情况抛出结论。
    ///
    /// 纯函数：只依赖两个布尔值，不读注册表也不写任何东西，所以可以直接单测四种组合。
    /// 判定逻辑放在这里而不是散在上面的读取代码里，是为了让「读到了什么」和「该不该拒绝」
    /// 两件事分开——前者依赖环境，后者必须可复现。
    /// </summary>
    internal static void ThrowIfOverridePresent(bool clsidOverride, bool progIdOverride)
    {
        if (clsidOverride)
        {
            throw new InvalidOperationException(
                $@"任务计划程序的 COM 类存在用户级注册覆盖（HKCU\{UserClassesPrefix}\CLSID\{TaskSchedulerClsid}），"
                + "可能已被劫持。已中止在分身中启动程序。");
        }

        if (progIdOverride)
        {
            throw new InvalidOperationException(
                $@"任务计划程序的 ProgID 存在用户级注册覆盖（HKCU\{UserClassesPrefix}\Schedule.Service），"
                + "可能已被劫持。已中止在分身中启动程序。");
        }
    }

    /// <summary>
    /// 校验 HKLM 下 <c>Schedule.Service</c> 的 ProgID/CLSID 指向系统目录里的模块。
    ///
    /// 这一层挡不住普通用户（改 HKLM 已需管理员），只用于发现被管理员级工具改写过的注册。
    /// 注册表读不到时放行，但留日志，便于排查「功能在策略收紧的机器上不可用」这类问题。
    /// </summary>
    private static void EnsureMachineRegistrationIsSystemOwned()
    {
        const string progIdPath = $@"{UserClassesPrefix}\Schedule.Service\CLSID";
        const string classesRoot = $@"{UserClassesPrefix}\CLSID";

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);

            using var progIdKey = localMachine.OpenSubKey(progIdPath);
            var clsid = progIdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(clsid))
            {
                // 读不到就交给后续 COM 调用报错，不在这里拦死。
                return;
            }

            if (!string.Equals(clsid, TaskSchedulerClsid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "任务计划程序的 ProgID 指向了非预期的 COM 类（"
                    + clsid
                    + "），可能已被劫持。已中止在分身中启动程序。");
            }

            using var clsidKey = localMachine.OpenSubKey($@"{classesRoot}\{clsid}\InprocServer32");
            var serverPath = clsidKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(serverPath))
            {
                return;
            }

            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var expanded = Environment.ExpandEnvironmentVariables(serverPath);

            bool isUnderSystem;
            try
            {
                isUnderSystem = Path.GetFullPath(expanded)
                    .StartsWith(
                        Path.GetFullPath(systemDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or NotSupportedException
                                                  or PathTooLongException)
            {
                // 注册表里的路径本身畸形（含非法字符、过长等）。这不是「读不到」，是「读到了垃圾」，
                // 不能按 fail-open 放行；同时也不能让 .NET 的英文原始消息直接弹给用户。
                AppLog.Warn($"任务计划程序的 COM 注册路径无法解析：{expanded}", exception);
                throw new InvalidOperationException(
                    "任务计划程序的 COM 注册指向了无法解析的路径（" + expanded + "），已中止在分身中启动程序。",
                    exception);
            }

            if (!isUnderSystem)
            {
                throw new InvalidOperationException(
                    "任务计划程序的 COM 注册指向了非系统目录（"
                    + expanded
                    + "），可能已被劫持。已中止在分身中启动程序。");
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法读取 HKLM 的任务计划程序注册信息，跳过该层校验。", exception);
        }
    }

    /// <summary>
    /// 在指定会话里运行一条命令。
    ///
    /// 与 <see cref="LaunchProgram"/> 的区别：不校验可执行文件路径——
    /// 调用方传的是系统自带的 <c>powershell.exe</c>，不是用户选的目标程序。
    /// 用途是拉起分身端的脚本代理。
    /// </summary>
    /// <returns>是否已成功下发启动任务。</returns>
    internal static bool RunInSession(
        uint sessionId,
        DateTime expectedLogonTimeUtc,
        string executablePath,
        string arguments,
        string taskNamePrefix)
    {
        try
        {
            LaunchWithTemporaryTask(
                sessionId,
                executablePath,
                arguments,
                Path.GetDirectoryName(executablePath) ?? string.Empty,
                runAsAdministrator: false,
                taskNamePrefix: taskNamePrefix,
                expectedLogonTimeUtc: expectedLogonTimeUtc);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or COMException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            AppLog.Warn("在分身会话里启动命令失败。", exception);
            return false;
        }
    }

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string arguments,
        string workingDirectory,
        bool runAsAdministrator,
        string? taskNamePrefix = null,
        DateTime? expectedLogonTimeUtc = null)
    {
        // 会话可能在用户点击后被重建，启动前必须确认目标会话号仍然有效，
        // 否则会把程序启动到一个已经注销的会话里（表现为「点了没反应」）。
        var actualChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (actualChildSessionId != childSessionId)
        {
            throw new InvalidOperationException(
                $"目标分身会话已发生变化。请求会话为 {childSessionId}，当前会话为 "
                + (actualChildSessionId?.ToString(CultureInfo.InvariantCulture) ?? "无")
                + "。");
        }

        if (expectedLogonTimeUtc is { } expectedLogonTime)
        {
            var actualLogonTime = ChildSessionNativeMethods.TryGetSessionLogonTime(childSessionId);
            if (actualLogonTime is not { } actual
                || expectedLogonTime == default
                || actual.Ticks != expectedLogonTime.Ticks)
            {
                throw new InvalidOperationException(
                    $"目标分身会话的登录身份已发生变化。请求登录时间 "
                    + $"{expectedLogonTime:O}，当前登录时间 "
                    + (actualLogonTime?.ToString("O", CultureInfo.InvariantCulture) ?? "未知")
                    + "。");
            }
        }

        EnsureTaskSchedulerIsTrusted();

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");

        var taskName = (taskNamePrefix ?? TaskNamePrefix) + Guid.NewGuid().ToString("N");
        var accountName = GetCurrentUserAccountName();

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        object? registrationInfoObject = null;
        object? settingsObject = null;
        object? principalObject = null;
        object? actionsObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        object? runningTaskObject = null;
        var taskRegistered = false;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;
            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject;

            registrationInfoObject = taskDefinition.RegistrationInfo;
            dynamic registrationInfo = registrationInfoObject;
            registrationInfo.Author = TaskAuthor;
            registrationInfo.Description = BuildTaskDescription(executablePath, childSessionId);

            settingsObject = taskDefinition.Settings;
            dynamic settings = settingsObject;
            settings.Enabled = true;
            settings.Hidden = true;
            settings.AllowDemandStart = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";

            principalObject = taskDefinition.Principal;
            dynamic principal = principalObject;
            principal.UserId = accountName;
            principal.LogonType = TaskLogonInteractiveToken;
            principal.RunLevel = runAsAdministrator
                ? TaskRunLevelHighest
                : TaskRunLevelLimited;

            actionsObject = taskDefinition.Actions;
            dynamic actions = actionsObject;
            actionObject = actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = executablePath;
            action.Arguments = arguments;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                taskName,
                taskDefinition,
                TaskCreate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;

            dynamic registeredTask = registeredTaskObject;
            runningTaskObject = registeredTask.RunEx(
                null,
                TaskRunUseSessionId,
                checked((int)childSessionId),
                null);

            if (runningTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回运行实例。");
            }

            AppLog.Info(
                $"已在会话 {childSessionId} 中启动 {executablePath}"
                + (runAsAdministrator ? "（管理员权限）" : "（普通权限）"));
        }
        finally
        {
            if (taskRegistered)
            {
                // 不能在这里立刻删任务。
                //
                // RunEx 只是「登记运行」，进程真正起来有几秒延迟；任务计划程序在删除
                // 任务定义时会连带终止它的运行实例——删得太快，刚拉起的进程会被掐掉。
                // 实测表现：「已在会话 X 中启动」的日志有，但会话里根本没有进程，
                // 三次拉起只有一次碰巧活下来。所以改成延迟清理，异常退出时留给下次启动清扫。
                ScheduleTaskCleanup(taskName);
            }

            ReleaseComObject(runningTaskObject);
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(actionsObject);
            ReleaseComObject(principalObject);
            ReleaseComObject(settingsObject);
            ReleaseComObject(registrationInfoObject);
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>
    /// 在后台延迟删除临时计划任务。
    ///
    /// 延迟期间目标进程已经稳定运行，此时删除任务不会影响它。
    /// </summary>
    /// <param name="taskName">要删除的任务名。</param>
    private static void ScheduleTaskCleanup(string taskName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TaskCleanupDelay).ConfigureAwait(false);

                // 延迟清理同样会在提权进程中实例化 COM。必须重新做一次注册表
                // 守卫，不能因为任务名是自己生成的，就假设 45 秒后 COM 仍然可信。
                if (!IsManagedTemporaryTaskName(taskName))
                {
                    return;
                }

                EnsureTaskSchedulerIsTrusted();

                var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType is null)
                {
                    return;
                }

                object? schedulerObject = null;
                object? rootFolderObject = null;
                try
                {
                    schedulerObject = Activator.CreateInstance(schedulerType);
                    if (schedulerObject is null)
                    {
                        return;
                    }

                    dynamic scheduler = schedulerObject;
                    scheduler.Connect();
                    rootFolderObject = scheduler.GetFolder("\\");
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                finally
                {
                    ReleaseComObject(rootFolderObject);
                    ReleaseComObject(schedulerObject);
                }
            }
            catch (Exception exception) when (exception is COMException
                                                  or InvalidOperationException
                                                  or UnauthorizedAccessException
                                                  or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                                  or InvalidCastException)
            {
                // 删不掉不算错：下次启动 SweepOrphanTasks 会清掉。
                AppLog.Warn($"延迟删除临时计划任务 {taskName} 失败，留给下次启动清扫。", exception);
            }
        });
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
