using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// Explorer 的四个 Run/RunOnce 策略开关。策略只影响可见 Explorer 加载启动项，
/// 不替换登录外壳，因此桌面、任务栏和文件资源管理器仍按系统默认方式启动。
/// </summary>
internal sealed record ExplorerRunPolicyTarget(string Hive, string ValueName)
{
    internal string KeyPath => ExplorerRunPolicyTargets.KeyPath;

    internal RegistryHive RegistryHive => Hive switch
    {
        "CurrentUser" => RegistryHive.CurrentUser,
        "LocalMachine" => RegistryHive.LocalMachine,
        _ => throw new ArgumentException("不支持的 Explorer 策略配置单元：" + Hive)
    };

    internal string DisplayPath => Hive + @"\" + KeyPath + @"\" + ValueName;
}

internal static class ExplorerRunPolicyTargets
{
    internal const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    internal static IReadOnlyList<ExplorerRunPolicyTarget> All { get; } = new[]
    {
        new ExplorerRunPolicyTarget("CurrentUser", "DisableCurrentUserRun"),
        new ExplorerRunPolicyTarget("CurrentUser", "DisableCurrentUserRunOnce"),
        new ExplorerRunPolicyTarget("LocalMachine", "DisableLocalMachineRun"),
        new ExplorerRunPolicyTarget("LocalMachine", "DisableLocalMachineRunOnce")
    };

    internal static ExplorerRunPolicyTarget Resolve(string hive, string valueName)
    {
        var target = All.FirstOrDefault(item =>
            string.Equals(item.Hive, hive, StringComparison.Ordinal)
            && string.Equals(item.ValueName, valueName, StringComparison.Ordinal));
        return target ?? throw new InvalidOperationException(
            "不支持的 Explorer Run 策略目标：" + hive + @"\" + valueName);
    }
}

internal readonly record struct ExplorerRunPolicySnapshot(
    bool KeyExists,
    bool ValueExists,
    RegistryValueKind Kind,
    int? Value);

internal interface IExplorerRunPolicyStore
{
    IReadOnlyList<ExplorerRunPolicyTarget> Targets { get; }

    ExplorerRunPolicySnapshot Read(ExplorerRunPolicyTarget target);

    void WriteDisabledPolicy(ExplorerRunPolicyTarget target);

    void RestorePolicyValue(ExplorerRunPolicyTarget target, int value);

    void DeleteDisabledPolicy(ExplorerRunPolicyTarget target);
}

/// <summary>
/// 生产实现只访问 64 位 HKCU/HKLM 下固定的 Policies\Explorer 键。每次操作都在
/// 写入前重新解析目标，拒绝日志或测试替身把任意注册表路径带进提权进程。
/// </summary>
internal sealed class ExplorerRunPolicyStore : IExplorerRunPolicyStore
{
    public IReadOnlyList<ExplorerRunPolicyTarget> Targets => ExplorerRunPolicyTargets.All;

    public ExplorerRunPolicySnapshot Read(ExplorerRunPolicyTarget target)
    {
        var canonical = ExplorerRunPolicyTargets.Resolve(target.Hive, target.ValueName);
        using var baseKey = RegistryKey.OpenBaseKey(
            canonical.RegistryHive,
            RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(canonical.KeyPath, writable: false);
        if (key is null)
        {
            return new ExplorerRunPolicySnapshot(
                KeyExists: false,
                ValueExists: false,
                Kind: RegistryValueKind.Unknown,
                Value: null);
        }

        var raw = key.GetValue(
            canonical.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return new ExplorerRunPolicySnapshot(
                KeyExists: true,
                ValueExists: false,
                Kind: RegistryValueKind.Unknown,
                Value: null);
        }

        var kind = key.GetValueKind(canonical.ValueName);
        return new ExplorerRunPolicySnapshot(
            KeyExists: true,
            ValueExists: true,
            Kind: kind,
            Value: raw is int value ? value : null);
    }

    public void WriteDisabledPolicy(ExplorerRunPolicyTarget target)
    {
        var canonical = ExplorerRunPolicyTargets.Resolve(target.Hive, target.ValueName);
        using var baseKey = RegistryKey.OpenBaseKey(
            canonical.RegistryHive,
            RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(canonical.KeyPath, writable: true)
            ?? throw new InvalidOperationException(
                "无法创建 Explorer 策略键：" + canonical.DisplayPath);
        key.SetValue(
            canonical.ValueName,
            1,
            RegistryValueKind.DWord);
    }

    public void RestorePolicyValue(ExplorerRunPolicyTarget target, int value)
    {
        var canonical = ExplorerRunPolicyTargets.Resolve(target.Hive, target.ValueName);
        using var baseKey = RegistryKey.OpenBaseKey(
            canonical.RegistryHive,
            RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(canonical.KeyPath, writable: true)
            ?? throw new InvalidOperationException(
                "无法创建 Explorer 策略键：" + canonical.DisplayPath);
        key.SetValue(canonical.ValueName, value, RegistryValueKind.DWord);
    }

    public void DeleteDisabledPolicy(ExplorerRunPolicyTarget target)
    {
        var canonical = ExplorerRunPolicyTargets.Resolve(target.Hive, target.ValueName);
        using var baseKey = RegistryKey.OpenBaseKey(
            canonical.RegistryHive,
            RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(canonical.KeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        var raw = key.GetValue(
            canonical.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return;
        }

        if (key.GetValueKind(canonical.ValueName) != RegistryValueKind.DWord
            || raw is not int value
            || value != 1)
        {
            throw new InvalidOperationException(
                "Explorer 策略值在恢复前已被外部修改：" + canonical.DisplayPath);
        }

        key.DeleteValue(canonical.ValueName, throwOnMissingValue: false);
    }
}
