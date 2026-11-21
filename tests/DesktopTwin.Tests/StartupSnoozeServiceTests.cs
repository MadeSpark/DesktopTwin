using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using DesktopTwin.Interop;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 「开机自启动项」关停/恢复里那些不需要触碰真机就能验证的判定逻辑。
///
/// 这些判定出错的方式都很隐蔽：字节位判断反了会把用户本来就关着的启动项重新打开；
/// 计划任务按 State 判断会把运行中的任务当成没关掉；WOW64 视图搞错会把值写进影子键。
/// 所以每一条都要有对应的测试。
/// </summary>
public sealed class StartupSnoozeServiceTests
{
    private const string TaskNamespace = ScheduledTaskXml.TaskNamespaceUri;

    private static string Task(string body) => $"<Task xmlns=\"{TaskNamespace}\">{body}</Task>";

    [Fact]
    public void 启动项状态按明确的已知值分类_未知值不猜测()
    {
        var original = new byte[12];
        original[0] = StartupApproval.DisabledFlag;
        BitConverter.GetBytes(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).ToFileTimeUtc())
            .CopyTo(original, 4);

        Assert.True(StartupApproval.IsDisabled(original));

        // 0x01/0x03 是关闭；0x02/0x04/0x06 是启用；
        // 0x08 是策略锁定的启用，0x09 是策略锁定的关闭。
        foreach (var flag in new byte[] { 0x01 })
        {
            var disabled = (byte[])original.Clone();
            disabled[0] = flag;
            Assert.True(StartupApproval.IsDisabled(disabled));
        }

        foreach (var flag in new byte[] { 0x02, 0x04, 0x06, 0x08 })
        {
            var enabled = (byte[])original.Clone();
            enabled[0] = flag;
            Assert.False(StartupApproval.IsDisabled(enabled));
        }

        var lockedDisabled = (byte[])original.Clone();
        lockedDisabled[0] = 0x09;
        Assert.True(StartupApproval.IsDisabled(lockedDisabled));

        foreach (var flag in new byte[] { 0x00, 0x05, 0x07, 0x0A })
        {
            var unknown = (byte[])original.Clone();
            unknown[0] = flag;
            Assert.False(StartupApproval.IsDisabled(unknown));
            Assert.Equal(StartupApprovalState.Unknown, StartupApproval.Classify(unknown));
            Assert.False(StartupApproval.CanDisable(unknown));
        }
    }

    [Fact]
    public void 空值不会被判定为已关闭()
    {
        Assert.False(StartupApproval.IsDisabled(null));
        Assert.False(StartupApproval.IsDisabled(Array.Empty<byte>()));
    }

    [Fact]
    public void 生成的关闭字节是十二字节且带关闭时间()
    {
        var at = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var bytes = StartupApproval.CreateDisabled(at);

        Assert.Equal(12, bytes.Length);
        Assert.Equal(StartupApproval.DisabledFlag, bytes[0]);
        Assert.Equal(at.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
    }

    [Fact]
    public void 计划任务按_Settings_里的_Enabled_判断而不是_State()
    {
        // 真实的 schtasks /Query /XML 输出：声明是 UTF-16，但重定向后是 OEM 文本。
        // 解析前移除声明，避免编码不符；正文仍按 XML 严格解析。
        const string runningButDisabled = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>test</Author></RegistrationInfo>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <Enabled>false</Enabled>
              </Settings>
              <State>Running</State>
            </Task>
            """;

        Assert.False(ScheduledTaskXml.IsEnabled(runningButDisabled));
    }

    [Fact]
    public void 计划任务空值或损坏时失败关闭_合法任务缺少_Enabled_才按启用处理()
    {
        foreach (var unknown in new[]
                 {
                     null,
                     string.Empty,
                     "<Task><Settings>",
                     "<Task><Settings></Settings></Task>",
                     "<Task xmlns=\"urn:not-the-task-namespace\" />",
                     Task("<Settings><Enabled>not-a-bool</Enabled></Settings>"),
                     Task("<Settings><Enabled></Enabled></Settings>"),
                     Task("<Settings><Enabled> </Enabled></Settings>"),
                     Task("<Settings><Enabled>2</Enabled></Settings>"),
                     Task("<Settings><Enabled>true</Enabled><Enabled>false</Enabled></Settings>"),
                     Task("<Settings /><Settings />")
                 })
        {
            Assert.Equal(TaskEnabledState.Unknown, ScheduledTaskXml.GetEnabledState(unknown));
            Assert.False(ScheduledTaskXml.IsEnabled(unknown));
            Assert.False(ScheduledTaskXml.IsDisabled(unknown));
        }

        // XSD boolean 允许 1/0 和 true/false；缺少 Settings 或 Enabled 时任务默认启用。
        foreach (var enabled in new[]
                 {
                     Task(string.Empty),
                     Task("<Settings></Settings>"),
                     Task("<Settings><Enabled>1</Enabled></Settings>"),
                     Task("<Settings><Enabled>true</Enabled></Settings>"),
                     Task("<Settings><Enabled>TRUE</Enabled></Settings>")
                 })
        {
            Assert.True(ScheduledTaskXml.IsEnabled(enabled));
            Assert.False(ScheduledTaskXml.IsDisabled(enabled));
        }

        foreach (var disabled in new[]
                 {
                     Task("<Settings><Enabled>0</Enabled></Settings>"),
                     Task("<Settings><Enabled>false</Enabled></Settings>"),
                     Task("<Settings><Enabled>FALSE</Enabled></Settings>")
                 })
        {
            Assert.True(ScheduledTaskXml.IsDisabled(disabled));
            Assert.False(ScheduledTaskXml.IsEnabled(disabled));
        }
    }

    [Fact]
    public void 计划任务收集对无法确定的_Enabled_失败关闭而不是静默跳过()
    {
        const string currentUser = @"CONTOSO\alice";

        // 损坏、空值、未知 token、重复设置都必须阻断本次关停，不能当成已禁用跳过。
        foreach (var unknown in new[]
                 {
                     string.Empty,
                     Task("<Settings><Enabled>not-a-bool</Enabled></Settings>"),
                     Task("<Settings><Enabled /></Settings>"),
                     Task("<Settings /><Settings />"),
                     Task("<Settings><Enabled>0</Enabled><Enabled>0</Enabled></Settings>")
                 })
        {
            Assert.Equal(
                ScheduledTaskIsolationDecision.Block,
                ScheduledTaskIsolation.Classify(unknown, currentUser, null));
        }

        // 明确禁用才是 Skip；缺少 Enabled 的合法任务默认启用，并在登录触发器下进入隔离。
        Assert.Equal(
            ScheduledTaskIsolationDecision.Skip,
            ScheduledTaskIsolation.Classify(
                Task("<Settings><Enabled>false</Enabled></Settings>"),
                currentUser,
                null));
        Assert.Equal(
            ScheduledTaskIsolationDecision.Isolate,
            ScheduledTaskIsolation.Classify(
                Task("<Settings /><Triggers><LogonTrigger><UserId>CONTOSO\\alice</UserId></LogonTrigger></Triggers>"),
                currentUser,
                null));
        Assert.Equal(
            ScheduledTaskIsolationDecision.Isolate,
            ScheduledTaskIsolation.Classify(
                Task("<Triggers><LogonTrigger><UserId>CONTOSO\\alice</UserId></LogonTrigger></Triggers>"),
                currentUser,
                null));
    }

    [Fact]
    public void 计划任务查询结果明确区分存在_确认不存在_和无法确认()
    {
        var valid = Task("<Settings><Enabled>false</Enabled></Settings>");
        var output = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + valid;
        var present = StartupSnoozeService.ParseTaskXmlQueryOutput("\uFEFF" + output);
        Assert.True(present.IsPresent);
        Assert.Equal(output, present.Xml);

        var absent = StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Absent);
        Assert.True(absent.IsAbsent);
        Assert.Null(absent.Xml);

        foreach (var presence in new[]
                 {
                     StartupSnoozeService.ScheduledTaskPresence.Present,
                     StartupSnoozeService.ScheduledTaskPresence.Unknown
                 })
        {
            var unknown = StartupSnoozeService.InterpretFailedTaskXmlQuery(presence);
            Assert.True(unknown.IsUnknown);
            Assert.Null(unknown.Xml);
        }

        foreach (var invalid in new[]
                 {
                     null,
                     string.Empty,
                     "<Task><Settings></Settings></Task>",
                     "<Task xmlns=\"urn:not-the-task-namespace\"><Settings /></Task>",
                     "<Task xmlns=\"" + TaskNamespace + "\"><Settings>",
                     "<!DOCTYPE Task [<!ENTITY x SYSTEM 'file:///c:/windows/win.ini'>]>"
                     + Task("<Settings><Enabled>&x;</Enabled></Settings>")
                 })
        {
            Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput(invalid).IsUnknown);
        }
    }

    [Fact]
    public void 计划任务只认_Settings_这个元素而不是同前缀的别的元素()
    {
        // 元素名前缀相同不能算命中，否则会把别的块里的 Enabled 当成本任务的开关。
        var xml = Task("""
            <SettingsData><Enabled>false</Enabled></SettingsData>
            """);

        Assert.True(ScheduledTaskXml.IsEnabled(xml));
    }

    [Fact]
    public void 登录触发器只选择当前用户或无法排除当前用户的任务()
    {
        const string currentUser = @"CONTOSO\alice";
        const string currentUserSid = "S-1-5-21-1000";
        const string otherPrincipal = "<Principals><Principal id=\"Author\"><UserId>CONTOSO\\bob</UserId></Principal></Principals>";
        const string currentPrincipal = "<Principals><Principal id=\"Author\"><UserId>CONTOSO\\alice</UserId></Principal></Principals>";
        const string currentPrincipalSid = "<Principals><Principal id=\"Author\"><UserId>S-1-5-21-1000</UserId></Principal></Principals>";
        const string groupPrincipal = "<Principals><Principal id=\"Author\"><GroupId>S-1-5-32-545</GroupId></Principal></Principals>";

        // 触发器显式绑定当前用户（含短名和 SID）时命中。
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><UserId>CONTOSO\\alice</UserId></LogonTrigger></Triggers>"),
            currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><UserId>alice</UserId></LogonTrigger></Triggers>"),
            currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><UserId>S-1-5-21-1000</UserId></LogonTrigger></Triggers>"),
            currentUser,
            currentUserSid));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><SessionStateChangeTrigger><StateChange>ConsoleConnect</StateChange><UserId>CONTOSO\\alice</UserId></SessionStateChangeTrigger></Triggers>"),
            currentUser));

        // UserId 缺失或为空时必须结合 Principal：只有唯一、静态地指向当前用户才可排除。
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task(currentPrincipal + "<Triggers><LogonTrigger /></Triggers>"),
            currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task(currentPrincipal + "<Triggers><LogonTrigger><UserId /></LogonTrigger></Triggers>"),
            currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task(currentPrincipalSid + "<Triggers><LogonTrigger /></Triggers>"),
            currentUser,
            currentUserSid));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task(otherPrincipal + "<Triggers><LogonTrigger /></Triggers>"),
            currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task(otherPrincipal + "<Triggers><LogonTrigger><UserId /></LogonTrigger></Triggers>"),
            currentUser));

        // 没有 Principal、Principal 是组 / Everyone / Users / 多个主体：无法证明，必须失败关闭。
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger /></Triggers>"), currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><UserId /></LogonTrigger></Triggers>"), currentUser));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger(
            Task(groupPrincipal + "<Triggers><LogonTrigger /></Triggers>"), currentUser));
        foreach (var broadUser in new[] { "Users", "Everyone", "BUILTIN\\Users", "S-1-1-0", "Authenticated Users" })
        {
            Assert.True(
                ScheduledTaskXml.HasSessionStartTrigger(
                    Task("<Triggers><LogonTrigger><UserId>" + broadUser + "</UserId></LogonTrigger></Triggers>"),
                    currentUser),
                "broadUser=" + broadUser);
        }

        // 明确绑定其他用户、触发器被禁用、或其他触发器类型都不能选中。
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><UserId>CONTOSO\\bob</UserId></LogonTrigger></Triggers>"),
            currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>"),
            currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><RegistrationTrigger /></Triggers>"), currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><TimeTrigger /></Triggers>"), currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            "<Task xmlns=\"urn:not-the-task-namespace\"><Triggers><LogonTrigger /></Triggers></Task>",
            currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(
            Task("<Triggers><x:LogonTrigger xmlns:x=\"urn:not-the-task-namespace\" /></Triggers>"),
            currentUser));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(null, currentUser));
    }

    [Fact]
    public void 会话状态触发器只认登录相关的官方状态值()
    {
        const string currentUser = @"CONTOSO\alice";
        foreach (var stateChange in new[] { "ConsoleConnect", "RemoteConnect", "SessionUnlock" })
        {
            var trigger = "<SessionStateChangeTrigger><StateChange>" + stateChange + "</StateChange><UserId>CONTOSO\\alice</UserId></SessionStateChangeTrigger>";
            Assert.True(
                ScheduledTaskXml.HasSessionStartTrigger(Task("<Triggers>" + trigger + "</Triggers>"), currentUser),
                "StateChange=" + stateChange);
        }

        foreach (var stateChange in new[]
                 {
                     "SessionLock",
                     "ConsoleDisconnect",
                     "RemoteDisconnect",
                     "SessionLogoff",
                     "SessionLogon",
                     "SessionConnect",
                     "SessionRemoteConnect",
                     "SessionRemoteReconnect",
                     "UnknownState",
                     "consoleconnect",
                     string.Empty
                 })
        {
            var trigger = "<SessionStateChangeTrigger><StateChange>" + stateChange + "</StateChange><UserId>CONTOSO\\alice</UserId></SessionStateChangeTrigger>";
            Assert.False(
                ScheduledTaskXml.HasSessionStartTrigger(Task("<Triggers>" + trigger + "</Triggers>"), currentUser),
                "StateChange=" + stateChange);
        }
    }

    [Fact]
    public void 计划任务身份只随定义变化_不随_Enabled_或无关键变化()
    {
        const string baseline = """
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Vendor</Author></RegistrationInfo>
              <Principals><Principal id="Author"><UserId>alice</UserId></Principal></Principals>
              <Triggers><LogonTrigger><UserId>alice</UserId></LogonTrigger></Triggers>
              <Settings><Enabled>true</Enabled><IdleSettings><Enabled>false</Enabled></IdleSettings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
              <Actions><Exec><Command>C:\App\app.exe</Command></Exec></Actions>
              <State>Ready</State>
            </Task>
            """;
        var toggled = baseline
            .Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>", StringComparison.Ordinal)
            .Replace("<State>Ready</State>", "<State>Running</State>", StringComparison.Ordinal);
        var actionChanged = baseline.Replace("app.exe", "other.exe", StringComparison.Ordinal);
        var principalChanged = baseline.Replace("<UserId>alice</UserId></Principal>", "<UserId>bob</UserId></Principal>", StringComparison.Ordinal);
        var triggerChanged = baseline.Replace("</Triggers>", "<TimeTrigger /></Triggers>", StringComparison.Ordinal);
        var registrationChanged = baseline.Replace("<Author>Vendor</Author>", "<Author>Other</Author>", StringComparison.Ordinal);
        var nestedSettingsChanged = baseline.Replace(
            "<IdleSettings><Enabled>false</Enabled></IdleSettings>",
            "<IdleSettings><Enabled>true</Enabled></IdleSettings>",
            StringComparison.Ordinal);

        var identity = ScheduledTaskXml.GetIdentity(baseline);
        Assert.NotNull(identity);
        Assert.True(ScheduledTaskXml.IsValidIdentity(identity));
        Assert.Equal(identity, ScheduledTaskXml.GetIdentity(toggled));
        Assert.NotEqual(identity, ScheduledTaskXml.GetIdentity(actionChanged));
        Assert.NotEqual(identity, ScheduledTaskXml.GetIdentity(principalChanged));
        Assert.NotEqual(identity, ScheduledTaskXml.GetIdentity(triggerChanged));
        Assert.NotEqual(identity, ScheduledTaskXml.GetIdentity(registrationChanged));
        Assert.NotEqual(identity, ScheduledTaskXml.GetIdentity(nestedSettingsChanged));
        Assert.Null(ScheduledTaskXml.GetIdentity("<Task><Actions></Task>"));
        Assert.Null(ScheduledTaskXml.GetIdentity(
            "<Task xmlns=\"urn:not-the-task-namespace\"><Actions /></Task>"));
        Assert.False(ScheduledTaskXml.IsValidIdentity("sha256:not-a-hash"));
    }

    [Fact]
    public void 计划任务恢复只接受当前版日志的完全相同身份()
    {
        const string baseline = """
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Vendor</Author></RegistrationInfo>
              <Triggers><LogonTrigger><UserId>alice</UserId></LogonTrigger></Triggers>
              <Settings><Enabled>true</Enabled></Settings>
              <Actions><Exec><Command>C:/App/app.exe</Command></Exec></Actions>
              <State>Ready</State>
            </Task>
            """;
        var identity = ScheduledTaskXml.GetIdentity(baseline);
        Assert.NotNull(identity);

        var method = typeof(StartupSnoozeService).GetMethod(
            "ScheduledTaskIdentityMatches",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var journal = new StartupSnoozeJournal { Version = 3 };
        var task = new StartupSnoozeTaskEntry { Identity = identity };

        Assert.True((bool)method!.Invoke(null, [journal, task, baseline])!);
        Assert.False((bool)method.Invoke(
            null,
            [new StartupSnoozeJournal { Version = 2 }, task, baseline])!);
        Assert.False((bool)method.Invoke(
            null,
            [journal, task, baseline.Replace("app.exe", "other.exe", StringComparison.Ordinal)])!);
        Assert.False((bool)method.Invoke(
            null,
            [journal, new StartupSnoozeTaskEntry { Identity = null }, baseline])!);
        Assert.False((bool)method.Invoke(null, [journal, task, "<Task><Actions>"])!);
        Assert.False((bool)method.Invoke(
            null,
            [journal, task, "<Task xmlns=\"urn:not-the-task-namespace\"><Actions /></Task>"])!);
    }

    [Fact]
    public void 只跳过明确的微软系统任务而不是整个_Microsoft_前缀()
    {
        // 只有白名单里的 Windows 系统任务目录才算系统任务。
        Assert.True(StartupSnoozeService.IsMicrosoftSystemTaskPath(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.True(StartupSnoozeService.IsMicrosoftSystemTaskPath(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"));
        Assert.True(StartupSnoozeService.IsMicrosoftSystemTaskPath(@"\Microsoft\Windows\Application Experience\StartupAppTask"));
        Assert.True(StartupSnoozeService.IsMicrosoftSystemTaskPath(@"\Microsoft\Windows\Task Manager\StartupAppTask"));
        Assert.True(StartupSnoozeService.IsMicrosoftSystemTaskPath(@"\CreateExplorerShellUnelevatedTask"));

        // \Microsoft 本身、未列出的子目录和第三方/商店应用任务都必须照常隔离。
        foreach (var userTask in new[]
                 {
                     @"\Microsoft",
                     @"\Microsoft\Windows",
                     @"\Microsoft\Windows\Defrag",
                     @"\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
                     @"\Microsoft\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
                     @"\Microsoft\Microsoft\Office\OfficeBackgroundTaskHandlerLogon",
                     @"\Microsoft\MicrosoftEdge\Update\MicrosoftEdgeUpdateTaskMachineUA",
                     @"\Microsoft\Windows Defender",
                     @"\Microsoft\Windows Defender Advanced Threat Protection\Windows Defender Scheduled Scan",
                     @"\Microsoft\Windows\YourPhone\YourPhone",
                     @"\Microsoft\Windows\CloudExperienceHost",
                     @"\Microsoft\Windows\WindowsUpdate",
                     @"\MicrosoftUpdate",
                     @"\MicrosoftUpdate\Daily",
                     @"\Microsoft\WindowsUpdate\Scheduled Start",
                     @"\Vendor\Microsoft\Daily",
                     @"\Microsoft.Office.Desktop\Office Startup",
                     @"\Microsoft.YourPhone\Startup"
                 })
        {
            Assert.False(
                StartupSnoozeService.IsMicrosoftSystemTaskPath(userTask),
                "用户或第三方任务不应被当成系统任务：" + userTask);
        }
    }

    [Fact]
    public void 计划任务能取出直接启动的程序路径()
    {
        var xml = Task("""
            <Actions><Exec>
              <Command>"C:\Program Files\Vendor &amp; Co\app.exe"</Command>
              <Arguments>--startup</Arguments>
            </Exec></Actions>
            """);

        Assert.Equal(
            "\"C:\\Program Files\\Vendor & Co\\app.exe\"",
            ScheduledTaskXml.GetExecutable(xml));
    }

    [Fact]
    public void 商店应用只有状态二才算确认启用()
    {
        Assert.True(UwpStartupState.IsEnabled(2));
        Assert.False(UwpStartupState.IsEnabled(0));
        Assert.False(UwpStartupState.IsEnabled(1));
    }

    [Fact]
    public void RunOnce_只放行微软_Edge_的清理项()
    {
        var roots = new[] { @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application" };
        var exe = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\120.0\installer\setup.exe";

        Assert.True(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            "\"" + exe + "\" --uninstall",
            true,
            exe,
            roots));

        // 名字对但路径不在允许目录里 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_evil",
            "\"C:\\Temp\\evil.exe\"",
            true,
            @"C:\Temp\evil.exe",
            roots));

        // 路径对但名字不是这个前缀 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "SomeOtherUpdater",
            "\"" + exe + "\"",
            true,
            exe,
            roots));

        // 文件不存在 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_x",
            "\"" + exe + "\"",
            false,
            exe,
            roots));
    }

    [Fact]
    public void 兜底可执行文件_完整路径和_UNC_直接接受_其余名字不凭空推断()
    {
        Assert.Equal(
            @"C:\Apps\Contoso\contoso.exe",
            ShortcutTargetResolver.ResolveExecutable(@"C:\Apps\Contoso\contoso.exe"));
        Assert.Equal(
            @"\\server\share\contoso.exe",
            ShortcutTargetResolver.ResolveExecutable(@"\\server\share\contoso.exe"));

        // PATH/App Paths 解析依赖真实机器状态，这里不能把裸名字原样当成路径。
        // 若当前环境能唯一定位则返回完整路径，否则必须拒绝。
        var resolved = ShortcutTargetResolver.ResolveExecutable("contoso.exe");
        if (resolved is not null)
        {
            Assert.True(Path.IsPathRooted(resolved));
        }
    }

    [Fact]
    public void RunOnce_裸命令名即使能在系统里解析也不能放行()
    {
        var roots = new[] { @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application" };

        // PATH 上的可执行程序（例如 msedge.exe）不是明确的文件位置：按当前
        // 目录或 PATH 解析出的目标无法用于精确兜底，必须整项拒绝。
        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_bare",
            "setup.exe --uninstall",
            true,
            "setup.exe",
            roots));

        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_bare",
            "\\\\Program Files\\Vendor\\setup.exe",
            true,
            "\\\\Program Files\\Vendor\\setup.exe",
            roots));
    }

    [Fact]
    public void 从命令行里取出可执行文件路径()
    {
        Assert.Equal(@"C:\a b\app.exe", RunOnceClassifier.ParseExecutable("\"C:\\a b\\app.exe\" --flag"));
        Assert.Equal(@"C:\app.exe", RunOnceClassifier.ParseExecutable("C:\\app.exe --flag"));
        Assert.Equal(@"C:\app.exe", RunOnceClassifier.ParseExecutable("  C:\\app.exe  "));
        Assert.Equal(
            @"C:\Program Files\Vendor\app.exe",
            RunOnceClassifier.ParseExecutable(@"C:\Program Files\Vendor\app.exe --startup"));
        Assert.Null(RunOnceClassifier.ParseExecutable("   "));
        Assert.Null(RunOnceClassifier.ParseExecutable(null));
    }

    [Fact]
    public void 启动文件夹快捷方式能解析到快捷方式指向的exe()
    {
        var target = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(target));

        var shortcutPath = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            Assert.NotNull(shellType);
            shell = Activator.CreateInstance(shellType!);
            Assert.NotNull(shell);

            dynamic shellApi = shell!;
            shortcut = shellApi.CreateShortcut(shortcutPath);
            Assert.NotNull(shortcut);

            dynamic shortcutApi = shortcut!;
            shortcutApi.TargetPath = target;
            shortcutApi.Save();

            Assert.Equal(
                Path.GetFullPath(target!),
                Path.GetFullPath(ShortcutTargetResolver.ResolveExecutable(shortcutPath)!),
                ignoreCase: true);
        }
        finally
        {
            File.Delete(shortcutPath);
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    [Fact]
    public void 关停日志能原样往返序列化()
    {
        var journal = new StartupSnoozeJournal
        {
            CreatedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Machine = "TEST-PC",
            User = "tester",
            Completed = true
        };
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            Kind = "RunValue",
            Hive = "LocalMachine",
            RunView = "Registry32",
            ApprovedView = "Registry64",
            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            ValueName = "delta_force_launcher",
            CommandLine = "\"C:\\Program Files\\delta\\launcher.exe\"",
            OriginalExists = true,
            OriginalBase64 = Convert.ToBase64String(new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
            WrittenBase64 = Convert.ToBase64String(new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 }),
            Applied = true
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\Clash Verge",
            TaskName = "Clash Verge",
            Identity = "sha256:" + new string('a', 64),
            Executable = @"C:\Program Files\Clash Verge\clash-verge.exe",
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = true
        });
        journal.Uwp.Add(new StartupSnoozeUwpEntry
        {
            KeyPath = @"Software\Classes\Local Settings\x",
            PackageFamilyName = "Test.App_abc",
            Task = "Startup",
            OriginalState = 2,
            WrittenState = 0
        });
        journal.Warnings.Add("无法关闭：sysdiag");
        journal.DeferredExecutables.Add(@"C:\Program Files\Huorong\Sysdiag\bin\hipstray.exe");

        var json = JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<StartupSnoozeJournal>(json);

        Assert.NotNull(back);
        Assert.Equal(journal.Version, back!.Version);
        Assert.Equal(journal.CreatedAt, back.CreatedAt);
        Assert.True(back.Completed);
        Assert.Single(back.Registry);
        Assert.Equal("Registry32", back.Registry[0].RunView);
        Assert.Equal("Registry64", back.Registry[0].ApprovedView);
        Assert.Equal(journal.Registry[0].OriginalBase64, back.Registry[0].OriginalBase64);
        Assert.True(back.Registry[0].Applied);
        Assert.Equal(3, back.Version);
        Assert.Single(back.Tasks);
        Assert.Equal(journal.Tasks[0].Identity, back.Tasks[0].Identity);
        Assert.True(back.Tasks[0].WriteAttempted);
        Assert.True(back.Tasks[0].ConfirmedDisabled);
        Assert.Equal(journal.Tasks[0].Executable, back.Tasks[0].Executable);
        Assert.Equal(
            journal.DeferredExecutables[0],
            Assert.Single(back.DeferredExecutables));
        Assert.Single(back.Uwp);
        Assert.Equal(2, back.Uwp[0].OriginalState);
        Assert.Equal("无法关闭：sysdiag", Assert.Single(back.Warnings));
    }

    [Fact]
    public void 启动项守卫接受延迟出现的用户程序并跳过系统外壳()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-guard-targets-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var app = Path.Combine(directory, "app.exe");
            var duplicate = Path.Combine(directory, "APP.EXE");
            var text = Path.Combine(directory, "script.cmd");
            File.WriteAllBytes(app, [0]);
            File.WriteAllText(text, "echo test");

            var targets = StartupGuardService.NormalizeTargets(
            [
                app,
                duplicate,
                text,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Path.Combine(directory, "missing.exe")
            ]);

            Assert.Contains(app, targets);
            Assert.Contains(duplicate, targets);
            Assert.Contains(Path.Combine(directory, "missing.exe"), targets);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 启动项守卫只终止目标会话且路径必须精确匹配()
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Twin\app.exe"
        };

        Assert.True(StartupGuardService.ShouldTerminate(
            7, 7, 1, @"c:\apps\twin\APP.EXE", exact));
        Assert.False(StartupGuardService.ShouldTerminate(
            1, 7, 1, @"C:\Apps\Twin\app.exe", exact));
        Assert.False(StartupGuardService.ShouldTerminate(
            8, 7, 1, @"C:\Apps\Twin\app.exe", exact));
        Assert.False(StartupGuardService.ShouldTerminate(
            7, 7, 1, @"C:\Apps\Other\app.exe", exact));

        // 读不到模块路径时无法证明完整路径，必须拒绝按名称终止。
        Assert.False(StartupGuardService.ShouldTerminate(
            7, 7, 1, null, exact));
        Assert.False(StartupGuardService.ShouldTerminate(
            1, 7, 1, null, exact));
    }

    [Fact]
    public void 关停日志安全校验接受正常条目()
    {
        StartupSnoozeService.ValidateJournal(CreateValidJournal());
    }

    [Fact]
    public void 恢复日志拒绝不在_StartupApproved_下的注册表路径()
    {
        var error = AssertInvalidJournal(journal =>
            journal.Registry[0].KeyPath =
                @"Software\Microsoft\Windows\CurrentVersion\Run");

        Assert.Contains("Run", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝未知注册表配置单元()
    {
        var error = AssertInvalidJournal(journal => journal.Registry[0].Hive = "HKLM");

        Assert.Contains("配置单元", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝非_64_位的_StartupApproved_视图()
    {
        var error = AssertInvalidJournal(journal =>
            journal.Registry[0].ApprovedView = "Registry32");

        Assert.Contains("视图", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝损坏的注册表原始字节()
    {
        var error = AssertInvalidJournal(journal =>
        {
            journal.Registry[0].OriginalExists = true;
            journal.Registry[0].OriginalBase64 = "not-base64";
        });

        Assert.Contains("Base64", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝_Microsoft_系统计划任务()
    {
        var error = AssertInvalidJournal(journal =>
            journal.Tasks[0].Relative = @"\Microsoft\Windows\Defrag\ScheduledDefrag");

        Assert.Contains("Microsoft", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝计划任务路径穿越()
    {
        var error = AssertInvalidJournal(journal =>
            journal.Tasks[0].Relative = @"\Folder\..\Task");

        Assert.Contains("任务", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝越界的_UWP_键路径()
    {
        var error = AssertInvalidJournal(journal =>
            journal.Uwp[0].KeyPath =
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppDataEvil\Contoso");

        Assert.Contains("UWP", error.Message);
    }

    [Fact]
    public void 恢复日志拒绝空_UWP_任务名()
    {
        var error = AssertInvalidJournal(journal => journal.Uwp[0].Task = string.Empty);

        Assert.Contains("UWP", error.Message);
    }

    [Fact]
    public void 启动项守卫绝不终止主会话或非目标会话()
    {
        const uint mainSession = 1;
        const uint childSession = 2;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Contoso\contoso.exe"
        };

        // 即使主会话里运行着同一个程序，也绝不能因为路径命中就去终止它。
        Assert.False(StartupGuardService.ShouldTerminate(
            mainSession, childSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", paths));
        Assert.False(StartupGuardService.ShouldTerminate(
            3, childSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", paths));

        // 目标会话号本身等于主会话号时，整条守卫都必须拒绝工作。
        Assert.False(StartupGuardService.ShouldTerminate(
            mainSession, mainSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", paths));
    }

    [Fact]
    public void 启动项守卫只按精确路径匹配_读不到路径时拒绝终止()
    {
        const uint mainSession = 1;
        const uint childSession = 2;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Contoso\contoso.exe"
        };

        Assert.True(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            @"c:\apps\contoso\CONTOSO.EXE", paths));

        // 同名但不在启动项路径里的程序不能被杀。
        Assert.False(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            @"C:\Other\contoso.exe", paths));

        // 主模块路径不可读时不能按进程名猜测。
        Assert.False(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            null, paths));
    }

    [Fact]
    public void 启动项守卫常驻阶段只终止服务拉起的受管路径()
    {
        const uint mainSession = 1;
        const uint childSession = 2;
        var expectedPath = Path.GetFullPath("C:/Apps/Contoso/contoso.exe");
        var otherPath = Path.GetFullPath("C:/Apps/Other/contoso.exe");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            expectedPath
        };
        var childStartedAt = DateTime.UtcNow;
        var serviceStartedAt = childStartedAt.AddMinutes(-1);

        // 会话 0 的服务把已登记启动程序创建到子会话，是常驻阶段唯一允许的命中。
        Assert.True(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            "c:/apps/contoso/CONTOSO.EXE", 0, serviceStartedAt, childStartedAt, paths));

        // 用户在子会话里自己启动同一程序，或父进程在其它会话时，不能误杀。
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            expectedPath, childSession, serviceStartedAt, childStartedAt, paths));
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            expectedPath, 4, serviceStartedAt, childStartedAt, paths));
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            expectedPath, null, null, childStartedAt, paths));

        // 父 PID 已被回收并指向后创建的服务时，不能作为服务拉起证据。
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            expectedPath, 0, childStartedAt.AddSeconds(1), childStartedAt, paths));

        // 路径不可读、路径不相同、会话不是目标会话或目标就是主会话时都拒绝。
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession, null,
            0, serviceStartedAt, childStartedAt, paths));
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            childSession, childSession, mainSession,
            otherPath, 0, serviceStartedAt, childStartedAt, paths));
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            mainSession, childSession, mainSession,
            expectedPath, 0, serviceStartedAt, childStartedAt, paths));
        Assert.False(StartupGuardService.ShouldTerminateServiceSpawn(
            mainSession, mainSession, mainSession,
            expectedPath, 0, serviceStartedAt, childStartedAt, paths));
    }

    [Fact]
    public void 启动文件夹快捷方式会解析成启动项守卫目标()
    {
        var target = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(target));
        var targetPath = Path.GetFullPath(target!);
        var shortcutPath = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-guard-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            Assert.NotNull(shellType);
            shell = Activator.CreateInstance(shellType!);
            Assert.NotNull(shell);

            dynamic shellApi = shell!;
            shortcut = shellApi.CreateShortcut(shortcutPath);
            Assert.NotNull(shortcut);
            dynamic shortcutApi = shortcut!;
            shortcutApi.TargetPath = targetPath;
            shortcutApi.Save();

            var journal = new StartupSnoozeJournal();
            var entry = new StartupSnoozeRegistryEntry
            {
                Kind = "StartupFolderItem",
                ValueName = "guard-shortcut",
                CommandLine = shortcutPath,
                Applied = false
            };
            journal.Registry.Add(entry);

            var unconfirmedMethod = typeof(StartupSnoozeService).GetMethod(
                "NormalizeStartupExecutables",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var managedMethod = typeof(StartupSnoozeService).GetMethod(
                "NormalizeManagedStartupExecutables",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(unconfirmedMethod);
            Assert.NotNull(managedMethod);

            var unconfirmed = (IReadOnlyList<string>)unconfirmedMethod!.Invoke(null, [journal])!;
            Assert.Contains(targetPath, unconfirmed);
            var managed = (IReadOnlyList<string>)managedMethod!.Invoke(null, [journal])!;
            Assert.Contains(targetPath, managed);

            // 已确认关闭的项不再参与登录后的无条件兜底，但仍属于常驻目标：
            // 厂商服务可能绕过启动项，直接把同一个程序拉进子会话。
            entry.Applied = true;
            var confirmed = (IReadOnlyList<string>)unconfirmedMethod.Invoke(null, [journal])!;
            Assert.DoesNotContain(targetPath, confirmed);
            var stillManaged = (IReadOnlyList<string>)managedMethod.Invoke(null, [journal])!;
            Assert.Contains(targetPath, stillManaged);
        }
        finally
        {
            File.Delete(shortcutPath);
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    [Fact]
    public void 启动项守卫常驻目标包含已确认关闭的项目()
    {
        var journal = new StartupSnoozeJournal();
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            ValueName = "confirmed",
            CommandLine = "\"C:\\Apps\\confirmed.exe\" --startup",
            Applied = true
        });
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            ValueName = "unconfirmed",
            CommandLine = "\"C:\\Apps\\unconfirmed.exe\" --startup",
            Applied = false
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\ConfirmedTask",
            Executable = @"C:\Apps\task-confirmed.exe",
            ConfirmedDisabled = true
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\UnconfirmedTask",
            Executable = @"C:\Apps\task-unconfirmed.exe",
            ConfirmedDisabled = false
        });
        journal.DeferredExecutables.Add(@"C:\Apps\deferred.exe");

        var method = typeof(StartupSnoozeService).GetMethod(
            "NormalizeManagedStartupExecutables",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var targets = (IReadOnlyList<string>)method!.Invoke(null, [journal])!;

        Assert.Contains(@"C:\Apps\confirmed.exe", targets);
        Assert.Contains(@"C:\Apps\unconfirmed.exe", targets);
        Assert.Contains(@"C:\Apps\task-confirmed.exe", targets);
        Assert.Contains(@"C:\Apps\task-unconfirmed.exe", targets);
        Assert.Contains(@"C:\Apps\deferred.exe", targets);
    }

    [Fact]
    public void 启动项守卫只在子会话持续离开后重置会话状态()
    {
        const uint observed = 21;
        const uint replacement = 22;

        // 单次读取失败可能只是 RDP 重连中的瞬态，不能立刻清掉登录窗口。
        Assert.False(StartupGuardService.ShouldResetObservedSession(observed, null, 1));
        Assert.False(StartupGuardService.ShouldResetObservedSession(observed, 0, 3));
        Assert.True(StartupGuardService.ShouldResetObservedSession(observed, null, 4));

        // Windows 会复用会话号；编号不同必须立即丢弃旧会话的基线。
        Assert.True(StartupGuardService.ShouldResetObservedSession(observed, replacement, 0));
        Assert.False(StartupGuardService.ShouldResetObservedSession(observed, observed, 4));
        Assert.False(StartupGuardService.ShouldResetObservedSession(0, null, 4));
    }

    [Fact]
    public void 启动项守卫从会话扩展信息读取登录时间()
    {
        var expected = new DateTime(2026, 11, 21, 15, 18, 5, DateTimeKind.Utc)
            .AddTicks(2761209);
        var sessionInfo = new byte[176];
        BinaryPrimitives.WriteInt32LittleEndian(sessionInfo.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(
            sessionInfo.AsSpan(168, 8),
            expected.ToFileTimeUtc());

        Assert.Equal(expected, ChildSessionNativeMethods.ParseSessionLogonTime(sessionInfo));

        // WTSINFOEXW 可能返回更高的 level；不认识的布局不能被误读成登录时间。
        BinaryPrimitives.WriteInt32LittleEndian(sessionInfo.AsSpan(0, 4), 2);
        Assert.Null(ChildSessionNativeMethods.ParseSessionLogonTime(sessionInfo));

        // 会话 0 或尚未登录时 LogonTime 为 0，不能当成有效身份。
        BinaryPrimitives.WriteInt32LittleEndian(sessionInfo.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(sessionInfo.AsSpan(168, 8), 0);
        Assert.Null(ChildSessionNativeMethods.ParseSessionLogonTime(sessionInfo));
    }

    [Fact]
    public void 启动项守卫只兜底未确认关闭的项目()
    {
        var journal = new StartupSnoozeJournal();
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            ValueName = "confirmed",
            CommandLine = "\"C:\\Apps\\confirmed.exe\" --startup",
            Applied = true
        });
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            ValueName = "unconfirmed",
            CommandLine = "\"C:\\Apps\\unconfirmed.exe\" --startup",
            Applied = false
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\ConfirmedTask",
            Executable = @"C:\Apps\task-confirmed.exe",
            ConfirmedDisabled = true
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\UnconfirmedTask",
            Executable = @"C:\Apps\task-unconfirmed.exe",
            ConfirmedDisabled = false
        });
        journal.DeferredExecutables.Add(@"C:\Apps\deferred.exe");

        var method = typeof(StartupSnoozeService).GetMethod(
            "NormalizeStartupExecutables",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var targets = (IReadOnlyList<string>)method!.Invoke(null, [journal])!;

        Assert.Contains(@"C:\Apps\unconfirmed.exe", targets);
        Assert.Contains(@"C:\Apps\task-unconfirmed.exe", targets);
        Assert.Contains(@"C:\Apps\deferred.exe", targets);
        Assert.DoesNotContain(@"C:\Apps\confirmed.exe", targets);
        Assert.DoesNotContain(@"C:\Apps\task-confirmed.exe", targets);
    }

    [Fact]
    public void 审批值分类不会把错误类型当成已读取()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "ClassifyApprovalValue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        object?[] wrongTypeArguments = [RegistryValueKind.String, "not-bytes", null];
        Assert.Null(method!.Invoke(null, wrongTypeArguments));
        Assert.NotNull(wrongTypeArguments[2]);

        var bytes = new byte[] { 0x02, 1, 2, 3 };
        object?[] binaryArguments = [RegistryValueKind.Binary, bytes, null];
        Assert.Same(bytes, method.Invoke(null, binaryArguments));
        Assert.Null(binaryArguments[2]);

        object?[] nullArguments = [RegistryValueKind.Binary, null, null];
        Assert.Null(method.Invoke(null, nullArguments));
        Assert.NotNull(nullArguments[2]);
    }

    [Fact]
    public void 启动项恢复标记必须逐字节一致()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "IsWrittenValue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var written = new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 };
        var sameFlagDifferentTimestamp = new byte[] { 0x03, 0, 0, 0, 4, 4, 4, 4, 4, 4, 4, 4 };

        Assert.True((bool)method!.Invoke(null, [written, written])!);
        Assert.False((bool)method.Invoke(null, [sameFlagDifferentTimestamp, written])!);
        Assert.False((bool)method.Invoke(null, [null, written])!);
    }

    [Fact]
    public void 计划任务恢复必须同时证明原始启用_写入尝试_回读确认和身份()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "CanRestoreScheduledTask",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var identity = "sha256:" + new string('c', 64);
        StartupSnoozeTaskEntry Owned() => new()
        {
            Identity = identity,
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = true
        };
        bool CanRestore(StartupSnoozeJournal journal, StartupSnoozeTaskEntry task) =>
            (bool)method!.Invoke(null, [journal, task])!;

        var current = new StartupSnoozeJournal { Version = 3 };
        Assert.True(CanRestore(current, Owned()));

        // 外部关停（或写入未确认）会被任何一个缺失的证明拦住，绝不自动重新启用。
        Assert.False(CanRestore(current, new StartupSnoozeTaskEntry
        {
            Identity = identity,
            OriginalEnabled = true,
            WriteAttempted = false,
            ConfirmedDisabled = true
        }));
        Assert.False(CanRestore(current, new StartupSnoozeTaskEntry
        {
            Identity = identity,
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = false
        }));
        Assert.False(CanRestore(current, new StartupSnoozeTaskEntry
        {
            Identity = identity,
            OriginalEnabled = false,
            WriteAttempted = true,
            ConfirmedDisabled = true
        }));
        Assert.False(CanRestore(current, new StartupSnoozeTaskEntry
        {
            Identity = null,
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = true
        }));
        Assert.False(CanRestore(current, new StartupSnoozeTaskEntry
        {
            Identity = "sha256:not-a-hash",
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = true
        }));

        // 旧版日志没有身份和写入标记，即使回读确认过禁用也不能重新启用。
        foreach (var version in new[] { 1, 2 })
        {
            var legacy = new StartupSnoozeJournal { Version = version };
            Assert.False(CanRestore(legacy, Owned()));
            Assert.False(CanRestore(legacy, new StartupSnoozeTaskEntry { ConfirmedDisabled = true }));
        }
    }

    [Fact]
    public void 恢复日志拒绝相对路径的设备路径和空延迟程序()
    {
        foreach (var invalid in new[]
                 {
                     string.Empty,
                     @"relative\app.exe",
                     @"\\?\C:\Apps\app.exe",
                     @"\\.\C:\Apps\app.exe",
                     "C:"
                 })
        {
            var error = AssertInvalidJournal(journal =>
            {
                journal.DeferredExecutables.Clear();
                journal.DeferredExecutables.Add(invalid);
            });
            Assert.Contains("启动程序路径", error.Message);
        }
    }

    [Fact]
    public void 恢复日志拒绝包含_NUL_的超长延迟程序路径()
    {
        AssertInvalidJournal(journal =>
        {
            journal.DeferredExecutables.Clear();
            journal.DeferredExecutables.Add(string.Concat(@"C:\Apps\bad", (char)0, "app.exe"));
        });

        AssertInvalidJournal(journal =>
        {
            journal.DeferredExecutables.Clear();
            journal.DeferredExecutables.Add(@"C:\" + new string('a', 32768));
        });
    }

    [Fact]
    public void 启动项守卫在会话号被复用时按登录时间重置()
    {
        const uint sameSession = 21;
        var oldLogon = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var newLogon = oldLogon.AddHours(2);

        Assert.True(StartupGuardService.ShouldResetObservedSession(
            sameSession, sameSession, 0, observedLogonTime: oldLogon, childLogonTime: newLogon));
        Assert.False(StartupGuardService.ShouldResetObservedSession(
            sameSession, sameSession, 0, observedLogonTime: oldLogon, childLogonTime: oldLogon));

        // 单次 WTS 查询失败（时间为 null）不能丢弃原会话基线。
        Assert.False(StartupGuardService.ShouldResetObservedSession(
            sameSession, sameSession, 0, observedLogonTime: oldLogon, childLogonTime: null));
        Assert.False(StartupGuardService.ShouldResetObservedSession(
            sameSession, sameSession, 0, observedLogonTime: null, childLogonTime: newLogon));
    }

    [Fact]
    public void 关停日志接受带身份的当前版和旧版并拒绝未来版本()
    {
        StartupSnoozeService.ValidateJournal(CreateValidJournal());

        var legacy = CreateValidJournal();
        legacy.Version = 1;
        legacy.Tasks[0].Identity = null;
        StartupSnoozeService.ValidateJournal(legacy);

        var previous = CreateValidJournal();
        previous.Version = 2;
        previous.Tasks[0].Identity = null;
        StartupSnoozeService.ValidateJournal(previous);

        AssertInvalidJournal(journal => journal.Version = 4);
        AssertInvalidJournal(journal => journal.Tasks[0].Identity = null);
    }

    [Fact]
    public void UWP恢复只接受原始启用_写入确认和当前仍为写入值的组合()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "CanRestoreUwpState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        StartupSnoozeUwpEntry Entry(
            int originalState = 2,
            int writtenState = 0,
            bool writeConfirmed = true) => new()
        {
            OriginalState = originalState,
            WrittenState = writtenState,
            WriteConfirmed = writeConfirmed
        };
        bool CanRestore(int state, StartupSnoozeUwpEntry entry) =>
            (bool)method!.Invoke(null, [state, entry])!;

        // 正常路径：原状态启用，写完回读确认 0，当前仍是 0，才允许恢复。
        Assert.True(CanRestore(0, Entry()));
        Assert.True(CanRestore(0, Entry(originalState: 4)));

        // 外部关停（没有写入确认）、写入未确认、原本未启用、当前不是 0 都不能恢复。
        Assert.False(CanRestore(0, Entry(writeConfirmed: false)));
        Assert.False(CanRestore(0, Entry(writtenState: 2)));
        Assert.False(CanRestore(0, Entry(originalState: 0)));
        Assert.False(CanRestore(0, Entry(originalState: 1)));
        Assert.False(CanRestore(1, Entry()));
        Assert.False(CanRestore(2, Entry()));
        Assert.False(CanRestore(3, Entry()));
    }

    [Fact]
    public void UWP外部启用后恢复只清理日志不反向写入()
    {
        // 当前值已被外部改成启用时，恢复流程按 idempotent 路径直接清日志；
        // CanRestore 必须为 false，任何调用方都不能把它当成可写回的对象。
        var entry = new StartupSnoozeUwpEntry
        {
            OriginalState = 2,
            WrittenState = 0,
            WriteConfirmed = true
        };

        Assert.True(StartupSnoozeService.CanRestoreUwpState(0, entry));
        Assert.False(StartupSnoozeService.CanRestoreUwpState(2, entry));
        Assert.False(StartupSnoozeService.CanRestoreUwpState(4, entry));
    }

    [Fact]
    public void 恢复遇到已存在分身会话时必须保留日志且不恢复()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Present);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: true));

            var result = service.RecoverOnStartup();

            Assert.Null(result);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 恢复遇到无法确认分身会话时必须保留日志并失败()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Unknown);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: true));

            var result = service.RecoverOnStartup();

            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.Contains("无法确认", result.Message);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 恢复只有确认没有分身会话时才清理日志()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Absent);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: true));

            var result = service.RecoverOnStartup();

            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.False(service.HasPendingJournal);
            Assert.False(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 已有分身会话时没有日志必须拒绝接管()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Present);

            var result = service.EnsureSnoozedForExistingChildSession();

            Assert.False(result.Success);
            Assert.Contains("日志", result.Message);
            Assert.False(service.HasPendingJournal);
            Assert.False(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 已有分身会话时可接管完整的已完成日志()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Present);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: true));

            var result = service.EnsureSnoozedForExistingChildSession();

            Assert.True(result.Success);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 已有分身会话时拒绝接管不完整日志()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Present);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: false));

            var result = service.EnsureSnoozedForExistingChildSession();

            Assert.False(result.Success);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 已有分身会话时拒绝接管当前状态不匹配的完成日志()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Present);
            var journal = CreateEmptyTestJournal(completed: true);
            journal.Registry.Add(new StartupSnoozeRegistryEntry
            {
                Kind = "RunValue",
                Hive = "CurrentUser",
                RunView = "Registry64",
                ApprovedView = "Registry64",
                KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                ValueName = "DesktopTwin-Missing-" + Guid.NewGuid().ToString("N"),
                CommandLine = "\"C:\\Program Files\\Contoso\\contoso.exe\"",
                OriginalExists = true,
                OriginalBase64 = Convert.ToBase64String(new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
                WrittenBase64 = Convert.ToBase64String(new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 }),
                Applied = true
            });
            WriteTestJournal(service, journal);

            var result = service.EnsureSnoozedForExistingChildSession();

            Assert.False(result.Success);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    [Fact]
    public void 普通连接准备在分身存在或状态未知时拒绝修改启动项()
    {
        foreach (var presence in new[] { StartupSnoozeService.ChildSessionPresence.Present, StartupSnoozeService.ChildSessionPresence.Unknown })
        {
            WithTestDirectory(directory =>
            {
                var service = new StartupSnoozeService(directory, () => presence);

                var result = service.EnsureSnoozed();

                Assert.False(result.Success);
                Assert.False(service.HasPendingJournal);
                Assert.False(File.Exists(service.JournalPath));
            });
        }
    }

    [Fact]
    public void 已有分身会话的接管要求确认会话存在()
    {
        WithTestDirectory(directory =>
        {
            var service = new StartupSnoozeService(
                directory,
                () => StartupSnoozeService.ChildSessionPresence.Absent);
            WriteTestJournal(service, CreateEmptyTestJournal(completed: true));

            var result = service.EnsureSnoozedForExistingChildSession();

            Assert.False(result.Success);
            Assert.True(service.HasPendingJournal);
            Assert.True(File.Exists(service.JournalPath));
        });
    }

    private static InvalidDataException AssertInvalidJournal(Action<StartupSnoozeJournal> mutate)
    {
        var journal = CreateValidJournal();
        mutate(journal);
        return Assert.Throws<InvalidDataException>(
            () => StartupSnoozeService.ValidateJournal(journal));
    }

    private static StartupSnoozeJournal CreateValidJournal()
    {
        var journal = new StartupSnoozeJournal
        {
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Completed = true
        };
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            Kind = "RunValue",
            Hive = "CurrentUser",
            RunView = "Registry64",
            ApprovedView = "Registry64",
            KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            ValueName = "ContosoStartup",
            CommandLine = "\"C:\\Program Files\\Contoso\\contoso.exe\"",
            OriginalExists = true,
            OriginalBase64 = Convert.ToBase64String(new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
            WrittenBase64 = Convert.ToBase64String(new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 })
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\ContosoStartup",
            TaskName = "ContosoStartup",
            TaskPath = @"\",
            Identity = "sha256:" + new string('b', 64),
            Executable = @"C:\Program Files\Contoso\contoso.exe",
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = true
        });
        journal.Uwp.Add(new StartupSnoozeUwpEntry
        {
            KeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\Contoso.App_8wekyb3d8bbwe\ContosoStartup",
            PackageFamilyName = "Contoso.App_8wekyb3d8bbwe",
            Task = "ContosoStartup",
            OriginalState = 2,
            WrittenState = 0,
            WriteConfirmed = true
        });
        return journal;
    }

    private static StartupSnoozeJournal CreateEmptyTestJournal(bool completed)
    {
        var journal = CreateValidJournal();
        journal.Completed = completed;
        journal.Registry.Clear();
        journal.Tasks.Clear();
        journal.Uwp.Clear();
        return journal;
    }

    private static void WriteTestJournal(
        StartupSnoozeService service,
        StartupSnoozeJournal journal)
    {
        var parent = Path.GetDirectoryName(service.JournalPath);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        File.WriteAllText(service.JournalPath, JsonSerializer.Serialize(journal));
    }

    private static void WithTestDirectory(Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-snooze-gating-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            action(directory);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void 没有日志时不算已关停()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopTwin-snooze-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var service = new StartupSnoozeService(directory);
            Assert.False(service.IsSnoozed);
            Assert.False(service.HasPendingJournal);

            File.WriteAllText(service.JournalPath, "{}");
            Assert.True(service.IsSnoozed);
            Assert.True(service.HasPendingJournal);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void 恢复命令拒绝无效参数且不进入恢复流程()
    {
        // 不是恢复助手命令行时返回 false，调用方仍需按正常启动继续。
        Assert.False(StartupRecoveryCommand.TryHandle([], out var emptyExitCode));
        Assert.Equal(0, emptyExitCode);

        Assert.False(StartupRecoveryCommand.TryHandle(["--other"], out var otherExitCode));
        Assert.Equal(0, otherExitCode);

        // 下面每一条都必须识别成恢复命令（返回 true），但在 Run 之前就以退出码 2
        // 拒绝。任何一条被误放行，都会转去等待父进程并真的读写启动项。
        var selfPid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var invalidCommands = new[]
        {
            new[] { "--restore-startup-journal" },
            new[] { "--restore-startup-journal", "0" },
            new[] { "--restore-startup-journal", "-1" },
            new[] { "--restore-startup-journal", "+1" },
            new[] { "--restore-startup-journal", "1.5" },
            new[] { "--restore-startup-journal", "0x10" },
            new[] { "--restore-startup-journal", " 42" },
            new[] { "--restore-startup-journal", "not-a-pid" },
            new[] { "--restore-startup-journal", selfPid },
            new[] { "--RESTORE-STARTUP-JOURNAL", "not-a-pid" },
            new[] { "--restore-startup-journal", "43210", "not-ticks" },
            new[] { "--restore-startup-journal", "43210", "-1" },
            new[] { "--restore-startup-journal", "43210", "1", "extra" }
        };

        foreach (var command in invalidCommands)
        {
            Assert.True(
                StartupRecoveryCommand.TryHandle(command, out var exitCode),
                "应识别为恢复命令但被拒绝：" + string.Join(' ', command));
            Assert.Equal(2, exitCode);
        }
    }

    [Fact]
    public void 恢复助手按启动时间识别父进程_PID_复用()
    {
        var method = typeof(StartupRecoveryCommand).GetMethod(
            "WaitForParentExit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // 同一个 PID 但启动时间对不上：原父进程已经退出、PID 被别的长命进程复用，
        // 必须立刻判定父进程已退出，而不是陪一个无关进程等满 120 秒。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var mismatched = (bool)method!.Invoke(null, [Environment.ProcessId, 1L])!;
        stopwatch.Stop();
        Assert.True(mismatched);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));

        // PID 已经不存在：GetProcessById 直接抛 ArgumentException，按已退出处理。
        Assert.True((bool)method.Invoke(null, [int.MaxValue, 0L])!);
    }

    [Fact]
    public void 恢复日志的计划任务名必须是根路径且不能带通配符()
    {
        // 不以反斜杠开头时 schtasks /TN 会按当前任务文件夹解析，可能关掉不相干的任务；
        // 通配符和换行会让一个条目命中多个任务，恢复时无法保证只碰日志里的那个。
        foreach (var invalid in new[]
                 {
                     "ContosoStartup",
                     @"\Contoso\*",
                     @"\Contoso\Task?",
                     @"\Contoso\Bad" + '\r' + "Name",
                     @"\Contoso\Bad" + '\n' + "Name",
                     @"\Contoso\Bad" + '\0' + "Name",
                     @"\Contoso\",
                     @"\Contoso\\Task"
                 })
        {
            var error = AssertInvalidJournal(journal => journal.Tasks[0].Relative = invalid);
            Assert.Contains("任务", error.Message);
        }
    }

    [Fact]
    public void 恢复日志接受注册表默认值名并拒绝_null_与非法字符()
    {
        // Run 的空值名就是 (Default)，Windows 会启动它，日志必须能原样往返、按原字节恢复。
        var journal = CreateValidJournal();
        journal.Registry[0].ValueName = string.Empty;
        journal.Registry[0].OriginalBase64 = Convert.ToBase64String(new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 });
        journal.Registry[0].WrittenBase64 = Convert.ToBase64String(StartupApproval.CreateDisabled(DateTime.UtcNow));
        StartupSnoozeService.ValidateJournal(journal);
        Assert.True(StartupSnoozeService.IsCollectableRunValueName(string.Empty));
        Assert.False(StartupSnoozeService.IsCollectableRunValueName(null));

        // 空白值名也是合法的命名值，不能误当成默认值或非法值。
        var whitespace = CreateValidJournal();
        whitespace.Registry[0].ValueName = "   ";
        StartupSnoozeService.ValidateJournal(whitespace);

        // null 与 NUL 字符无法按注册表语义往返，必须拒绝。
        AssertInvalidJournal(value => value.Registry[0].ValueName = null!);
        foreach (var invalidName in new[] { "Contoso\0Startup" })
        {
            var error = AssertInvalidJournal(value => value.Registry[0].ValueName = invalidName);
            Assert.Contains("值名", error.Message);
        }

        // 类型和键路径必须彼此印证，否则可以借合法的单条路径把值写进另一类启动项。
        var misplacedFolderKind = AssertInvalidJournal(
            journal => journal.Registry[0].Kind = "StartupFolderItem");
        Assert.Contains("路径", misplacedFolderKind.Message);

        var misplacedRunKind = AssertInvalidJournal(journal =>
        {
            journal.Registry[0].KeyPath =
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        });
        Assert.Contains("Run", misplacedRunKind.Message);
    }

    [Fact]
    public void 注册表默认值名在_Run_中收集并按原字节往返_RunOnce_中拒绝()
    {
        // Run 的默认值 (Default) 会被 Windows 启动，必须能进入日志并逐字节恢复。
        var journal = CreateValidJournal();
        var original = new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        journal.Registry[0].ValueName = string.Empty;
        journal.Registry[0].OriginalExists = true;
        journal.Registry[0].OriginalBase64 = Convert.ToBase64String(original);
        journal.Registry[0].WrittenBase64 = Convert.ToBase64String(StartupApproval.CreateDisabled(DateTime.UtcNow));

        var json = JsonSerializer.Serialize(journal);
        var back = JsonSerializer.Deserialize<StartupSnoozeJournal>(json);
        Assert.NotNull(back);
        Assert.Equal(string.Empty, back!.Registry[0].ValueName);
        Assert.Equal(Convert.ToBase64String(original), back.Registry[0].OriginalBase64);
        StartupSnoozeService.ValidateJournal(back);

        // RunOnce 的默认值同样是未覆盖启动源，窄放行必须拒绝空名字。
        Assert.False(RunOnceClassifier.IsAllowed(
            string.Empty,
            "\"C:\\Program Files\\Microsoft\\Edge\\msedge.exe\" --cleanup",
            true,
            @"C:\Program Files\Microsoft\Edge\msedge.exe",
            new[] { @"C:\Program Files\Microsoft\Edge" }));

        // 组策略 Run 键只要有任何值（包括空名字的默认值）就阻断。
        Assert.True(StartupSnoozeService.HasPolicyStartupEntries(new[] { string.Empty }, Array.Empty<string>()));
        Assert.True(StartupSnoozeService.HasPolicyStartupEntries(Array.Empty<string>(), new[] { "SubKey" }));
        Assert.False(StartupSnoozeService.HasPolicyStartupEntries(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void 裸命令按_PATH_三态判定_未知时失败关闭_缺失时才跳过()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Windows\System32\cmd.exe",
            @"C:\Tools\app.bat"
        };
        bool Exists(string path) => existing.Contains(path);

        // PATH 中能唯一定位：存在。
        Assert.Equal(
            StartupSnoozeService.ExecutablePresence.Exists,
            StartupSnoozeService.ProveExecutablePresence(
                "cmd.exe /c exit",
                @"C:\Windows\System32",
                Exists));
        Assert.Equal(
            StartupSnoozeService.ExecutablePresence.Exists,
            StartupSnoozeService.ProveExecutablePresence(
                "app",
                "\"C:\\Tools\"",
                Exists));

        // 每个绝对 PATH 目录都查过且不存在：才允许判定 Missing。
        Assert.Equal(
            StartupSnoozeService.ExecutablePresence.Missing,
            StartupSnoozeService.ProveExecutablePresence(
                "cmd.exe",
                @"C:\Windows;C:\Tools",
                _ => false));

        // 空/相对 PATH、相对命令路径、drive-relative、解析失败：无法证明，必须 Unknown。
        foreach (var (commandLine, pathVariable) in new[]
                 {
                     ("cmd.exe", (string?)null),
                     ("cmd.exe", string.Empty),
                     ("cmd.exe", "   "),
                     ("cmd.exe", @".\bin"),
                     ("cmd.exe", @"C:\Tools;relative"),
                     (@"bin\cmd.exe", @"C:\Windows"),
                     (@"C:cmd.exe", @"C:\Windows"),
                     (string.Empty, @"C:\Windows"),
                     ("\"unterminated", @"C:\Windows")
                 })
        {
            Assert.Equal(
                StartupSnoozeService.ExecutablePresence.Unknown,
                StartupSnoozeService.ProveExecutablePresence(commandLine, pathVariable, Exists));
        }

        // 绝对路径存在/不存在是确定的。
        Assert.Equal(
            StartupSnoozeService.ExecutablePresence.Exists,
            StartupSnoozeService.ProveExecutablePresence(@"C:\Windows\System32\cmd.exe", null, Exists));
        Assert.Equal(
            StartupSnoozeService.ExecutablePresence.Missing,
            StartupSnoozeService.ProveExecutablePresence(@"C:\Windows\missing.exe", @"C:\Windows\System32", Exists));
    }

    [Fact]
    public void 启动项失败关闭判定只接受已经确认停用的状态()
    {
        var disabled = StartupApproval.CreateDisabled(DateTime.UtcNow);
        var enabled = (byte[])disabled.Clone();
        enabled[0] = 0x02;

        Assert.True(StartupSnoozeService.IsApprovalConfirmedDisabled(true, null, disabled));
        Assert.False(StartupSnoozeService.IsApprovalConfirmedDisabled(true, null, enabled));
        Assert.False(StartupSnoozeService.IsApprovalConfirmedDisabled(false, null, null));

        // 审批值读不出来时必须按“仍可能在子会话启动”处理，不能把错误吞成已隔离。
        Assert.False(StartupSnoozeService.IsApprovalConfirmedDisabled(
            true,
            "拒绝访问",
            disabled));

        Assert.True(StartupSnoozeService.IsUwpStateDisabled(0));
        Assert.False(StartupSnoozeService.IsUwpStateDisabled(2));
        Assert.False(StartupSnoozeService.IsUwpStateDisabled(null));
        Assert.False(StartupSnoozeService.IsUwpStateDisabled("0"));
    }

    [Fact]
    public void 无效关停日志让恢复失败并保留原文件()
    {
        var otherMachine = JsonSerializer.Serialize(new StartupSnoozeJournal
        {
            Machine = Environment.MachineName + "-other",
            User = Environment.UserName,
            Completed = true
        });

        var invalidJournals = new Dictionary<string, string>
        {
            ["截断的 JSON"] = "{\"Version\":2,",
            ["不支持的版本"] = "{\"Version\":0}",
            ["缺少列表"] = "{\"Version\":2,\"Registry\":null}",
            ["其他计算机的日志"] = otherMachine
        };

        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-snooze-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var service = new StartupSnoozeService(directory);

            foreach (var (name, json) in invalidJournals)
            {
                File.WriteAllText(service.JournalPath, json);

                // 恢复失败时绝不能丢掉日志，否则下次启动就再也找不到原始状态。
                var restored = service.Restore();
                Assert.False(restored.Success);
                Assert.Contains("读取关停日志失败", restored.Message);
                Assert.True(File.Exists(service.JournalPath), name + "：恢复失败后必须保留日志");
                Assert.Equal(json, File.ReadAllText(service.JournalPath));

                // 状态不明时也不能把旧日志当成空清单继续关停。
                var snoozed = service.EnsureSnoozed();
                Assert.False(snoozed.Success);
                Assert.Contains("已有关停日志但无法读取", snoozed.Message);
                Assert.Equal(json, File.ReadAllText(service.JournalPath));
                Assert.True(service.HasPendingJournal);
            }
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}
