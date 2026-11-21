using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
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
    [Fact]
    public void 关闭字节按低四位判定且保留原有的关闭时间()
    {
        var original = new byte[12];
        original[0] = 0x03;
        BitConverter.GetBytes(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).ToFileTimeUtc())
            .CopyTo(original, 4);

        Assert.True(StartupApproval.IsDisabled(original));

        // 0x02 / 0x01 / 0x06 都是「启用」，不能被误判成关闭。
        foreach (var flag in new byte[] { 0x00, 0x01, 0x02, 0x06 })
        {
            var enabled = (byte[])original.Clone();
            enabled[0] = flag;
            Assert.False(StartupApproval.IsDisabled(enabled));
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
        // 用 XML 解析器读会因编码不符抛异常，所以实现必须是字符串扫描。
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
    public void 计划任务缺少_Enabled_时按启用处理()
    {
        Assert.True(ScheduledTaskXml.IsEnabled(null));
        Assert.True(ScheduledTaskXml.IsEnabled(string.Empty));
        Assert.True(ScheduledTaskXml.IsEnabled("<Task><Settings></Settings></Task>"));
    }

    [Fact]
    public void 计划任务只认_Settings_这个元素而不是同前缀的别的元素()
    {
        // 元素名前缀相同不能算命中，否则会把别的块里的 Enabled 当成本任务的开关。
        const string xml = """
            <Task><SettingsData><Enabled>false</Enabled></SettingsData></Task>
            """;

        Assert.True(ScheduledTaskXml.IsEnabled(xml));
    }

    [Fact]
    public void 登录与注册触发器才算会话启动触发器()
    {
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><LogonTrigger /></Triggers>"));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><SessionStateChangeTrigger /></Triggers>"));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><RegistrationTrigger /></Triggers>"));

        Assert.False(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><TimeTrigger /></Triggers>"));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(null));
    }

    [Fact]
    public void 计划任务能取出直接启动的程序路径()
    {
        const string xml = """
            <Task><Actions><Exec>
              <Command>"C:\Program Files\Vendor &amp; Co\app.exe"</Command>
              <Arguments>--startup</Arguments>
            </Exec></Actions></Task>
            """;

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
        Assert.Equal(2, back.Version);
        Assert.Single(back.Tasks);
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app" };

        Assert.True(StartupGuardService.ShouldTerminate(
            7, 7, 1, @"c:\apps\twin\APP.EXE", "app", exact, names));
        Assert.False(StartupGuardService.ShouldTerminate(
            1, 7, 1, @"C:\Apps\Twin\app.exe", "app", exact, names));
        Assert.False(StartupGuardService.ShouldTerminate(
            8, 7, 1, @"C:\Apps\Twin\app.exe", "app", exact, names));
        Assert.False(StartupGuardService.ShouldTerminate(
            7, 7, 1, @"C:\Apps\Other\app.exe", "app", exact, names));

        // 读不到模块路径时才允许按进程名兜底，且仍然受会话号约束。
        Assert.True(StartupGuardService.ShouldTerminate(
            7, 7, 1, null, "app", exact, names));
        Assert.False(StartupGuardService.ShouldTerminate(
            1, 7, 1, null, "app", exact, names));
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contoso" };

        // 即使主会话里运行着同一个程序，也绝不能因为路径命中就去终止它。
        Assert.False(StartupGuardService.ShouldTerminate(
            mainSession, childSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", "contoso", paths, names));
        Assert.False(StartupGuardService.ShouldTerminate(
            3, childSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", "contoso", paths, names));

        // 目标会话号本身等于主会话号时，整条守卫都必须拒绝工作。
        Assert.False(StartupGuardService.ShouldTerminate(
            mainSession, mainSession, mainSession,
            @"C:\Apps\Contoso\contoso.exe", "contoso", paths, names));
    }

    [Fact]
    public void 启动项守卫只按精确路径匹配_读不到路径时才用进程名兜底()
    {
        const uint mainSession = 1;
        const uint childSession = 2;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Contoso\contoso.exe"
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contoso" };

        Assert.True(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            @"c:\apps\contoso\CONTOSO.EXE", "contoso", paths, names));

        // 同名但不在启动项路径里的程序不能被杀。
        Assert.False(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            @"C:\Other\contoso.exe", "contoso", paths, names));

        // 只有主模块路径不可读时才允许按进程名兜底。
        Assert.True(StartupGuardService.ShouldTerminate(
            childSession, childSession, mainSession,
            null, "contoso", paths, names));
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
    public void 计划任务恢复能区分新版标记和旧版日志()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "CanRestoreScheduledTask",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var current = new StartupSnoozeJournal { Version = 2 };
        Assert.True((bool)method!.Invoke(null,
            [current, new StartupSnoozeTaskEntry { WriteAttempted = true }])!);
        Assert.False((bool)method.Invoke(null,
            [current, new StartupSnoozeTaskEntry { WriteAttempted = false, ConfirmedDisabled = true }])!);

        // 旧版日志没有 WriteAttempted，只能把回读确认过的禁用视为旧版自己的写入。
        var legacy = new StartupSnoozeJournal { Version = 1 };
        Assert.True((bool)method.Invoke(null,
            [legacy, new StartupSnoozeTaskEntry { ConfirmedDisabled = true }])!);
        Assert.False((bool)method.Invoke(null,
            [legacy, new StartupSnoozeTaskEntry { ConfirmedDisabled = false }])!);
    }

    [Fact]
    public void 关停日志版本接受旧版并拒绝未来版本()
    {
        var legacy = CreateValidJournal();
        legacy.Version = 1;
        StartupSnoozeService.ValidateJournal(legacy);

        AssertInvalidJournal(journal => journal.Version = 3);
    }

    [Fact]
    public void UWP恢复只接受记录过的关闭状态()
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "CanRestoreUwpState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var entry = new StartupSnoozeUwpEntry { OriginalState = 2, WrittenState = 0 };
        var unrelatedEntry = new StartupSnoozeUwpEntry { OriginalState = 2, WrittenState = 2 };

        Assert.True((bool)method!.Invoke(null, [0, entry])!);
        Assert.False((bool)method.Invoke(null, [0, unrelatedEntry])!);
        Assert.False((bool)method.Invoke(null, [1, entry])!);
        Assert.False((bool)method.Invoke(null, [3, entry])!);
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
            Executable = @"C:\Program Files\Contoso\contoso.exe",
            OriginalEnabled = true
        });
        journal.Uwp.Add(new StartupSnoozeUwpEntry
        {
            KeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\Contoso.App_8wekyb3d8bbwe\ContosoStartup",
            PackageFamilyName = "Contoso.App_8wekyb3d8bbwe",
            Task = "ContosoStartup",
            OriginalState = 2,
            WrittenState = 2
        });
        return journal;
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
}
