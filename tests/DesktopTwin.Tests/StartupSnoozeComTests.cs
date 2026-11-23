using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using DesktopTwin.Services;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace DesktopTwin.Tests;

/// <summary>
/// 计划任务 COM 迁移测试。除一个带唯一名称、仅操作 \DesktopTwin 临时文件夹的
/// 真实 COM 探针外，其余用例只使用可注入假客户端和临时目录；探针在 finally 中
/// 删除自己的任务与文件夹，不读取、禁用或删除任何其他系统任务。
/// </summary>
public sealed class StartupSnoozeComTests
{
    private const string TaskNamespace = ScheduledTaskXml.TaskNamespaceUri;
    private const string RelativeTaskPath = @"\ContosoStartup";
    private readonly ITestOutputHelper _output;

    public StartupSnoozeComTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void 合法任务XML才被判定为存在()
    {
        var xml = TaskXml(enabled: true);
        var result = StartupSnoozeService.ParseTaskXmlQueryOutput("\uFEFF" + xml);

        Assert.True(result.IsPresent);
        Assert.Equal(xml, result.Xml);
        Assert.False(result.IsAbsent);
        Assert.False(result.IsUnknown);

        Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput(null).IsUnknown);
        Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput(string.Empty).IsUnknown);
        Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput("<Task><Actions></Task>").IsUnknown);
        Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput(
            "<Task xmlns=\"urn:not-the-task-namespace\"><Actions /></Task>").IsUnknown);
    }

    [Fact]
    public void DTD任务XML不会越过严格解析边界()
    {
        var dtdXml = $"""
            <!DOCTYPE Task [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <Task xmlns="{TaskNamespace}">
              <Actions><Exec><Command>&xxe;</Command></Exec></Actions>
            </Task>
            """;

        Assert.True(StartupSnoozeService.ParseTaskXmlQueryOutput(dtdXml).IsUnknown);
    }

    [Fact]
    public void 失败查询只在明确不存在时才映射为Absent()
    {
        Assert.True(StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Absent).IsAbsent);
        Assert.True(StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Present,
            definitionFileConfirmedMissing: true).IsAbsent);

        Assert.True(StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Present).IsUnknown);
        Assert.True(StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Unknown).IsUnknown);
        Assert.True(StartupSnoozeService.InterpretFailedTaskXmlQuery(
            StartupSnoozeService.ScheduledTaskPresence.Unknown,
            definitionFileConfirmedMissing: true).IsUnknown);
    }

    [Fact]
    public void COM读取异常不会变成任务不存在()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true))
            {
                ReadException = new IOException("fake COM failure")
            };
            try
            {
                var service = CreateService(directory, fake);
                var result = InvokeQueryTaskXml(service, RelativeTaskPath);

                Assert.True(result.IsUnknown);
                Assert.False(result.IsAbsent);
                Assert.False(result.IsPresent);
                Assert.Equal(1, fake.ReadCalls);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void COM权限异常会让恢复保留日志并失败关闭()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: false))
            {
                ReadException = new UnauthorizedAccessException("fake access denied")
            };
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: false), confirmedDisabled: true);
                WriteJournal(service, journal);

                var result = service.Restore();

                Assert.False(result.Success);
                Assert.True(File.Exists(service.JournalPath));
                var retained = ReadJournal(service);
                Assert.Single(retained.Tasks);
                Assert.Empty(fake.WriteCalls);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 禁用回读成功后才记录已确认关闭()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true));
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: true));
                var warnings = new List<string>();
                var blocked = new List<string>();

                InvokeApplyScheduledTasks(service, journal, warnings, blocked);

                var task = Assert.Single(journal.Tasks);
                Assert.True(task.WriteAttempted);
                Assert.True(task.ConfirmedDisabled);
                Assert.Empty(blocked);
                Assert.Single(fake.WriteCalls);
                Assert.False(fake.WriteCalls[0].Enabled);
                Assert.Equal(task.Identity, fake.WriteCalls[0].ExpectedIdentity);
                // 关停前快照与写前复核各一次；SetEnabled 已经完成最终回读，
                // 不得再按任务串行轮询并阻塞启动路径。
                Assert.Equal(2, fake.ReadCalls);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 禁用回读失败时保留恢复标记并阻断连接()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true))
            {
                WriteResultFactory = (_, _, _) =>
                    ScheduledTaskComWriteResult.ReadBackFailed("fake read-back failure")
            };
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: true));
                var warnings = new List<string>();
                var blocked = new List<string>();

                InvokeApplyScheduledTasks(service, journal, warnings, blocked);

                var task = Assert.Single(journal.Tasks);
                Assert.True(task.WriteAttempted);
                Assert.False(task.ConfirmedDisabled);
                Assert.Single(blocked);
                Assert.Contains("无法回读", warnings[0]);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 外部已禁用不会被记成本程序写入()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true))
            {
                WriteResultFactory = (_, _, _) =>
                    ScheduledTaskComWriteResult.AlreadyInDesiredState(enabled: false)
            };
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: true));
                var warnings = new List<string>();
                var blocked = new List<string>();

                InvokeApplyScheduledTasks(service, journal, warnings, blocked);

                var task = Assert.Single(journal.Tasks);
                Assert.False(task.WriteAttempted);
                Assert.False(task.ConfirmedDisabled);
                Assert.Empty(blocked);
                Assert.Contains("外部禁用", warnings[0]);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 写入前身份变化时拒绝禁用并阻断连接()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true))
            {
                WriteResultFactory = (_, _, _) =>
                    ScheduledTaskComWriteResult.IdentityChanged("fake identity changed")
            };
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: true));
                var warnings = new List<string>();
                var blocked = new List<string>();

                InvokeApplyScheduledTasks(service, journal, warnings, blocked);

                var task = Assert.Single(journal.Tasks);
                Assert.False(task.WriteAttempted);
                Assert.False(task.ConfirmedDisabled);
                Assert.Single(blocked);
                Assert.Contains("发生变化", warnings[0]);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 恢复成功会通过同一假客户端重新启用并清除日志()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: false));
            try
            {
                var service = CreateService(directory, fake);
                WriteJournal(service, CreateJournal(TaskXml(enabled: false), confirmedDisabled: true));

                var result = service.Restore();

                Assert.True(result.Success);
                Assert.False(File.Exists(service.JournalPath));
                Assert.Single(fake.WriteCalls);
                Assert.True(fake.WriteCalls[0].Enabled);
                // 恢复前只读一次；最终启用状态由 SetEnabled 同连接回读。
                Assert.Equal(1, fake.ReadCalls);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 只有文件不存在才映射为任务不存在()
    {
        Assert.True(ScheduledTaskComClient.IsTaskNotFound(
            new FileNotFoundException("fake missing task")));
        Assert.True(ScheduledTaskComClient.IsTaskNotFound(
            new COMException("fake missing task", unchecked((int)0x80070002))));

        Assert.False(ScheduledTaskComClient.IsTaskNotFound(
            new COMException("SCHED_E_INVALID_TASK", unchecked((int)0x8004130E))));
        Assert.False(ScheduledTaskComClient.IsTaskNotFound(
            new COMException(
                "SCHED_E_ACCOUNT_INFORMATION_NOT_SET",
                unchecked((int)0x8004130F))));
        Assert.False(ScheduledTaskComClient.IsTaskNotFound(
            new COMException("path not found", unchecked((int)0x80070003))));
        Assert.False(ScheduledTaskComClient.IsTaskNotFound(new IOException("fake I/O failure")));
    }

    [Fact]
    public void 关停前读取失败不会写入未知任务()
    {
        WithTestDirectory(directory =>
        {
            var fake = new FakeScheduledTaskComClient(TaskXml(enabled: true))
            {
                ReadException = new COMException(
                    "SCHED_E_ACCOUNT_INFORMATION_NOT_SET",
                    unchecked((int)0x8004130F))
            };
            try
            {
                var service = CreateService(directory, fake);
                var journal = CreateJournal(TaskXml(enabled: true));
                var warnings = new List<string>();
                var blocked = new List<string>();

                Assert.Throws<InvalidOperationException>(
                    () => InvokeApplyScheduledTasks(service, journal, warnings, blocked));

                Assert.Empty(fake.WriteCalls);
                Assert.Empty(blocked);
                Assert.Single(journal.Tasks);
            }
            finally
            {
                fake.Dispose();
            }
        });
    }

    [Fact]
    public void 缺少身份时拒绝写入且不创建COM客户端()
    {
        var trustCalls = 0;
        var client = new ScheduledTaskComClient(() => trustCalls++);
        try
        {
            var write = client.SetEnabled(RelativeTaskPath, enabled: false, expectedIdentity: null);

            Assert.Equal(ScheduledTaskComWriteStatus.Failed, write.Status);
            Assert.IsType<InvalidOperationException>(write.Failure);
            Assert.Equal(0, trustCalls);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void 信任门禁失败不会创建COM客户端()
    {
        var trustCalls = 0;
        var client = new ScheduledTaskComClient(() =>
        {
            trustCalls++;
            throw new InvalidOperationException("fake trust failure");
        });
        try
        {
            var read = client.ReadXml(RelativeTaskPath);

            Assert.Equal(ScheduledTaskComReadStatus.Failed, read.Status);
            Assert.IsType<InvalidOperationException>(read.Failure);
            Assert.Equal(1, trustCalls);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void COM客户端读取和释放都限制在创建线程()
    {
        var trustCalls = 0;
        var client = new ScheduledTaskComClient(() => trustCalls++);
        try
        {
            ScheduledTaskComReadResult? read = null;
            var reader = new Thread(() => read = client.ReadXml(RelativeTaskPath));
            reader.Start();
            reader.Join();

            Assert.NotNull(read);
            Assert.Equal(ScheduledTaskComReadStatus.Failed, read!.Value.Status);
            Assert.IsType<InvalidOperationException>(read.Value.Failure);
            Assert.Equal(0, trustCalls);

            Exception? disposeFailure = null;
            var disposer = new Thread(() =>
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });
            disposer.Start();
            disposer.Join();

            var failure = Assert.IsType<InvalidOperationException>(disposeFailure);
            Assert.Contains("工作线程", failure.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void 真实COM临时任务探针()
    {
        const string folderPath = @"\DesktopTwin";
        var taskName = "CodexProbe-" + Guid.NewGuid().ToString("N");
        var taskPath = folderPath + @"\" + taskName;
        var stopwatch = new Stopwatch();
        var disableElapsed = TimeSpan.Zero;
        var enableElapsed = TimeSpan.Zero;

        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Schedule.Service ProgID unavailable");
        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Schedule.Service activation returned null");
        dynamic? root = null;
        dynamic? folder = null;
        object? registeredTask = null;
        ScheduledTaskComClient? client = null;
        var registered = false;
        var folderCreated = false;
        var existsAfterDelete = true;
        Exception? cleanupFailure = null;

        try
        {
            service.Connect();
            root = service.GetFolder(@"\");
            try
            {
                folder = root.GetFolder(folderPath);
            }
            catch (Exception exception) when (IsMissingTaskSchedulerObject(exception))
            {
                folder = root.CreateFolder(folderPath);
                folderCreated = true;
            }

            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "DesktopTwin transient COM probe";
            definition.Principal.LogonType = 3;
            definition.Principal.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            // Task Scheduler 拒绝没有 Action 的定义；任务没有任何触发器，
            // 注册后立即被禁用，这个 Action 永远不会执行。
            dynamic action = definition.Actions.Create(0);
            action.Path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            action.Arguments = "/c exit 0";
            registeredTask = folder.RegisterTaskDefinition(
                taskName,
                definition,
                2,
                null,
                null,
                3);
            registered = true;

            client = new ScheduledTaskComClient();
            var before = client.ReadXml(taskPath);
            Assert.Equal(ScheduledTaskComReadStatus.Xml, before.Status);
            var identity = ScheduledTaskXml.GetIdentity(before.Xml);
            Assert.True(ScheduledTaskXml.IsValidIdentity(identity));
            Assert.Equal(TaskEnabledState.Enabled, ScheduledTaskXml.GetEnabledState(before.Xml));
            var directPath = (string)((dynamic)registeredTask).Path;

            stopwatch.Restart();
            var disable = client.SetEnabled(taskPath, enabled: false, identity);
            stopwatch.Stop();
            disableElapsed = stopwatch.Elapsed;
            _output.WriteLine($"PROBE_DISABLE_RESULT={disable.Status}:{disable.Message}");
            Assert.Equal(ScheduledTaskComWriteStatus.Succeeded, disable.Status);

            var disableImmediate = client.ReadXml(taskPath);
            Assert.Equal(ScheduledTaskComReadStatus.Xml, disableImmediate.Status);
            Assert.Equal(TaskEnabledState.Disabled, ScheduledTaskXml.GetEnabledState(disableImmediate.Xml));
            Assert.Equal(identity, ScheduledTaskXml.GetIdentity(disableImmediate.Xml));
            dynamic disabledView = root.GetTask(taskPath);
            var directDisabled = (bool)disabledView.Enabled;
            var directDisabledPath = (string)disabledView.Path;
            Marshal.FinalReleaseComObject(disabledView);

            Thread.Sleep(1000);
            var disableOneSecond = client.ReadXml(taskPath);
            Assert.Equal(ScheduledTaskComReadStatus.Xml, disableOneSecond.Status);
            Assert.Equal(TaskEnabledState.Disabled, ScheduledTaskXml.GetEnabledState(disableOneSecond.Xml));
            Assert.Equal(identity, ScheduledTaskXml.GetIdentity(disableOneSecond.Xml));

            stopwatch.Restart();
            var enable = client.SetEnabled(taskPath, enabled: true, identity);
            stopwatch.Stop();
            enableElapsed = stopwatch.Elapsed;
            Assert.Equal(ScheduledTaskComWriteStatus.Succeeded, enable.Status);

            var enableImmediate = client.ReadXml(taskPath);
            Assert.Equal(ScheduledTaskComReadStatus.Xml, enableImmediate.Status);
            Assert.Equal(TaskEnabledState.Enabled, ScheduledTaskXml.GetEnabledState(enableImmediate.Xml));
            Assert.Equal(identity, ScheduledTaskXml.GetIdentity(enableImmediate.Xml));
            dynamic enabledView = root.GetTask(taskPath);
            var directEnabled = (bool)enabledView.Enabled;
            Marshal.FinalReleaseComObject(enabledView);

            Thread.Sleep(1000);
            var enableOneSecond = client.ReadXml(taskPath);
            Assert.Equal(ScheduledTaskComReadStatus.Xml, enableOneSecond.Status);
            Assert.Equal(TaskEnabledState.Enabled, ScheduledTaskXml.GetEnabledState(enableOneSecond.Xml));
            Assert.Equal(identity, ScheduledTaskXml.GetIdentity(enableOneSecond.Xml));

            _output.WriteLine($"PROBE_TASK_PATH={taskPath}");
            _output.WriteLine($"PROBE_DIRECT_TASK_PATH={directPath}");
            _output.WriteLine($"PROBE_DIRECT_DISABLED_PATH={directDisabledPath}");
            _output.WriteLine($"PROBE_IDENTITY_BEFORE={identity}");
            _output.WriteLine($"PROBE_READ_BEFORE={before.Status}/{ScheduledTaskXml.GetEnabledState(before.Xml)}");
            _output.WriteLine($"PROBE_DISABLE_STATUS={disable.Status}");
            _output.WriteLine($"PROBE_DISABLE_WRITE_TO_SAME_CONNECTION_READBACK_MS={disableElapsed.TotalMilliseconds:F3}");
            _output.WriteLine($"PROBE_DISABLE_IMMEDIATE={disableImmediate.Status}/{ScheduledTaskXml.GetEnabledState(disableImmediate.Xml)}");
            _output.WriteLine($"PROBE_DISABLE_IMMEDIATE_IDENTITY_MATCH={identity == ScheduledTaskXml.GetIdentity(disableImmediate.Xml)}");
            _output.WriteLine($"PROBE_DISABLE_DIRECT_ENABLED={directDisabled}");
            _output.WriteLine($"PROBE_DISABLE_1S={disableOneSecond.Status}/{ScheduledTaskXml.GetEnabledState(disableOneSecond.Xml)}");
            _output.WriteLine($"PROBE_DISABLE_1S_IDENTITY_MATCH={identity == ScheduledTaskXml.GetIdentity(disableOneSecond.Xml)}");
            _output.WriteLine($"PROBE_ENABLE_STATUS={enable.Status}");
            _output.WriteLine($"PROBE_ENABLE_WRITE_TO_SAME_CONNECTION_READBACK_MS={enableElapsed.TotalMilliseconds:F3}");
            _output.WriteLine($"PROBE_ENABLE_IMMEDIATE={enableImmediate.Status}/{ScheduledTaskXml.GetEnabledState(enableImmediate.Xml)}");
            _output.WriteLine($"PROBE_ENABLE_IMMEDIATE_IDENTITY_MATCH={identity == ScheduledTaskXml.GetIdentity(enableImmediate.Xml)}");
            _output.WriteLine($"PROBE_ENABLE_DIRECT_ENABLED={directEnabled}");
            _output.WriteLine($"PROBE_ENABLE_1S={enableOneSecond.Status}/{ScheduledTaskXml.GetEnabledState(enableOneSecond.Xml)}");
            _output.WriteLine($"PROBE_ENABLE_1S_IDENTITY_MATCH={identity == ScheduledTaskXml.GetIdentity(enableOneSecond.Xml)}");
        }
        finally
        {
            try
            {
                client?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            if (registeredTask is not null)
            {
                Marshal.FinalReleaseComObject(registeredTask);
            }

            try
            {
                if (registered && folder is not null)
                {
                    folder.DeleteTask(taskName, 0);
                }

                if (root is not null)
                {
                    try
                    {
                        dynamic stillThere = root.GetTask(taskPath);
                        existsAfterDelete = true;
                        Marshal.FinalReleaseComObject(stillThere);
                    }
                    catch (Exception exception) when (IsMissingTaskSchedulerObject(exception))
                    {
                        existsAfterDelete = false;
                    }

                    if (folderCreated)
                    {
                        try
                        {
                            root.DeleteFolder(folderPath, 0);
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            finally
            {
                if (folder is not null)
                {
                    Marshal.FinalReleaseComObject(folder);
                }

                if (root is not null)
                {
                    Marshal.FinalReleaseComObject(root);
                }

                Marshal.FinalReleaseComObject(service);
            }

            _output.WriteLine($"PROBE_TASK_REGISTERED={registered}");
            _output.WriteLine($"PROBE_FOLDER_CREATED={folderCreated}");
            _output.WriteLine($"PROBE_EXISTS_AFTER_DELETE={existsAfterDelete}");
            _output.WriteLine($"PROBE_CLEANUP_FAILURE={cleanupFailure?.GetType().Name ?? "none"}:{cleanupFailure?.Message}");
        }

        Assert.Null(cleanupFailure);
        Assert.False(existsAfterDelete);
    }

    private static bool IsMissingTaskSchedulerObject(Exception exception) =>
        exception is FileNotFoundException
        || exception is COMException com
        && com.HResult == unchecked((int)0x80070002);

    private static string TaskXml(bool enabled, string command = "C:/App/app.exe")
    {
        var value = enabled ? "true" : "false";
        return $"""
            <Task version="1.2" xmlns="{TaskNamespace}">
              <RegistrationInfo><Author>Contoso</Author></RegistrationInfo>
              <Triggers><LogonTrigger><UserId>alice</UserId></LogonTrigger></Triggers>
              <Settings><Enabled>{value}</Enabled></Settings>
              <Actions><Exec><Command>{command}</Command></Exec></Actions>
            </Task>
            """;
    }

    private static StartupSnoozeJournal CreateJournal(
        string taskXml,
        bool confirmedDisabled = false)
    {
        var identity = ScheduledTaskXml.GetIdentity(taskXml);
        Assert.NotNull(identity);
        var journal = new StartupSnoozeJournal
        {
            Version = 3,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Completed = false
        };
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = RelativeTaskPath,
            TaskName = "ContosoStartup",
            TaskPath = "\\",
            Identity = identity,
            Executable = "C:/App/app.exe",
            OriginalEnabled = true,
            WriteAttempted = true,
            ConfirmedDisabled = confirmedDisabled
        });
        return journal;
    }

    private static StartupSnoozeService CreateService(
        string directory,
        IScheduledTaskComClient client) =>
        new(
            directory,
            () => StartupSnoozeService.ChildSessionPresence.Absent,
            TimeSpan.Zero,
            _ => new List<string>(),
            startupSnapshotProvider: null,
            shellStore: new FakeCurrentUserShellStore(),
            scheduledTaskComClientFactory: () => client);

    private static StartupSnoozeService.ScheduledTaskXmlQueryResult InvokeQueryTaskXml(
        StartupSnoozeService service,
        string relative)
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "QueryTaskXml",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (StartupSnoozeService.ScheduledTaskXmlQueryResult)method!.Invoke(service, [relative])!;
    }

    private static void InvokeApplyScheduledTasks(
        StartupSnoozeService service,
        StartupSnoozeJournal journal,
        List<string> warnings,
        List<string> blocked)
    {
        var method = typeof(StartupSnoozeService).GetMethod(
            "ApplyScheduledTasks",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(service, [journal, warnings, blocked]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static void WriteJournal(StartupSnoozeService service, StartupSnoozeJournal journal)
    {
        var parent = Path.GetDirectoryName(service.JournalPath);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        File.WriteAllText(service.JournalPath, JsonSerializer.Serialize(journal));
    }

    private static StartupSnoozeJournal ReadJournal(StartupSnoozeService service)
    {
        var journal = JsonSerializer.Deserialize<StartupSnoozeJournal>(
            File.ReadAllText(service.JournalPath));
        Assert.NotNull(journal);
        return journal!;
    }

    private static void WithTestDirectory(Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DesktopTwin-com-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeScheduledTaskComClient : IScheduledTaskComClient
    {
        private string _taskXml;

        public FakeScheduledTaskComClient(string taskXml)
        {
            _taskXml = taskXml;
        }

        public Exception? ReadException { get; set; }

        public Func<string, bool, string?, ScheduledTaskComWriteResult>? WriteResultFactory { get; set; }

        public int ReadCalls { get; private set; }

        public List<(bool Enabled, string? ExpectedIdentity)> WriteCalls { get; } = new();

        public ScheduledTaskComReadResult ReadXml(string relativeTaskPath)
        {
            ReadCalls++;
            return ReadException is null
                ? ScheduledTaskComReadResult.Success(_taskXml)
                : ScheduledTaskComReadResult.Failed(ReadException);
        }

        public ScheduledTaskComWriteResult SetEnabled(
            string relativeTaskPath,
            bool enabled,
            string? expectedIdentity)
        {
            Assert.Equal(RelativeTaskPath, relativeTaskPath);
            WriteCalls.Add((enabled, expectedIdentity));
            if (WriteResultFactory is not null)
            {
                return WriteResultFactory(relativeTaskPath, enabled, expectedIdentity);
            }

            _taskXml = TaskXml(enabled);
            return ScheduledTaskComWriteResult.Success();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeCurrentUserShellStore : ICurrentUserShellStore
    {
        private CurrentUserShellSnapshot _current = new(
            false,
            RegistryValueKind.String,
            null);

        public string KeyPath => CurrentUserShellStore.WinlogonKeyPath;

        public string ValueName => "Shell";

        public CurrentUserShellSnapshot Read() => _current;

        public void Write(string value, RegistryValueKind kind) =>
            _current = new CurrentUserShellSnapshot(true, kind, value);

        public void Delete() =>
            _current = new CurrentUserShellSnapshot(false, RegistryValueKind.String, null);
    }
}
