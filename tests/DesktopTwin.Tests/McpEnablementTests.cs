using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// MCP 启用失败回滚协调器与环回服务停止生命周期的测试。
///
/// 编排测试不触碰真实子会话；监听器重试测试只绑定 127.0.0.1:0，不访问外部网络。
/// </summary>
public sealed class McpEnablementTests
{
    private const string FailureTitle = "MCP 启用失败";
    private const string SyncFailureMessage = "MCP 已启动，但分身控制通道安装失败。";

    [Fact]
    public void 启用成功时不触发回滚且顺序正确()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([true])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.True(result);
        Assert.False(harness.RolledBack);
        Assert.Equal(
            ["persist:true", "ui:true", "start", "sync"],
            harness.Operations);
        Assert.True(harness.PersistedValue);
        Assert.True(harness.UiValue);
        Assert.Equal(1, harness.SyncCount);
    }

    [Fact]
    public void 同步返回false时按安全顺序回滚()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([false, false, false])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.True(harness.RolledBack);
        Assert.Equal(
            [
                "persist:true", "ui:true", "start", "sync",
                "stop", "ui:false", "persist:false", "status", "sync", "sync", "notify"
            ],
            harness.Operations);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(3, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Contains(SyncFailureMessage, harness.NotifiedMessage);
        Assert.Contains("重新同步分身代理：分身代理同步返回失败", harness.NotifiedMessage);
    }

    [Fact]
    public void 同步抛出异常时视为失败并回滚且异常不逃逸()
    {
        var syncException = new InvalidOperationException("agent install failed");
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncException = syncException
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.True(harness.RolledBack);
        Assert.Equal(
            [
                "persist:true", "ui:true", "start", "sync",
                "stop", "ui:false", "persist:false", "status", "sync", "sync", "notify"
            ],
            harness.Operations);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(3, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Contains("agent install failed", harness.NotifiedMessage);
        Assert.Same(syncException, harness.NotifiedException);
    }

    [Fact]
    public void 回滚同步持续抛出异常时重试两次并汇总到唯一通知()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncException = new InvalidOperationException("agent rollback failed")
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.True(harness.RolledBack);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(3, harness.SyncCount);
        Assert.NotNull(harness.RollbackSyncWarning);
        Assert.Equal(
            "MCP 回滚后同步分身代理失败；MCP 仍保持关闭。",
            harness.RollbackSyncWarningMessage);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains("重新同步分身代理：agent rollback failed", harness.NotifiedMessage);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
    }

    [Fact]
    public void 回滚同步持续返回false时重试两次并汇总到唯一通知()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([false, false, false])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(3, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains("重新同步分身代理：分身代理同步返回失败", harness.NotifiedMessage);
        Assert.Equal(
            "MCP 回滚后未能重新同步分身代理；MCP 仍保持关闭。",
            harness.RollbackSyncWarningMessage);
    }

    [Fact]
    public void 回滚中停止MCP持续抛出异常时仍完成其余关闭步骤()
    {
        var stopException = new InvalidOperationException("stop unavailable");
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([false, true]),
            StopMcpException = stopException
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(2, harness.StopMcpCount);
        Assert.Equal(2, harness.SyncCount);
        Assert.Equal(
            [
                "persist:true", "ui:true", "start", "sync",
                "stop", "stop", "ui:false", "persist:false", "status", "sync", "notify"
            ],
            harness.Operations);
        Assert.Contains(
            "停止 MCP（已重试两次）；端口可能仍在监听：stop unavailable",
            harness.NotifiedMessage);
        Assert.Contains("请立即检查 MCP 开关和端口状态", harness.NotifiedMessage);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Same(stopException, harness.NotifiedException);
    }

    [Fact]
    public void 启动MCP返回false时也必须回滚()
    {
        var harness = new Harness
        {
            StartMcpResult = false,
            SyncResults = new Queue<bool>([true])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.True(harness.RolledBack);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Equal("MCP 启动失败。", harness.NotifiedMessage);
        Assert.Equal(1, harness.SyncCount);
    }

    [Fact]
    public void 启动MCP抛出异常时回滚且异常不逃逸()
    {
        var harness = new Harness
        {
            StartMcpException = new InvalidOperationException("loopback failed"),
            SyncResults = new Queue<bool>([true])
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.True(harness.RolledBack);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Equal("loopback failed", harness.NotifiedMessage);
    }

    [Fact]
    public void 启用前持久化失败时仍关闭界面和持久化状态并通知一次()
    {
        var persistException = new InvalidOperationException("settings save unavailable");
        var harness = new Harness
        {
            PersistTrueException = persistException,
            SyncResults = new Queue<bool>([true])
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(1, harness.StopMcpCount);
        Assert.Equal(1, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains(
            "准备启用 MCP 时保存设置失败：settings save unavailable",
            harness.NotifiedMessage);
        Assert.Same(persistException, harness.NotifiedException);
        Assert.DoesNotContain("start", harness.Operations);
    }

    [Fact]
    public void 启用前UI更新失败时仍关闭界面和持久化状态并通知一次()
    {
        var uiException = new InvalidOperationException("ui unavailable");
        var harness = new Harness
        {
            UiTrueException = uiException,
            SyncResults = new Queue<bool>([true])
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(1, harness.StopMcpCount);
        Assert.Equal(1, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains(
            "准备启用 MCP 时更新界面开关失败：ui unavailable",
            harness.NotifiedMessage);
        Assert.Same(uiException, harness.NotifiedException);
        Assert.DoesNotContain("start", harness.Operations);
    }

    [Fact]
    public void 普通关闭在停止失败时仍落盘并同步代理且警告端口可能残留()
    {
        var stopException = new InvalidOperationException("disable stop unavailable");
        var harness = new Harness
        {
            StopMcpException = stopException,
            SyncResults = new Queue<bool>([true])
        };

        bool disabled = false;
        var exception = Record.Exception(() =>
            disabled = harness.Coordinator.Disable(FailureTitle, "无法停用 MCP。", notifyUser: true));

        Assert.Null(exception);
        Assert.False(disabled);
        Assert.Equal(2, harness.StopMcpCount);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(1, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains("端口可能仍在监听", harness.NotifiedMessage);
        Assert.Contains("disable stop unavailable", harness.NotifiedMessage);
        Assert.Same(stopException, harness.NotifiedException);
    }

    [Fact]
    public void 隔离失败关闭不因开关为false而跳过停止且不重新同步代理()
    {
        var harness = new Harness();

        var exception = Record.Exception(() =>
            harness.Coordinator.DisableForIsolationFailure(
                FailureTitle,
                "隔离验证失败。",
                notifyUser: true));

        Assert.Null(exception);
        Assert.Equal(1, harness.StopMcpCount);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(0, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Equal("隔离验证失败。", harness.NotifiedMessage);
        Assert.Equal(
            ["stop", "ui:false", "persist:false", "status", "notify"],
            harness.Operations);
    }

    [Fact]
    public void 隔离失败时停止持续失败仍完成清理并明确警告端口可能仍在监听()
    {
        var stopException = new InvalidOperationException("isolation stop unavailable");
        var harness = new Harness { StopMcpException = stopException };

        var exception = Record.Exception(() =>
            harness.Coordinator.DisableForIsolationFailure(
                FailureTitle,
                "隔离验证失败。",
                notifyUser: true));

        Assert.Null(exception);
        Assert.Equal(2, harness.StopMcpCount);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(0, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains("端口可能仍在监听", harness.NotifiedMessage);
        Assert.Contains("isolation stop unavailable", harness.NotifiedMessage);
        Assert.Same(stopException, harness.NotifiedException);
    }

    [Fact]
    public void 直接回滚入口在停止持续失败时完成所有步骤并只通知一次()
    {
        const string directFailureMessage = "MCP 控制通道热键代理启用失败。";
        var stopException = new InvalidOperationException("direct stop unavailable");
        var harness = new Harness
        {
            SyncResults = new Queue<bool>([false, false]),
            StopMcpException = stopException
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.RollbackAfterFailure(FailureTitle, directFailureMessage, notifyUser: true));

        Assert.Null(exception);
        Assert.True(harness.RolledBack);
        Assert.Equal(2, harness.StopMcpCount);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(
            ["stop", "stop", "ui:false", "persist:false", "status", "sync", "sync", "notify"],
            harness.Operations);
        Assert.Equal(2, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Contains(directFailureMessage, harness.NotifiedMessage);
        Assert.Contains(
            "停止 MCP（已重试两次）；端口可能仍在监听：direct stop unavailable",
            harness.NotifiedMessage);
        Assert.Contains("重新同步分身代理：分身代理同步返回失败", harness.NotifiedMessage);
        Assert.Same(stopException, harness.NotifiedException);
    }

    [Fact]
    public void 回滚通知和诊断回调异常不会逃逸()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([false, false, false]),
            StopMcpException = new InvalidOperationException("stop unavailable"),
            NotifyException = new InvalidOperationException("notification unavailable"),
            ReportRollbackWarningException = new InvalidOperationException("diagnostics unavailable")
        };

        var exception = Record.Exception(() =>
            harness.Coordinator.Enable(FailureTitle, SyncFailureMessage));

        Assert.Null(exception);
        Assert.Equal(2, harness.StopMcpCount);
        Assert.Equal(3, harness.SyncCount);
        Assert.Equal(1, harness.NotifyCount);
        Assert.Contains(
            "停止 MCP（已重试两次）；端口可能仍在监听：stop unavailable",
            harness.NotifiedMessage);
        Assert.Contains("重新同步分身代理：分身代理同步返回失败", harness.NotifiedMessage);
    }

    [Fact]
    public void Dispose首次停止失败后第二次重试同一TcpListener并完成停止()
    {
        var stoppedListeners = new List<TcpListener>();
        var server = new McpLoopbackServer(
            McpSecretStore.CreateBase64Url(32),
            0,
            () => null,
            stopListener: listener =>
            {
                stoppedListeners.Add(listener);
                if (stoppedListeners.Count == 1)
                {
                    throw new InvalidOperationException("transient listener stop failure");
                }

                listener.Stop();
            });

        try
        {
            server.Start();
            var port = server.Port;

            var firstFailure = Assert.Throws<InvalidOperationException>(() => server.Dispose());

            Assert.Equal("transient listener stop failure", firstFailure.Message);
            Assert.True(server.IsListening);
            Assert.Single(stoppedListeners);

            server.Dispose();

            Assert.False(server.IsListening);
            Assert.Equal(2, stoppedListeners.Count);
            Assert.Same(stoppedListeners[0], stoppedListeners[1]);

            using var probe = new TcpClient();
            Assert.ThrowsAny<SocketException>(() => probe.Connect(IPAddress.Loopback, port));
        }
        finally
        {
            if (server.IsListening)
            {
                try
                {
                    server.Dispose();
                }
                catch
                {
                    // The primary assertion should report the lifecycle failure.
                }
            }
        }
    }

    [Fact]
    public void 操作对象为null时抛出参数异常()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new McpEnablementCoordinator(null!));
    }

    private sealed class Harness
    {
        public bool StartMcpResult { get; set; } = true;

        public Exception? StartMcpException { get; set; }

        public Exception? PersistTrueException { get; set; }

        public Exception? UiTrueException { get; set; }

        public Exception? SyncException { get; set; }

        public Exception? StopMcpException { get; set; }

        public Exception? NotifyException { get; set; }

        public Exception? ReportRollbackWarningException { get; set; }

        public Queue<bool> SyncResults { get; set; } = new();

        public McpEnablementCoordinator Coordinator { get; }

        public List<string> Operations { get; } = [];

        public bool PersistedValue { get; private set; }

        public bool UiValue { get; private set; }

        public bool RolledBack { get; private set; }

        public int SyncCount { get; private set; }

        public int StopMcpCount { get; private set; }

        public int NotifyCount { get; private set; }

        public string? NotifiedTitle { get; private set; }

        public string? NotifiedMessage { get; private set; }

        public Exception? NotifiedException { get; private set; }

        public Exception? RollbackSyncWarning { get; private set; }

        public string? RollbackSyncWarningMessage { get; private set; }

        public Harness()
        {
            Coordinator = new McpEnablementCoordinator(
                new McpEnablementCoordinator.Operations(
                    persistEnabled: value =>
                    {
                        Operations.Add($"persist:{value.ToString().ToLowerInvariant()}");
                        if (value && PersistTrueException is not null)
                        {
                            throw PersistTrueException;
                        }

                        PersistedValue = value;
                    },
                    startMcp: () =>
                    {
                        Operations.Add("start");
                        if (StartMcpException is not null)
                        {
                            throw StartMcpException;
                        }

                        return StartMcpResult;
                    },
                    syncSessionAgent: () =>
                    {
                        Operations.Add("sync");
                        SyncCount++;
                        if (SyncException is not null)
                        {
                            throw SyncException;
                        }

                        return SyncResults.Count > 0 ? SyncResults.Dequeue() : false;
                    },
                    stopMcp: () =>
                    {
                        RolledBack = true;
                        StopMcpCount++;
                        Operations.Add("stop");
                        if (StopMcpException is not null)
                        {
                            throw StopMcpException;
                        }
                    },
                    setUiEnabled: value =>
                    {
                        Operations.Add($"ui:{value.ToString().ToLowerInvariant()}");
                        if (value && UiTrueException is not null)
                        {
                            throw UiTrueException;
                        }

                        UiValue = value;
                    },
                    updateStatus: () => Operations.Add("status"),
                    notifyError: (title, message, exception) =>
                    {
                        NotifyCount++;
                        NotifiedTitle = title;
                        NotifiedMessage = message;
                        NotifiedException = exception;
                        Operations.Add("notify");
                        if (NotifyException is not null)
                        {
                            throw NotifyException;
                        }
                    },
                    reportRollbackSyncWarning: (message, exception) =>
                    {
                        RollbackSyncWarning = exception;
                        RollbackSyncWarningMessage = message;
                        if (ReportRollbackWarningException is not null)
                        {
                            throw ReportRollbackWarningException;
                        }
                    }));
        }
    }
}