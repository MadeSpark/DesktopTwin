using System;

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
    /// false、再次尝试同步分身代理；回滚中的第二次同步抛异常只记录，不得逃出本方法。
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
        _operations.StopMcp();
        _operations.SetUiEnabled(false);
        _operations.PersistEnabled(false);
        _operations.UpdateStatus();

        try
        {
            if (!_operations.SyncSessionAgent())
            {
                _operations.ReportRollbackSyncWarning(
                    "MCP 回滚后未能重新同步分身代理；MCP 仍保持关闭。",
                    null);
            }
        }
        catch (Exception exception)
        {
            _operations.ReportRollbackSyncWarning(
                "MCP 回滚后同步分身代理失败；MCP 仍保持关闭。",
                exception);
        }

        if (notifyUser)
        {
            _operations.NotifyError(title, message, null);
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
