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
}
