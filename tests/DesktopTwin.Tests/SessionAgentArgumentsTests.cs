using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopTwin.Services;
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
        // Agent.exe 是 DesktopTwin.dll 的嵌入资源，不是测试程序集的资源。
        // 之前误用测试程序集，永远读不到新资源，随后悄悄回退到旧中间产物。
        var twinAssembly = typeof(SessionAgentService).Assembly;

        // 只能读取本次构建嵌入 DesktopTwin.dll 的代理。绝不能回退到
        // src/SessionAgent/obj 下的历史产物，否则测试会悄悄验证旧 exe，
        // 与当前 SessionAgent 源码脱节。
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

        Assert.Fail(
            "DesktopTwin.Agent.exe 未嵌入当前 DesktopTwin.dll；资源："
            + string.Join(", ", twinAssembly.GetManifestResourceNames()));
        return null!;
    }

    [Fact]
    public void 托管代理参数经构建器与解析器往返仍保留精确身份()
    {
        var logonTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var commandLine = SessionAgentService.BuildAgentArguments(
            hotkey: null,
            controlPipe: null,
            hostSessionId: 0,
            expectedSessionId: 7u,
            expectedLogonTimeUtc: logonTime);

        var parsed = Read(ParseArguments(SplitCommandLine(commandLine)));

        Assert.True(parsed.IsValid);
        Assert.True(parsed.NoHotkey);
        Assert.True(parsed.HasExpectedSessionIdentity);
        Assert.Equal(7, parsed.ExpectedSessionId);
        Assert.Equal(logonTime.ToFileTimeUtc(), parsed.ExpectedLogonTimeFileTimeUtc);
        Assert.Null(parsed.ControlPipe);
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        Assert.False(quoted, "测试命令行引号必须闭合");
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.ToArray();
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

    private static bool IsExpectedSessionIdentity(
        int expectedSessionId,
        long expectedLogonTime,
        int actualSessionId,
        bool hasActualLogonTime,
        long actualLogonTime)
    {
        var agent = LoadAgentAssembly();
        var programType = agent.GetType("DesktopTwinSessionAgent.Program")
            ?? throw new InvalidOperationException(
                "DesktopTwinSessionAgent.Program not found in agent assembly");
        var method = programType.GetMethod(
                "IsExpectedSessionIdentity",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsExpectedSessionIdentity method not found");
        return (bool)method.Invoke(null, new object[]
        {
            expectedSessionId,
            expectedLogonTime,
            actualSessionId,
            hasActualLogonTime,
            actualLogonTime
        })!;
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
            ExpectedSessionId = (int)t.GetField("ExpectedSessionId", Fields)!.GetValue(parsed)!,
            ExpectedLogonTimeFileTimeUtc =
                (long)t.GetField("ExpectedLogonTimeFileTimeUtc", Fields)!.GetValue(parsed)!,
            HasExpectedSessionIdentity =
                (bool)t.GetField("HasExpectedSessionIdentity", Fields)!.GetValue(parsed)!,
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
        public int ExpectedSessionId;
        public long ExpectedLogonTimeFileTimeUtc;
        public bool HasExpectedSessionIdentity;
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
        Assert.False(r.HasExpectedSessionIdentity);
    }

    [Fact]
    public void 显式启动的预期会话身份成对解析()
    {
        var r = Read(ParseArguments(new[]
        {
            "--no-hotkey",
            "--control-pipe", "desktop-twin-session-2",
            "--host-session", "1",
            "--expected-session", "2",
            "--expected-logon-time", "133500000000000000"
        }));

        Assert.True(r.IsValid);
        Assert.True(r.HasExpectedSessionIdentity);
        Assert.Equal(2, r.ExpectedSessionId);
        Assert.Equal(133500000000000000L, r.ExpectedLogonTimeFileTimeUtc);
    }

    [Fact]
    public void 代理只接受会话号和登录时间精确匹配()
    {
        Assert.True(IsExpectedSessionIdentity(
            7, 133500000000000000L, 7, true, 133500000000000000L));
        Assert.False(IsExpectedSessionIdentity(
            7, 133500000000000000L, 8, true, 133500000000000000L));
        Assert.False(IsExpectedSessionIdentity(
            7, 133500000000000000L, 7, false, 0));
        Assert.False(IsExpectedSessionIdentity(
            7, 133500000000000000L, 7, true, 133500000000000001L));
        Assert.False(IsExpectedSessionIdentity(
            0, 133500000000000000L, 0, true, 133500000000000000L));
    }

    [Fact]
    public void 预期会话身份必须成对且为正数()
    {
        var missingLogonTime = Read(ParseArguments(new[]
        {
            "--control-pipe", "pipe", "--host-session", "1",
            "--expected-session", "2"
        }));
        Assert.False(missingLogonTime.IsValid);
        Assert.Contains("同时提供", missingLogonTime.ErrorMessage);

        foreach (var malformed in new[]
                 {
                     new[] { "0", "133500000000000000" },
                     new[] { "2", "0" },
                     new[] { "2", "not-a-filetime" }
                 })
        {
            var r = Read(ParseArguments(new[]
            {
                "--control-pipe", "pipe", "--host-session", "1",
                "--expected-session", malformed[0],
                "--expected-logon-time", malformed[1]
            }));
            Assert.False(r.IsValid);
            Assert.Contains("预期会话身份", r.ErrorMessage);
        }
    }

    [Fact]
    public void 热键only的托管启动允许携带预期会话身份()
    {
        var r = Read(ParseArguments(new[]
        {
            "--no-hotkey",
            "--expected-session", "2",
            "--expected-logon-time", "133500000000000000"
        }));
        Assert.True(r.IsValid);
        Assert.True(r.HasExpectedSessionIdentity);
        Assert.Equal(2, r.ExpectedSessionId);
        Assert.Equal(133500000000000000L, r.ExpectedLogonTimeFileTimeUtc);
        Assert.Null(r.ControlPipe);
    }

    [Fact]
    public void 所有选项必须严格数字且拒绝空白与正号()
    {
        foreach (var badHost in new[] { " 1", "1 ", "+1", "0x2" })
        {
            var r = Read(ParseArguments(new[] { "--host-session", badHost }));
            Assert.False(r.IsValid);
            Assert.Contains("--host-session", r.ErrorMessage);
        }

        foreach (var badIdentity in new[]
                 {
                     new[] { " 2", "133500000000000000" },
                     new[] { "2", "+133500000000000000" },
                     new[] { "2", "133500000000000000 " }
                 })
        {
            var r = Read(ParseArguments(new[]
            {
                "--no-hotkey",
                "--expected-session", badIdentity[0],
                "--expected-logon-time", badIdentity[1]
            }));
            Assert.False(r.IsValid);
            Assert.Contains("预期会话身份", r.ErrorMessage);
        }
    }

    [Fact]
    public void 裸位置参数和空白选项值必须拒绝()
    {
        var positional = Read(ParseArguments(new[] { "--no-hotkey", "not-an-option" }));
        Assert.False(positional.IsValid);
        Assert.Contains("位置参数", positional.ErrorMessage);

        var whitespacePipe = Read(ParseArguments(new[]
        {
            "--control-pipe", "   ", "--host-session", "1"
        }));
        Assert.False(whitespacePipe.IsValid);
        Assert.Contains("--control-pipe", whitespacePipe.ErrorMessage);
    }

    [Fact]
    public void 互斥热键选项同时出现必须拒绝()
    {
        var r = Read(ParseArguments(new[]
        {
            "--hotkey", "Ctrl+Q", "--no-hotkey"
        }));
        Assert.False(r.IsValid);
        Assert.Contains("不能同时", r.ErrorMessage);
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
