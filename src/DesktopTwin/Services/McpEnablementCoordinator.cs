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
    /// false、再次尝试同步分身代理；回滚中的每一步互相隔离并独立重试一次，
    /// 任何一步抛异常都不能跳过后面的关闭、持久化、状态刷新和通知。
    /// </remarks>
    internal bool Enable(string failureTitle, string syncFailureMessage)
    {
        ValidateFailureNotification(failureTitle, syncFailureMessage);

        _operations.PersistEnabled(true);
        _operations.SetUiEnabled(true);

        if (!TryStartMcp(out var failure))
        {
            Rollback(failureTitle, failure, notifyUser: true);
            return false;
        }

        if (!TrySyncSessionAgent(out failure))
        {
            Rollback(failureTitle, syncFailureMessage, notifyUser: true);
            return false;
        }

        return true;
    }

    private bool TryStartMcp(out string failure)
    {
        try
        {
            if (_operations.StartMcp())
            {
                failure = string.Empty;
                return true;
            }

            failure = "MCP 启动失败。";
            return false;
        }
        catch (Exception exception)
        {
            failure = exception.GetBaseException().Message;
            return false;
        }
    }

    private bool TrySyncSessionAgent(out string failure)
    {
        try
        {
            if (_operations.SyncSessionAgent())
            {
                failure = string.Empty;
                return true;
            }

            failure = "分身控制通道同步失败。";
            return false;
        }
        catch (Exception exception)
        {
            failure = exception.GetBaseException().Message;
            return false;
        }
    }

    private void Rollback(string title, string message, bool notifyUser)
    {
        var rollbackFailures = new List<string>();
        Exception? firstRollbackException = null;

        // 这些动作不能互相短路：StopMcp 失败时仍必须关闭 UI、持久化 false 并刷新状态；
        // 每一关键步骤重试一次，覆盖短暂的 Win32/COM 竞争。即使两次都失败，也只记录
        // 错误并继续执行剩余步骤，最后用唯一一条通知把未解决的回滚异常告知用户。
        _ = TryRunRollbackStep(
            "停止 MCP",
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

        try
        {
            if (!_operations.SyncSessionAgent())
            {
                ReportRollbackWarning(
                    "MCP 回滚后未能重新同步分身代理；MCP 仍保持关闭。",
                    null);
            }
        }
        catch (Exception exception)
        {
            ReportRollbackWarning(
                "MCP 回滚后同步分身代理失败；MCP 仍保持关闭。",
                exception);
        }

        if (!notifyUser)
        {
            return;
        }

        var notification = rollbackFailures.Count == 0
            ? message
            : message
              + "\n\n回滚告警："
              + string.Join("；", rollbackFailures)
              + "。请立即检查 MCP 开关和端口状态。";
        try
        {
            _operations.NotifyError(title, notification, firstRollbackException);
        }
        catch (Exception exception)
        {
            // 通知回调自身失败时不能反向吞掉原始启用失败；至少写一条回滚日志。
            ReportRollbackWarning("MCP 回滚后显示错误通知失败。", exception);
        }
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
