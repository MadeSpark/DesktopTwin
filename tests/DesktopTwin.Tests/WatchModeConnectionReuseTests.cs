using System;
using System.Collections.Generic;
using System.IO;
using DesktopTwin.Interop;
using DesktopTwin.Localization;
using DesktopTwin.Models;
using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// 观看模式只改变当前 RDP 连接的输入阻断状态。生产 seam 不暴露建连、登录或
/// 重连能力；本测试不启动 WPF/COM，也不把 COM 的 KeyboardHookMode 读回当成
/// 本机全局键鼠钩子已生效的现场证据。
/// </summary>
public sealed class WatchModeConnectionReuseTests
{
    [Fact]
    public void 连续切换观看和控制模式始终复用同一个输入连接()
    {
        var connection = new FakeWatchModeConnection(
            RdpInputWindowApplyResult.Applied,
            isConnected: true);
        var settings = new SessionSettings { WatchMode = false };
        var saveCount = 0;
        var statusMessages = new List<string>();

        ChildSessionService.ApplyWatchModeTransition(
            enabled: true,
            connection,
            settings,
            () => saveCount++,
            statusMessages.Add);
        ChildSessionService.ApplyWatchModeTransition(
            enabled: false,
            connection,
            settings,
            () => saveCount++,
            statusMessages.Add);

        Assert.Equal(new[] { true, false }, connection.InputBlockedCalls);
        Assert.False(settings.WatchMode);
        Assert.Equal(2, saveCount);
        Assert.Equal(
            new[]
            {
                Locale.Get("Svc.Status.WatchModeOn"),
                Locale.Get("Svc.Status.WatchModeOff")
            },
            statusMessages);
    }

    [Fact]
    public void 生产适配器始终转发到同一个输入控件()
    {
        var host = new FakeInputWindowController(
            RdpInputWindowApplyResult.Applied,
            connectedState: 1);
        var connection = new ChildSessionService.RdpHostWatchModeConnection(host);
        var settings = new SessionSettings { WatchMode = false };

        ChildSessionService.ApplyWatchModeTransition(
            enabled: true,
            connection,
            settings,
            () => { },
            _ => { });
        ChildSessionService.ApplyWatchModeTransition(
            enabled: false,
            connection,
            settings,
            () => { },
            _ => { });

        Assert.Equal(new[] { true, false }, host.InputBlockedCalls);
        Assert.False(settings.WatchMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 切换观看模式只对现有连接调用输入阻断(bool enabled)
    {
        var connection = new FakeWatchModeConnection(
            RdpInputWindowApplyResult.Applied,
            isConnected: true);
        var settings = new SessionSettings { WatchMode = !enabled };
        var saveCount = 0;

        ChildSessionService.ApplyWatchModeTransition(
            enabled,
            connection,
            settings,
            () => saveCount++,
            _ => { });

        Assert.Equal(new[] { enabled }, connection.InputBlockedCalls);
        Assert.Equal(enabled, settings.WatchMode);
        Assert.Equal(1, saveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowMissing允许记录期望状态且不创建新连接(bool enabled)
    {
        var connection = new FakeWatchModeConnection(
            RdpInputWindowApplyResult.WindowMissing);
        var settings = new SessionSettings { WatchMode = !enabled };
        var saveCount = 0;

        ChildSessionService.ApplyWatchModeTransition(
            enabled,
            connection,
            settings,
            () => saveCount++,
            _ => { });

        Assert.Equal(new[] { enabled }, connection.InputBlockedCalls);
        Assert.Equal(enabled, settings.WatchMode);
        Assert.Equal(1, saveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 已连接但输入窗口未确认时必须失败关闭(bool enabled)
    {
        var connection = new FakeWatchModeConnection(
            RdpInputWindowApplyResult.WindowMissing,
            isConnected: true);
        var settings = new SessionSettings { WatchMode = !enabled };
        var saveCount = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChildSessionService.ApplyWatchModeTransition(
                enabled,
                connection,
                settings,
                () => saveCount++,
                _ => { }));

        Assert.Equal(Locale.Get("Mcp.WatchMode.Failed"), exception.Message);
        Assert.Equal(new[] { enabled }, connection.InputBlockedCalls);
        Assert.Equal(!enabled, settings.WatchMode);
        Assert.Equal(0, saveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 没有桌面窗口时只记录期望状态而不启动连接(bool enabled)
    {
        var settings = new SessionSettings { WatchMode = !enabled };
        var saveCount = 0;

        ChildSessionService.ApplyWatchModeTransition(
            enabled,
            connection: null,
            settings,
            () => saveCount++,
            _ => { });

        Assert.Equal(enabled, settings.WatchMode);
        Assert.Equal(1, saveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed必须失败关闭且不得保存(bool enabled)
    {
        var connection = new FakeWatchModeConnection(
            RdpInputWindowApplyResult.Failed,
            isConnected: true);
        var settings = new SessionSettings { WatchMode = !enabled };
        var saveCount = 0;
        var refreshed = false;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChildSessionService.ApplyWatchModeTransition(
                enabled,
                connection,
                settings,
                () => saveCount++,
                _ => refreshed = true));

        Assert.Equal(Locale.Get("Mcp.WatchMode.Failed"), exception.Message);
        Assert.Equal(new[] { enabled }, connection.InputBlockedCalls);
        Assert.Equal(!enabled, settings.WatchMode);
        Assert.Equal(0, saveCount);
        Assert.False(refreshed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 观看模式开关必须通过SettingsService落盘(bool enabled)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-WatchMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var settings = new SettingsService(directory))
            {
                settings.Current.Session.WatchMode = !enabled;
                var connection = new FakeWatchModeConnection(
                    RdpInputWindowApplyResult.Applied,
                    isConnected: true);

                ChildSessionService.ApplyWatchModeTransition(
                    enabled,
                    connection,
                    settings.Current.Session,
                    settings.SaveSoon,
                    _ => { });

                settings.Flush();
            }

            using var reloaded = new SettingsService(directory);
            Assert.Equal(enabled, reloaded.Current.Session.WatchMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeInputWindowController(
        RdpInputWindowApplyResult result,
        int connectedState) : IRdpInputWindowController
    {
        public int ConnectedState { get; } = connectedState;

        public List<bool> InputBlockedCalls { get; } = new();

        public RdpInputWindowApplyResult SetInputBlocked(bool blocked)
        {
            InputBlockedCalls.Add(blocked);
            return result;
        }
    }

    private sealed class FakeWatchModeConnection(
        RdpInputWindowApplyResult result,
        bool isConnected = false) : IWatchModeRdpConnection
    {
        public bool IsConnected { get; } = isConnected;

        public List<bool> InputBlockedCalls { get; } = new();

        public RdpInputWindowApplyResult SetInputBlocked(bool blocked)
        {
            InputBlockedCalls.Add(blocked);
            return result;
        }
    }
}