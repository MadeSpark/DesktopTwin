using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [Fact]
    public void 连接前外壳门卫程序不匹配时不得读取工作目录或调用连接()
    {
        var workDirectoryRead = false;
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunShellGatePreflightBeforeConnect(
                @"C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe",
                @"C:\ProgramData\DesktopTwin\Agent",
                readStartProgram: () => @"C:\Windows\explorer.exe",
                readWorkingDirectory: () =>
                {
                    workDirectoryRead = true;
                    return @"C:\ProgramData\DesktopTwin\Agent";
                },
                connect: () => connected = true));

        Assert.Contains("StartProgram", exception.Message);
        Assert.False(workDirectoryRead);
        Assert.False(connected);
    }

    [Fact]
    public void 连接前外壳门卫工作目录不匹配时不得调用连接()
    {
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunShellGatePreflightBeforeConnect(
                @"C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe",
                @"C:\ProgramData\DesktopTwin\Agent",
                readStartProgram: () =>
                    @"""C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe""",
                readWorkingDirectory: () => @"C:\Windows",
                connect: () => connected = true));

        Assert.Contains("WorkDir", exception.Message);
        Assert.False(connected);
    }

    [Fact]
    public void 连接前外壳门卫程序与工作目录一致后才允许调用连接()
    {
        var connected = 0;

        RdpActiveXHost.RunShellGatePreflightBeforeConnect(
            @"C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe",
            @"C:\ProgramData\DesktopTwin\Agent",
            readStartProgram: () =>
                @"""C:\PROGRAMDATA\DesktopTwin\Agent\DesktopTwin.ShellGate.exe""",
            readWorkingDirectory: () => @"C:\ProgramData\DesktopTwin\Agent\",
            connect: () => connected++);

        Assert.Equal(1, connected);
    }

    [Fact]
    public void 连接前外壳门卫禁止接受带命令行参数的读回()
    {
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunShellGatePreflightBeforeConnect(
                @"C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe",
                @"C:\ProgramData\DesktopTwin\Agent",
                readStartProgram: () =>
                    @"""C:\ProgramData\DesktopTwin\Agent\DesktopTwin.ShellGate.exe"" --shell-gate",
                readWorkingDirectory: () => @"C:\ProgramData\DesktopTwin\Agent",
                connect: () => connected = true));

        Assert.Contains("StartProgram", exception.Message);
        Assert.False(connected);
    }

    [Fact]
    public void 启动项模式连接前残留启动程序时不得读取工作目录或调用连接()
    {
        var workDirectoryRead = false;
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunExplorerShellPreflightBeforeConnect(
                readStartProgram: () => @"C:\Windows\explorer.exe",
                readWorkingDirectory: () =>
                {
                    workDirectoryRead = true;
                    return null;
                },
                connect: () => connected = true));

        Assert.Contains("StartProgram", exception.Message);
        Assert.False(workDirectoryRead);
        Assert.False(connected);
    }

    [Fact]
    public void 启动项模式连接前残留工作目录时不得调用连接()
    {
        var connected = false;

        var exception = Assert.Throws<COMException>(() =>
            RdpActiveXHost.RunExplorerShellPreflightBeforeConnect(
                readStartProgram: () => null,
                readWorkingDirectory: () => @"C:\ProgramData\DesktopTwin\Agent",
                connect: () => connected = true));

        Assert.Contains("WorkDir", exception.Message);
        Assert.False(connected);
    }

    [Fact]
    public void 启动项模式连接前两个外壳值都为空后才允许调用连接()
    {
        var connected = 0;

        RdpActiveXHost.RunExplorerShellPreflightBeforeConnect(
            readStartProgram: () => null,
            readWorkingDirectory: () => "  ",
            connect: () => connected++);

        Assert.Equal(1, connected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 启动项模式把空值和纯空白都视为未配置外壳(string? value)
    {
        Assert.True(RdpActiveXHost.IsUnsetConfiguredPath(value));
    }

    [Fact]
    public void 启动项模式不接受非空外壳值()
    {
        Assert.False(RdpActiveXHost.IsUnsetConfiguredPath(@"C:\Windows\explorer.exe"));
        Assert.False(RdpActiveXHost.IsUnsetConfiguredPath(0));
    }

    [Fact]
    public void 外壳模式可在门卫和资源管理器之间显式切换()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-ShellMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var gatePath = Path.Combine(directory, "DesktopTwin.ShellGate.exe");
            File.WriteAllText(gatePath, "test");
            var host = (RdpActiveXHost)RuntimeHelpers.GetUninitializedObject(
                typeof(RdpActiveXHost));

            host.ClearShellGate();
            Assert.True(GetPrivateField<bool>(host, "_shellGateSelectionConfigured"));
            Assert.False(GetPrivateField<bool>(host, "_useAlternateShell"));
            Assert.Null(GetPrivateField<string?>(host, "_shellGateProgramPath"));
            Assert.Null(GetPrivateField<string?>(host, "_shellGateWorkingDirectory"));

            host.SetShellGate(gatePath, directory);
            Assert.True(GetPrivateField<bool>(host, "_shellGateSelectionConfigured"));
            Assert.True(GetPrivateField<bool>(host, "_useAlternateShell"));
            Assert.Equal(gatePath, GetPrivateField<string>(host, "_shellGateProgramPath"));
            Assert.Equal(directory, GetPrivateField<string>(host, "_shellGateWorkingDirectory"));

            host.ClearShellGate();
            Assert.True(GetPrivateField<bool>(host, "_shellGateSelectionConfigured"));
            Assert.False(GetPrivateField<bool>(host, "_useAlternateShell"));
            Assert.Null(GetPrivateField<string?>(host, "_shellGateProgramPath"));
            Assert.Null(GetPrivateField<string?>(host, "_shellGateWorkingDirectory"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 门卫模式拒绝任意文件名()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-ShellMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var wrongPath = Path.Combine(directory, "not-the-gate.exe");
            File.WriteAllText(wrongPath, "test");
            var host = (RdpActiveXHost)RuntimeHelpers.GetUninitializedObject(
                typeof(RdpActiveXHost));

            var exception = Assert.Throws<ArgumentException>(
                () => host.SetShellGate(wrongPath, directory));

            Assert.Contains("固定安装约定", exception.Message);
            Assert.False(GetPrivateField<bool>(host, "_shellGateSelectionConfigured"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        var field = typeof(RdpActiveXHost).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }
}
