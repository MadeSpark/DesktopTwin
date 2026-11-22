using System.Reflection;
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
    public void 键盘钩子失败时仍调用原生输入窗口禁用()
    {
        var called = false;
        var expectedHost = new IntPtr(0x1234);
        var appliedEnabledValue = true;

        var result = RdpActiveXHost.ContainKeyboardHookFailure(
            expectedHost,
            (hostWindow, enabled) =>
            {
                called = true;
                Assert.Equal(expectedHost, hostWindow);
                appliedEnabledValue = enabled;
                return RdpInputWindowApplyResult.Applied;
            });

        Assert.True(called);
        Assert.False(appliedEnabledValue);
        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
    }

    [Fact]
    public void 键盘钩子失败且原生窗口缺失时不调用空句柄()
    {
        var called = false;

        var result = RdpActiveXHost.ContainKeyboardHookFailure(
            IntPtr.Zero,
            (_, _) =>
            {
                called = true;
                return RdpInputWindowApplyResult.Applied;
            });

        Assert.False(called);
        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
    }

    [Fact]
    public void 生产输入屏蔽在键盘钩子读回失败时仍禁用原生输入窗口()
    {
        var host = new IntPtr(0x1234);
        var nativeCalls = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: true,
            tryApplyKeyboardHookMode: _ => false,
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => true,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { false }, nativeCalls);
    }

    [Fact]
    public void 解除输入屏蔽时键盘钩子应用失败仍回滚为屏蔽()
    {
        var host = new IntPtr(0x2234);
        var nativeCalls = new List<bool>();
        var hookModes = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: false,
            tryApplyKeyboardHookMode: mode =>
            {
                hookModes.Add(mode);
                return mode;
            },
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => true,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { false, true }, hookModes);
        Assert.Equal(new[] { false }, nativeCalls);
    }

    [Fact]
    public void 生产输入屏蔽在OCX或InvokeMember抛异常时仍禁用原生输入窗口()
    {
        var host = new IntPtr(0x2345);
        var nativeCalls = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: false,
            tryApplyKeyboardHookMode: _ =>
                throw new TargetInvocationException(
                    new COMException("InvokeMember(SecuredSettings2) failed")),
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => true,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { false }, nativeCalls);
    }

    [Fact]
    public void 生产输入屏蔽在键盘钩子失败且Handle首次读取抛异常时仍重试禁用()
    {
        var host = new IntPtr(0x3456);
        var handleReads = 0;
        var nativeCalls = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: true,
            tryApplyKeyboardHookMode: _ => false,
            isHandleCreated: () => true,
            getHostWindow: () =>
            {
                handleReads++;
                if (handleReads == 1)
                {
                    throw new InvalidOperationException("Handle getter failed");
                }

                return host;
            },
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => true,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(2, handleReads);
        Assert.Equal(new[] { false }, nativeCalls);
    }

    [Fact]
    public void 生产输入屏蔽在原生窗口回调抛异常时仍重试禁用()
    {
        var host = new IntPtr(0x4567);
        var nativeCalls = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: true,
            tryApplyKeyboardHookMode: _ => true,
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                if (nativeCalls.Count == 1)
                {
                    throw new COMException("SetRdpInputWindowEnabled failed");
                }

                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => true,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { false, false }, nativeCalls);
    }

    [Fact]
    public void 生产输入屏蔽在解除时原生回调返回Failed后必须再次禁用()
    {
        var host = new IntPtr(0x4789);
        var nativeCalls = new List<bool>();
        var hookModes = new List<bool>();
        var focusCalled = false;

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: false,
            tryApplyKeyboardHookMode: mode =>
            {
                hookModes.Add(mode);
                return true;
            },
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Failed;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ =>
            {
                focusCalled = true;
                return true;
            },
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { true, false }, nativeCalls);
        Assert.Equal(new[] { false, true }, hookModes);
        Assert.False(focusCalled);
    }

    [Fact]
    public void 生产输入屏蔽在焦点回调返回false时不得报告解除成功()
    {
        var host = new IntPtr(0x5678);
        var nativeCalls = new List<bool>();
        var hookModes = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: false,
            tryApplyKeyboardHookMode: mode =>
            {
                hookModes.Add(mode);
                return true;
            },
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ => false,
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { true, false }, nativeCalls);
        Assert.Equal(new[] { false, true }, hookModes);
    }

    [Fact]
    public void 生产输入屏蔽在焦点回调抛异常时回滚为屏蔽并禁用()
    {
        var host = new IntPtr(0x5678);
        var nativeCalls = new List<bool>();
        var hookModes = new List<bool>();

        var result = RdpActiveXHost.ApplyInputBlocked(
            blocked: false,
            tryApplyKeyboardHookMode: mode =>
            {
                hookModes.Add(mode);
                return true;
            },
            isHandleCreated: () => true,
            getHostWindow: () => host,
            applyInputWindowState: (handle, enabled) =>
            {
                Assert.Equal(host, handle);
                nativeCalls.Add(enabled);
                return RdpInputWindowApplyResult.Applied;
            },
            getConnectedState: () => 1,
            tryFocusInputWindow: _ =>
                throw new InvalidOperationException("TryFocusRdpInputWindow failed"),
            out var inputBlocked);

        Assert.Equal(RdpInputWindowApplyResult.Failed, result);
        Assert.True(inputBlocked);
        Assert.Equal(new[] { true, false }, nativeCalls);
        Assert.Equal(new[] { false, true }, hookModes);
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
