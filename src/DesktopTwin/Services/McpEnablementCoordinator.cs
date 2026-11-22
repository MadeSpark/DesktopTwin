using System;
using System.Collections.Generic;

namespace DesktopTwin.Services;

/// <summary>
/// MCP 启用流程的最小纯编排器。
///
/// <para>调用方注入「持久化开关」「启动/停止 MCP」「同步分身代理」「UI 状态」「通知」
/// 等委托；本类只负责严格按安全顺序协调它们，不重新实现 watch mode、监听或代理安装。
/// 这样启用失败回滚的顺序可以脱离 WPF ViewModel 单独测试，测试也不会启动真实 MCP。</para>
/// </summary>
internal sealed class McpEnablementCoordinator
{
    /// <summary>
    /// 表示一次 MCP 启用流程所需的外部操作。生产代码由 <c>MainWindowViewModel</c>
    /// 提供真实实现；测试提供记录用替身。
    /// </summary>
    internal sealed class Operations
    {
        internal Operations(
            Action<bool> persistEnabled,
            Func<bool> startMcp,
            Func<bool> syncSessionAgent,
            Action stopMcp,
            Action<bool> setUiEnabled,
            Action updateStatus,
            Action<string, string, Exception?> notifyError,
            Action<string, Exception?> reportRollbackSyncWarning)
        {
            PersistEnabled = persistEnabled;
            StartMcp = startMcp;
            SyncSessionAgent = syncSessionAgent;
            StopMcp = stopMcp;
            SetUiEnabled = setUiEnabled;
            UpdateStatus = updateStatus;
            NotifyError = notifyError;
            ReportRollbackSyncWarning = reportRollbackSyncWarning;
        }

        internal Action<bool> PersistEnabled { get; }
        internal Func<bool> StartMcp { get; }
        internal Func<bool> SyncSessionAgent { get; }
        internal Action StopMcp { get; }
        internal Action<bool> SetUiEnabled { get; }
        internal Action UpdateStatus { get; }
        internal Action<string, string, Exception?> NotifyError { get; }
        internal Action<string, Exception?> ReportRollbackSyncWarning { get; }
    }

    private readonly Operations _operations;

    internal McpEnablementCoordinator(Operations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    /// <summary>
    /// 请求启用 MCP。返回 true 表示已成功启用；false 表示已按安全顺序回滚。
    /// </summary>
    /// <remarks>
    /// 顺序约束：先持久化 true 并把 UI 设为 true，再启动 MCP；启动或分身代理同步
    /// 返回 false/抛异常都视为失败。失败时必须停止 MCP、把 UI 和持久化值改回
    /// false、最多两次尝试重新同步分身代理；回滚中的每一步互相隔离并最多尝试两次，
    /// 任何一步抛异常都不能跳过后面的关闭、持久化、状态刷新、同步和唯一通知。
    /// </remarks>
    internal bool Enable(string failureTitle, string syncFailureMessage)
    {
        ValidateFailureNotification(failureTitle, syncFailureMessage);

        // 持久化和 UI 也是启用流程的一部分；它们抛异常时 listener 可能尚未创建，
        // 仍必须走统一回滚，保证设置、开关、状态和通知不会停在半启用状态。
        try
        {
            _operations.PersistEnabled(true);
        }
        catch (Exception exception)
        {
            RollbackAfterFailure(
                failureTitle,
                "准备启用 MCP 时保存设置失败：" + exception.GetBaseException().Message,
                notifyUser: true,
                failureException: exception);
            return false;
        }

        try
        {
            _operations.SetUiEnabled(true);
        }
        catch (Exception exception)
        {
            RollbackAfterFailure(
                failureTitle,
                "准备启用 MCP 时更新界面开关失败：" + exception.GetBaseException().Message,
                notifyUser: true,
                failureException: exception);
            return false;
        }

        if (!TryStartMcp(out var failure, out var failureException))
        {
            RollbackAfterFailure(
                failureTitle,
                failure,
                notifyUser: true,
                failureException);
            return false;
        }

        if (!TrySyncSessionAgent(out failure, out failureException))
        {
            RollbackAfterFailure(
                failureTitle,
                syncFailureMessage,
                notifyUser: true,
                failureException);
            return false;
        }

        return true;
    }

    private bool TryStartMcp(out string failure, out Exception? exception)
    {
        try
        {
            if (_operations.StartMcp())
            {
                failure = string.Empty;
                exception = null;
                return true;
            }

            failure = "MCP 启动失败。";
            exception = null;
            return false;
        }
        catch (Exception caught)
        {
            failure = caught.GetBaseException().Message;
            exception = caught;
            return false;
        }
    }

    private bool TrySyncSessionAgent(out string failure, out Exception? exception)
    {
        try
        {
            if (_operations.SyncSessionAgent())
            {
                failure = string.Empty;
                exception = null;
                return true;
            }

            failure = "分身控制通道同步失败。";
            exception = null;
            return false;
        }
        catch (Exception caught)
        {
            failure = caught.GetBaseException().Message;
            exception = caught;
            return false;
        }
    }

    /// <summary>
    /// 对已经发生的启用失败执行统一安全回滚，并至多发送一条汇总通知。
    /// 直接启动路径与 <see cref="Enable"/> 共用此入口，避免维护两套回滚语义。
    /// </summary>
    internal void RollbackAfterFailure(
        string title,
        string message,
        bool notifyUser,
        Exception? failureException = null)
    {
        _ = DisableCore(
            title,
            message,
            syncSessionAgent: true,
            notifyOnSuccess: true,
            notifyUser: notifyUser,
            failureException);
    }

    /// <summary>
    /// 输入隔离失败时无条件关闭 MCP，但不重新同步分身代理。
    /// 即使启用开关已是 false，也必须检查并停止可能残留的 listener。
    /// </summary>
    internal bool DisableForIsolationFailure(
        string title,
        string message,
        bool notifyUser)
    {
        ValidateFailureNotification(title, message);
        return DisableCore(
            title,
            message,
            syncSessionAgent: false,
            notifyOnSuccess: true,
            notifyUser: notifyUser);
    }

    /// <summary>
    /// 普通关闭 MCP 的共享路径。返回 false 表示至少一个安全步骤未确认完成，
    /// 但 UI、持久化 false 和状态刷新仍已尝试，调用方不得再次吞掉告警。
    /// </summary>
    internal bool Disable(string title, string message, bool notifyUser)
    {
        ValidateFailureNotification(title, message);
        return DisableCore(
            title,
            message,
            syncSessionAgent: true,
            notifyOnSuccess: false,
            notifyUser: notifyUser);
    }

    private bool DisableCore(
        string title,
        string message,
        bool syncSessionAgent,
        bool notifyOnSuccess,
        bool notifyUser,
        Exception? failureException = null)
    {
        var rollbackFailures = new List<string>();
        Exception? firstRollbackException = failureException;

        // 这些动作不能互相短路：StopMcp 失败时仍必须关闭 UI、持久化 false 并刷新状态；
        // 每一关键步骤重试一次，覆盖短暂的 Win32/COM 竞争。即使两次都失败，也只记录
        // 错误并继续执行剩余步骤，最后用唯一一条通知把未解决的回滚异常告知用户。
        _ = TryRunRollbackStep(
            "停止 MCP（已重试两次）；端口可能仍在监听",
            _operations.StopMcp,
            rollbackFailures,
            ref firstRollbackException);
        _ = TryRunRollbackStep(
            "关闭 MCP UI 开关",
            () => _operations.SetUiEnabled(false),
            rollbackFailures,
            ref firstRollbackException);
        _ = TryRunRollbackStep(
            "持久化 MCP 关闭状态",
            () => _operations.PersistEnabled(false),
            rollbackFailures,
            ref firstRollbackException);
        _ = TryRunRollbackStep(
            "刷新 MCP 状态",
            _operations.UpdateStatus,
            rollbackFailures,
            ref firstRollbackException);

        if (syncSessionAgent)
        {
            TrySyncSessionAgentDuringRollback(
                rollbackFailures,
                ref firstRollbackException);
        }

        var succeeded = rollbackFailures.Count == 0;
        if (notifyUser && (notifyOnSuccess || !succeeded))
        {
            NotifyRollbackFailure(
                title,
                message,
                rollbackFailures,
                firstRollbackException);
        }

        return succeeded;
    }

    private void NotifyRollbackFailure(
        string title,
        string message,
        List<string> rollbackFailures,
        Exception? firstException)
    {
        var notification = rollbackFailures.Count == 0
            ? message
            : message
              + "\n\n回滚告警："
              + string.Join("；", rollbackFailures)
              + "。请立即检查 MCP 开关和端口状态。";
        try
        {
            _operations.NotifyError(title, notification, firstException);
        }
        catch (Exception exception)
        {
            // 通知回调自身失败时不能反向吞掉原始启用失败；至少写一条回滚日志。
            ReportRollbackWarning("MCP 回滚后显示错误通知失败。", exception);
        }
    }

    private void TrySyncSessionAgentDuringRollback(
        List<string> failures,
        ref Exception? firstException)
    {
        string lastFailure = "分身代理同步返回失败";
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_operations.SyncSessionAgent())
                {
                    return;
                }

                lastFailure = "分身代理同步返回失败";
                lastException = null;
            }
            catch (Exception exception)
            {
                lastFailure = exception.GetBaseException().Message;
                lastException = exception;
                firstException ??= exception;
            }
        }

        failures.Add("重新同步分身代理：" + lastFailure);
        ReportRollbackWarning(
            lastException is null
                ? "MCP 回滚后未能重新同步分身代理；MCP 仍保持关闭。"
                : "MCP 回滚后同步分身代理失败；MCP 仍保持关闭。",
            lastException);
    }

    private static bool TryRunRollbackStep(
        string label,
        Action action,
        List<string> failures,
        ref Exception? firstException)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception exception)
            {
                lastException = exception;
                firstException ??= exception;
            }
        }

        failures.Add(
            label
            + "："
            + (lastException?.GetBaseException().Message ?? "未知错误"));
        return false;
    }

    private void ReportRollbackWarning(string message, Exception? exception)
    {
        try
        {
            _operations.ReportRollbackSyncWarning(message, exception);
        }
        catch
        {
            // 诊断回调失败不能破坏已经完成的安全回滚。
        }
    }

    private static void ValidateFailureNotification(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("失败通知标题不能为空。", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("失败通知内容不能为空。", nameof(message));
        }
    }
}
