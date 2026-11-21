using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpInputIsolationTests
{
    [Fact]
    public void 观看模式绝不允许安装本机键盘钩子()
    {
        Assert.Equal(0, RdpActiveXHost.GetSafeKeyboardHookMode(
            sendSystemShortcutsToRemote: true,
            inputBlocked: true));
        Assert.Equal(1, RdpActiveXHost.GetSafeKeyboardHookMode(
            sendSystemShortcutsToRemote: true,
            inputBlocked: false));
        Assert.Equal(0, RdpActiveXHost.GetSafeKeyboardHookMode(
            sendSystemShortcutsToRemote: false,
            inputBlocked: true));
        Assert.Equal(0, RdpActiveXHost.GetSafeKeyboardHookMode(
            sendSystemShortcutsToRemote: false,
            inputBlocked: false));
    }

    [Fact]
    public void 键盘钩子读回缺失或类型异常时必须拒绝()
    {
        Assert.False(RdpActiveXHost.MatchesKeyboardHookMode(null, 0));
        Assert.False(RdpActiveXHost.MatchesKeyboardHookMode("not-a-number", 0));
        Assert.False(RdpActiveXHost.MatchesKeyboardHookMode("0", 0));
        Assert.False(RdpActiveXHost.MatchesKeyboardHookMode(false, 0));
        Assert.False(RdpActiveXHost.MatchesKeyboardHookMode(1, 0));
        Assert.True(RdpActiveXHost.MatchesKeyboardHookMode(0, 0));
        Assert.True(RdpActiveXHost.MatchesKeyboardHookMode((short)1, 1));
    }

    [Fact]
    public void 连接前键盘钩子不匹配时不得读取剪贴板或调用连接()
    {
        var clipboardRead = false;
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunIsolationPreflightBeforeConnect(
                expectedKeyboardHookMode: 0,
                readKeyboardHookMode: () => 1,
                readClipboardRedirect: () =>
                {
                    clipboardRead = true;
                    return false;
                },
                connect: () => connected = true));

        Assert.Contains("KeyboardHookMode", exception.Message);
        Assert.False(clipboardRead);
        Assert.False(connected);
    }

    [Fact]
    public void 连接前剪贴板隔离未确认时不得调用连接()
    {
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunIsolationPreflightBeforeConnect(
                expectedKeyboardHookMode: 0,
                readKeyboardHookMode: () => 0,
                readClipboardRedirect: () => null,
                connect: () => connected = true));

        Assert.Contains("剪贴板", exception.Message);
        Assert.False(connected);
    }

    [Fact]
    public void 连接前两项隔离都通过后才允许调用连接()
    {
        var connected = 0;

        RdpActiveXHost.RunIsolationPreflightBeforeConnect(
            expectedKeyboardHookMode: 0,
            readKeyboardHookMode: () => (short)0,
            readClipboardRedirect: () => false,
            connect: () => connected++);

        Assert.Equal(1, connected);
    }
}
