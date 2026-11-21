using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace DesktopTwin.Tests;

/// <summary>
/// SessionAgent 命令行解析契约测试。
/// SessionAgent 是框架 csc(C# 5)编译的独立 exe,以 LogicalName "DesktopTwin.Agent.exe"
/// 作为嵌入资源打进 DesktopTwin 程序集。这里用反射从该资源加载 exe,
/// 调用其中的 DesktopTwinSessionAgent.SessionAgentArguments.Parse 纯函数,
/// 不启动真实代理进程。
/// </summary>
public sealed class SessionAgentArgumentsTests
{
    private static Assembly LoadAgentAssembly()
    {
        // 先试 obj 中间产物:DesktopTwin.Tests.dll 位于
        // <root>\tests\DesktopTwin.Tests\bin\<cfg>\<tfm>\,上溯 5 级到仓库根。
        var twinAssembly = typeof(RdpInputIsolationTests).Assembly;
        var repoRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(twinAssembly.Location)!,
            "..", "..", "..", "..", ".."));
        var candidate = Path.Combine(
            repoRoot, "src", "SessionAgent", "obj", "Release", "DesktopTwin.Agent.exe");
        if (File.Exists(candidate))
        {
            return Assembly.Load(File.ReadAllBytes(candidate));
        }

        // 回退:DesktopTwin 嵌入资源 DesktopTwin.Agent.exe(LogicalName)。
        foreach (var name in twinAssembly.GetManifestResourceNames())
        {
            if (name.EndsWith("DesktopTwin.Agent.exe", StringComparison.Ordinal))
            {
                using var stream = twinAssembly.GetManifestResourceStream(name);
                Assert.NotNull(stream);
                using var ms = new MemoryStream();
                stream!.CopyTo(ms);
                return Assembly.Load(ms.ToArray());
            }
        }

        // 都没找到:同时报告两条路径便于诊断。
        Assert.Fail(
            "DesktopTwin.Agent.exe not found at " + candidate
            + " nor embedded in DesktopTwin.dll (resources: "
            + string.Join(", ", twinAssembly.GetManifestResourceNames()) + ")");
        return null!;
    }

    private static object ParseArguments(string[] args)
    {
        var agent = LoadAgentAssembly();
        var parserType = agent.GetType("DesktopTwinSessionAgent.SessionAgentArguments")
            ?? throw new InvalidOperationException(
                "DesktopTwinSessionAgent.SessionAgentArguments not found in agent assembly");
        var parse = parserType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Parse method not found");
        return parse.Invoke(null, new object[] { args })!;
    }

    private static ParsedArgs Read(object parsed)
    {
        // SessionAgentArguments 与其字段均为 internal,跨程序集反射要用 NonPublic。
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = parsed.GetType();
        return new ParsedArgs
        {
            IsValid = (bool)t.GetField("IsValid", Fields)!.GetValue(parsed)!,
            ErrorMessage = (string?)t.GetField("ErrorMessage", Fields)!.GetValue(parsed),
            ControlPipe = (string?)t.GetField("ControlPipe", Fields)!.GetValue(parsed),
            HostSessionId = (int)t.GetField("HostSessionId", Fields)!.GetValue(parsed)!,
            NoHotkey = (bool)t.GetField("NoHotkey", Fields)!.GetValue(parsed)!,
            Hotkey = (string?)t.GetField("Hotkey", Fields)!.GetValue(parsed),
            TestMode = (bool)t.GetField("TestMode", Fields)!.GetValue(parsed)!,
        };
    }

    private sealed class ParsedArgs
    {
        public bool IsValid;
        public string? ErrorMessage;
        public string? ControlPipe;
        public int HostSessionId;
        public bool NoHotkey;
        public string? Hotkey;
        public bool TestMode;
    }

    [Fact]
    public void Empty_args_are_valid_with_defaults()
    {
        var r = Read(ParseArguments(Array.Empty<string>()));
        Assert.True(r.IsValid);
        Assert.Null(r.ControlPipe);
        Assert.Equal(0, r.HostSessionId);
        Assert.False(r.NoHotkey);
        Assert.Equal("Ctrl+Alt+U", r.Hotkey);
        Assert.False(r.TestMode);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void Canonical_pipe_session_args_parse()
    {
        var r = Read(ParseArguments(new[]
        {
            "--no-hotkey", "--control-pipe", "desktop-twin-session-2", "--host-session", "2"
        }));
        Assert.True(r.IsValid);
        Assert.Equal("desktop-twin-session-2", r.ControlPipe);
        Assert.Equal(2, r.HostSessionId);
        Assert.True(r.NoHotkey);
    }

    [Fact]
    public void Control_pipe_without_value_fails()
    {
        var r = Read(ParseArguments(new[] { "--control-pipe" }));
        Assert.False(r.IsValid);
        Assert.Contains("--control-pipe", r.ErrorMessage);
    }

    [Fact]
    public void Control_pipe_with_empty_value_fails()
    {
        var r = Read(ParseArguments(new[] { "--control-pipe", "", "--host-session", "2" }));
        Assert.False(r.IsValid);
        Assert.Contains("--control-pipe", r.ErrorMessage);
    }

    [Fact]
    public void Control_pipe_consuming_next_option_fails()
    {
        // 回归点:--control-pipe --host-session 2 不得把 --host-session 当成管道名。
        var r = Read(ParseArguments(new[] { "--control-pipe", "--host-session", "2" }));
        Assert.False(r.IsValid);
        Assert.Contains("--control-pipe", r.ErrorMessage);
    }

    [Fact]
    public void Control_pipe_consuming_flag_value_fails()
    {
        var r = Read(ParseArguments(new[] { "--control-pipe", "--no-hotkey" }));
        Assert.False(r.IsValid);
        Assert.Contains("--control-pipe", r.ErrorMessage);
    }

    [Fact]
    public void Explicit_host_session_without_pipe_still_validates_bad_values()
    {
        // 显式 host-session 无 pipe:0/负数/非整数/溢出都要拒绝,悬空缺值也要拒绝。
        foreach (var bad in new[] { "0", "-1", "abc", "99999999999" })
        {
            var r = Read(ParseArguments(new[] { "--host-session", bad }));
            Assert.False(r.IsValid);
            Assert.Contains("--host-session", r.ErrorMessage);
        }

        var dangling = Read(ParseArguments(new[] { "--host-session" }));
        Assert.False(dangling.IsValid);
        Assert.Contains("--host-session", dangling.ErrorMessage);
    }

    [Fact]
    public void Explicit_valid_host_session_without_pipe_is_ok()
    {
        var r = Read(ParseArguments(new[] { "--host-session", "5" }));
        Assert.True(r.IsValid);
        Assert.Equal(5, r.HostSessionId);
        Assert.Null(r.ControlPipe);
    }

    [Fact]
    public void Pipe_without_host_session_fails()
    {
        var r = Read(ParseArguments(new[] { "--control-pipe", "some-pipe" }));
        Assert.False(r.IsValid);
        Assert.Contains("--host-session", r.ErrorMessage);
    }

    [Fact]
    public void Pipe_with_zero_or_negative_session_fails()
    {
        foreach (var bad in new[] { "0", "-3" })
        {
            var r = Read(ParseArguments(new[]
            {
                "--control-pipe", "pipe", "--host-session", bad
            }));
            Assert.False(r.IsValid);
            Assert.Contains("--host-session", r.ErrorMessage);
        }
    }

    [Fact]
    public void Hotkey_explicit_value_parsed()
    {
        var r = Read(ParseArguments(new[] { "--hotkey", "Ctrl+Q" }));
        Assert.True(r.IsValid);
        Assert.Equal("Ctrl+Q", r.Hotkey);
    }

    [Fact]
    public void Test_flag_parsed()
    {
        var r = Read(ParseArguments(new[] { "--test", "--no-hotkey" }));
        Assert.True(r.IsValid);
        Assert.True(r.TestMode);
        Assert.True(r.NoHotkey);
    }
}
