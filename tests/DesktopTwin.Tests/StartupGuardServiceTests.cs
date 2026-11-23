using System;
using System.Collections.Generic;
using System.Reflection;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 子会话号会被 Windows 复用。这里只验证不碰 WTS、也不启动监控线程的纯判定，
/// 以及 ResetObservedSession 对旧基线的清理，避免把“同一会话号”误当成“同一登录”。
/// </summary>
public sealed class StartupGuardServiceTests
{
    private static readonly DateTime LogonTime = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 不同会话号总是重置旧会话状态()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 8,
            consecutiveMissingScans: 0,
            observedLogonTime: LogonTime,
            childLogonTime: LogonTime,
            unknownLogonDuration: TimeSpan.Zero);

        Assert.Equal(ObservedSessionDecision.Reset, decision);
    }

    [Fact]
    public void 相同会话号且登录时间相等时保留状态()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: LogonTime,
            childLogonTime: LogonTime,
            unknownLogonDuration: TimeSpan.Zero);

        Assert.Equal(ObservedSessionDecision.None, decision);
    }

    [Fact]
    public void 相同会话号但登录时间不等时重置状态()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: LogonTime,
            childLogonTime: LogonTime.AddSeconds(1),
            unknownLogonDuration: TimeSpan.Zero);

        Assert.Equal(ObservedSessionDecision.Reset, decision);
    }

    [Fact]
    public void 登录时间从未知变为已知时重置状态()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: null,
            childLogonTime: LogonTime,
            unknownLogonDuration: TimeSpan.FromMinutes(1));

        Assert.Equal(ObservedSessionDecision.Reset, decision);
    }

    [Fact]
    public void 登录时间从已知变为未知时失败关闭()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: LogonTime,
            childLogonTime: null,
            unknownLogonDuration: TimeSpan.Zero);

        Assert.Equal(ObservedSessionDecision.FailClosed, decision);
    }

    [Fact]
    public void 两边登录时间都未知但未超过等待上限时保持等待()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: null,
            childLogonTime: null,
            unknownLogonDuration: TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        Assert.Equal(ObservedSessionDecision.None, decision);
    }

    [Fact]
    public void 两边登录时间都未知且达到等待上限时失败关闭()
    {
        var decision = StartupGuardService.DecideObservedSession(
            observedSessionId: 7,
            childSessionId: 7,
            consecutiveMissingScans: 0,
            observedLogonTime: null,
            childLogonTime: null,
            unknownLogonDuration: TimeSpan.FromMinutes(5));

        Assert.Equal(ObservedSessionDecision.FailClosed, decision);
    }

    [Fact]
    public void 同一会话的重复登录通知必须刷新时间并要求重置()
    {
        var first = StartupGuardService.ApplyLoginCompletedNotification(
            new LoginCompletionState(0, 0),
            notifiedSessionId: 7,
            completedAtTimestamp: 100);
        var second = StartupGuardService.ApplyLoginCompletedNotification(
            first.Completion,
            notifiedSessionId: 7,
            completedAtTimestamp: 200);

        Assert.Equal(7u, second.Completion.SessionId);
        Assert.Equal(200, second.Completion.CompletedAtTimestamp);
        Assert.True(first.InvalidateObservedSession);
        Assert.True(second.InvalidateObservedSession);
    }

    [Fact]
    public void 新会话的登录通知会替换旧登录窗口()
    {
        var previous = new LoginCompletionState(7, 100);

        var update = StartupGuardService.ApplyLoginCompletedNotification(
            previous,
            notifiedSessionId: 8,
            completedAtTimestamp: 300);

        Assert.Equal(8u, update.Completion.SessionId);
        Assert.Equal(300, update.Completion.CompletedAtTimestamp);
        Assert.True(update.InvalidateObservedSession);
    }

    [Fact]
    public void 重置会话状态会清空身份基线与登录窗口()
    {
        // 空受管路径使构造函数直接完成，不启动 Monitor，也不会调用 WTS。
        using var service = new StartupGuardService(
            Array.Empty<string>(),
            Array.Empty<string>(),
            () => null);

        WritePrivate(service, "_observedSessionId", 7u);
        WritePrivate(service, "_observedSessionLogonTime", (DateTime?)LogonTime);
        WritePrivate(service, "_missingSessionScans", 3);
        WritePrivate(service, "_logonIdentityUnknownSinceTimestamp", 123L);
        WritePrivate(service, "_sessionFirstSeenTimestamp", 456L);
        WritePrivate(service, "_loggedResidentPhase", true);
        WritePrivate(service, "_loginNotificationRefreshRequested", 0);
        WritePrivate(service, "_loginCompletedSessionId", 7u);
        WritePrivate(service, "_loginCompletedTimestamp", 789L);

        ReadPrivate<Dictionary<int, DateTime>>(service, "_baselineProcesses")[42] = LogonTime;
        ReadPrivate<Dictionary<int, DateTime>>(service, "_failedProcesses")[43] = LogonTime;
        ReadPrivate<HashSet<string>>(service, "_reportedFailurePaths").Add(@"C:\Apps\startup.exe");

        service.ResetObservedSession();

        Assert.Equal(0u, ReadPrivate<uint>(service, "_observedSessionId"));
        Assert.Null(ReadPrivate<DateTime?>(service, "_observedSessionLogonTime"));
        Assert.Equal(0, ReadPrivate<int>(service, "_missingSessionScans"));
        Assert.Equal(0L, ReadPrivate<long>(service, "_logonIdentityUnknownSinceTimestamp"));
        Assert.Equal(0L, ReadPrivate<long>(service, "_sessionFirstSeenTimestamp"));
        Assert.False(ReadPrivate<bool>(service, "_loggedResidentPhase"));
        Assert.Empty(ReadPrivate<Dictionary<int, DateTime>>(service, "_baselineProcesses"));
        Assert.Empty(ReadPrivate<Dictionary<int, DateTime>>(service, "_failedProcesses"));
        Assert.Empty(ReadPrivate<HashSet<string>>(service, "_reportedFailurePaths"));
        Assert.Equal(0u, ReadPrivate<uint>(service, "_loginCompletedSessionId"));
        Assert.Equal(0L, ReadPrivate<long>(service, "_loginCompletedTimestamp"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Huorong\Sysdiag\bin\HipsTray.exe")]
    [InlineData(@"C:\Program Files (x86)\Huorong\Sysdiag\bin\HipsMain.exe")]
    [InlineData(@"C:\Program Files\Windows Defender\SecurityHealthSystray.exe")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.0.0-0\MsMpEng.exe")]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe")]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.Windows.SecHealthUI_cw5n1h2txyewy\SecHealthUI.exe")]
    public void 安全软件进程会被识别为不可终止(string executablePath)
    {
        Assert.True(StartupGuardService.IsProtectedSecurityExecutable(executablePath));
    }

    [Fact]
    public void 普通目录中的同名文件不会获得安全软件豁免()
    {
        Assert.False(StartupGuardService.IsProtectedSecurityExecutable(
            @"C:\Users\Public\HipsTray.exe"));
    }

    [Fact]
    public void 安全软件路径即使出现在启动项精确集合中也不会被终止()
    {
        const string executablePath =
            @"C:\Program Files\Huorong\Sysdiag\bin\HipsTray.exe";
        var exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            executablePath
        };

        Assert.False(StartupGuardService.ShouldTerminate(
            processSessionId: 7,
            targetSessionId: 7,
            mainSessionId: 1,
            executablePath,
            exactPaths));
        Assert.False(StartupGuardService.ShouldTerminateManagedServiceSpawn(
            processSessionId: 7,
            targetSessionId: 7,
            mainSessionId: 1,
            executablePath,
            parentProcessId: 42,
            processStartedAt: LogonTime,
            exactPaths,
            _ => null));
    }

    [Fact]
    public void 普通启动程序仍按精确路径进入终止判定()
    {
        const string executablePath = @"D:\Apps\startup.exe";
        var exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            executablePath
        };

        Assert.False(StartupGuardService.IsProtectedSecurityExecutable(executablePath));
        Assert.True(StartupGuardService.ShouldTerminate(
            processSessionId: 7,
            targetSessionId: 7,
            mainSessionId: 1,
            executablePath,
            exactPaths));
    }

    private static T ReadPrivate<T>(object instance, string fieldName)
    {
        var field = typeof(StartupGuardService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field.GetValue(instance)!;
    }

    private static void WritePrivate(object instance, string fieldName, object? value)
    {
        var field = typeof(StartupGuardService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }
}
