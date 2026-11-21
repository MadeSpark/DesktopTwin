using DesktopTwin.Services;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// 分身代理替换只允许命中「当前 WTS 子会话 + 规范化 AgentPath 精确相等」的进程。
/// 这些是纯路径/会话判断，不枚举、不终止任何真实进程。
/// </summary>
public sealed class SessionAgentServiceTests
{
    private const uint ChildSessionId = 7;
    private const string AgentPath =
        @"C:\Users\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe";

    [Fact]
    public void 同会话且完整路径精确相等时匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            ChildSessionId,
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
            ChildSessionId,
            processSessionId,
            AgentPath));
    }

    [Fact]
    public void 会话身份不可读时拒绝()
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            ChildSessionId,
            processSessionId: null,
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
            ChildSessionId,
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
            ChildSessionId,
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
            ChildSessionId,
            processSessionId: 7,
            processImagePath));
    }

    [Fact]
    public void 路径大小写按Windows语义不敏感匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            @"C:\USERS\Test\AppData\Roaming\DesktopTwin\DesktopTwin.Agent.exe",
            ChildSessionId,
            processSessionId: 7,
            processImagePath:
                @"c:\users\test\appdata\roaming\desktoptwin\DesktopTwin.Agent.exe"));
    }

    [Fact]
    public void 路径规范化后相等时匹配()
    {
        Assert.True(SessionAgentService.IsMatchingAgentProcess(
            @"C:\Users\Test\AppData\Local\..\Roaming\DesktopTwin\DesktopTwin.Agent.exe",
            ChildSessionId,
            processSessionId: 7,
            processImagePath:
                @"C:\Users\Test\AppData\Roaming\DesktopTwin\.\DesktopTwin.Agent.exe"));
    }

    [Fact]
    public void 目标会话必须为正数()
    {
        Assert.False(SessionAgentService.IsMatchingAgentProcess(
            AgentPath,
            childSessionId: 0,
            processSessionId: 0,
            processImagePath: AgentPath));
    }
}
