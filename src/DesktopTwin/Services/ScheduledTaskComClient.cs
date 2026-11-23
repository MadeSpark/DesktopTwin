using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopTwin.Services;

/// <summary>
/// 一次计划任务 COM 读取的结果。只有 <see cref="Status.NotFound"/> 才表示
/// Task Scheduler 明确报告任务不存在；连接失败、权限拒绝、XML 读取异常等
/// 一律是 <see cref="Status.Failed"/>，调用方必须按未知状态失败关闭。
/// </summary>
internal enum ScheduledTaskComReadStatus
{
    /// <summary>已取得任务 XML 字符串，仍需调用方做严格 XML 校验。</summary>
    Xml,
    /// <summary>Task Scheduler 明确报告任务路径不存在。</summary>
    NotFound,
    /// <summary>无法完成 COM 读取；绝不能当作任务不存在。</summary>
    Failed
}

internal readonly record struct ScheduledTaskComReadResult(
    ScheduledTaskComReadStatus Status,
    string? Xml,
    Exception? Failure)
{
    public static ScheduledTaskComReadResult Success(string xml) =>
        new(ScheduledTaskComReadStatus.Xml, xml, null);

    public static ScheduledTaskComReadResult NotFound(Exception? failure = null) =>
        new(ScheduledTaskComReadStatus.NotFound, null, failure);

    public static ScheduledTaskComReadResult Failed(Exception failure) =>
        new(ScheduledTaskComReadStatus.Failed, null, failure);
}

/// <summary>
/// 计划任务 Enabled 写入的结果。写入成功必须在同一条 COM 连接上重新读取 XML，
/// 同时确认身份没有变化且 Enabled 已变成目标值。
/// </summary>
internal enum ScheduledTaskComWriteStatus
{
    /// <summary>写入并回读确认成功。</summary>
    Succeeded,
    /// <summary>任务在写入前已经被外部改成目标状态，不能据此声明所有权。</summary>
    AlreadyInDesiredState,
    /// <summary>写入前后的任务定义身份不一致。</summary>
    IdentityChanged,
    /// <summary>Task Scheduler 明确报告任务路径不存在。</summary>
    NotFound,
    /// <summary>写入后无法确认 XML 身份或 Enabled 状态。</summary>
    ReadBackFailed,
    /// <summary>COM 连接、权限或属性写入失败。</summary>
    Failed
}

internal readonly record struct ScheduledTaskComWriteResult(
    ScheduledTaskComWriteStatus Status,
    Exception? Failure,
    string Message)
{
    public static ScheduledTaskComWriteResult Success() =>
        new(ScheduledTaskComWriteStatus.Succeeded, null, string.Empty);

    public static ScheduledTaskComWriteResult AlreadyInDesiredState(bool enabled) =>
        new(
            ScheduledTaskComWriteStatus.AlreadyInDesiredState,
            null,
            enabled ? "任务在写入前已经启用" : "任务在写入前已经禁用");

    public static ScheduledTaskComWriteResult IdentityChanged(string message) =>
        new(ScheduledTaskComWriteStatus.IdentityChanged, null, message);

    public static ScheduledTaskComWriteResult NotFound(Exception? failure = null) =>
        new(ScheduledTaskComWriteStatus.NotFound, failure, "计划任务不存在");

    public static ScheduledTaskComWriteResult ReadBackFailed(string message) =>
        new(ScheduledTaskComWriteStatus.ReadBackFailed, null, message);

    public static ScheduledTaskComWriteResult Failed(Exception failure) =>
        new(ScheduledTaskComWriteStatus.Failed, failure, failure.Message);
}

/// <summary>
/// 启动隔离使用的计划任务客户端 seam。生产实现持有一条
/// <c>Schedule.Service</c> COM 连接；测试实现不得访问真实系统任务。
/// </summary>
internal interface IScheduledTaskComClient : IDisposable
{
    ScheduledTaskComReadResult ReadXml(string relativeTaskPath);

    ScheduledTaskComWriteResult SetEnabled(
        string relativeTaskPath,
        bool enabled,
        string? expectedIdentity);
}

/// <summary>
/// 在一个线程上创建、使用并释放一条 Task Scheduler COM 连接的客户端。
///
/// <para>创建 COM 对象前必须复用
/// <see cref="ChildSessionProcessLauncher.EnsureTaskSchedulerIsTrusted"/>，
/// 不能在这里另写一套注册表校验。根文件夹与每个任务 RCW 都在
/// <see cref="Dispose"/> 或 finally 中显式释放，服务级 RCW 也在
/// <see cref="Dispose"/> 中释放。</para>
///
/// <para>线程约束是刻意的：服务的一批启动项操作在同一个调用线程上完成，若把 COM
/// 对象泄漏到别的线程，STA/MTA 切换会让错误路径和释放路径都难以证明。
/// 因此每次读取和写入都会校验当前线程。</para>
/// </summary>
internal sealed class ScheduledTaskComClient : IScheduledTaskComClient
{
    // GetTask 对缺失任务通常翻译成 FileNotFoundException；底层 HRESULT 是
    // ERROR_FILE_NOT_FOUND。0x8004130E/0x8004130F 分别是
    // SCHED_E_INVALID_TASK 与 SCHED_E_ACCOUNT_INFORMATION_NOT_SET，绝不能
    // 当成“任务不存在”，否则账号库错误会绕过失败关闭。
    private static readonly int FileNotFoundHResult = unchecked((int)0x80070002);

    private readonly int _ownerThreadId;
    private readonly Action _ensureTrusted;
    private object? _service;
    private object? _rootFolder;
    private bool _disposed;

    internal ScheduledTaskComClient(Action? ensureTrusted = null)
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _ensureTrusted = ensureTrusted
            ?? ChildSessionProcessLauncher.EnsureTaskSchedulerIsTrusted;
    }

    public ScheduledTaskComReadResult ReadXml(string relativeTaskPath)
    {
        object? task = null;
        try
        {
            EnsureOwnerThread();
            var path = NormalizeTaskPath(relativeTaskPath);
            var rootFolder = EnsureRootFolder();
            task = ((dynamic)rootFolder).GetTask(path);
            if (task is null)
            {
                return ScheduledTaskComReadResult.NotFound();
            }

            var xml = ((dynamic)task).Xml as string;
            return string.IsNullOrWhiteSpace(xml)
                ? ScheduledTaskComReadResult.Failed(
                    new InvalidOperationException("Task Scheduler 返回了空 XML"))
                : ScheduledTaskComReadResult.Success(xml);
        }
        catch (Exception exception) when (IsTaskNotFound(exception))
        {
            return ScheduledTaskComReadResult.NotFound(exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not StackOverflowException)
        {
            return ScheduledTaskComReadResult.Failed(exception);
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    public ScheduledTaskComWriteResult SetEnabled(
        string relativeTaskPath,
        bool enabled,
        string? expectedIdentity)
    {
        object? task = null;
        try
        {
            EnsureOwnerThread();
            if (!ScheduledTaskXml.IsValidIdentity(expectedIdentity))
            {
                return ScheduledTaskComWriteResult.Failed(
                    new InvalidOperationException("计划任务日志缺少可核验的身份"));
            }

            var path = NormalizeTaskPath(relativeTaskPath);
            var rootFolder = EnsureRootFolder();
            task = ((dynamic)rootFolder).GetTask(path);
            if (task is null)
            {
                return ScheduledTaskComWriteResult.NotFound();
            }

            var beforeXml = ((dynamic)task).Xml as string;
            var beforeIdentity = ScheduledTaskXml.GetIdentity(beforeXml);
            if (beforeIdentity is null)
            {
                return ScheduledTaskComWriteResult.ReadBackFailed(
                    "写入前无法严格解析任务 XML");
            }

            if (!string.Equals(beforeIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                return ScheduledTaskComWriteResult.IdentityChanged(
                    "任务定义身份已变化，拒绝写入");
            }

            var beforeState = ScheduledTaskXml.GetEnabledState(beforeXml);
            if (beforeState == TaskEnabledState.Unknown)
            {
                return ScheduledTaskComWriteResult.ReadBackFailed(
                    "写入前无法确认任务启用状态");
            }

            if ((enabled && beforeState == TaskEnabledState.Enabled)
                || (!enabled && beforeState == TaskEnabledState.Disabled))
            {
                return ScheduledTaskComWriteResult.AlreadyInDesiredState(enabled);
            }

            ((dynamic)task).Enabled = enabled;

            // IRegisteredTask.Xml 是任务对象的定义快照；对同一个 RCW 写入
            // Enabled 后再读 Xml，Windows 仍可能返回写入前的值。必须在同一条
            // COM 连接上重新 GetTask，再用新的 RCW 读取持久化后的 XML。
            // 这不是为了掩盖传播延迟：同一连接、紧邻写入的独立任务对象必须已经
            // 反映目标状态；否则仍按 ReadBackFailed 失败关闭。
            ReleaseComObject(task);
            task = ((dynamic)rootFolder).GetTask(path);
            if (task is null)
            {
                return ScheduledTaskComWriteResult.NotFound();
            }

            var afterXml = ((dynamic)task).Xml as string;
            var afterIdentity = ScheduledTaskXml.GetIdentity(afterXml);
            if (afterIdentity is null)
            {
                return ScheduledTaskComWriteResult.ReadBackFailed(
                    "写入后无法严格解析任务 XML");
            }

            if (!string.Equals(afterIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                return ScheduledTaskComWriteResult.IdentityChanged(
                    "任务定义在写入期间发生变化");
            }

            var afterState = ScheduledTaskXml.GetEnabledState(afterXml);
            if (afterState == TaskEnabledState.Unknown)
            {
                return ScheduledTaskComWriteResult.ReadBackFailed(
                    "写入后无法确认任务启用状态");
            }

            if ((enabled && afterState != TaskEnabledState.Enabled)
                || (!enabled && afterState != TaskEnabledState.Disabled))
            {
                return ScheduledTaskComWriteResult.ReadBackFailed(
                    "写入后回读的启用状态与目标不一致");
            }

            return ScheduledTaskComWriteResult.Success();
        }
        catch (Exception exception) when (IsTaskNotFound(exception))
        {
            return ScheduledTaskComWriteResult.NotFound(exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not StackOverflowException)
        {
            return ScheduledTaskComWriteResult.Failed(exception);
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        _disposed = true;
        var rootFolder = _rootFolder;
        _rootFolder = null;
        ReleaseComObject(rootFolder);
        var service = _service;
        _service = null;
        ReleaseComObject(service);
    }

    private object EnsureRootFolder()
    {
        EnsureOwnerThread();
        if (_rootFolder is not null)
        {
            return _rootFolder;
        }

        var service = EnsureService();
        var rootFolder = ((dynamic)service).GetFolder("\\")
            ?? throw new InvalidOperationException("Task Scheduler 未返回根任务文件夹");
        _rootFolder = rootFolder;
        return rootFolder;
    }

    private object EnsureService()
    {
        EnsureOwnerThread();
        if (_service is not null)
        {
            return _service;
        }

        _ensureTrusted();
        var type = Type.GetTypeFromCLSID(
            new Guid(ChildSessionProcessLauncher.TaskSchedulerClsid),
            throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException("系统中没有可用的 Schedule.Service COM 类");
        }

        object? service = null;
        try
        {
            service = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法创建 Schedule.Service COM 对象");
            ((dynamic)service).Connect();
            _service = service;
            return service;
        }
        catch
        {
            ReleaseComObject(service);
            throw;
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "ScheduledTaskComClient 只能在创建它的 StartupSnoozeService 工作线程内使用");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScheduledTaskComClient));
        }
    }

    private static string NormalizeTaskPath(string relativeTaskPath)
    {
        if (string.IsNullOrWhiteSpace(relativeTaskPath))
        {
            throw new ArgumentException("计划任务路径不能为空", nameof(relativeTaskPath));
        }

        var path = relativeTaskPath.Trim();
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }

        if (path.Length <= 1 || path.EndsWith('\\'))
        {
            throw new ArgumentException("计划任务路径无效", nameof(relativeTaskPath));
        }

        return path;
    }

    internal static bool IsTaskNotFound(Exception exception) =>
        exception is FileNotFoundException
        || (exception is COMException comException
            && comException.HResult == FileNotFoundHResult);

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidComObjectException
                                              or COMException)
        {
            // 释放失败不能改变上层“未知/失败关闭”的结论，但要留下诊断。
            AppLog.Warn("释放 Schedule.Service COM 对象失败。", exception);
        }
    }
}
