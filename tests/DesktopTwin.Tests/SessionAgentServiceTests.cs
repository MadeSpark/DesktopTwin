using System;
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
}
