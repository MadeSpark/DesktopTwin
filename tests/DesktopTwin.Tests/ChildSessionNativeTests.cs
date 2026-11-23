using DesktopTwin.Interop;
using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// WTS 子会话查询必须区分「确认不存在」与「查询失败」。
/// 恢复和守卫只能用前者证明可以放开启动项。
/// </summary>
public sealed class ChildSessionNativeTests
{
    [Fact]
    public void 查询成功且无子会话哨兵表示确认没有子会话()
    {
        var known = ChildSessionNativeMethods.InterpretChildSessionQuery(
            succeeded: true,
            value: uint.MaxValue,
            win32Error: 0,
            out var sessionId);

        Assert.True(known);
        Assert.Null(sessionId);
    }

    [Fact]
    public void 查询成功却返回会话零必须视为未知()
    {
        var known = ChildSessionNativeMethods.InterpretChildSessionQuery(
            succeeded: true,
            value: 0,
            win32Error: 0,
            out var sessionId);

        Assert.False(known);
        Assert.Null(sessionId);
    }

    [Fact]
    public void 查询成功且非零返回实际子会话()
    {
        var known = ChildSessionNativeMethods.InterpretChildSessionQuery(
            succeeded: true,
            value: 7,
            win32Error: 0,
            out var sessionId);

        Assert.True(known);
        Assert.Equal(7u, sessionId);
    }

    [Fact]
    public void ERROR_NOT_FOUND表示确认没有子会话()
    {
        var known = ChildSessionNativeMethods.InterpretChildSessionQuery(
            succeeded: false,
            value: 123,
            win32Error: 1168,
            out var sessionId);

        Assert.True(known);
        Assert.Null(sessionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(1167)]
    public void 其他查询错误必须返回未知而不是确认消失(int win32Error)
    {
        var known = ChildSessionNativeMethods.InterpretChildSessionQuery(
            succeeded: false,
            value: 123,
            win32Error: win32Error,
            out var sessionId);

        Assert.False(known);
        Assert.Null(sessionId);
    }

    [Fact]
    public void 有关停日志时禁止改写分身端启动项()
    {
        Assert.True(ChildSessionService.ShouldDeferSessionAgentStartupMutation(
            journalExists: true,
            querySucceeded: true,
            sessionId: null));
    }

    [Fact]
    public void 子会话仍存在时禁止改写分身端启动项()
    {
        Assert.True(ChildSessionService.ShouldDeferSessionAgentStartupMutation(
            journalExists: false,
            querySucceeded: true,
            sessionId: 7));
    }

    [Fact]
    public void 无法确认子会话时必须失败关闭并禁止改写启动项()
    {
        Assert.True(ChildSessionService.ShouldDeferSessionAgentStartupMutation(
            journalExists: false,
            querySucceeded: false,
            sessionId: null));
    }

    [Fact]
    public void 仅确认没有子会话且无日志时才允许维护启动项()
    {
        Assert.False(ChildSessionService.ShouldDeferSessionAgentStartupMutation(
            journalExists: false,
            querySucceeded: true,
            sessionId: null));
    }

    [Fact]
    public void 延迟登录检查在启动代际推进后必须作废()
    {
        Assert.False(ChildSessionService.IsCurrentLoginGeneration(
            capturedGeneration: 4,
            currentGeneration: 5));
    }

    [Fact]
    public void 延迟登录检查仍属于同一启动代际时才可继续()
    {
        Assert.True(ChildSessionService.IsCurrentLoginGeneration(
            capturedGeneration: 5,
            currentGeneration: 5));
    }

    [Fact]
    public void 新启动已接手时旧启动失败不得恢复启动项()
    {
        Assert.True(ChildSessionService.ShouldTreatStartAsSuperseded(
            startGeneration: 4,
            activeStartGeneration: 5,
            activeStartOperationExists: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 同一代际的启动不得误判为被新启动取代(
        bool activeStartOperationExists)
    {
        Assert.False(ChildSessionService.ShouldTreatStartAsSuperseded(
            startGeneration: 5,
            activeStartGeneration: 5,
            activeStartOperationExists));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void 当前初始化或设置重连仍必须接收RDP失败事件(
        bool connectionAttemptPending,
        bool settingsReconnectPending)
    {
        Assert.True(ChildSessionService.ShouldAcceptRdpConnectionFailure(
            disposed: false,
            isolationFailureInProgress: false,
            senderIsForeign: false,
            connectionAttemptPending,
            settingsReconnectPending));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public void 已结束外来隔离失败或已释放连接的RDP事件必须丢弃(
        bool disposed,
        bool isolationFailureInProgress,
        bool senderIsForeign,
        bool connectionAttemptPending,
        bool settingsReconnectPending)
    {
        Assert.False(ChildSessionService.ShouldAcceptRdpConnectionFailure(
            disposed,
            isolationFailureInProgress,
            senderIsForeign,
            connectionAttemptPending,
            settingsReconnectPending));
    }

    [Fact]
    public void 可见桌面只接受目标子会话中绝对路径的_explorer()
    {
        Assert.True(ChildSessionService.IsExactExplorerCandidate(
            processSessionId: 7,
            processImagePath: @"C:\Windows\explorer.exe",
            expectedSessionId: 7,
            expectedExplorerPath: @"C:\Windows\explorer.exe"));
        Assert.True(ChildSessionService.IsExactExplorerCandidate(
            processSessionId: 7,
            processImagePath: @"c:\windows\EXPLORER.EXE",
            expectedSessionId: 7,
            expectedExplorerPath: @"C:\Windows\explorer.exe"));
    }

    [Theory]
    [InlineData(1u, 7u, @"C:\Windows\explorer.exe")]
    [InlineData(0u, 7u, @"C:\Windows\explorer.exe")]
    [InlineData(7u, 0u, @"C:\Windows\explorer.exe")]
    [InlineData(7u, 7u, @"C:\Windows\System32\cmd.exe")]
    [InlineData(7u, 7u, @"explorer.exe")]
    [InlineData(7u, 7u, @"..\Windows\explorer.exe")]
    public void 可见桌面拒绝会话一_零_错误路径和相对路径(
        uint processSessionId,
        uint expectedSessionId,
        string processImagePath)
    {
        Assert.False(ChildSessionService.IsExactExplorerCandidate(
            processSessionId,
            processImagePath,
            expectedSessionId,
            @"C:\Windows\explorer.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void 可见桌面拒绝缺失的进程路径(string? processImagePath)
    {
        Assert.False(ChildSessionService.IsExactExplorerCandidate(
            7,
            processImagePath!,
            7,
            @"C:\Windows\explorer.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void 可见桌面拒绝缺失的_explorer_预期路径(string? expectedExplorerPath)
    {
        Assert.False(ChildSessionService.IsExactExplorerCandidate(
            7,
            @"C:\Windows\explorer.exe",
            7,
            expectedExplorerPath!));
    }
}
