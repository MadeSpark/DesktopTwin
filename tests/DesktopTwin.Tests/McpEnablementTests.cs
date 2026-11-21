using System;
using System.Collections.Generic;
using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// MCP 启用失败回滚协调器的纯编排测试。
///
/// 这里只验证顺序与状态：不启动真实 MCP 端口，也不触碰真实子会话。
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
            SyncResults = new Queue<bool>([false, true])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.True(harness.RolledBack);
        Assert.Equal(
            [
                "persist:true", "ui:true", "start", "sync",
                "stop", "ui:false", "persist:false", "status", "sync", "notify"
            ],
            harness.Operations);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(2, harness.SyncCount);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Equal(SyncFailureMessage, harness.NotifiedMessage);
    }

    [Fact]
    public void 同步抛出异常时视为失败并回滚且异常不逃逸()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncException = new InvalidOperationException("agent install failed"),
            SyncResults = new Queue<bool>([true])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.True(harness.RolledBack);
        Assert.Equal(
            [
                "persist:true", "ui:true", "start", "sync",
                "stop", "ui:false", "persist:false", "status", "sync", "notify"
            ],
            harness.Operations);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Equal(SyncFailureMessage, harness.NotifiedMessage);
    }

    [Fact]
    public void 回滚中第二次同步抛出异常时被吞掉并报告()
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
        Assert.NotNull(harness.RollbackSyncWarning);
        Assert.Equal(
            "MCP 回滚后同步分身代理失败；MCP 仍保持关闭。",
            harness.RollbackSyncWarningMessage);
        Assert.Equal(FailureTitle, harness.NotifiedTitle);
        Assert.Equal(SyncFailureMessage, harness.NotifiedMessage);
    }

    [Fact]
    public void 回滚中第二次同步返回false时记录警告且保持关闭()
    {
        var harness = new Harness
        {
            StartMcpResult = true,
            SyncResults = new Queue<bool>([false, false])
        };

        var result = harness.Coordinator.Enable(FailureTitle, SyncFailureMessage);

        Assert.False(result);
        Assert.False(harness.PersistedValue);
        Assert.False(harness.UiValue);
        Assert.Equal(2, harness.SyncCount);
        Assert.Equal(
            "MCP 回滚后未能重新同步分身代理；MCP 仍保持关闭。",
            harness.RollbackSyncWarningMessage);
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
    public void 操作对象为null时抛出参数异常()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new McpEnablementCoordinator(null!));
    }

    private sealed class Harness
    {
        public bool StartMcpResult { get; set; } = true;

        public Exception? StartMcpException { get; set; }

        public Exception? SyncException { get; set; }

        public Queue<bool> SyncResults { get; set; } = new();

        public McpEnablementCoordinator Coordinator { get; }

        public List<string> Operations { get; } = [];

        public bool PersistedValue { get; private set; }

        public bool UiValue { get; private set; }

        public bool RolledBack { get; private set; }

        public int SyncCount { get; private set; }

        public string? NotifiedTitle { get; private set; }

        public string? NotifiedMessage { get; private set; }

        public Exception? RollbackSyncWarning { get; private set; }

        public string? RollbackSyncWarningMessage { get; private set; }

        public Harness()
        {
            Coordinator = new McpEnablementCoordinator(
                new McpEnablementCoordinator.Operations(
                    persistEnabled: value =>
                    {
                        PersistedValue = value;
                        Operations.Add($"persist:{value.ToString().ToLowerInvariant()}");
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
                        Operations.Add("stop");
                    },
                    setUiEnabled: value =>
                    {
                        UiValue = value;
                        Operations.Add($"ui:{value.ToString().ToLowerInvariant()}");
                    },
                    updateStatus: () => Operations.Add("status"),
                    notifyError: (title, message, exception) =>
                    {
                        NotifiedTitle = title;
                        NotifiedMessage = message;
                        Operations.Add("notify");
                    },
                    reportRollbackSyncWarning: (message, exception) =>
                    {
                        RollbackSyncWarning = exception;
                        RollbackSyncWarningMessage = message;
                    }));
        }
    }
}