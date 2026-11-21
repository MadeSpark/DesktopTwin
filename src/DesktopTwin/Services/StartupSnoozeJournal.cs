using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using System.Xml.Linq;

namespace DesktopTwin.Services;

/// <summary>
/// 「启动应用」开关（<c>Explorer\StartupApproved</c>）的字节语义。
///
/// 首字节不是简单的奇偶位：0x02/0x04/0x06 是用户可切换的启用态，0x01/0x03
/// 是用户可切换的关闭态；0x08 是策略锁定启用，0x09 是策略锁定关闭。未知值绝不能
/// 猜测或改写成 0x03，否则可能覆盖组策略或安全软件维护的状态。
/// </summary>
public static class StartupApproval
{
    /// <summary>由本程序写入的关闭状态值。</summary>
    public const byte DisabledFlag = 0x03;

    /// <summary>按首字节识别 <c>StartupApproved</c> 的状态。</summary>
    public static StartupApprovalState Classify(byte[]? value)
    {
        if (value is not { Length: >= 4 })
        {
            return StartupApprovalState.Unknown;
        }

        return value[0] switch
        {
            0x02 or 0x04 or 0x06 => StartupApprovalState.Enabled,
            0x01 or 0x03 => StartupApprovalState.Disabled,
            0x08 => StartupApprovalState.EnabledLocked,
            0x09 => StartupApprovalState.DisabledLocked,
            _ => StartupApprovalState.Unknown
        };
    }

    /// <summary>判断一个值是否已经表示关闭（包括策略锁定关闭）。</summary>
    public static bool IsDisabled(byte[]? value) =>
        Classify(value) is StartupApprovalState.Disabled or StartupApprovalState.DisabledLocked;

    /// <summary>判断一个值是否可由本程序安全改写为关闭。</summary>
    public static bool CanDisable(byte[]? value) => Classify(value) == StartupApprovalState.Enabled;

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

/// <summary><c>StartupApproved</c> 首字节的可识别状态。</summary>
public enum StartupApprovalState
{
    Unknown,
    Enabled,
    Disabled,
    EnabledLocked,
    DisabledLocked
}

/// <summary>任务 XML 里 <c>Settings/Enabled</c> 的三态语义。</summary>
public enum TaskEnabledState
{
    /// <summary>XML 损坏、值未知或重复出现，不能推断启用状态。</summary>
    Unknown,
    /// <summary>明确启用。</summary>
    Enabled,
    /// <summary>明确禁用。</summary>
    Disabled
}

/// <summary>
/// 计划任务 XML 的严格读取、身份计算和触发器筛选。
///
/// <para><c>schtasks /Query /XML</c> 会带 <c>encoding="UTF-16"</c> 声明，但重定向读到的
/// 是 OEM 代码页文本。解析前只移除 XML 声明，保留正文，并禁止 DTD / 外部实体。</para>
///
/// <para>任务身份只覆盖任务定义，不包含开关本身和运行期 State。因此用户仅切换
/// <c>Settings/Enabled</c> 时身份不变，而动作、主体、触发器或注册信息变化时身份必变。</para>
/// </summary>
public static class ScheduledTaskXml
{
    /// <summary>Windows 任务计划程序任务定义使用的固定 XML 命名空间。</summary>
    public const string TaskNamespaceUri = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private const string IdentityPrefix = "sha256:";
    private static readonly XNamespace TaskNamespace = XNamespace.Get(TaskNamespaceUri);

    /// <summary>
    /// 解析任务 XML 的 <c>Settings/Enabled</c>（XSD boolean）。缺少 Settings 或 Enabled
    /// 时按 Windows 默认值启用；接受 <c>1/0/true/false</c>；空值、未知值、重复的
    /// Settings/Enabled 或无法解析的 XML 一律返回 <see cref="TaskEnabledState.Unknown"/>，
    /// 由调用方失败关闭，绝不把未知状态当成「已禁用」。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    public static TaskEnabledState GetEnabledState(string? xml)
    {
        if (!TryParse(xml, out var document))
        {
            return TaskEnabledState.Unknown;
        }

        return GetEnabledState(document.Root!);
    }

    /// <summary>兼容语义：只有明确启用才返回 true；Unknown 返回 false。</summary>
    public static bool IsEnabled(string? xml) =>
        GetEnabledState(xml) == TaskEnabledState.Enabled;

    /// <summary>只有明确禁用才返回 true；Unknown 返回 false。</summary>
    public static bool IsDisabled(string? xml) =>
        GetEnabledState(xml) == TaskEnabledState.Disabled;

    private static TaskEnabledState GetEnabledState(XElement task)
    {
        var settings = task.Elements(TaskNamespace + "Settings").ToArray();
        if (settings.Length == 0)
        {
            return TaskEnabledState.Enabled;
        }

        if (settings.Length > 1)
        {
            return TaskEnabledState.Unknown;
        }

        var enabled = settings[0].Elements(TaskNamespace + "Enabled").ToArray();
        if (enabled.Length == 0)
        {
            return TaskEnabledState.Enabled;
        }

        if (enabled.Length > 1)
        {
            return TaskEnabledState.Unknown;
        }

        var text = enabled[0].Value.Trim();
        if (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return TaskEnabledState.Enabled;
        }

        if (text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return TaskEnabledState.Disabled;
        }

        return TaskEnabledState.Unknown;
    }

    /// <summary>
    /// 判断任务是否带当前用户可用的登录 / 会话状态变化触发器。注册触发器不是登录触发器，
    /// 必须排除；触发器显式禁用或绑定到其他用户时也不能选中。
    /// </summary>
    public static bool HasSessionStartTrigger(string? xml)
    {
        string? currentUser = null;
        string? currentUserSid = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUser = identity.Name;
            currentUserSid = identity.User?.Value;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException
                                              or InvalidOperationException)
        {
            // 身份读取失败时仍可接受没有 UserId 的触发器，但不能把其他用户的触发器当成当前用户。
            currentUser = Environment.UserName;
        }

        return HasSessionStartTrigger(xml, currentUser, currentUserSid);
    }

    /// <inheritdoc cref="HasSessionStartTrigger(string, string?, string?)"/>
    public static bool HasSessionStartTrigger(string? xml, string? currentUser) =>
        HasSessionStartTrigger(xml, currentUser, currentUserSid: null);

    /// <summary>
    /// 使用显式当前用户判断触发器是否适用；便于离线测试，不依赖运行测试的机器身份。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    /// <param name="currentUser">当前用户，可带域名。</param>
    /// <param name="currentUserSid">当前用户 SID，可为 null。</param>
    public static bool HasSessionStartTrigger(
        string? xml,
        string? currentUser,
        string? currentUserSid)
    {
        if (!TryParse(xml, out var document))
        {
            return false;
        }

        var root = document.Root!;
        var triggers = root.Element(TaskNamespace + "Triggers");
        if (triggers is null)
        {
            return false;
        }

        var principal = ResolvePrincipal(root, currentUser, currentUserSid);

        foreach (var trigger in triggers.Elements())
        {
            if (trigger.Name != TaskNamespace + "LogonTrigger"
                && trigger.Name != TaskNamespace + "SessionStateChangeTrigger")
            {
                continue;
            }

            var enabled = trigger.Element(TaskNamespace + "Enabled");
            if (enabled is not null)
            {
                var enabledText = enabled.Value.Trim();
                if (enabledText == "0"
                    || string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // true/1/空值/未知值都按激活处理：无法证明触发器已关闭时宁可
                // 多隔离，绝不能把未知状态当成「不启动」。
            }

            if (trigger.Name == TaskNamespace + "SessionStateChangeTrigger"
                && !IsLogonRelatedStateChange(trigger))
            {
                continue;
            }

            var groupId = trigger.Element(TaskNamespace + "GroupId")?.Value.Trim();
            if (!string.IsNullOrEmpty(groupId))
            {
                // 组触发器只有匹配当前用户（或本身是 Users/Everyone 这类普惠组）
                // 才可能影响当前登录；其他组明确排除。
                if (UserMatches(groupId, currentUser, currentUserSid)
                    || IsBroadUserGroup(groupId))
                {
                    return true;
                }

                continue;
            }

            var userId = trigger.Element(TaskNamespace + "UserId")?.Value.Trim();
            if (!string.IsNullOrEmpty(userId))
            {
                if (UserMatches(userId, currentUser, currentUserSid)
                    || IsBroadUserGroup(userId))
                {
                    return true;
                }

                // 明确绑定其他用户：不影响当前登录。
                continue;
            }

            // UserId 缺失或为空时，任务计划程序会按 Principal 解析身份。
            // 只有 Principal 静态且唯一地指向当前用户时才可排除；Principal 缺失、
            // 是组 / Everyone / Users / 任意用户或无法证明时，必须按影响当前用户
            // 处理并纳入隔离（失败关闭），不能静默 continue。
            switch (principal)
            {
                case PrincipalKind.CurrentUser:
                    return true;
                case PrincipalKind.OtherUser:
                    continue;
                default:
                    return true;
            }
        }

        return false;
    }

    /// <summary>任务主体的静态身份分类。</summary>
    private enum PrincipalKind
    {
        /// <summary>缺失、空、重复或无法唯一证明。</summary>
        Unknown,
        /// <summary>唯一主体明确是当前用户。</summary>
        CurrentUser,
        /// <summary>唯一主体明确是其他具体用户。</summary>
        OtherUser,
        /// <summary>组主体或 Everyone / Users 这类任意用户主体。</summary>
        GroupOrAny
    }

    private static PrincipalKind ResolvePrincipal(
        XElement task,
        string? currentUser,
        string? currentUserSid)
    {
        var principals = task.Element(TaskNamespace + "Principals")?
            .Elements(TaskNamespace + "Principal")
            .ToArray();
        if (principals is null || principals.Length == 0)
        {
            return PrincipalKind.Unknown;
        }

        // 多个主体、或任何主体带 GroupId 时都无法静态证明任务只作用于当前用户。
        if (principals.Length > 1
            || principals.Any(item => !string.IsNullOrWhiteSpace(
                item.Element(TaskNamespace + "GroupId")?.Value)))
        {
            return PrincipalKind.GroupOrAny;
        }

        var userId = principals[0].Element(TaskNamespace + "UserId")?.Value.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            return PrincipalKind.Unknown;
        }

        if (UserMatches(userId, currentUser, currentUserSid))
        {
            return PrincipalKind.CurrentUser;
        }

        if (IsBroadUserGroup(userId))
        {
            return PrincipalKind.GroupOrAny;
        }

        return PrincipalKind.OtherUser;
    }

    /// <summary>
    /// 判断一个身份是否是 Users / Everyone / Authenticated Users 这类不限定单个
    /// 账户的主体。此类主体无法证明只影响当前用户，必须失败关闭。
    /// </summary>
    private static bool IsBroadUserGroup(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545" or "S-1-5-4" or "S-1-2-0")
        {
            return true;
        }

        var name = text;
        var separator = name.LastIndexOf('\\');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        return name.Equals("Everyone", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Users", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Authenticated Users", StringComparison.OrdinalIgnoreCase)
               || name.Equals("INTERACTIVE", StringComparison.OrdinalIgnoreCase)
               || name.Equals("LOCAL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 只接受官方 XSD 中真正会在子会话登录路径上触发的状态变化：
    /// <c>ConsoleConnect</c>、<c>RemoteConnect</c>、<c>SessionUnlock</c>。
    /// <c>ConsoleDisconnect</c>/<c>RemoteDisconnect</c>/<c>SessionLock</c> 属于退出路径，
    /// <c>SessionLogon</c>/<c>SessionConnect</c> 等不是官方 token，一律排除。
    /// </summary>
    private static bool IsLogonRelatedStateChange(XElement trigger)
    {
        var stateChange = trigger.Element(TaskNamespace + "StateChange")?.Value.Trim();
        return stateChange is "ConsoleConnect" or "RemoteConnect" or "SessionUnlock";
    }

    private static bool UserMatches(string taskUser, string? currentUser, string? currentUserSid)
    {
        if (!string.IsNullOrWhiteSpace(currentUserSid)
            && string.Equals(taskUser, currentUserSid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(currentUser))
        {
            return false;
        }

        var user = currentUser.Trim();
        if (string.Equals(taskUser, user, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 有些任务写 DOMAIN\user，有些只写 user；只允许当前用户的短名，不跨用户猜测。
        var separator = user.LastIndexOf('\\');
        return separator >= 0
               && taskUser.IndexOf('\\') < 0
               && string.Equals(taskUser, user[(separator + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取出任务直接启动的命令。只用于子会话内按精确路径兜底拦截；
    /// 命令行仍由计划任务自己执行。无法严格解析时返回 null。
    /// </summary>
    /// <param name="xml">任务的 XML 定义。</param>
    public static string? GetExecutable(string? xml)
    {
        if (!TryParse(xml, out var document))
        {
            return null;
        }

        var command = document.Root!.Descendants(TaskNamespace + "Command").FirstOrDefault();
        var value = command?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>严格解析任务 XML；格式错误、非任务命名空间的 Task 根节点或 DTD 都返回 false。</summary>
    public static bool TryParse(string? xml, out XDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        var text = xml.Trim().TrimStart('\uFEFF');
        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var declarationEnd = text.IndexOf("?>", StringComparison.Ordinal);
            if (declarationEnd < 0)
            {
                return false;
            }

            text = text[(declarationEnd + 2)..].TrimStart();
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var parsed = XDocument.Load(reader);
            if (parsed.Root is null || parsed.Root.Name != TaskNamespace + "Task")
            {
                return false;
            }

            document = parsed;
            return true;
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成稳定的任务身份。只忽略 <c>Settings/Enabled</c> 和运行期根 State，
    /// 其余定义按 XML 语义规范化后计算 SHA-256。
    /// </summary>
    public static string? GetIdentity(string? xml)
    {
        if (!TryParse(xml, out var document))
        {
            return null;
        }

        var canonical = new StringBuilder();
        AppendCanonical(canonical, document.Root!, isRoot: true);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return IdentityPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>验证由 <see cref="GetIdentity"/> 生成的身份格式。</summary>
    public static bool IsValidIdentity(string? identity)
    {
        if (identity is null
            || identity.Length != IdentityPrefix.Length + 64
            || !identity.StartsWith(IdentityPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = IdentityPrefix.Length; index < identity.Length; index++)
        {
            var value = identity[index];
            if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendCanonical(
        StringBuilder builder,
        XElement element,
        bool isRoot,
        bool isTaskSettings = false)
    {
        builder.Append('E');
        AppendToken(builder, element.Name.NamespaceName);
        AppendToken(builder, element.Name.LocalName);

        foreach (var attribute in element.Attributes()
                     .Where(attribute => !attribute.IsNamespaceDeclaration)
                     .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                     .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        {
            builder.Append('A');
            AppendToken(builder, attribute.Name.NamespaceName);
            AppendToken(builder, attribute.Name.LocalName);
            AppendToken(builder, attribute.Value);
        }

        builder.Append('{');
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    if (isRoot && child.Name == TaskNamespace + "State")
                    {
                        continue;
                    }

                    if (isTaskSettings && child.Name == TaskNamespace + "Enabled")
                    {
                        continue;
                    }

                    AppendCanonical(
                        builder,
                        child,
                        isRoot: false,
                        isTaskSettings: isRoot && child.Name == TaskNamespace + "Settings");
                    break;
                case XText text when !string.IsNullOrWhiteSpace(text.Value):
                    builder.Append('T');
                    AppendToken(builder, text.Value);
                    break;
            }
        }

        builder.Append('}');
    }

    private static void AppendToken(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}

/// <summary>收集计划任务时的三分决策。</summary>
public enum ScheduledTaskIsolationDecision
{
    /// <summary>不在当前用户的登录路径上，或明确已禁用。</summary>
    Skip,
    /// <summary>当前用户登录时会启动，必须关停。</summary>
    Isolate,
    /// <summary>启用状态无法证明；必须失败关闭并阻断本次隔离。</summary>
    Block
}

/// <summary>
/// 计划任务的纯 XML 分类：先按 XSD 语义解析启用状态，再判断触发器是否影响当前用户。
/// 任何无法证明的状态都返回 <see cref="ScheduledTaskIsolationDecision.Block"/>，绝不静默跳过。
/// </summary>
public static class ScheduledTaskIsolation
{
    /// <inheritdoc cref="Classify(string, string?, string?)"/>
    public static ScheduledTaskIsolationDecision Classify(string? xml) =>
        ScheduledTaskXml.GetEnabledState(xml) switch
        {
            TaskEnabledState.Unknown => ScheduledTaskIsolationDecision.Block,
            TaskEnabledState.Disabled => ScheduledTaskIsolationDecision.Skip,
            _ => ScheduledTaskXml.HasSessionStartTrigger(xml)
                ? ScheduledTaskIsolationDecision.Isolate
                : ScheduledTaskIsolationDecision.Skip
        };

    /// <summary>使用显式当前用户分类，便于离线测试，不访问真实系统。</summary>
    public static ScheduledTaskIsolationDecision Classify(
        string? xml,
        string? currentUser,
        string? currentUserSid)
    {
        switch (ScheduledTaskXml.GetEnabledState(xml))
        {
            case TaskEnabledState.Unknown:
                return ScheduledTaskIsolationDecision.Block;
            case TaskEnabledState.Disabled:
                return ScheduledTaskIsolationDecision.Skip;
            default:
                return ScheduledTaskXml.HasSessionStartTrigger(xml, currentUser, currentUserSid)
                    ? ScheduledTaskIsolationDecision.Isolate
                    : ScheduledTaskIsolationDecision.Skip;
        }
    }
}
/// <summary>
/// UWP / 商店应用启动任务的 <c>State</c> 取值语义。
/// </summary>
public static class UwpStartupState
{
    /// <summary>启用（用户已确认）。</summary>
    public const int EnabledConfirmed = 2;

    /// <summary>由策略启用；登录时同样会启动，不能当作未启用跳过。</summary>
    public const int EnabledByPolicy = 4;

    /// <summary>
    /// 判断任务当前是否启用。<c>2</c> 和 <c>4</c> 都会在登录时启动；
    /// <c>0</c>（用户关闭）、<c>1</c>（未确认）都不启动。
    /// </summary>
    /// <param name="state">注册表里的 <c>State</c> 值。</param>
    public static bool IsEnabled(int state) =>
        state is EnabledConfirmed or EnabledByPolicy;
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
            return ResolveExecutablePath(path);
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

    /// <summary>
    /// 兜底守卫只接受能唯一定位的可执行文件。PATH 裸命令会被 Shell/系统按
    /// 当前目录、PATH 和 App Paths 解析成不同目标，拿它匹配进程既可能漏掉
    /// 真实程序，也可能误杀同名文件；这里解析成明确的完整路径。
    /// </summary>
    private static string? ResolveExecutablePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        // App Paths 是登录阶段实际生效的 Shell 解析来源；必须本地机器与当前用户
        // 两侧都一致，且值是完整路径，才允许当作守卫目标。
        var appPaths = FindAppPathExecutable(path);
        if (appPaths is not null)
        {
            return appPaths;
        }

        // 最后才查 PATH。仍然要求解析结果存在且是明确的完整路径；任何歧义都
        // 直接返回 null，让上层把该启动项记为无法兜底。
        return FindPathExecutable(path);
    }

    private static string? FindAppPathExecutable(string command)
    {
        const string appPathsRoot =
            @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        var baseName = Path.GetFileName(command);
        string? localValue = null;
        string? userValue = null;

        using (var localKey = Registry.LocalMachine.OpenSubKey(appPathsRoot + "\\" + baseName))
        {
            localValue = ReadAppPathExecutable(localKey);
        }

        using (var userKey = Registry.CurrentUser.OpenSubKey(appPathsRoot + "\\" + baseName))
        {
            userValue = ReadAppPathExecutable(userKey);
        }

        if (string.IsNullOrWhiteSpace(localValue) || string.IsNullOrWhiteSpace(userValue))
        {
            return null;
        }

        return string.Equals(localValue, userValue, StringComparison.OrdinalIgnoreCase)
            ? localValue
            : null;
    }

    private static string? ReadAppPathExecutable(RegistryKey? key)
    {
        if (key is null)
        {
            return null;
        }

        var value = key.GetValue(
            string.Empty,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is string path && Path.IsPathRooted(path) ? Expand(path) : null;
    }

    private static string? FindPathExecutable(string command)
    {
        if (command.Contains('"'))
        {
            return null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var rawDirectory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim();
            if (directory.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, Path.GetFileName(command));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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

    /// <summary>
    /// 关停前的稳定任务身份。版本 3 起必填；旧日志没有该字段时恢复必须失败关闭。
    /// </summary>
    public string? Identity { get; set; }

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

    /// <summary>
    /// 写入后严格回读确认的状态值等于 <see cref="WrittenState"/>。只有为 true
    /// 才能证明这是本程序自己的写入；外部关闭或写入失败都无法命中原值，恢复
    /// 时必须保持禁用并保留日志。
    /// </summary>
    public bool WriteConfirmed { get; set; }
}

/// <summary>
/// 关停日志。先写日志再改系统：中途崩溃也能靠它原样恢复。
/// </summary>
public sealed class StartupSnoozeJournal
{
    /// <summary>
    /// 日志格式版本。版本 2 增加计划任务写入前标记；版本 3 要求计划任务身份。
    /// </summary>
    public int Version { get; set; } = 3;

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
