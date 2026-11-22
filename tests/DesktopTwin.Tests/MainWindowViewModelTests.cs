using DesktopTwin.ViewModel;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// 隔离期间推迟的登录自启项同步只能在子会话确认结束且日志清空后重试，
/// 失败后还要限速，避免 500ms 状态轮询反复操作任务计划程序和注册表。
/// </summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public void 关闭UU视角切换后必须忽略外部热键输入()
    {
        Assert.False(MainWindowViewModel.ShouldAcceptUuSwitchInput(enabled: false));
    }

    [Fact]
    public void 开启UU视角切换后允许外部热键输入()
    {
        Assert.True(MainWindowViewModel.ShouldAcceptUuSwitchInput(enabled: true));
    }

    [Fact]
    public void 没有待补做同步时不得尝试()
    {
        Assert.False(MainWindowViewModel.ShouldAttemptDeferredSessionAgentStartupReconcile(
            needsReconcile: false,
            deferStartupMutation: false,
            nowTicks: 100_000,
            lastAttemptTicks: 0,
            retryIntervalMs: 10_000));
    }

    [Fact]
    public void 子会话仍在或日志未清空时不得尝试()
    {
        Assert.False(MainWindowViewModel.ShouldAttemptDeferredSessionAgentStartupReconcile(
            needsReconcile: true,
            deferStartupMutation: true,
            nowTicks: 100_000,
            lastAttemptTicks: 0,
            retryIntervalMs: 10_000));
    }

    [Fact]
    public void 首次满足条件时立即尝试()
    {
        Assert.True(MainWindowViewModel.ShouldAttemptDeferredSessionAgentStartupReconcile(
            needsReconcile: true,
            deferStartupMutation: false,
            nowTicks: 100_000,
            lastAttemptTicks: 0,
            retryIntervalMs: 10_000));
    }

    [Fact]
    public void 失败后未到重试间隔不得尝试()
    {
        Assert.False(MainWindowViewModel.ShouldAttemptDeferredSessionAgentStartupReconcile(
            needsReconcile: true,
            deferStartupMutation: false,
            nowTicks: 109_999,
            lastAttemptTicks: 100_000,
            retryIntervalMs: 10_000));
    }

    [Fact]
    public void 到达重试间隔后允许再次尝试()
    {
        Assert.True(MainWindowViewModel.ShouldAttemptDeferredSessionAgentStartupReconcile(
            needsReconcile: true,
            deferStartupMutation: false,
            nowTicks: 110_000,
            lastAttemptTicks: 100_000,
            retryIntervalMs: 10_000));
    }
}
