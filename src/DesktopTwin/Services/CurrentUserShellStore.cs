using System;
using System.Linq;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 当前用户 Winlogon Shell 的只读快照。原始值必须按存在性、注册表类型和内容
/// 三部分精确比较；仅比较字符串无法区分字符串类型与可扩展字符串类型。
/// </summary>
internal readonly record struct CurrentUserShellSnapshot(
    bool Exists,
    RegistryValueKind Kind,
    string? Value);

/// <summary>
/// 对当前用户 Winlogon Shell 的最小受控访问接口。生产实现只碰 HKCU 的
/// 64 位视图；测试实现可完全隔离，防止单元测试改动真实登录外壳。
/// </summary>
internal interface ICurrentUserShellStore
{
    string KeyPath { get; }

    string ValueName { get; }

    CurrentUserShellSnapshot Read();

    void Write(string value, RegistryValueKind kind);

    void Delete();
}

/// <summary>
/// 当前用户 Winlogon Shell 的注册表实现。只使用 HKCU，绝不触碰机器级
/// HKLM Winlogon、Userinit 或安全策略。
/// </summary>
internal sealed class CurrentUserShellStore : ICurrentUserShellStore
{
    internal const string WinlogonKeyPath =
        @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public string KeyPath => WinlogonKeyPath;

    public string ValueName => "Shell";

    public CurrentUserShellSnapshot Read()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(WinlogonKeyPath, writable: false);
        if (key is null)
        {
            return new CurrentUserShellSnapshot(false, RegistryValueKind.String, null);
        }

        var names = key.GetValueNames();
        var exists = names.Any(name => string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return new CurrentUserShellSnapshot(false, RegistryValueKind.String, null);
        }

        var kind = key.GetValueKind(ValueName);
        var raw = key.GetValue(
            ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new CurrentUserShellSnapshot(
            true,
            kind,
            raw is string text ? text : null);
    }

    public void Write(string value, RegistryValueKind kind)
    {
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new ArgumentException("Winlogon Shell 只允许字符串或可扩展字符串。", nameof(kind));
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(WinlogonKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户 Winlogon 键。");
        key.SetValue(ValueName, value, kind);
    }

    public void Delete()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(WinlogonKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (key.GetValueNames().Any(name =>
                string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
