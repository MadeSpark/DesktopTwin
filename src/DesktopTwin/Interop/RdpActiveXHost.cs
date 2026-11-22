using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopTwin.Models;
using DesktopTwin.Services;
using DrawingSize = System.Drawing.Size;
using DesktopTwin.Localization;

namespace DesktopTwin.Interop;

internal sealed class RdpActiveXHost : AxHost
{
    // Windows 10+ 自带的非脚本化 RDP ActiveX 控件（MsRdpClient10）。
    private const string RdpClientClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private const short VariantFalse = 0;
    private const short VariantTrue = -1;
    private const int RedirectAudioToClient = 0;
    private const int DisableRemoteAudio = 2;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private static readonly RemoteKey LeftWindowsKey = new(0x5B, IsExtended: true);
    private static readonly RemoteKey DKey = new(0x20, IsExtended: false);
    private static readonly RemoteKey TabKey = new(0x0F, IsExtended: false);
    private ConnectionPointCookie? _eventCookie;
    private RdpEventSink? _eventSink;
    private bool _connectionAttemptInProgress;
    private bool _connectionFailureReported;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionDiagnostic;
    private bool _disconnectRequested;
    private bool _sendSystemShortcutsToRemote = true;
    private bool _audioMuted;

    /// <summary>
    /// 待重连的代际号。取消（DisconnectSession / ResetDisconnectState）时自增，
    /// 让已经排上、还在等「会话释放」的那次重连作废——否则它醒来后照样去连，
    /// 把刚刚发起的断开又接回来。
    /// </summary>
    private int _reconnectGeneration;
    private DrawingSize? _pendingReconnectDesktopSize;
    private bool _smartSizingEnabled = true;
    private readonly RdpSmartSizingController _smartSizing;
    private bool _inputBlocked;
    private Func<(string UserName, string Domain, string Password)?>? _credentialProvider;

    internal event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    internal event EventHandler? LoginCompleted;

    internal ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic =>
        _lastConnectionDiagnostic;

    internal RdpActiveXHost()
        : base(RdpClientClsid)
    {
        _smartSizing = new RdpSmartSizingController(
            ApplySmartSizing,
            callback => BeginInvoke(callback),
            RefreshSmartSizing);
        Dock = DockStyle.Fill;
    }

    internal int ConnectedState
    {
        get
        {
            if (!IsHandleCreated)
            {
                return 0;
            }

            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "Connected"),
                CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 设置自动登录凭据的来源。每次发起连接时都会调用一次，取不到就退回交互式输入。
    ///
    /// 用回调而不是缓存值：设置类重连（改分辨率、切音频）也会走连接流程，
    /// 回调能保证这些路径同样拿得到最新凭据。
    /// </summary>
    internal void SetCredentialProvider(
        Func<(string UserName, string Domain, string Password)?>? provider)
    {
        _credentialProvider = provider;
    }

    internal void ConnectToChildSession(DrawingSize desktopSize)
    {
        if (ConnectedState != 0)
        {
            return;
        }

        ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        var client = GetRequiredOcx();
        var width = Math.Clamp(desktopSize.Width, 200, 8192);
        var height = Math.Clamp(desktopSize.Height, 200, 8192);

        // 显式使用回环 IP 而不是 "localhost"：目标地址不参与任何名称解析，
        // 避免 hosts 文件或 DNS 配置把连接引到别处。
        SetComProperty(client, "Server", "127.0.0.1");
        SetComProperty(client, "DesktopWidth", width);
        SetComProperty(client, "DesktopHeight", height);
        SetComProperty(client, "ColorDepth", 32);
        SetComProperty(client, "ConnectingText", Locale.Get("Rdp.ConnectingText"));
        SetComProperty(client, "DisconnectedText", Locale.Get("Rdp.DisconnectedText"));

        var securedSettings = GetComProperty(client, "SecuredSettings2")
            ?? throw new COMException(Locale.Get("Rdp.Error.SecuredSettings"));
        // 观看模式必须把键盘钩子留在远程会话内。SendSystemShortcutsToRemote
        // 只影响控制模式；用户以前存下的 true 不能在观看/MCP 模式下恢复本机钩子。
        var hookMode = GetSafeKeyboardHookMode(
            _sendSystemShortcutsToRemote,
            _inputBlocked);
        RunComStep(Locale.Get("Rdp.Step.ShortcutTarget"), () =>
        {
            SetComProperty(securedSettings, "KeyboardHookMode", hookMode);

            // 这个属性既决定 RDP 是否安装本机键盘钩子，也决定组合键是否转发到
            // 远程会话。观看模式下读不回来或值不一致时不能继续连接，否则本地
            // 热键可能被远程会话截获。
            var actual = GetComProperty(securedSettings, "KeyboardHookMode");
            if (!MatchesKeyboardHookMode(actual, hookMode))
            {
                throw new COMException(
                    $"KeyboardHookMode 读回 {actual ?? "(空)"}，期望 {hookMode}。");
            }

            AppLog.Info($"KeyboardHookMode 已写入 {hookMode}，读回 {actual}。");
        });
        RunComStep(Locale.Get("Rdp.Step.AudioRedirection"), () =>
            SetComProperty(
                securedSettings,
                "AudioRedirectionMode",
                _audioMuted ? DisableRemoteAudio : RedirectAudioToClient));

        var advancedSettings = GetComProperty(client, "AdvancedSettings7")
            ?? throw new COMException(Locale.Get("Rdp.Error.AdvancedSettings"));
        RunComStep(Locale.Get("Rdp.Step.Port"), () =>
            SetComProperty(
                advancedSettings,
                "RDPPort",
                ChildSessionNativeMethods.GetConfiguredRdpPort()));
        RunComStep(Locale.Get("Rdp.Step.CredSsp"), () =>
            SetComProperty(advancedSettings, "EnableCredSspSupport", true));
        RunComStep(Locale.Get("Rdp.Step.ClipboardRedirect"), () =>
        {
            // RDP 剪贴板重定向默认开启。若不显式关闭，分身里的剪贴板读写会
            // 经 RDP 通道桥接主桌面，违背“只操作分身”的隔离承诺。
            SetComProperty(advancedSettings, "RedirectClipboard", false);
            var actual = GetComProperty(advancedSettings, "RedirectClipboard");
            if (!IsConfirmedFalse(actual))
            {
                throw new COMException(
                    $"RDP 剪贴板重定向读回 {actual ?? "(空)"}，拒绝建立隔离。");
            }
        });
        RunComStep(Locale.Get("Rdp.Step.RemoteWindowsKey"), () =>
            SetComProperty(advancedSettings, "EnableWindowsKey", 1));
        // 每次连接重新确认设置，同时作废上一连接遗留的异步重排。
        _smartSizing.Reset();
        _smartSizing.Apply(_smartSizingEnabled);

        object connectToChildSession = true;
        RunComStep(Locale.Get("Rdp.Step.ChildSession"), () =>
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            TrySetExtendedProperty(extendedSettings, "EnableZoom", true);
            extendedSettings.set_Property("ConnectToChildSession", ref connectToChildSession);
        });

        // 写入后、原生 Connect 前再读回一次。若控件在配置过程中改写了隔离属性，
        // 这里必须终止连接；测试也以该入口证明失败时 Connect 不会被调用。
        RunIsolationPreflightBeforeConnect(
            hookMode,
            () => GetComProperty(securedSettings, "KeyboardHookMode"),
            () => GetComProperty(advancedSettings, "RedirectClipboard"),
            () =>
            {
                ApplyCredentials(client);

                _connectionAttemptInProgress = true;
                _connectionFailureReported = false;
                _lastConnectionDiagnostic = null;
                _disconnectRequested = false;
                try
                {
                    RunComStep(Locale.Get("Rdp.Step.Connect"), () => InvokeComMethod(client, "Connect"));
                }
                catch
                {
                    _connectionAttemptInProgress = false;
                    throw;
                }
            });
    }

    internal void SendShowDesktopShortcut()
    {
        KeyStroke[] strokes =
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ];

        SendShortcut(strokes, "Win+D");
    }

    internal void SendTaskViewShortcut()
    {
        KeyStroke[] strokes =
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ];

        SendShortcut(strokes, "Win+Tab");
    }

    internal void SetSmartSizing(bool enabled)
    {
        _smartSizingEnabled = enabled;
        if (!IsHandleCreated)
        {
            return;
        }

        if (!_smartSizing.Apply(enabled))
        {
            return;
        }

        // 这个属性允许联机修改，但控件是在「尺寸变化」时按它重算缩放比例的：
        // 只改属性不会重绘，画面上看不出任何变化（全屏自适应/1:1 切换失灵的原因）。
        // 把尺寸推一像素再复位，逼它按新设置重新排版。
        if (ConnectedState != 1 || !IsHandleCreated)
        {
            return;
        }

        _smartSizing.ScheduleRefresh();
    }

    private void ApplySmartSizing(bool enabled)
    {
        var advancedSettings = GetComProperty(GetRequiredOcx(), "AdvancedSettings7")
            ?? throw new COMException(Locale.Get("Rdp.Error.AdvancedSettings"));
        RunComStep(Locale.Get("Rdp.Step.DisplayScale"), () =>
            SetComProperty(advancedSettings, "SmartSizing", enabled));
    }

    private void RefreshSmartSizing()
    {
        // 全屏先定缩放、再改窗口尺寸，必须在回调里取最新大小；断开后不再重排。
        if (IsDisposed || Disposing || !IsHandleCreated || ConnectedState != 1
            || Width <= 1 || Height <= 1)
        {
            return;
        }

        var size = Size;
        Size = new DrawingSize(size.Width - 1, size.Height);
        Size = size;
    }

    internal void SetSendSystemShortcutsToRemote(bool enabled)
    {
        _sendSystemShortcutsToRemote = enabled;
    }

    /// <summary>
    /// 计算本次连接必须写入的 RDP KeyboardHookMode。屏蔽本机输入期间
    /// 无条件选择 0；控制模式才允许根据系统组合键设置安装本机钩子。
    /// </summary>
    internal static int GetSafeKeyboardHookMode(
        bool sendSystemShortcutsToRemote,
        bool inputBlocked)
    {
        return inputBlocked || !sendSystemShortcutsToRemote ? 0 : 1;
    }

    /// <summary>
    /// 只接受确实读回且与期望一致的 KeyboardHookMode。COM 缺少该属性时
    /// 可能返回 null，不能把它静默转换为 0 后误判为已建立隔离。
    /// </summary>
    internal static bool MatchesKeyboardHookMode(object? actual, int expected)
    {
        if (actual is null)
        {
            return false;
        }

        return actual switch
        {
            sbyte value => value == expected,
            byte value => value == expected,
            short value => value == expected,
            ushort value => value == expected,
            int value => value == expected,
            uint value => value == expected,
            long value => value == expected,
            ulong value => value == (ulong)expected,
            _ => false
        };
    }

    /// <summary>
    /// 在触发 RDP 原生 <c>Connect</c> 前重新读回两项隔离属性。
    /// 任一读回缺失、类型异常或不匹配时都必须抛错且不得调用 <c>connect</c>。
    /// </summary>
    internal static void RunIsolationPreflightBeforeConnect(
        int expectedKeyboardHookMode,
        Func<object?> readKeyboardHookMode,
        Func<object?> readClipboardRedirect,
        Action connect)
    {
        ArgumentNullException.ThrowIfNull(readKeyboardHookMode);
        ArgumentNullException.ThrowIfNull(readClipboardRedirect);
        ArgumentNullException.ThrowIfNull(connect);

        var hookMode = readKeyboardHookMode();
        if (!MatchesKeyboardHookMode(hookMode, expectedKeyboardHookMode))
        {
            throw new COMException(
                $"连接前 KeyboardHookMode 读回 {hookMode ?? "(空)"}，期望 {expectedKeyboardHookMode}。");
        }

        var clipboardRedirect = readClipboardRedirect();
        if (!IsConfirmedFalse(clipboardRedirect))
        {
            throw new COMException(
                $"连接前 RDP 剪贴板重定向读回 {clipboardRedirect ?? "(空)"}，拒绝建立隔离。");
        }

        connect();
    }

    /// <summary>
    /// 只接受确实读回为 false 的布尔属性；属性缺失、类型异常或值为 true
    /// 都必须阻止连接，不能把 null 转换成 false。
    /// </summary>
    internal static bool IsConfirmedFalse(object? actual)
    {
        if (actual is null)
        {
            return false;
        }

        return actual is bool value && !value;
    }

    /// <summary>
    /// 输入隔离应用失败时仍要尝试禁用 RDP 的原生 Input Capture Window。
    /// 无论失败发生在键盘钩子、控件句柄还是原生窗口回调上，都不能因此跳过
    /// 本机输入窗口阻断；合并结果始终是 <see cref="RdpInputWindowApplyResult.Failed"/>，
    /// 因为隔离前置条件已经失败，调用方不得据此继续连接。
    /// </summary>
    internal static RdpInputWindowApplyResult ContainKeyboardHookFailure(
        IntPtr hostWindow,
        Func<IntPtr, bool, RdpInputWindowApplyResult> applyInputWindowState)
    {
        ArgumentNullException.ThrowIfNull(applyInputWindowState);
        if (hostWindow == IntPtr.Zero)
        {
            return RdpInputWindowApplyResult.Failed;
        }

        try
        {
            var result = applyInputWindowState(hostWindow, false);
            AppLog.Warn(
                "输入隔离未能确认，已同步尝试禁用 Input Capture Window："
                + $"applyResult={result}，host=0x{hostWindow:X}。");
        }
        catch (Exception exception)
        {
            AppLog.Warn(
                "输入隔离未能确认，且禁用 Input Capture Window 失败。",
                exception);
        }

        return RdpInputWindowApplyResult.Failed;
    }

    internal void SetAudioMuted(bool muted)
    {
        _audioMuted = muted;
    }

    /// <summary>
    /// 立即把当前期望的 KeyboardHookMode 写入 RDP 控件并读回。
    /// 失败返回 false；调用方必须把它当成隔离未建立，而不是只记录设置值。
    /// </summary>
    private bool TryApplyKeyboardHookMode(bool inputBlocked)
    {
        try
        {
            if (!IsHandleCreated)
            {
                return true;
            }

            var hookMode = GetSafeKeyboardHookMode(_sendSystemShortcutsToRemote, inputBlocked);
            var client = GetRequiredOcx();
            var securedSettings = GetComProperty(client, "SecuredSettings2")
                                 ?? throw new COMException(
                                     Locale.Get("Rdp.Error.SecuredSettings"));
            SetComProperty(securedSettings, "KeyboardHookMode", hookMode);
            var actual = GetComProperty(securedSettings, "KeyboardHookMode");
            if (!MatchesKeyboardHookMode(actual, hookMode))
            {
                AppLog.Warn(
                    $"KeyboardHookMode 读回 {actual ?? "(空)"}，期望 {hookMode}，拒绝建立输入隔离。");
                return false;
            }

            AppLog.Info($"KeyboardHookMode 已即时写入 {hookMode}，读回 {actual}。");
            return true;
        }
        catch (Exception exception)
        {
            // GetRequiredOcx 内部会读取 Handle，InvokeMember 也可能抛
            // TargetInvocationException 或其它未被具体 COM 异常类型覆盖的异常。
            // 这里一律按“未确认隔离”返回，让 SetInputBlocked 继续执行原生
            // Input Capture Window 禁用，绝不能把异常带到窗口状态处理之外。
            AppLog.Warn("KeyboardHookMode 即时应用失败，拒绝建立输入隔离。", exception);
            return false;
        }
    }

    /// <summary>
    /// 屏蔽或恢复本机进入桌面分身的鼠标键盘输入。期望状态会被记住，
    /// 每次连接登录后由 ChildSessionService 重新施加。
    ///
    /// 实际流程由 <see cref="ApplyInputBlocked"/> 完成；这里只绑定生产依赖，
    /// 并通过可注入 helper 保证测试覆盖真实的 SetInputBlocked 调用链。
    /// </summary>
    internal RdpInputWindowApplyResult SetInputBlocked(bool blocked)
    {
        var result = ApplyInputBlocked(
            blocked,
            TryApplyKeyboardHookMode,
            () => IsHandleCreated,
            () => Handle,
            ChildSessionNativeMethods.SetRdpInputWindowEnabled,
            () => ConnectedState,
            ChildSessionNativeMethods.TryFocusRdpInputWindow,
            out var inputBlocked);
        _inputBlocked = inputBlocked;
        return result;
    }

    /// <summary>
    /// SetInputBlocked 的异常安全实现。任何依赖（键盘钩子、OCX/InvokeMember、
    /// Handle、原生窗口或焦点回调）抛异常时，都会维持最严格的屏蔽期望，尽力
    /// 禁用原生 Input Capture Window，并返回 <see cref="RdpInputWindowApplyResult.Failed"/>。
    /// </summary>
    internal static RdpInputWindowApplyResult ApplyInputBlocked(
        bool blocked,
        Func<bool, bool> tryApplyKeyboardHookMode,
        Func<bool> isHandleCreated,
        Func<IntPtr> getHostWindow,
        Func<IntPtr, bool, RdpInputWindowApplyResult> applyInputWindowState,
        Func<int> getConnectedState,
        Func<IntPtr, bool> tryFocusInputWindow,
        out bool inputBlocked)
    {
        ArgumentNullException.ThrowIfNull(tryApplyKeyboardHookMode);
        ArgumentNullException.ThrowIfNull(isHandleCreated);
        ArgumentNullException.ThrowIfNull(getHostWindow);
        ArgumentNullException.ThrowIfNull(applyInputWindowState);
        ArgumentNullException.ThrowIfNull(getConnectedState);
        ArgumentNullException.ThrowIfNull(tryFocusInputWindow);

        inputBlocked = true;
        try
        {
            if (!tryApplyKeyboardHookMode(blocked))
            {
                // 未确认键盘钩子范围时只能维持更严格的阻断状态。这里不能提前返回：
                // 键盘钩子失败并不代表原生 Input Capture Window 已经禁用，必须仍尝试
                // 关掉窗口层输入，否则观看/MCP 模式可能继续把本机键鼠送进子会话。
                TryDisableInputWindowBestEffort(
                    getHostWindow,
                    applyInputWindowState,
                    "KeyboardHookMode 未能确认隔离");

                // 解除屏蔽时钩子写入可能已部分生效才失败。无论读回失败发生在哪一步，
                // 都必须再次尝试把 KeyboardHookMode 压回 0，避免只剩原生窗口阻断
                // 而系统快捷键仍被送进子会话。
                if (!blocked)
                {
                    try
                    {
                        if (!tryApplyKeyboardHookMode(true))
                        {
                            AppLog.Warn("KeyboardHookMode 应用失败后，未能回滚为 0。");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        AppLog.Warn("KeyboardHookMode 应用失败后，回滚也失败。", rollbackException);
                    }
                }

                return RdpInputWindowApplyResult.Failed;
            }

            if (!isHandleCreated())
            {
                inputBlocked = blocked;
                AppLog.Info($"SetInputBlocked: blocked={blocked}，控件句柄未创建，仅记录期望状态。");
                return blocked
                    ? RdpInputWindowApplyResult.WindowMissing
                    : RdpInputWindowApplyResult.Applied;
            }

            var hostWindow = getHostWindow();
            var result = applyInputWindowState(hostWindow, !blocked);
            if (result == RdpInputWindowApplyResult.WindowMissing)
            {
                // 未登录或新连接尚未创建输入窗口时没有可接收本机输入的 RDP 子窗口。
                // 记录期望状态，登录完成后再强制施加并读回。
                inputBlocked = blocked;
                AppLog.Info(
                    $"SetInputBlocked: blocked={blocked}，Input Capture Window 尚未创建，"
                    + $"仅记录期望状态（控件句柄 0x{hostWindow:X}）。");
                return result;
            }

            if (result != RdpInputWindowApplyResult.Applied)
            {
                // 找到了窗口却无法读回目标状态时按最安全的解释处理：认为输入窗口仍被屏蔽。
                // 原生回调返回 Failed 时，目标状态可能已经部分生效；尤其解除屏蔽失败时，
                // 不能只回滚 KeyboardHookMode 就返回，必须先尽力再次禁用 Input Capture Window。
                inputBlocked = true;
                TryDisableInputWindowBestEffort(
                    getHostWindow,
                    applyInputWindowState,
                    $"SetInputBlocked 未能验证目标状态（result={result}）");

                // 若这次原本要解除屏蔽，刚才可能已把 KeyboardHookMode 写成 1；
                // 因此还要立即把钩子模式压回 0，不能留下一个失败的半解除状态。
                if (!blocked && !tryApplyKeyboardHookMode(true))
                {
                    AppLog.Warn("解除输入屏蔽失败后，KeyboardHookMode 也未能回滚为 0。");
                }

                AppLog.Warn(
                    $"SetInputBlocked 未能验证目标状态：blocked={blocked}，result={result}，"
                    + $"控件句柄 0x{hostWindow:X}。");
                return RdpInputWindowApplyResult.Failed;
            }

            inputBlocked = blocked;

            // 解除阻止后把焦点交还给输入窗口：被禁用期间 Windows 已把焦点移走，
            // 仅重新启用输入窗口不会让 RDP 自动恢复输入转发，必须让它重新持有焦点，
            // 否则表现为「按钮已切回控制模式，画面却始终像观看模式一样没反应」。
            if (!blocked && getConnectedState() == 1)
            {
                var focused = tryFocusInputWindow(hostWindow);
                if (!focused)
                {
                    // 解除屏蔽后必须把焦点交还 RDP 输入窗口，否则界面看似已回到
                    // 控制模式，实际键鼠仍不会进入子会话。不能把这种半成功报告为
                    // Applied；立即重新阻断原生窗口和键盘钩子，回到 fail-closed 终态。
                    inputBlocked = true;
                    TryDisableInputWindowBestEffort(
                        getHostWindow,
                        applyInputWindowState,
                        "解除输入屏蔽后焦点归还失败");
                    try
                    {
                        if (!tryApplyKeyboardHookMode(true))
                        {
                            AppLog.Warn("解除输入屏蔽后焦点归还失败，KeyboardHookMode 也未能回滚为 0。");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        AppLog.Warn("解除输入屏蔽后焦点归还失败，KeyboardHookMode 回滚失败。", rollbackException);
                    }

                    AppLog.Warn(
                        $"SetInputBlocked: blocked={blocked}，解除后焦点归还输入窗口失败，"
                        + $"已回退屏蔽；前台窗口 0x{ChildSessionNativeMethods.GetForegroundWindow():X}。");
                    return RdpInputWindowApplyResult.Failed;
                }

                AppLog.Info(
                    $"SetInputBlocked: blocked={blocked}，解除后焦点归还输入窗口成功，"
                    + $"前台窗口 0x{ChildSessionNativeMethods.GetForegroundWindow():X}。");
            }
            else
            {
                AppLog.Info($"SetInputBlocked: blocked={blocked}，控件句柄 0x{hostWindow:X}。");
            }

            return RdpInputWindowApplyResult.Applied;
        }
        catch (Exception exception)
        {
            // Handle、ConnectedState 与原生/焦点回调都可能在保护范围外抛错。
            // 任何一处异常都要先把内存状态压回屏蔽，再同时回滚键盘钩子并重试
            // 原生窗口禁用，最后向调用方报告失败，不能把异常或半解除状态泄漏出去。
            inputBlocked = true;
            AppLog.Warn("SetInputBlocked 在确认输入隔离时发生异常，按失败处理。", exception);

            // 若这次原本要解除屏蔽，KeyboardHookMode 可能已经写成 1，随后才在
            // 窗口句柄、原生回调或焦点归还处失败。只禁用 Input Capture Window
            // 仍不够，键盘钩子也必须压回 0，否则半解除状态仍可能把本机系统
            // 快捷键送进子会话。这里是最后的保护范围，回滚本身也绝不能抛出去。
            if (!blocked)
            {
                try
                {
                    if (!tryApplyKeyboardHookMode(true))
                    {
                        AppLog.Warn("SetInputBlocked 异常后，KeyboardHookMode 未能回滚为 0。");
                    }
                }
                catch (Exception rollbackException)
                {
                    AppLog.Warn("SetInputBlocked 异常后，KeyboardHookMode 回滚失败。", rollbackException);
                }
            }

            TryDisableInputWindowBestEffort(
                getHostWindow,
                applyInputWindowState,
                "SetInputBlocked 异常");
            return RdpInputWindowApplyResult.Failed;
        }
    }

    private static void TryDisableInputWindowBestEffort(
        Func<IntPtr> getHostWindow,
        Func<IntPtr, bool, RdpInputWindowApplyResult> applyInputWindowState,
        string failureContext)
    {
        try
        {
            // Handle 求值本身也可能抛错。失败路径上再读一次句柄，避免一次
            // 瞬时的句柄读取异常就直接跳过原生 Input Capture Window 禁用。
            IntPtr hostWindow;
            try
            {
                hostWindow = getHostWindow();
            }
            catch (Exception firstException)
            {
                try
                {
                    hostWindow = getHostWindow();
                }
                catch (Exception secondException)
                {
                    throw new AggregateException(
                        "读取 RDP 控件 Handle 连续失败。",
                        firstException,
                        secondException);
                }
            }

            if (hostWindow == IntPtr.Zero)
            {
                AppLog.Warn(
                    $"{failureContext}，控件句柄不可用，未能禁用 Input Capture Window。");
                return;
            }

            _ = ContainKeyboardHookFailure(hostWindow, applyInputWindowState);
        }
        catch (Exception exception)
        {
            AppLog.Warn(
                $"{failureContext}，读取控件句柄或禁用 Input Capture Window 失败。",
                exception);
        }
    }

    /// <summary>
    /// 登录完成后重新施加已记录的输入屏蔽状态，并返回是否已通过原生窗口读回验证。
    /// </summary>
    internal bool TryReapplyInputBlockState()
    {
        return SetInputBlocked(_inputBlocked) == RdpInputWindowApplyResult.Applied;
    }

    internal bool InputBlocked => _inputBlocked;

    /// <summary>
    /// 清掉「正在断开」的排队状态。
    ///
    /// <see cref="Disconnect"/> 会置 <c>_disconnectRequested</c>，要等 <c>OnDisconnected</c>
    /// 回调回来才复位。回调一旦丢失，就会永远卡在「正在断开」：
    /// <see cref="ReconnectToChildSession"/> 走的是「只排队、不重连」那条分支，
    /// 表现为切回分身时画面一直黑着，用户只能自己去点「启动并连接」。
    ///
    /// 调用方必须已经确认过会话状态，否则可能打断一次真正的断开。
    /// </summary>
    internal void ResetDisconnectState()
    {
        _reconnectGeneration++;
        _disconnectRequested = false;
        _pendingReconnectDesktopSize = null;
    }

    internal void ReconnectToChildSession(DrawingSize desktopSize)
    {
        if (_disconnectRequested || _pendingReconnectDesktopSize.HasValue)
        {
            _pendingReconnectDesktopSize = desktopSize;
            return;
        }

        if (ConnectedState == 0)
        {
            // 直接连也作废在途的那次「等会话释放」重连：它拿的是旧尺寸，
            // 等这边失败（控件回 0）时它会醒过来，用旧分辨率把新的一次顶掉。
            _reconnectGeneration++;
            ConnectToChildSession(desktopSize);
            return;
        }

        _pendingReconnectDesktopSize = desktopSize;
        try
        {
            if (!DisconnectCore())
            {
                _pendingReconnectDesktopSize = null;
                ConnectToChildSession(desktopSize);
            }
        }
        catch
        {
            _pendingReconnectDesktopSize = null;
            throw;
        }
    }

    /// <summary>
    /// 作废尚未执行的重连。
    ///
    /// 切到主桌面时用：那次重连可能正停在「等会话释放」上，等完就会把分身连回来，
    /// 和「切到整台电脑」的目的正好相反。只是断开（Disconnect）不会动这个标记，
    /// 所以要显式取消。
    /// </summary>
    internal void CancelPendingReconnect()
    {
        _reconnectGeneration++;
        _pendingReconnectDesktopSize = null;
    }

    internal void DisconnectSession()
    {
        _reconnectGeneration++;
        _pendingReconnectDesktopSize = null;
        _ = DisconnectCore();
    }

    /// <summary>
    /// 主动断开 RDP 连接，但保留远端会话（桌面上的程序继续运行）。
    /// 走这条路径不会触发「连接意外断开」的失败提示。
    /// </summary>
    /// <returns>确实发起了断开返回 true。</returns>
    internal bool Disconnect() => DisconnectCore();

    private bool DisconnectCore()
    {
        if (IsHandleCreated)
        {
            ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        }

        if (ConnectedState == 0)
        {
            return false;
        }

        _disconnectRequested = true;
        try
        {
            InvokeComMethod(GetRequiredOcx(), "Disconnect");
            return true;
        }
        catch
        {
            _disconnectRequested = false;
            throw;
        }
    }

    internal bool IsInputWindowFocused()
    {
        return ConnectedState == 1
               && ChildSessionNativeMethods.IsRdpInputWindowFocused(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _smartSizing.Reset();
        if (IsHandleCreated)
        {
            ChildSessionNativeMethods.ClearRdpInputWindowCache(Handle);
        }

        base.OnHandleDestroyed(e);
    }

    protected override void CreateSink()
    {
        base.CreateSink();

        _eventSink = new RdpEventSink(this);
        var ocx = GetOcx()
                  ?? throw new InvalidOperationException(
                      Locale.Get("Rdp.Error.SinkNotReady"));
        _eventCookie = new ConnectionPointCookie(
            ocx,
            _eventSink,
            typeof(IMsTscAxEvents));
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
            _eventCookie = null;
            _eventSink = null;
        }
        finally
        {
            base.DetachSink();
        }
    }

    private void SendShortcut(KeyStroke[] strokes, string displayName)
    {
        if (ConnectedState != 1)
        {
            throw new InvalidOperationException(
                Locale.Format("Rdp.Error.CannotSend", displayName));
        }

        // 观看模式下绝不为了组合键短暂打开 RDP 输入窗口。否则本机键鼠会在
        // 发送窗口内重新挂到远程会话，MCP 并发注入时尤其无法证明输入隔离。
        if (_inputBlocked)
        {
            throw new InvalidOperationException(
                Locale.Get("Rdp.Error.ShortcutBlockedByWatchMode"));
        }

        if (!ChildSessionNativeMethods.TryFocusRdpInputWindow(Handle))
        {
            throw new InvalidOperationException(Locale.Get("Rdp.Error.CannotFocus"));
        }

        RunComStep($"向 Child Session 发送 {displayName}", () => SendKeyStrokes(strokes));
    }

    private void SendKeyStrokes(KeyStroke[] strokes)
    {
        var keyUpStates = new short[strokes.Length];
        var keyData = new int[strokes.Length];

        for (var index = 0; index < strokes.Length; index++)
        {
            var stroke = strokes[index];
            keyUpStates[index] = stroke.IsKeyUp ? VariantTrue : VariantFalse;
            keyData[index] = CreateRdpScanCode(stroke.Key);
        }

        var nonScriptableClient = (IMsRdpClientNonScriptable)GetRequiredOcx();
        nonScriptableClient.SendKeys(
            strokes.Length,
            ref keyUpStates[0],
            ref keyData[0]);
    }

    private static int CreateRdpScanCode(RemoteKey key)
    {
        const int extendedScanCodeFlag = 0x0100;
        return key.ScanCode | (key.IsExtended ? extendedScanCodeFlag : 0);
    }

    private void OnLoginComplete()
    {
        _connectionAttemptInProgress = false;
        _connectionFailureReported = false;
        _lastConnectionDiagnostic = null;
        LoginCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(int disconnectReason)
    {
        var failedWhileConnecting = _connectionAttemptInProgress;
        _connectionAttemptInProgress = false;

        if (_disconnectRequested)
        {
            _disconnectRequested = false;
            if (_pendingReconnectDesktopSize.HasValue && !IsDisposed && !Disposing)
            {
                try
                {
                    BeginInvoke(new Action(ConnectPendingReconnect));
                }
                catch (InvalidOperationException)
                {
                    _pendingReconnectDesktopSize = null;
                }
            }
            return;
        }

        var extendedDisconnectReason = TryGetExtendedDisconnectReason();
        if (_connectionFailureReported
            || (!failedWhileConnecting
                && IsNormalDisconnect(disconnectReason, extendedDisconnectReason)))
        {
            return;
        }

        var errorDescription = TryGetErrorDescription(
            disconnectReason,
            extendedDisconnectReason);
        var failureTitle = failedWhileConnecting
            ? "RDP 连接失败"
            : "RDP 连接意外断开";
        var message = string.IsNullOrWhiteSpace(errorDescription)
            ? $"{failureTitle}。\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}"
            : $"{failureTitle}：{errorDescription}\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}";

        ReportConnectionFailure(
            message,
            disconnectReason,
            extendedDisconnectReason);
    }

    private async void ConnectPendingReconnect()
    {
        var generation = _reconnectGeneration;

        await Task.Delay(ReconnectDelay);

        // 等待期间可能被取消（主动断开）或被新的一次重连取代：这时不能再连。
        if (generation != _reconnectGeneration || IsDisposed || Disposing)
        {
            return;
        }

        var desktopSize = _pendingReconnectDesktopSize;
        _pendingReconnectDesktopSize = null;
        if (!desktopSize.HasValue || IsDisposed || Disposing)
        {
            return;
        }

        // 控件回到「已断开」不代表上一轮连接在系统里已经收尾。这时立刻重连，
        // RDS 会用「访问被拒绝」把新连接顶回来——日志里每次改设置都这样，
        // 隔几秒再连就好。等会话真的不再是 Active 再连，和「切回分身」那条
        // 路径（先等会话离开 Active 再 StartAsync）保持一致。
        await WaitForChildSessionReleasedAsync(TimeSpan.FromSeconds(10));
        if (generation != _reconnectGeneration || IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            ConnectToChildSession(desktopSize.Value);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            ReportConnectionFailure(
                $"RDP 自动重新连接失败：{actualException.Message}",
                errorCode);
        }
    }

    /// <summary>
    /// 等分身会话离开 Active 状态。没有会话（已经注销）也算可以连了。
    /// </summary>
    private static async Task WaitForChildSessionReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // WTS 查询失败不等于会话已经释放。查询故障时继续等待到超时，
            // 绝不能把不确定性当成「可以立刻重连」。
            if (ChildSessionNativeMethods.TryQueryChildSessionId(out var sessionId)
                && (sessionId is null || !ChildSessionNativeMethods.IsSessionActive(sessionId.Value)))
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private void OnFatalError(int errorCode)
    {
        _connectionAttemptInProgress = false;
        if (_disconnectRequested)
        {
            return;
        }

        ReportConnectionFailure(
            $"RDP 客户端发生致命错误：{GetFatalErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}",
            errorCode);
    }

    private void OnLogonError(int errorCode)
    {
        if (_disconnectRequested)
        {
            return;
        }

        var message =
            $"RDP 登录阶段：{GetLogonErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}";
        if (IsNonTerminalLogonEvent(errorCode))
        {
            _lastConnectionDiagnostic =
                new ChildSessionConnectionFailedEventArgs(message, errorCode);
            return;
        }

        _connectionAttemptInProgress = false;
        ReportConnectionFailure(
            message.Replace("RDP 登录阶段：", "RDP 登录失败：", StringComparison.Ordinal),
            errorCode);
    }

    private int TryGetExtendedDisconnectReason()
    {
        try
        {
            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "ExtendedDisconnectReason"),
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return 0;
        }
    }

    private string? TryGetErrorDescription(
        int disconnectReason,
        int extendedDisconnectReason)
    {
        try
        {
            return Convert.ToString(
                InvokeComMethod(
                    GetRequiredOcx(),
                    "GetErrorDescription",
                    disconnectReason,
                    extendedDisconnectReason),
                CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    private void ReportConnectionFailure(
        string message,
        int errorCode,
        int? extendedErrorCode = null)
    {
        if (_connectionFailureReported)
        {
            return;
        }

        _connectionFailureReported = true;
        ConnectionFailed?.Invoke(
            this,
            new ChildSessionConnectionFailedEventArgs(
                message,
                errorCode,
                extendedErrorCode));
    }

    private static bool IsNormalDisconnect(
        int disconnectReason,
        int extendedDisconnectReason)
    {
        // 1～3 分别表示本地主动断开、远端用户断开和服务器主动断开，并非连接错误。
        // 扩展原因 1、2 分别表示 API 主动断开和 API 主动注销。
        return disconnectReason is 1 or 2 or 3
               && extendedDisconnectReason is 0 or 1 or 2;
    }

    private static string FormatErrorCode(int errorCode)
    {
        return $"{errorCode} (0x{unchecked((uint)errorCode):X8})";
    }

    private static string GetFatalErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            0 => "发生未知错误。",
            1 => "发生内部错误（1）。",
            2 => "内存不足。",
            3 => "无法创建 RDP 窗口。",
            4 => "发生内部错误（2）。",
            5 => "RDP 客户端进入了无效状态。",
            6 => "发生内部错误（4）。",
            7 => "建立客户端连接时发生不可恢复的错误。",
            100 => "Windows 套接字初始化失败。",
            _ => "发生未识别的致命错误。"
        };
    }

    private static string GetLogonErrorDescription(int errorCode)
    {
        return errorCode switch
        {
            -7 => "Winlogon 正在显示“拒绝断开现有会话”对话框。",
            -6 => "Winlogon 正在显示“无权限”对话框。",
            -5 => "Winlogon 正在显示会话争用选项。",
            -4 => "Winlogon 正在显示重新连接选项。",
            -3 => "Winlogon 已静默终止登录。",
            -1 => "访问被拒绝。",
            0 => "登录凭据无效。",
            1 => "密码已过期，必须先修改密码。",
            2 => "登录或登录后的处理发生错误。",
            3 => "RDP 客户端正在显示登录警告。",
            unchecked((int)0xC000006D) => "用户名或身份验证信息无效。",
            unchecked((int)0xC000006E) => "身份验证受到用户账户限制。",
            unchecked((int)0xC0000224) => "密码已过期，必须先修改密码。",
            _ => "登录阶段发生未识别的错误或事件。"
        };
    }

    private static bool IsNonTerminalLogonEvent(int errorCode)
    {
        // 这些代码表示登录仍在继续或 ActiveX 正在显示可交互选项，不应提前判定连接失败。
        return errorCode is -5 or -4 or -2 or 3;
    }

    private object GetRequiredOcx()
    {
        if (!IsHandleCreated)
        {
            // WindowsFormsHost 在未连接空白态会被折叠，普通 CreateControl() 会因不可见而跳过创建。
            // 读取 Handle 会忽略可见性并强制 AxHost 创建底层窗口及 ActiveX 实例。
            _ = Handle;
        }

        return GetOcx()
               ?? throw new InvalidOperationException(
                   "RDP ActiveX 控件尚未完成初始化，无法访问 COM 实例。");
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);
    }

    private static void TrySetExtendedProperty(
        IMsRdpExtendedSettings extendedSettings,
        string propertyName,
        object value)
    {
        try
        {
            extendedSettings.set_Property(propertyName, ref value);
        }
        catch (COMException)
        {
            // 旧版 MsTscAx 可能不支持该扩展属性，继续使用原有显示行为。
        }
    }

    /// <summary>
    /// 把自动登录凭据交给 RDP 客户端。
    ///
    /// 每一步都单独兜住异常：凭据设不上只是退回「让用户自己输」，
    /// 不该因为一个可选属性不支持就让整次连接失败。
    /// </summary>
    private void ApplyCredentials(object client)
    {
        var credential = _credentialProvider?.Invoke();
        if (credential is not { } value || string.IsNullOrWhiteSpace(value.UserName))
        {
            // 这里以前是静默返回的：连不上时完全看不出是「没提供凭据」还是
            // 「提供了但认证失败」，两种情况的修法完全不同。
            AppLog.Info("自动登录：本次连接没有可用凭据，将走交互式输入。");
            return;
        }

        AppLog.Info(
            $"自动登录：已提供凭据（用户 {value.UserName}，"
            + $"域 {(string.IsNullOrEmpty(value.Domain) ? "空" : value.Domain)}，"
            + $"密码长度 {value.Password?.Length ?? 0}）。");

        try
        {
            SetComProperty(client, "UserName", value.UserName);
        }
        catch (Exception exception) when (IsCredentialStepException(exception))
        {
            AppLog.Warn("设置 RDP 登录用户名失败，将退回手动输入。", exception);
        }

        if (!string.IsNullOrWhiteSpace(value.Domain))
        {
            try
            {
                SetComProperty(client, "Domain", value.Domain);
            }
            catch (Exception exception) when (IsCredentialStepException(exception))
            {
                AppLog.Warn("设置 RDP 登录域失败，将退回手动输入。", exception);
            }
        }

        if (string.IsNullOrEmpty(value.Password))
        {
            return;
        }

        try
        {
            // 密码只经过 RDP 的凭据通道，不写入任何持久化存储。
            ((IMsRdpClientNonScriptable)client).put_ClearTextPassword(value.Password);
        }
        catch (Exception exception) when (IsCredentialStepException(exception))
        {
            AppLog.Warn("设置 RDP 登录密码失败，将退回手动输入。", exception);
        }
    }

    private static bool IsCredentialStepException(Exception exception) =>
        exception.GetBaseException() is COMException
            or InvalidCastException
            or TargetInvocationException
            or MissingMemberException
            or InvalidOperationException;

    private static object? InvokeComMethod(object target, string methodName, params object[]? args)
    {
        return target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);
    }

    private static void RunComStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var actualException = exception.GetBaseException();
            if (actualException is COMException comException)
            {
                throw new COMException(
                    Locale.Format("Rdp.Step.Failed", stepName, comException.Message),
                    comException.ErrorCode);
            }

            throw;
        }
    }

    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    [TypeLibType(TypeLibTypeFlags.FDispatchable)]
    private interface IMsTscAxEvents
    {
        [DispId(1)]
        void OnConnecting();

        [DispId(2)]
        void OnConnected();

        [DispId(3)]
        void OnLoginComplete();

        [DispId(4)]
        void OnDisconnected([In] int disconnectReason);

        [DispId(5)]
        void OnEnterFullScreenMode();

        [DispId(6)]
        void OnLeaveFullScreenMode();

        [DispId(7)]
        void OnChannelReceivedData(
            [In, MarshalAs(UnmanagedType.BStr)] string channelName,
            [In, MarshalAs(UnmanagedType.BStr)] string data);

        [DispId(8)]
        void OnRequestGoFullScreen();

        [DispId(9)]
        void OnRequestLeaveFullScreen();

        [DispId(10)]
        void OnFatalError([In] int errorCode);

        [DispId(11)]
        void OnWarning([In] int warningCode);

        [DispId(12)]
        void OnRemoteDesktopSizeChange([In] int width, [In] int height);

        [DispId(13)]
        void OnIdleTimeoutNotification();

        [DispId(14)]
        void OnRequestContainerMinimize();

        [DispId(15)]
        void OnConfirmClose([Out] out bool allowClose);

        [DispId(16)]
        void OnReceivedTSPublicKey(
            [In, MarshalAs(UnmanagedType.BStr)] string publicKey,
            [Out] out bool continueLogon);

        [DispId(17)]
        void OnAutoReconnecting(
            [In] int disconnectReason,
            [In] int attemptCount,
            [Out] out AutoReconnectContinueState continueStatus);

        [DispId(18)]
        void OnAuthenticationWarningDisplayed();

        [DispId(19)]
        void OnAuthenticationWarningDismissed();

        [DispId(20)]
        void OnRemoteProgramResult(
            [In, MarshalAs(UnmanagedType.BStr)] string remoteProgram,
            [In] RemoteProgramResult error,
            [In] bool isExecutable);

        [DispId(21)]
        void OnRemoteProgramDisplayed(
            [In] bool displayed,
            [In] uint displayInformation);

        [DispId(29)]
        void OnRemoteWindowDisplayed(
            [In] bool displayed,
            [In] ref RemotableHandle windowHandle,
            [In] RemoteWindowDisplayedAttribute windowAttribute);

        [DispId(22)]
        void OnLogonError([In] int errorCode);

        [DispId(23)]
        void OnFocusReleased([In] int direction);

        [DispId(24)]
        void OnUserNameAcquired(
            [In, MarshalAs(UnmanagedType.BStr)] string userName);

        [DispId(26)]
        void OnMouseInputModeChanged([In] bool isRelativeMouseMode);

        [DispId(28)]
        void OnServiceMessageReceived(
            [In, MarshalAs(UnmanagedType.BStr)] string serviceMessage);

        [DispId(30)]
        void OnConnectionBarPullDown();

        [DispId(32)]
        void OnNetworkStatusChanged(
            [In] uint qualityLevel,
            [In] int bandwidth,
            [In] int roundTripTime);

        [DispId(35)]
        void OnDevicesButtonPressed();

        [DispId(33)]
        void OnAutoReconnected();

        [DispId(34)]
        void OnAutoReconnecting2(
            [In] int disconnectReason,
            [In] bool networkAvailable,
            [In] int attemptCount,
            [In] int maxAttemptCount);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RdpEventSink(RdpActiveXHost owner) : IMsTscAxEvents
    {
        public void OnConnecting()
        {
        }

        public void OnConnected()
        {
        }

        public void OnLoginComplete()
        {
            owner.OnLoginComplete();
        }

        public void OnDisconnected(int disconnectReason)
        {
            owner.OnDisconnected(disconnectReason);
        }

        public void OnEnterFullScreenMode()
        {
        }

        public void OnLeaveFullScreenMode()
        {
        }

        public void OnChannelReceivedData(string channelName, string data)
        {
        }

        public void OnRequestGoFullScreen()
        {
        }

        public void OnRequestLeaveFullScreen()
        {
        }

        public void OnFatalError(int errorCode)
        {
            owner.OnFatalError(errorCode);
        }

        public void OnWarning(int warningCode)
        {
        }

        public void OnRemoteDesktopSizeChange(int width, int height)
        {
        }

        public void OnIdleTimeoutNotification()
        {
        }

        public void OnRequestContainerMinimize()
        {
        }

        public void OnConfirmClose(out bool allowClose)
        {
            allowClose = true;
        }

        public void OnReceivedTSPublicKey(
            string publicKey,
            out bool continueLogon)
        {
            continueLogon = true;
        }

        public void OnAutoReconnecting(
            int disconnectReason,
            int attemptCount,
            out AutoReconnectContinueState continueStatus)
        {
            continueStatus = AutoReconnectContinueState.Automatic;
        }

        public void OnAuthenticationWarningDisplayed()
        {
        }

        public void OnAuthenticationWarningDismissed()
        {
        }

        public void OnRemoteProgramResult(
            string remoteProgram,
            RemoteProgramResult error,
            bool isExecutable)
        {
        }

        public void OnRemoteProgramDisplayed(
            bool displayed,
            uint displayInformation)
        {
        }

        public void OnRemoteWindowDisplayed(
            bool displayed,
            ref RemotableHandle windowHandle,
            RemoteWindowDisplayedAttribute windowAttribute)
        {
        }

        public void OnLogonError(int errorCode)
        {
            owner.OnLogonError(errorCode);
        }

        public void OnFocusReleased(int direction)
        {
        }

        public void OnUserNameAcquired(string userName)
        {
        }

        public void OnMouseInputModeChanged(bool isRelativeMouseMode)
        {
        }

        public void OnServiceMessageReceived(string serviceMessage)
        {
        }

        public void OnConnectionBarPullDown()
        {
        }

        public void OnNetworkStatusChanged(
            uint qualityLevel,
            int bandwidth,
            int roundTripTime)
        {
        }

        public void OnDevicesButtonPressed()
        {
        }

        public void OnAutoReconnected()
        {
            owner.OnLoginComplete();
        }

        public void OnAutoReconnecting2(
            int disconnectReason,
            bool networkAvailable,
            int attemptCount,
            int maxAttemptCount)
        {
        }
    }

    private enum AutoReconnectContinueState
    {
        Automatic,
        Stop,
        Manual
    }

    private enum RemoteProgramResult
    {
        Ok,
        Locked,
        ProtocolError,
        NotInWhitelist,
        NetworkPathDenied,
        FileNotFound,
        Failure,
        HookNotLoaded
    }

    private enum RemoteWindowDisplayedAttribute
    {
        None,
        WindowDisplayed,
        ShellIconDisplayed
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RemotableHandle
    {
        internal int Context;
        internal RemotableHandleUnion Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RemotableHandleUnion
    {
        [FieldOffset(0)]
        internal int InProcessHandle;

        [FieldOffset(0)]
        internal int RemoteHandle;
    }

    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    [ComImport]
    [Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpClientNonScriptable
    {
        void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortablePassword();

        void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortableSalt();

        void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinaryPassword();

        void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinarySalt();

        void ResetPassword();

        void NotifyRedirectDeviceChange(nuint wParam, nint lParam);

        void SendKeys(
            int numKeys,
            [In] ref short keyUpStates,
            [In] ref int keyData);
    }

    private readonly record struct RemoteKey(int ScanCode, bool IsExtended);

    private readonly record struct KeyStroke(RemoteKey Key, bool IsKeyUp);
}
