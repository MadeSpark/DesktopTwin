using System.Diagnostics;
using System.IO;
using System.Security;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

/// <summary>
/// 旧分身端替换必须在同一个进程句柄上复核创建时间、镜像路径和会话号。
/// 三个不一致分支必须在终止前失败关闭；另一个用例只终止本测试显式拉起的
/// 短生命周期进程，验证句柄、FILETIME 和镜像路径完全一致时确实能完成替换。
/// </summary>
public sealed class ProcessNativeMethodsTests
{
    private const int ErrorInvalidData = 13;

    [Fact]
    public void 身份完全一致时终止并等待该进程()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            Arguments = "-n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        Assert.NotNull(process);
        var identity = CaptureProcess(process!);

        var succeeded = ProcessNativeMethods.TryTerminateProcessExact(
            identity.ProcessId,
            identity.SessionId,
            identity.CreationTimeUtc,
            identity.ImagePath,
            timeoutMilliseconds: 5000,
            out var terminated,
            out var errorCode);

        Assert.True(succeeded, $"精确身份终止失败，Win32 错误码：{errorCode}");
        Assert.True(terminated);
        Assert.True(process!.WaitForExit(5000));
    }

    [Fact]
    public void 创建时间不一致时拒绝终止进程()
    {
        var identity = CaptureCurrentProcess();

        var succeeded = ProcessNativeMethods.TryTerminateProcessExact(
            identity.ProcessId,
            identity.SessionId,
            identity.CreationTimeUtc + 1,
            identity.ImagePath,
            timeoutMilliseconds: 100,
            out var terminated,
            out var errorCode);

        Assert.False(succeeded);
        Assert.False(terminated);
        Assert.Equal(ErrorInvalidData, errorCode);
    }

    [Fact]
    public void 镜像路径不一致时拒绝终止进程()
    {
        var identity = CaptureCurrentProcess();

        var succeeded = ProcessNativeMethods.TryTerminateProcessExact(
            identity.ProcessId,
            identity.SessionId,
            identity.CreationTimeUtc,
            identity.ImagePath + ".not-the-agent",
            timeoutMilliseconds: 100,
            out var terminated,
            out var errorCode);

        Assert.False(succeeded);
        Assert.False(terminated);
        Assert.Equal(ErrorInvalidData, errorCode);
    }

    [Fact]
    public void 会话号不一致时拒绝终止进程()
    {
        var identity = CaptureCurrentProcess();
        var otherSessionId = identity.SessionId == uint.MaxValue
            ? identity.SessionId - 1
            : identity.SessionId + 1;

        var succeeded = ProcessNativeMethods.TryTerminateProcessExact(
            identity.ProcessId,
            otherSessionId,
            identity.CreationTimeUtc,
            identity.ImagePath,
            timeoutMilliseconds: 100,
            out var terminated,
            out var errorCode);

        Assert.False(succeeded);
        Assert.False(terminated);
        Assert.Equal(ErrorInvalidData, errorCode);
    }

    private static ProcessIdentity CaptureCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return CaptureProcess(process);
    }

    private static ProcessIdentity CaptureProcess(Process process)
    {
        var imagePath = process.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(imagePath));

        try
        {
            return new ProcessIdentity(
                process.Id,
                checked((uint)process.SessionId),
                process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                imagePath!);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException
                                              or SecurityException)
        {
            throw new InvalidOperationException("测试进程身份不可读取，无法验证安全拒绝路径。", exception);
        }
    }

    private sealed record ProcessIdentity(
        int ProcessId,
        uint SessionId,
        long CreationTimeUtc,
        string ImagePath);
}
