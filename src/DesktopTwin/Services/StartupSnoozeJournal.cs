using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DesktopTwin.Services;

/// <summary>
/// 「启动应用」开关（<c>Explorer\StartupApproved</c>）的字节语义。
///
/// Windows 只用首字节的低 4 位判断状态：<c>0x03</c> 表示被用户关闭，其余（<c>0x02</c>、
/// <c>0x01</c>、<c>0x06</c> 等）都表示启用。首字节之后是 FILETIME 形式的关闭时间，
/// 恢复时必须原样写回，不能自己造一个时间戳——否则用户原来的「关闭时间」就被改了。
/// </summary>
public static class StartupApproval
{
    /// <summary>关闭状态在首字节低 4 位上的取值。</summary>
    public const byte DisabledFlag = 0x03;

    /// <summary>
    /// 判断一个 <c>StartupApproved</c> 值是否表示「已关闭」。
    /// </summary>
    /// <param name="value">注册表里读到的原始字节；为空时无法判断，按未关闭处理。</param>
    public static bool IsDisabled(byte[]? value) =>
        value is { Length: > 0 } && (value[0] & 0x0F) == DisabledFlag;

    /// <summary>
    /// 生成「已关闭」字节：<c>03 00 00 00</c> + 当前 UTC 时间的 FILETIME。
    /// </summary>
    /// <param name="utcNow">关闭时间，仅用于写入，恢复时不会被读回覆盖原值。</param>
    public static byte[] CreateDisabled(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = DisabledFlag;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }
}

/// <summary>
/// 计划任务 XML 里 <c>Settings/Enabled</c> 的判定。
///
/// 只能看这个元素：<c>schtasks /Query</c> 的 State 列在任务正在运行时仍然是 Running，
/// 禁用后也不会立刻变成 Disabled（实测 Clash Verge 就是如此），拿它判断会误报失败。
/// </summary>
public static class ScheduledTaskXml
{
    /// <summary>
    /// 判断任务 XML 是否启用。缺少 Enabled 元素时按 Windows 默认值 true 处理。
    ///
    /// 有意用字符串扫描而不是 XML 解析：schtasks /Query /XML 的输出带 encoding="UTF-16"
    /// 声明，但重定向读到的是 OEM 代码页文本，交给 XML 解析器会直接抛「编码声明不匹配」。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    public static bool IsEnabled(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return true;
        }

        // 有 <Settings> 就以它为准：里面的 <Enabled> 才是任务自己的开关。
        if (TryExtractBlock(xml, "Settings", out var settings))
        {
            return IsEnabledIn(settings);
        }

        // 出现了 <Settings 的标签却没能配对取出内容（截断、命名空间怪异）：
        // 这时候宁可认为任务健在，也不要凭一次误读去改用户的任务。
        if (xml.Contains("<Settings", StringComparison.Ordinal))
        {
            return true;
        }

        // 整份定义里本来就没有 Settings 块（少数旧任务）——退回整份 XML 找。
        return IsEnabledIn(xml);
    }

    private static bool IsEnabledIn(string xml)
    {
        if (!TryExtractBlock(xml, "Enabled", out var enabled))
        {
            return true;
        }

        return !string.Equals(enabled.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断任务是否带「登录 / 会话状态变化 / 注册」触发器——只有这类任务会跟着分身
    /// 里的那次登录在子会话里再跑一遍，是真正的第二启动路径。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    public static bool HasSessionStartTrigger(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        return xml.Contains("<LogonTrigger", StringComparison.Ordinal)
            || xml.Contains("<SessionStateChangeTrigger", StringComparison.Ordinal)
            || xml.Contains("<RegistrationTrigger", StringComparison.Ordinal);
    }

    /// <summary>
    /// 取出任务直接启动的命令。只用于子会话内按精确路径兜底拦截；
    /// 命令行仍由计划任务自己执行。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    public static string? GetExecutable(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !TryExtractBlock(xml, "Command", out var command))
        {
            return null;
        }

        var value = command.Trim();
        if (value.StartsWith("<![CDATA[", StringComparison.Ordinal)
            && value.EndsWith("]]>", StringComparison.Ordinal))
        {
            value = value.Substring(9, value.Length - 12).Trim();
        }
        else
        {
            value = WebUtility.HtmlDecode(value).Trim();
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// 取出 element 标签之间的内容（跳过同名但带属性的伪匹配）。
    /// </summary>
    private static bool TryExtractBlock(string xml, string element, out string inner)
    {
        inner = string.Empty;
        var open = "<" + element;
        var index = xml.IndexOf(open, StringComparison.Ordinal);
        while (index >= 0)
        {
            var after = index + open.Length;
            if (after < xml.Length && (xml[after] == '>' || char.IsWhiteSpace(xml[after])))
            {
                var closeBracket = xml.IndexOf('>', after);
                if (closeBracket < 0)
                {
                    return false;
                }

                var end = xml.IndexOf("</" + element + ">", closeBracket, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                inner = xml.Substring(closeBracket + 1, end - closeBracket - 1);
                return true;
            }

            index = xml.IndexOf(open, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
/// <summary>
/// UWP / 商店应用启动任务的 <c>State</c> 取值语义。
/// </summary>
public static class UwpStartupState
{
    /// <summary>启用（含「用户已确认」）。</summary>
    public const int EnabledConfirmed = 2;

    /// <summary>
    /// 判断任务当前是否启用。只有 <c>2</c> 会真的在登录时启动；
    /// <c>0</c>（用户关闭）、<c>1</c>（未确认）都不启动。
    /// </summary>
    /// <param name="state">注册表里的 <c>State</c> 值。</param>
    public static bool IsEnabled(int state) => state == EnabledConfirmed;
}

/// <summary>
/// <c>RunOnce</c> 的窄放行规则。
///
/// <c>RunOnce</c> 本身就是「下次登录跑一次」，属于我们必须拒绝的未覆盖启动源；
/// 唯一的例外是微软 Edge / WebView2 自带的清理项：它跑完自删除、由微软签名、
/// 位于 Edge 安装目录，不会拉起用户的第三方软件。
/// </summary>
public static class RunOnceClassifier
{
    /// <summary>放行项的名称前缀。</summary>
    public const string AllowedPrefix = "msedge_cleanup_";

    /// <summary>
    /// 判断一个 <c>RunOnce</c> 项能否放行。
    /// </summary>
    /// <param name="valueName">值名称。</param>
    /// <param name="commandLine">值内容（命令行）。</param>
    /// <param name="executableExists">命令行的可执行文件是否存在。</param>
    /// <param name="executablePath">从命令行解析出的可执行文件路径。</param>
    /// <param name="allowedRoots">允许的安装根目录。</param>
    public static bool IsAllowed(
        string? valueName,
        string? commandLine,
        bool executableExists,
        string? executablePath,
        IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrEmpty(valueName)
            || !valueName.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(commandLine) || !executableExists || string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or System.IO.PathTooLongException)
        {
            return false;
        }

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string rootFull;
            try
            {
                rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从命令行里取出可执行文件路径：带引号时取引号内；未加引号时先按
    /// <c>.exe</c> 边界保留带空格的路径，再退回第一个空格之前。
    /// </summary>
    /// <param name="commandLine">注册表里的命令行。</param>
    public static string? ParseExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed.Substring(1, end - 1) : trimmed.Trim('"');
        }

        // 不少安装程序把未加引号的 "C:\Program Files\Vendor\app.exe --flag"
        // 直接写进 Run。只按空格切会把路径截成 "C:\Program"，后面的兜底守卫
        // 永远匹配不到真实进程。以 .exe 作为边界只影响解析结果；实际拦截时
        // StartupGuardService 仍要求路径存在、位于子会话且完全相等。
        var searchFrom = 0;
        while (searchFrom < trimmed.Length)
        {
            var exe = trimmed.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
            {
                break;
            }

            var end = exe + 4;
            if (end == trimmed.Length || char.IsWhiteSpace(trimmed[end]))
            {
                return trimmed.Substring(0, end);
            }

            searchFrom = end;
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed.Substring(0, space) : trimmed;
    }
}

/// <summary>
/// 把启动文件夹里的快捷方式解析成真实 EXE，供子会话兜底守卫精确匹配。
///
/// 只解析 .lnk，不执行任何东西。WScript.Shell 可能被杀软拦截或 COM 注册损坏，
/// 这种时候保守地返回 null：漏掉一个兜底目标，好过拿 .lnk 文件名误杀进程。
/// </summary>
internal static class ShortcutTargetResolver
{
    public static string? ResolveExecutable(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var path = Expand(candidate);
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            // COM 对象的 GetType() 只是 System.__ComObject，不能靠普通反射读
            // TargetPath；dynamic 会走 IDispatch，才能真正取到快捷方式目标。
            dynamic shellApi = shell;
            shortcut = shellApi.CreateShortcut(path);
            if (shortcut is null)
            {
                return null;
            }

            dynamic shortcutApi = shortcut;
            string? target = shortcutApi.TargetPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var executable = Expand(target);
            return string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
                ? executable
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 快捷方式解析只是兜底路径：COM 被拦截、快捷方式损坏、目标类型不对
            // 都只能让这一项失去兜底保护，绝不能让整轮启动项关停失败。
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string Expand(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // COM 对象已经断开或已被运行时释放；没有需要补救的状态。
        }
    }
}

/// <summary>
/// 一条被关闭（或准备关闭）的注册表启动项。
/// </summary>
public sealed class StartupSnoozeRegistryEntry
{
    /// <summary>启动项种类：<c>RunValue</c> 或 <c>StartupFolderItem</c>。</summary>
    public string Kind { get; set; } = "RunValue";

    /// <summary>注册表配置单元，<c>CurrentUser</c> / <c>LocalMachine</c>。</summary>
    public string Hive { get; set; } = "CurrentUser";

    /// <summary>枚举 Run 时用的视图，仅用于排查。</summary>
    public string RunView { get; set; } = "Registry64";

    /// <summary>写 <c>StartupApproved</c> 时用的视图（WOW64 重定向决定了它可能和 Run 视图不同）。</summary>
    public string ApprovedView { get; set; } = "Registry64";

    /// <summary><c>StartupApproved</c> 子键路径。</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>值名称。</summary>
    public string ValueName { get; set; } = string.Empty;

    /// <summary>命令行，只用于显示和排查。</summary>
    public string? CommandLine { get; set; }

    /// <summary>原值是否存在（不存在时恢复要删掉我们新建的值）。</summary>
    public bool OriginalExists { get; set; }

    /// <summary>原值的 Base64 表示。</summary>
    public string? OriginalBase64 { get; set; }

    /// <summary>写进去的值的 Base64 表示。</summary>
    public string? WrittenBase64 { get; set; }

    /// <summary>是否已确认写入生效。</summary>
    public bool Applied { get; set; }
}

/// <summary>
/// 一条被禁用的计划任务。
/// </summary>
public sealed class StartupSnoozeTaskEntry
{
    /// <summary>相对任务名（含路径），如 <c>\AutoStartSteamGame</c>。</summary>
    public string Relative { get; set; } = string.Empty;

    /// <summary>任务名。</summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>任务路径。</summary>
    public string TaskPath { get; set; } = string.Empty;

    /// <summary>任务直接启动的可执行文件（能解析时）；只用于子会话内的短时兜底。</summary>
    public string? Executable { get; set; }

    /// <summary>原来的启用状态。</summary>
    public bool OriginalEnabled { get; set; }

    /// <summary>是否已经准备调用 schtasks 禁用。用于区分「本次写入」和外部修改。</summary>
    public bool WriteAttempted { get; set; }

    /// <summary>禁用后回读确认的结果。</summary>
    public bool ConfirmedDisabled { get; set; }
}

/// <summary>
/// 一条被关闭的 UWP 启动任务。
/// </summary>
public sealed class StartupSnoozeUwpEntry
{
    /// <summary>注册表键路径（HKCU 下）。</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>包族名。</summary>
    public string PackageFamilyName { get; set; } = string.Empty;

    /// <summary>启动任务名（键名）。</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>原来的 <c>State</c> 值。</summary>
    public int OriginalState { get; set; }

    /// <summary>写进去的 <c>State</c> 值。</summary>
    public int WrittenState { get; set; }
}

/// <summary>
/// 关停日志。先写日志再改系统：中途崩溃也能靠它原样恢复。
/// </summary>
public sealed class StartupSnoozeJournal
{
    /// <summary>日志格式版本。版本 2 起为计划任务记录写入前标记。</summary>
    public int Version { get; set; } = 2;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>计算机名。</summary>
    public string Machine { get; set; } = string.Empty;

    /// <summary>用户名。</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>注册表启动项。</summary>
    public List<StartupSnoozeRegistryEntry> Registry { get; set; } = new();

    /// <summary>计划任务。</summary>
    public List<StartupSnoozeTaskEntry> Tasks { get; set; } = new();

    /// <summary>UWP 启动任务。</summary>
    public List<StartupSnoozeUwpEntry> Uwp { get; set; } = new();

    /// <summary>未能关闭、但已明确告知用户的项。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// 系统拒绝关闭、需要在子会话里按精确路径拦截的启动程序。
    /// 只记录可执行文件路径，不记录或修改启动项内容。
    /// </summary>
    public List<string> DeferredExecutables { get; set; } = new();

    /// <summary>所有写入是否都完成（false 表示中途失败，必须恢复）。</summary>
    public bool Completed { get; set; }
}
