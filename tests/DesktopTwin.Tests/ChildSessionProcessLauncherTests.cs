using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 临时计划任务清扫的误删边界测试。
///
/// 所有用例都通过 <see cref="ChildSessionProcessLauncher.IOrphanTaskFolder"/> 注入，
/// 不启动 Schedule.Service，也不读取或修改系统里的真实任务。
/// </summary>
public sealed class ChildSessionProcessLauncherTests
{
    private const string LaunchTaskName =
        "DesktopTwin-SessionLaunch-0123456789abcdef0123456789ABCDEF";

    private const string AgentTaskName =
        "DesktopTwin-SessionAgent-FEDCBA9876543210fedcba9876543210";

    private const string MatchingAuthor = "DesktopTwin";
    private const string MatchingDescription = "临时启动 target.exe 到分身会话 3";

    [Theory]
    [InlineData("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcdef", true)]
    [InlineData("DesktopTwin-SessionAgent-FEDCBA9876543210fedcba9876543210", true)]
    [InlineData("DesktopTwin-Backup", false)]
    [InlineData("DesktopTwin-SessionLaunch-NotGuid", false)]
    [InlineData("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcde", false)]
    [InlineData("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcdeg", false)]
    [InlineData("DesktopTwin-SessionLaunch-{0123456789abcdef0123456789abcdef}", false)]
    [InlineData("desktoptwin-sessionlaunch-0123456789abcdef0123456789abcdef", false)]
    [InlineData("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcdef-extra", false)]
    [InlineData("DesktopTwin-SessionAgent-Autostart", false)]
    public void 临时任务名必须是精确前缀和32位GUID(string taskName, bool expected)
    {
        Assert.Equal(expected, ChildSessionProcessLauncher.IsManagedTemporaryTaskName(taskName));
    }

    [Theory]
    [InlineData(MatchingAuthor, MatchingDescription, true)]
    [InlineData(MatchingAuthor, "临时启动 app name.exe 到分身会话 4294967295", true)]
    [InlineData(null, MatchingDescription, false)]
    [InlineData("SomeoneElse", MatchingDescription, false)]
    [InlineData("desktoptwin", MatchingDescription, false)]
    [InlineData(MatchingAuthor, null, false)]
    [InlineData(MatchingAuthor, "用户自定义任务", false)]
    [InlineData(MatchingAuthor, "临时启动 target.exe 到分身会话 x", false)]
    [InlineData(MatchingAuthor, "临时启动 target.exe 到分身会话 -1", false)]
    [InlineData(MatchingAuthor, "临时启动 target.exe 到分身会话 4294967296", false)]
    [InlineData(MatchingAuthor, "临时启动  到分身会话 3", false)]
    [InlineData(MatchingAuthor, "临时启动 target.exe 到分身会话 3 ", false)]
    public void 注册信息必须来自DesktopTwin且描述格式匹配(
        string? author,
        string? description,
        bool expected)
    {
        Assert.Equal(
            expected,
            ChildSessionProcessLauncher.HasExpectedTaskRegistrationInfo(author, description));
    }

    [Fact]
    public void 创建描述与清扫校验使用同一份常量格式()
    {
        var description = ChildSessionProcessLauncher.BuildTaskDescription(
            @"C:\Apps\demo.exe",
            7);

        Assert.Equal("DesktopTwin", ChildSessionProcessLauncher.TaskAuthor);
        Assert.Equal("临时启动 demo.exe 到分身会话 7", description);
        Assert.True(ChildSessionProcessLauncher.HasExpectedTaskRegistrationInfo(
            ChildSessionProcessLauncher.TaskAuthor,
            description));
    }

    [Fact]
    public void 持久登录任务使用固定名称且不会被临时任务清扫误删()
    {
        Assert.Equal(
            "DesktopTwin-SessionAgent-Autostart",
            ChildSessionProcessLauncher.SessionAgentStartupTaskName);
        Assert.False(ChildSessionProcessLauncher.IsManagedTemporaryTaskName(
            ChildSessionProcessLauncher.SessionAgentStartupTaskName));
    }

    [Fact]
    public void 持久登录任务只接受精确的作者和描述()
    {
        Assert.True(ChildSessionProcessLauncher.HasExpectedSessionAgentTaskRegistrationInfo(
            ChildSessionProcessLauncher.TaskAuthor,
            ChildSessionProcessLauncher.SessionAgentStartupTaskDescription));

        Assert.False(ChildSessionProcessLauncher.HasExpectedSessionAgentTaskRegistrationInfo(
            "SomeoneElse",
            ChildSessionProcessLauncher.SessionAgentStartupTaskDescription));
        Assert.False(ChildSessionProcessLauncher.HasExpectedSessionAgentTaskRegistrationInfo(
            ChildSessionProcessLauncher.TaskAuthor,
            "用户自定义任务"));
        Assert.False(ChildSessionProcessLauncher.HasExpectedSessionAgentTaskRegistrationInfo(
            ChildSessionProcessLauncher.TaskAuthor,
            ChildSessionProcessLauncher.SessionAgentStartupTaskDescription + " "));
    }

    [Fact]
    public void 清扫只删除名称和元数据都匹配的候选()
    {
        var folder = new FakeOrphanTaskFolder();
        folder.Add(LaunchTaskName);
        folder.Add(AgentTaskName);
        folder.Add("DesktopTwin-Backup");
        folder.Add("DesktopTwin-SessionLaunch-NotGuid");
        folder.Add("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcde");
        folder.Add("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcdeg");
        folder.Add("DesktopTwin-SessionLaunch-{0123456789abcdef0123456789abcdef}");
        folder.Add(
            "DesktopTwin-SessionLaunch-11111111111111111111111111111111",
            author: "SomeoneElse");
        folder.Add(
            "DesktopTwin-SessionAgent-22222222222222222222222222222222",
            description: "用户创建的任务");
        folder.Add(
            "DesktopTwin-SessionLaunch-33333333333333333333333333333333",
            readSucceeds: false);

        ChildSessionProcessLauncher.SweepOrphanTasks(folder);

        Assert.Equal(2, folder.DeletedTaskNames.Count);
        Assert.Contains(LaunchTaskName, folder.DeletedTaskNames);
        Assert.Contains(AgentTaskName, folder.DeletedTaskNames);
        Assert.DoesNotContain("DesktopTwin-Backup", folder.DeletedTaskNames);
        Assert.DoesNotContain(
            "DesktopTwin-SessionLaunch-11111111111111111111111111111111",
            folder.DeletedTaskNames);
        Assert.DoesNotContain(
            "DesktopTwin-SessionAgent-22222222222222222222222222222222",
            folder.DeletedTaskNames);
        Assert.DoesNotContain(
            "DesktopTwin-SessionLaunch-33333333333333333333333333333333",
            folder.DeletedTaskNames);
    }

    [Fact]
    public void 读取元数据失败或抛异常时不删除也不崩溃()
    {
        var folder = new FakeOrphanTaskFolder();
        folder.Add(
            "DesktopTwin-SessionLaunch-44444444444444444444444444444444",
            readSucceeds: false);
        folder.Add(
            "DesktopTwin-SessionAgent-55555555555555555555555555555555",
            readException: new UnauthorizedAccessException("模拟权限拒绝"));

        var exception = Record.Exception(() =>
            ChildSessionProcessLauncher.SweepOrphanTasks(folder));

        Assert.Null(exception);
        Assert.Empty(folder.DeletedTaskNames);
    }

    [Fact]
    public void 枚举失败时不删除也不崩溃()
    {
        var folder = new FakeOrphanTaskFolder
        {
            EnumerationException = new UnauthorizedAccessException("模拟权限拒绝")
        };
        folder.Add(LaunchTaskName);

        var exception = Record.Exception(() =>
            ChildSessionProcessLauncher.SweepOrphanTasks(folder));

        Assert.Null(exception);
        Assert.Empty(folder.DeletedTaskNames);
    }

    [Fact]
    public void 删除单个任务失败时不崩溃并继续处理其他候选()
    {
        const string firstTaskName =
            "DesktopTwin-SessionLaunch-66666666666666666666666666666666";
        var folder = new FakeOrphanTaskFolder();
        folder.Add(firstTaskName, deleteException: new COMException("模拟并发删除"));
        folder.Add(LaunchTaskName);

        var exception = Record.Exception(() =>
            ChildSessionProcessLauncher.SweepOrphanTasks(folder));

        Assert.Null(exception);
        Assert.Equal([LaunchTaskName], folder.DeletedTaskNames);
    }

    private sealed class FakeOrphanTaskFolder : ChildSessionProcessLauncher.IOrphanTaskFolder
    {
        private readonly List<FakeTask> _tasks = [];

        public List<string> DeletedTaskNames { get; } = [];

        public Exception? EnumerationException { get; init; }

        public void Add(
            string name,
            string? author = MatchingAuthor,
            string? description = MatchingDescription,
            bool readSucceeds = true,
            Exception? readException = null,
            Exception? deleteException = null)
        {
            _tasks.Add(new FakeTask(
                name,
                author,
                description,
                readSucceeds,
                readException,
                deleteException));
        }

        public IReadOnlyList<string> GetTaskNames()
        {
            if (EnumerationException is not null)
            {
                throw EnumerationException;
            }

            return _tasks.Select(task => task.Name).ToArray();
        }

        public bool TryGetRegistrationInfo(
            string taskName,
            out string? author,
            out string? description)
        {
            var task = Find(taskName);
            if (task.ReadException is not null)
            {
                throw task.ReadException;
            }

            author = task.Author;
            description = task.Description;
            return task.ReadSucceeds;
        }

        public void DeleteTask(string taskName)
        {
            var task = Find(taskName);
            if (task.DeleteException is not null)
            {
                throw task.DeleteException;
            }

            DeletedTaskNames.Add(taskName);
        }

        private FakeTask Find(string name)
        {
            return _tasks.Single(task => task.Name == name);
        }
    }

    private sealed record FakeTask(
        string Name,
        string? Author,
        string? Description,
        bool ReadSucceeds,
        Exception? ReadException,
        Exception? DeleteException);
}
