using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// 分身代理的启动/复用/终止只允许命中「已捕获的会话号 + 该次登录时间 + 规范化
/// AgentPath 精确相等」的进程。这里全部是纯身份/路径判断：不查询 WTS、
/// 不枚举、不终止任何真实进程。
/// </summary>
public sealed class SessionAgentServiceTests
{
    private const uint ChildSessionId = 7;
    private const string AgentPath =
        @"C:\Users\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe";

    private static readonly DateTime ChildLogonTimeUtc =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static readonly SessionAgentService.ChildSessionIdentity Identity =
        new(ChildSessionId, ChildLogonTimeUtc);

    [Fact]
    public void 同会话且完整路径精确相等时匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            Identity,
            processSessionId: 7,
            processImagePath: AgentPath));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void 不同会话或无效会话身份拒绝(int processSessionId)
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            Identity,
            processSessionId,
            AgentPath));
    }

    [Fact]
    public void 会话身份不可读时拒绝()
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            Identity,
            processSessionId: null,
            processImagePath: AgentPath));
    }

    [Fact]
    public void 登录时间未知的身份不能匹配进程()
    {
        var incomplete = new SessionAgentService.ChildSessionIdentity(
            ChildSessionId,
            LogonTimeUtc: default);

        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            incomplete,
            processSessionId: 7,
            processImagePath: AgentPath));
    }

    [Theory]
    [InlineData(@"C:\Users\Test\AppData\Local\DesktopTwin\DesktopTwin.Agent.exe")]
    [InlineData(@"C:\Users\Test\AppData\Roaming\DesktopTwin.Old\DesktopTwin.Agent.exe")]
    [InlineData(@"C:\Users\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe.old")]
    [InlineData(@"C:\Users\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.copy.exe")]
    public void 同会话但不同目录或同名文件不匹配时拒绝(string processImagePath)
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            Identity,
            processSessionId: 7,
            processImagePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"DesktopTwin.Agent.exe")]
    [InlineData(@"..\DesktopTwin.Agent.exe")]
    [InlineData("C:\\bad\u0000path\\DesktopTwin.Agent.exe")]
    public void 空相对或无效期望路径拒绝(string? expectedAgentPath)
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            expectedAgentPath,
            Identity,
            processSessionId: 7,
            processImagePath: AgentPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"DesktopTwin.Agent.exe")]
    [InlineData("C:\\bad\u0000path\\DesktopTwin.Agent.exe")]
    public void 空相对或无效实际镜像路径拒绝(string? processImagePath)
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            Identity,
            processSessionId: 7,
            processImagePath));
    }

    [Fact]
    public void 路径大小写按Windows语义不敏感匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            @"C:\USERS\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe",
            Identity,
            processSessionId: 7,
            processImagePath:
                @"c:\users\test\appdata\roaming\desktoptwin\DesktopTwin.Agent.exe"));
    }

    [Fact]
    public void 路径规范化后相等时匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            @"C:\Users\Test\AppData\Local\..\Roaming\DesktopTwin\DesktopTwin.Agent.exe",
            Identity,
            processSessionId: 7,
            processImagePath:
                @"C:\Users\Test\AppData\Roaming\DesktopTwin\.\DesktopTwin.Agent.exe"));
    }

    [Fact]
    public void 目标会话必须为正数()
    {
        var invalid = new SessionAgentService.ChildSessionIdentity(
            SessionId: 0,
            ChildLogonTimeUtc);

        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            invalid,
            processSessionId: 0,
            processImagePath: AgentPath));
    }

    [Fact]
    public void 查询失败时不能建立会话身份()
    {
        Assert.False(SessionAgentService.TryBindChildSessionIdentity(
            querySucceeded: false,
            queriedSessionId: null,
            logonTimeUtc: null,
            out var identity,
            out var error));

        Assert.False(identity.IsComplete);
        Assert.Contains("查询", error);
    }

    [Fact]
    public void 确认没有子会话时不建立身份()
    {
        Assert.False(SessionAgentService.TryBindChildSessionIdentity(
            querySucceeded: true,
            queriedSessionId: null,
            logonTimeUtc: null,
            out var identity,
            out var error));

        Assert.False(identity.IsComplete);
        Assert.Contains("尚未创建", error);
    }

    [Fact]
    public void 无效会话号不建立身份()
    {
        Assert.False(SessionAgentService.TryBindChildSessionIdentity(
            querySucceeded: true,
            queriedSessionId: 0,
            logonTimeUtc: ChildLogonTimeUtc,
            out var identity,
            out var error));

        Assert.False(identity.IsComplete);
        Assert.Contains("无效", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public void 读不到登录时间时失败关闭(string? logonKind)
    {
        DateTime? logonTime = logonKind is null ? null : default(DateTime);

        Assert.False(SessionAgentService.TryBindChildSessionIdentity(
            querySucceeded: true,
            queriedSessionId: ChildSessionId,
            logonTimeUtc: logonTime,
            out var identity,
            out var error));

        Assert.False(identity.IsComplete);
        Assert.Contains("登录时间", error);
        Assert.Contains(ChildSessionId.ToString(), error);
    }

    [Fact]
    public void 三态查询完整时绑定同一个会话身份()
    {
        Assert.True(SessionAgentService.TryBindChildSessionIdentity(
            querySucceeded: true,
            queriedSessionId: ChildSessionId,
            logonTimeUtc: ChildLogonTimeUtc,
            out var identity,
            out var error));

        Assert.Equal(ChildSessionId, identity.SessionId);
        Assert.Equal(ChildLogonTimeUtc, identity.LogonTimeUtc);
        Assert.True(identity.IsComplete);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void 会话号和登录时间都未变时身份校验通过()
    {
        Assert.True(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: true,
            currentSessionId: ChildSessionId,
            currentLogonTimeUtc: ChildLogonTimeUtc,
            out var error));

        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void 会话号变化时校验失败并同时报告新旧会话()
    {
        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: true,
            currentSessionId: 8,
            currentLogonTimeUtc: ChildLogonTimeUtc,
            out var error));

        Assert.Contains("7", error);
        Assert.Contains("8", error);
        Assert.Contains("变", error);
    }

    [Fact]
    public void 同号但登录时间变化时按会话重建失败关闭()
    {
        var rebuiltLogonTime = ChildLogonTimeUtc.AddMinutes(1);

        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: true,
            currentSessionId: ChildSessionId,
            currentLogonTimeUtc: rebuiltLogonTime,
            out var error));

        Assert.Contains("重建", error);
        Assert.Contains(ChildSessionId.ToString(), error);
    }

    [Fact]
    public void 重新查询失败时身份校验失败关闭()
    {
        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: false,
            currentSessionId: null,
            currentLogonTimeUtc: null,
            out var error));

        Assert.Contains("WTS", error);
    }

    [Fact]
    public void 会话消失时身份校验失败关闭()
    {
        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: true,
            currentSessionId: null,
            currentLogonTimeUtc: null,
            out var error));

        Assert.Contains("已消失", error);
    }

    [Fact]
    public void 登录时间不可读时身份校验失败关闭()
    {
        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            Identity,
            currentQuerySucceeded: true,
            currentSessionId: ChildSessionId,
            currentLogonTimeUtc: null,
            out var error));

        Assert.Contains("登录时间", error);
    }

    [Fact]
    public void 捕获身份不完整时校验失败关闭()
    {
        var incomplete = new SessionAgentService.ChildSessionIdentity(
            ChildSessionId,
            LogonTimeUtc: default);

        Assert.False(SessionAgentService.TryValidateChildSessionIdentity(
            incomplete,
            currentQuerySucceeded: true,
            currentSessionId: ChildSessionId,
            currentLogonTimeUtc: ChildLogonTimeUtc,
            out var error));

        Assert.Contains("不完整", error);
    }

    [Fact]
    public void 进程分类只把目标会话中的精确路径视为在线()
    {
        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            Identity,
            [
                (8, AgentPath),
                (7, AgentPath)
            ]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Matching, result);
    }

    [Fact]
    public void 同会话同名但路径不同时分类为冲突而不是在线()
    {
        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            Identity,
            [(7, AgentPath + ".old")]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Conflict, result);
    }

    [Theory]
    [InlineData(null, AgentPath)]
    [InlineData(7, null)]
    [InlineData(7, @"DesktopTwin.Agent.exe")]
    public void 会话或路径不可读时分类为未知并失败关闭(int? sessionId, string? imagePath)
    {
        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            Identity,
            [(sessionId, imagePath)]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Unknown, result);
    }

    [Fact]
    public void 只有其他会话的同名进程时分类为不存在()
    {
        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            Identity,
            [(8, AgentPath)]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Absent, result);
    }

    [Fact]
    public void 同会话冲突项之外还有未知项时分类为未知()
    {
        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            Identity,
            [
                (7, AgentPath + ".old"),
                (null, null)
            ]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Unknown, result);
    }

    [Fact]
    public void 身份登录时间未知时分类为未知并失败关闭()
    {
        var incomplete = new SessionAgentService.ChildSessionIdentity(
            ChildSessionId,
            LogonTimeUtc: default);

        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            incomplete,
            [(7, AgentPath)]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Unknown, result);
    }

    [Fact]
    public void 只有捕获会话号的同路径进程才算在线()
    {
        var otherLogin = new SessionAgentService.ChildSessionIdentity(8, ChildLogonTimeUtc);

        var result = SessionAgentService.ClassifyAgentProcessPresence(
            AgentPath,
            otherLogin,
            [(7, AgentPath)]);

        Assert.Equal(SessionAgentService.AgentProcessPresence.Absent, result);
    }

    [Fact]
    public void 代理DACL必须同时具备管理员系统完全控制和用户读取执行()
    {
        var rules = BuildRules(
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));

        SessionAgentService.ValidateAccessRules(rules);
    }

    [Fact]
    public void 代理DACL缺少SYSTEM完全控制时必须失败()
    {
        var rules = BuildRules(
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));

        var exception = Assert.Throws<IOException>(
            () => SessionAgentService.ValidateAccessRules(rules));

        Assert.Contains("SYSTEM", exception.Message);
    }

    [Fact]
    public void 代理DACL缺少Administrators完全控制时必须失败()
    {
        var rules = BuildRules(
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));

        var exception = Assert.Throws<IOException>(
            () => SessionAgentService.ValidateAccessRules(rules));

        Assert.Contains("Administrators", exception.Message);
    }

    [Fact]
    public void 代理DACL缺少普通用户读取执行时必须失败()
    {
        var rules = BuildRules(
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));

        var exception = Assert.Throws<IOException>(
            () => SessionAgentService.ValidateAccessRules(rules));

        Assert.Contains("Builtin Users", exception.Message);
    }

    [Fact]
    public void 代理DACL出现普通用户写权限时必须失败()
    {
        var rules = BuildRules(
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify));

        var exception = Assert.Throws<IOException>(
            () => SessionAgentService.ValidateAccessRules(rules));

        Assert.Contains("写入", exception.Message);
    }

    [Fact]
    public void 固化DACL在创建时即保护且普通用户只有读取执行权限()
    {
        var directorySecurity = SessionAgentService.BuildDirectorySecurity(setOwner: true);
        Assert.True(directorySecurity.AreAccessRulesProtected);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            directorySecurity.GetOwner(typeof(SecurityIdentifier)));

        var directoryRules = directorySecurity.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));
        SessionAgentService.ValidateAccessRules(directoryRules);
        Assert.Equal(3, directoryRules.Count);
        var directoryUsersRule = directoryRules
            .Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.Equal(
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            directoryUsersRule.FileSystemRights);
        Assert.Equal(
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            directoryUsersRule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, directoryUsersRule.PropagationFlags);

        var fileSecurity = SessionAgentService.BuildFileSecurity(setOwner: true);
        Assert.True(fileSecurity.AreAccessRulesProtected);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            fileSecurity.GetOwner(typeof(SecurityIdentifier)));

        var fileRules = fileSecurity.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));
        SessionAgentService.ValidateAccessRules(fileRules);
        Assert.Equal(3, fileRules.Count);
        Assert.All(
            fileRules.Cast<FileSystemAccessRule>(),
            rule => Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags));
        var fileUsersRule = fileRules
            .Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.Equal(
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            fileUsersRule.FileSystemRights);
    }

    [Fact]
    public void 用户拥有的代理路径所有者不可信时拒绝()
    {
        var directory = CreateTempDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var owner = new DirectoryInfo(directory)
                .GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier));
            if (owner is not null
                && (owner.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
                    || owner.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))))
            {
                // 以 SYSTEM/Administrators 运行测试时不具备“不可信所有者”夹具。
                return;
            }

            var exception = Assert.Throws<IOException>(
                () => SessionAgentService.ValidateTrustedOwner(directory, isDirectory: true));
            Assert.Contains("所有者", exception.Message);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void 代理路径任一级祖先为目录联接时拒绝()
    {
        var root = CreateTempDirectory();
        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "junction");
        try
        {
            Directory.CreateDirectory(target);
            var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(junction);
            startInfo.ArgumentList.Add(target);
            using var process = Process.Start(startInfo)
                ?? throw new IOException("无法启动 mklink 测试进程。");
            Assert.True(process.WaitForExit(10_000), "创建目录联接超时。");
            Assert.Equal(0, process.ExitCode);

            var childDirectory = Path.Combine(junction, "child");
            Directory.CreateDirectory(childDirectory);
            var agentPath = Path.Combine(childDirectory, "DesktopTwin.Agent.exe");
            File.WriteAllBytes(agentPath, Array.Empty<byte>());

            var exception = Assert.Throws<IOException>(
                () => SessionAgentService.ValidateNotReparsePoint(agentPath, isDirectory: false));
            Assert.Contains("祖先目录是重解析点", exception.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction, recursive: false);
                }
            }
            catch (IOException)
            {
                // 清理失败不应掩盖真正的断言结果。
            }

            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void 隔离目录安装会加固临时与最终文件并跳过未变化内容()
    {
        if (!IsElevated())
        {
            // 设置所有者需要提升权限；非提升测试进程不做会失败的 DACL 夹具。
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var dataDirectory = Path.Combine(root, "Data");
            var agentDirectory = Path.Combine(root, "Agent");
            using var service = new SessionAgentService(
                Dispatcher.CurrentDispatcher,
                dataDirectory,
                agentDirectory,
                protectAgentDirectory: true);

            Assert.True(
                service.EnsureInstalled("Ctrl+Alt+U", null, out var firstError, updateStartupEntry: false),
                firstError);
            var agentPath = service.AgentPath;
            var original = File.ReadAllBytes(agentPath);
            AssertProtectedDirectory(root);
            AssertProtectedDirectory(agentDirectory);
            AssertProtectedFile(agentPath);
            Assert.False(File.Exists(agentPath + ".new"));
            Assert.False(File.Exists(agentPath + ".old"));

            var tampered = File.ReadAllBytes(agentPath);
            tampered[0] ^= 0xFF;
            File.WriteAllBytes(agentPath, tampered);
            Assert.True(
                service.EnsureInstalled("Ctrl+Alt+U", null, out var repairError, updateStartupEntry: false),
                repairError);
            Assert.Equal(original, File.ReadAllBytes(agentPath));
            AssertProtectedFile(agentPath);
            Assert.False(File.Exists(agentPath + ".new"));
            Assert.False(File.Exists(agentPath + ".old"));

            var timestamp = File.GetLastWriteTimeUtc(agentPath);
            Thread.Sleep(1100);
            Assert.True(
                service.EnsureInstalled("Ctrl+Alt+U", null, out var unchangedError, updateStartupEntry: false),
                unchangedError);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(agentPath));
            AssertProtectedFile(agentPath);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void 旧版AppData代理启动命令只做精确目标匹配()
    {
        var legacy = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin Legacy",
            "DesktopTwin.Agent.exe");

        Assert.True(SessionAgentService.IsLegacyAgentCommand(
            $"\"{legacy}\" --hotkey Alt+Q",
            legacy));
        Assert.True(SessionAgentService.IsLegacyAgentCommand(
            legacy,
            legacy));
        Assert.True(SessionAgentService.IsLegacyAgentCommand(
            $"\"{legacy}\"",
            legacy));
        Assert.False(SessionAgentService.IsLegacyAgentCommand(
            $"\"{legacy}",
            legacy));
        Assert.False(SessionAgentService.IsLegacyAgentCommand(
            $"\"{legacy}.bak\" --hotkey Alt+Q",
            legacy));
        Assert.False(SessionAgentService.IsLegacyAgentCommand(
            $"\"{legacy}\"suffix",
            legacy));
        Assert.False(SessionAgentService.IsLegacyAgentCommand(
            $"powershell.exe -File \"{legacy}\"",
            legacy));
        Assert.False(SessionAgentService.IsLegacyAgentCommand(
            "\"C:\\Temp\\DesktopTwin.Agent.exe\" --hotkey Alt+Q",
            legacy));
    }

    [Fact]
    public void Run值只有精确指向当前或旧版代理才允许删除()
    {
        const string current = @"C:\ProgramData\DesktopTwin\Agent\DesktopTwin.Agent.exe";
        const string legacy = @"C:\Users\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe";

        Assert.True(SessionAgentService.IsManagedAgentRunValue(
            $"\"{current}\" --hotkey Alt+Q",
            current,
            legacy));
        Assert.True(SessionAgentService.IsManagedAgentRunValue(
            $"\"{legacy}\" --hotkey Alt+Q",
            current,
            legacy));
        Assert.True(SessionAgentService.IsManagedAgentRunValue(current, current, legacy));
        Assert.False(SessionAgentService.IsManagedAgentRunValue(
            "\"C:\\Other\\DesktopTwin.Agent.exe\" --hotkey Alt+Q",
            current,
            legacy));
        Assert.False(SessionAgentService.IsManagedAgentRunValue(
            $"\"{current}.bak\" --hotkey Alt+Q",
            current,
            legacy));
        Assert.False(SessionAgentService.IsManagedAgentRunValue(
            $"\"{current}\"suffix",
            current,
            legacy));
        Assert.False(SessionAgentService.IsManagedAgentRunValue(
            $"powershell.exe -File \"{current}\"",
            current,
            legacy));
        Assert.False(SessionAgentService.IsManagedAgentRunValue(
            $"\"{current}",
            current,
            legacy));
    }

    [Fact]
    public void 卸载删除登录任务失败时保留代理文件()
    {
        var fixture = CreateUninstallFixture();
        fixture.Registration.LoginTaskDeleted = false;
        using var service = fixture.Service;

        Assert.False(service.Uninstall());

        Assert.Equal(1, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(0, fixture.Registration.RunDeleteCalls);
        Assert.True(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void 卸载遇到无法证明归属的Run值时失败关闭并保留代理文件()
    {
        var fixture = CreateUninstallFixture();
        fixture.Registration.RunDeleteException =
            new InvalidDataException("拒绝删除不属于本产品的 Run 值");
        using var service = fixture.Service;

        Assert.False(service.Uninstall());

        Assert.Equal(1, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(1, fixture.Registration.RunDeleteCalls);
        Assert.True(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void 卸载删除Run失败时保留代理文件()
    {
        var fixture = CreateUninstallFixture();
        fixture.Registration.RunDeleteException = new UnauthorizedAccessException("拒绝访问");
        using var service = fixture.Service;

        Assert.False(service.Uninstall());

        Assert.Equal(1, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(1, fixture.Registration.RunDeleteCalls);
        Assert.True(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void 卸载成功删除任务和Run后才清理代理文件()
    {
        var fixture = CreateUninstallFixture();
        using var service = fixture.Service;

        Assert.True(service.Uninstall());

        Assert.Equal(0, fixture.StopCalls());
        Assert.Equal(1, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(1, fixture.Registration.RunDeleteCalls);
        Assert.Null(fixture.Registration.ExpectedCommandLine);
        Assert.False(File.Exists(fixture.AgentPath));
        Assert.False(File.Exists(fixture.LegacyAgentPath));
        Assert.False(File.Exists(fixture.RequestPath));
        Assert.False(File.Exists(fixture.HeartbeatPath));
        Assert.False(File.Exists(fixture.LogPath));
        Assert.False(File.Exists(fixture.ShellGatePath));
        Assert.False(File.Exists(fixture.OldShellGatePath));
    }

    [Fact]
    public void 保留门卫参数可删除代理但不删除外壳门卫()
    {
        var fixture = CreateUninstallFixture();
        using var service = fixture.Service;

        Assert.True(service.Uninstall(removeShellGate: false));

        Assert.False(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.ShellGatePath));
        Assert.True(File.Exists(fixture.OldShellGatePath));
    }

    [Fact]
    public void 卸载删除代理文件失败时返回失败且不清理剩余状态()
    {
        var fixture = CreateUninstallFixture();
        File.Delete(fixture.AgentPath);
        Directory.CreateDirectory(fixture.AgentPath);
        using var service = fixture.Service;

        Assert.False(service.Uninstall());

        Assert.Equal(1, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(1, fixture.Registration.RunDeleteCalls);
        Assert.True(Directory.Exists(fixture.AgentPath));
        Assert.False(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void 隔离期间卸载停止旧代理但不得触碰启动项或代理文件()
    {
        var fixture = CreateUninstallFixture();
        using var service = fixture.Service;

        Assert.True(service.Uninstall(removeStartupEntry: false));

        Assert.Equal(1, fixture.StopCalls());
        Assert.Equal(0, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(0, fixture.Registration.RunDeleteCalls);
        Assert.True(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
        Assert.True(File.Exists(fixture.ShellGatePath));
        Assert.True(File.Exists(fixture.OldShellGatePath));
    }

    [Fact]
    public void 隔离期间停止旧代理失败时保留启动项代理文件和待重试状态()
    {
        var fixture = CreateUninstallFixture(
            new SessionAgentService.AgentStopResult(false, "无法证明会话身份"));
        using var service = fixture.Service;

        Assert.False(service.Uninstall(removeStartupEntry: false));

        Assert.Equal(1, fixture.StopCalls());
        Assert.Equal(0, fixture.Registration.LoginTaskDeleteCalls);
        Assert.Equal(0, fixture.Registration.RunDeleteCalls);
        Assert.True(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.LegacyAgentPath));
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.True(File.Exists(fixture.HeartbeatPath));
        Assert.True(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void 隔离期间完全冻结代理自启态()
    {
        var managedRun = SessionAgentService.PlanStartupSynchronization(
            updateStartupEntry: false,
            isManagedRun: true,
            manageLegacyDefaultInstallation: true);
        Assert.False(managedRun.CreateOrUpdateLoginTask);
        Assert.False(managedRun.RemoveRunValue);
        Assert.False(managedRun.DeleteLegacyAgentFile);

        var unrecognizedRun = SessionAgentService.PlanStartupSynchronization(
            updateStartupEntry: false,
            isManagedRun: false,
            manageLegacyDefaultInstallation: true);
        Assert.False(unrecognizedRun.CreateOrUpdateLoginTask);
        Assert.False(unrecognizedRun.RemoveRunValue);
        Assert.False(unrecognizedRun.DeleteLegacyAgentFile);
    }

    [Fact]
    public void 正常安装先注册登录任务并删除旧版Run()
    {
        var existingRun = SessionAgentService.PlanStartupSynchronization(
            updateStartupEntry: true,
            isManagedRun: true,
            manageLegacyDefaultInstallation: true);
        Assert.True(existingRun.CreateOrUpdateLoginTask);
        Assert.True(existingRun.RemoveRunValue);
        Assert.True(existingRun.DeleteLegacyAgentFile);

        var absent = SessionAgentService.PlanStartupSynchronization(
            updateStartupEntry: true,
            isManagedRun: false,
            manageLegacyDefaultInstallation: true);
        Assert.True(absent.CreateOrUpdateLoginTask);
        Assert.False(absent.RemoveRunValue);
        Assert.True(absent.DeleteLegacyAgentFile);
    }

    [Fact]
    public void 测试布局绝不创建计划任务或清理真实旧文件()
    {
        var plan = SessionAgentService.PlanStartupSynchronization(
            updateStartupEntry: true,
            isManagedRun: false,
            manageLegacyDefaultInstallation: false);

        Assert.False(plan.CreateOrUpdateLoginTask);
        Assert.False(plan.RemoveRunValue);
        Assert.False(plan.DeleteLegacyAgentFile);
    }

    [Fact]
    public void 代理DACL出现仅继承规则时必须失败()
    {
        var rules = BuildDirectoryRules(
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.InheritOnly,
                AccessControlType.Allow));

        var exception = Assert.Throws<IOException>(
            () => SessionAgentService.ValidateAccessRules(rules));

        Assert.Contains("继承", exception.Message);
    }

    [Fact]
    public async Task 线程池安装会由Dispatcher启动轮询并在释放时停止()
    {
        var dispatcherThread = StartDispatcherThread();
        var root = CreateTempDirectory();
        try
        {
            var service = new SessionAgentService(
                dispatcherThread.Dispatcher,
                dataDirectory: root,
                agentDirectory: root,
                protectAgentDirectory: false,
                manageLegacyDefaultInstallation: false,
                startupRegistration: new FakeStartupRegistration());

            var installed = await Task.Run(() =>
            {
                var result = service.EnsureInstalled(
                    "Ctrl+Alt+U",
                    controlPipe: null,
                    out var error,
                    updateStartupEntry: false);
                return (result, error);
            });

            Assert.True(installed.result, installed.error);
            var timerField = typeof(SessionAgentService).GetField(
                "_pollTimer",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(timerField);
            Assert.NotNull(await dispatcherThread.Dispatcher.InvokeAsync(
                () => timerField.GetValue(service),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle));

            Assert.True(await Task.Run(
                () => service.Uninstall(
                    removeStartupEntry: true,
                    removeShellGate: false)));
            Assert.Null(await dispatcherThread.Dispatcher.InvokeAsync(
                () => timerField.GetValue(service),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle));

            Assert.True(await Task.Run(() =>
                service.EnsureInstalled(
                    "Ctrl+Alt+U",
                    controlPipe: null,
                    out var error,
                    updateStartupEntry: false)));
            Assert.NotNull(await dispatcherThread.Dispatcher.InvokeAsync(
                () => timerField.GetValue(service),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle));

            await Task.Run(service.Dispose);
            Assert.Null(await dispatcherThread.Dispatcher.InvokeAsync(
                () => timerField.GetValue(service),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle));
        }
        finally
        {
            dispatcherThread.Shutdown();
            DeleteTempDirectory(root);
        }

        dispatcherThread.ThrowIfFailed();
    }

    [Fact]
    public void Dispatcher已关闭时释放服务不会抛出()
    {
        var dispatcherThread = StartDispatcherThread();
        var root = CreateTempDirectory();
        try
        {
            var service = new SessionAgentService(
                dispatcherThread.Dispatcher,
                dataDirectory: root,
                agentDirectory: root,
                protectAgentDirectory: false,
                manageLegacyDefaultInstallation: false,
                startupRegistration: new FakeStartupRegistration());

            dispatcherThread.Shutdown();
            service.Dispose();
        }
        finally
        {
            dispatcherThread.Shutdown();
            DeleteTempDirectory(root);
        }

        dispatcherThread.ThrowIfFailed();
    }

    private static DispatcherThreadFixture StartDispatcherThread()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                failure = exception;
                ready.Set();
            }
            finally
            {
                stopped.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Dispatcher 线程未能启动。");
        Assert.NotNull(dispatcher);
        return new DispatcherThreadFixture(dispatcher!, thread, stopped, () => failure);
    }

    private sealed class DispatcherThreadFixture
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stopped;
        private readonly Func<Exception?> _failure;
        private bool _shutdownRequested;

        internal DispatcherThreadFixture(
            Dispatcher dispatcher,
            Thread thread,
            ManualResetEventSlim stopped,
            Func<Exception?> failure)
        {
            Dispatcher = dispatcher;
            _thread = thread;
            _stopped = stopped;
            _failure = failure;
        }

        internal Dispatcher Dispatcher { get; }

        internal void Shutdown()
        {
            if (!_shutdownRequested)
            {
                _shutdownRequested = true;
                Dispatcher.InvokeShutdown();
            }

            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "Dispatcher 线程未能退出。");
        }

        internal void ThrowIfFailed()
        {
            var exception = _failure();
            if (exception is not null)
            {
                throw new InvalidOperationException("Dispatcher 线程失败。", exception);
            }
        }
    }

    private static UninstallFixture CreateUninstallFixture(
        SessionAgentService.AgentStopResult? stopResult = null)
    {
        var stopCalls = 0;
        var root = CreateTempDirectory();
        Directory.CreateDirectory(root);
        var legacyDirectory = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyDirectory);

        var agentPath = Path.Combine(root, SessionAgentService.AgentFileName);
        var legacyAgentPath = Path.Combine(
            legacyDirectory,
            SessionAgentService.AgentFileName);
        var requestPath = Path.Combine(root, "uu-switch-request.json");
        var heartbeatPath = Path.Combine(root, "session-agent-heartbeat.txt");
        var logPath = Path.Combine(root, "session-agent.log");
        var shellGatePath = Path.Combine(root, SessionAgentService.ShellGateFileName);
        var oldShellGatePath = shellGatePath + ".old";
        foreach (var path in new[]
                 {
                     agentPath,
                     legacyAgentPath,
                     requestPath,
                     heartbeatPath,
                     logPath,
                     shellGatePath,
                     oldShellGatePath
                 })
        {
            File.WriteAllText(path, "fixture");
        }

        var registration = new FakeStartupRegistration();
        var service = new SessionAgentService(
            Dispatcher.CurrentDispatcher,
            dataDirectory: root,
            agentDirectory: root,
            protectAgentDirectory: false,
            manageLegacyDefaultInstallation: true,
            legacyAgentPath: legacyAgentPath,
            startupRegistration: registration,
            stopIsolatedAgent: () =>
            {
                stopCalls++;
                return stopResult ?? new SessionAgentService.AgentStopResult(true, string.Empty);
            },
            shellGatePath: shellGatePath);
        return new UninstallFixture(
            service,
            registration,
            agentPath,
            legacyAgentPath,
            requestPath,
            heartbeatPath,
            logPath,
            shellGatePath,
            oldShellGatePath,
            () => stopCalls);
    }

    private readonly record struct UninstallFixture(
        SessionAgentService Service,
        FakeStartupRegistration Registration,
        string AgentPath,
        string LegacyAgentPath,
        string RequestPath,
        string HeartbeatPath,
        string LogPath,
        string ShellGatePath,
        string OldShellGatePath,
        Func<int> StopCalls);

    private sealed class FakeStartupRegistration
        : SessionAgentService.ISessionAgentStartupRegistration
    {
        public bool LoginTaskDeleted { get; set; } = true;

        public Exception? RunDeleteException { get; set; }

        public int LoginTaskDeleteCalls { get; private set; }

        public int RunDeleteCalls { get; private set; }

        public string? ExpectedCommandLine { get; private set; }

        public bool TryDeleteLogonTask(out string error)
        {
            LoginTaskDeleteCalls++;
            error = LoginTaskDeleted ? string.Empty : "模拟任务删除失败";
            return LoginTaskDeleted;
        }

        public void DeleteRunValue(string? expectedCommandLine)
        {
            RunDeleteCalls++;
            ExpectedCommandLine = expectedCommandLine;
            if (RunDeleteException is not null)
            {
                throw RunDeleteException;
            }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string CreateTempDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin.Tests." + Guid.NewGuid().ToString("N"));

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void AssertProtectedDirectory(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            security.GetOwner(typeof(SecurityIdentifier)));
        SessionAgentService.ValidateAccessRules(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)));
    }

    private static void AssertProtectedFile(string path)
    {
        var security = new FileSecurity(path, AccessControlSections.Owner | AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            security.GetOwner(typeof(SecurityIdentifier)));
        SessionAgentService.ValidateAccessRules(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)));
    }

    private static FileSystemAccessRule Allow(
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        return new FileSystemAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            AccessControlType.Allow);
    }

    private static AuthorizationRuleCollection BuildRules(
        params FileSystemAccessRule[] accessRules)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in accessRules)
        {
            security.AddAccessRule(rule);
        }

        return security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));
    }

    private static AuthorizationRuleCollection BuildDirectoryRules(
        params FileSystemAccessRule[] accessRules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in accessRules)
        {
            security.AddAccessRule(rule);
        }

        return security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));
    }
}
