using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 只监听 IPv4 回环地址的 MCP Streamable HTTP 服务。
///
/// 没有引入第三方 Web 框架：端口只在本机可见，请求体也有严格上限。这样既避开
/// MCP NuGet 依赖，又不把控制面暴露到局域网。
/// </summary>
internal sealed class McpLoopbackServer : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxTrailerBytes = 32 * 1024;
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int MaxRequestBodyReadTimeoutSeconds = 30;
    private const int RequestLifetimeSeconds = 60;
    private const int ShutdownWaitSeconds = 2;
    private const int ErrorResponseDrainSeconds = 5;
    private const int ErrorResponseDrainIdleMilliseconds = 500;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly HashSet<Task> _activeHandlers = new();
    private readonly string _accessToken;
    private readonly string _expectedPath;
    private readonly Func<string?> _pipeNameProvider;
    private readonly McpProtocolHandler _handler;
    private readonly Action<TcpListener> _stopListener;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private bool _disposed;
    private bool _disposeAttemptInProgress;

    public McpLoopbackServer(
        string accessToken,
        int preferredPort,
        Func<string?> pipeNameProvider,
        IMcpAgentBridge? agentBridge = null,
        Action<TcpListener>? stopListener = null)
    {
        if (!McpSecretStore.IsSafeToken(accessToken))
        {
            throw new ArgumentException("MCP 访问令牌格式无效。", nameof(accessToken));
        }

        _accessToken = accessToken;
        _expectedPath = "/mcp/" + accessToken;
        _pipeNameProvider = pipeNameProvider;
        _handler = new McpProtocolHandler(agentBridge ?? new SessionAgentControlClient(), pipeNameProvider);
        _stopListener = stopListener ?? (static listener => listener.Stop());
        PreferredPort = preferredPort;
    }

    public int PreferredPort { get; }

    public int Port { get; private set; }

    public bool IsListening => _listener is not null;

    public string? LastError { get; private set; }

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp/{_accessToken}";

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
            {
                return;
            }

            LastError = null;
            try
            {
                StartListener(PreferredPort);
            }
            catch (SocketException) when (PreferredPort != 0)
            {
                var fallbackMessage = $"端口 {PreferredPort} 已被占用，已改用系统分配的端口。";
                AppLog.Warn(fallbackMessage);
                StartListener(0);
                LastError = fallbackMessage;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var listener = _listener
                ?? throw new InvalidOperationException("MCP 监听器未成功创建。");
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        TcpListener? listener;
        Task? acceptLoop;
        Task[] handlers;
        lock (_gate)
        {
            if (_disposed || _disposeAttemptInProgress)
            {
                return;
            }

            // 只有底层监听器真正停止、活跃请求收尾完成后才能提交 disposed。
            // 任一步抛异常都保留字段和同一 TcpListener，让下一次 Dispose 能真正重试。
            _disposeAttemptInProgress = true;
            cancellation = _cancellation;
            listener = _listener;
            acceptLoop = _acceptLoop;
            handlers = new Task[_activeHandlers.Count];
            _activeHandlers.CopyTo(handlers);
        }

        try
        {
            cancellation?.Cancel();
            if (listener is not null)
            {
                _stopListener(listener);
            }

            WaitForActiveRequests(acceptLoop, handlers);

            lock (_gate)
            {
                _disposed = true;
                _cancellation = null;
                _listener = null;
                _acceptLoop = null;
                _activeHandlers.Clear();
            }

            cancellation?.Dispose();
        }
        finally
        {
            lock (_gate)
            {
                _disposeAttemptInProgress = false;
            }
        }
    }

    private static void WaitForActiveRequests(Task? acceptLoop, Task[] handlers)
    {
        var tasks = new List<Task>(handlers.Length + (acceptLoop is null ? 0 : 1));
        if (acceptLoop is not null)
        {
            tasks.Add(acceptLoop);
        }

        tasks.AddRange(handlers);
        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(ShutdownWaitSeconds)))
            {
                AppLog.Warn("MCP 控制服务关闭时仍有请求处理超时。");
            }
        }
        catch (AggregateException)
        {
            // 停止监听时 Accept 会以 SocketException/操作取消结束，属正常。
        }
    }

    private void StartListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        AppLog.Info($"MCP 控制服务已监听 http://127.0.0.1:{Port}");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                AppLog.Warn("MCP 监听器接受连接失败。", exception);
                continue;
            }

            if (!_connectionSlots.Wait(0))
            {
                // 认证前的连接也有上限，避免本机进程用连接洪泛耗尽线程和内存。
                client.Dispose();
                continue;
            }

            Task? handler = null;
            lock (_gate)
            {
                if (!_disposed && !_disposeAttemptInProgress)
                {
                    handler = HandleClientSafelyAsync(client, cancellationToken);
                    _activeHandlers.Add(handler);
                    _ = handler.ContinueWith(
                        completed =>
                        {
                            _ = completed.Exception;
                            lock (_gate)
                            {
                                _activeHandlers.Remove(completed);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            if (handler is null)
            {
                client.Dispose();
                _connectionSlots.Release();
                continue;
            }

            _ = handler;
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken serverCancellation)
    {
        try
        {
            using (client)
            {
                var drainPlan = new ResponseDrainPlan();
                try
                {
                    client.NoDelay = true;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(RequestLifetimeSeconds));
                    await HandleClientAsync(client, timeout.Token, drainPlan).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!serverCancellation.IsCancellationRequested)
                {
                    await TryWriteErrorAsync(client.GetStream(), 408, "Request Timeout").ConfigureAwait(false);
                }
                catch (RequestTooLargeException exception)
                {
                    drainPlan.RemainingBodyBytes = exception.RemainingBodyBytes;
                    await TryWriteErrorAsync(client.GetStream(), 413, "Request body too large.").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is MalformedChunkedRequestException
                                                      or MalformedRequestHeadException)
                {
                    await TryWriteErrorAsync(client.GetStream(), 400, "Bad Request.").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException
                                                      or SocketException
                                                      or ObjectDisposedException
                                                      or JsonException)
                {
                    // 客户端提前断开很常见，不把连接异常升级成服务级故障。
                    AppLog.Info($"MCP 客户端连接结束：{exception.Message}");
                }
                catch (Exception exception)
                {
                    AppLog.Warn("MCP 请求处理失败。", exception);
                }
                finally
                {
                    // 响应已经写出后把客户端已发送的剩余请求体读空，再半关闭发送
                    // 方向。否则未读请求体会让 Windows 在 Dispose 时发送 RST，客
                    // 户端可能看不到 401/404/413/415 等本应返回的 HTTP 错误。
                    await TryDrainBeforeCloseAsync(client, drainPlan.RemainingBodyBytes).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        ResponseDrainPlan drainPlan)
    {
        var stream = client.GetStream();
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(MaxRequestBodyReadTimeoutSeconds));
        var head = await ReadRequestHeadAsync(stream, readTimeout.Token).ConfigureAwait(false);
        if (head is null)
        {
            await WriteResponseAsync(stream, 431, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestHead = head.Value;
        drainPlan.RemainingBodyBytes = requestHead.Chunked
            ? null
            : Math.Max(0, requestHead.ContentLength - requestHead.BodyPrefix.Length);

        if (!IsExpectedHost(requestHead.Headers, out var hostError))
        {
            await WriteResponseAsync(stream, 403, ErrorBody(hostError), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsExpectedPath(requestHead.Target))
        {
            await WriteResponseAsync(stream, 404, ErrorBody("Not Found"), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedOrigin(requestHead.Headers))
        {
            await WriteResponseAsync(stream, 403, ErrorBody("Origin is not allowed."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(requestHead.Headers))
        {
            await WriteResponseAsync(
                stream,
                401,
                ErrorBody("Invalid bearer token."),
                cancellationToken,
                "WWW-Authenticate: Bearer\r\n").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(requestHead.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(
                stream,
                405,
                ErrorBody("Method Not Allowed"),
                cancellationToken,
                "Allow: POST\r\n").ConfigureAwait(false);
            return;
        }

        if (!IsJsonContentType(requestHead.Headers))
        {
            await WriteResponseAsync(
                stream,
                415,
                ErrorBody("Content-Type must be application/json."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = await ReadRequestBodyAsync(stream, requestHead, readTimeout.Token).ConfigureAwait(false);
        drainPlan.RemainingBodyBytes = 0;

        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            var parseError = McpProtocolHandler.Error(null, -32700, "Parse error");
            await WriteJsonAsync(stream, 400, parseError, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonNode? response;
        try
        {
            response = await _handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warn("MCP 方法处理失败。", exception);
            var internalError = McpProtocolHandler.Error(null, -32603, "Internal error");
            await WriteJsonAsync(stream, 500, internalError, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response is null)
        {
            await WriteResponseAsync(stream, 202, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(stream, 200, response, cancellationToken).ConfigureAwait(false);
    }

    private bool IsExpectedPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith('/'))
        {
            return false;
        }

        var question = target.IndexOf('?');
        var path = question >= 0 ? target[..question] : target;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_expectedPath));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private bool IsExpectedHost(Dictionary<string, string> headers, out string error)
    {
        error = "Host is not allowed.";
        if (!headers.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var allowed = new[]
        {
            $"127.0.0.1:{Port}",
            $"localhost:{Port}",
            $"[::1]:{Port}"
        };

        foreach (var candidate in allowed)
        {
            if (string.Equals(host.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAllowedOrigin(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("origin", out var origin) || string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Port != Port)
        {
            return false;
        }

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthorized(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("authorization", out var authorization)
            || string.IsNullOrWhiteSpace(authorization))
        {
            // 路径本身已经包含 256 位随机令牌；Bearer 头是可选加固，不强制客户端必须发送。
            return true;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = authorization[prefix.Length..].Trim();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_accessToken));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsJsonContentType(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("content-type", out var contentType))
        {
            return false;
        }

        // Content-Type 的媒体类型必须完整匹配，不能让 application/jsonfoo
        // 借前缀通过校验。合法参数（如 charset）由第一个分号分隔。
        var separator = contentType.IndexOf(';');
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequestHead?> ReadRequestHeadAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var headerBuffer = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var headerEnd = -1;
        try
        {
            while (true)
            {
                var remaining = MaxHeaderBytes - (int)headerBuffer.Length;
                if (remaining <= 0)
                {
                    return null;
                }

                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                var previousLength = (int)headerBuffer.Length;
                headerBuffer.Write(buffer, 0, read);
                var bytes = headerBuffer.GetBuffer();
                var currentLength = (int)headerBuffer.Length;
                var searchStart = Math.Max(0, previousLength - 3);
                for (var i = searchStart; i <= currentLength - 4; i++)
                {
                    if (bytes[i] == '\r' && bytes[i + 1] == '\n'
                        && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    {
                        headerEnd = i;
                        break;
                    }
                }

                if (headerEnd >= 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (headerEnd < 0)
        {
            return null;
        }

        var allBytes = headerBuffer.ToArray();
        var headerText = Encoding.Latin1.GetString(allBytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            throw new MalformedRequestHeadException("请求行缺失。");
        }

        // 请求行必须是严格的 "方法 SP 目标 SP HTTP/1.1"。不能用
        // RemoveEmptyEntries，否则双空格、尾随字段会被静默接受，
        // 给不同前端/代理留下请求走私的解释差异。
        var requestLine = lines[0];
        var firstSpace = requestLine.IndexOf(' ');
        var secondSpace = firstSpace >= 0 ? requestLine.IndexOf(' ', firstSpace + 1) : -1;
        if (firstSpace <= 0
            || secondSpace <= firstSpace + 1
            || requestLine.IndexOf(' ', secondSpace + 1) >= 0)
        {
            throw new MalformedRequestHeadException("请求行字段数无效。");
        }

        var method = requestLine[..firstSpace];
        var target = requestLine[(firstSpace + 1)..secondSpace];
        var version = requestLine[(secondSpace + 1)..];
        if (!IsHttpToken(method)
            || !string.Equals(version, "HTTP/1.1", StringComparison.Ordinal)
            || target.Length == 0
            || target.Any(static character => char.IsControl(character) || character == '#'))
        {
            throw new MalformedRequestHeadException("请求行无效。");
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            target = absoluteUri.PathAndQuery;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] is ' ' or '\t')
            {
                // 空头行和 obs-fold 都可能被不同中间件解释成另一组头，
                // 直接拒绝，不做“尽量容错”。
                throw new MalformedRequestHeadException("请求头包含空行或折行。");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new MalformedRequestHeadException("请求头缺少字段名或冒号。");
            }

            var name = line[..colon];
            if (!IsHttpToken(name))
            {
                throw new MalformedRequestHeadException("请求头字段名无效。");
            }

            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(static character =>
                    character == '\0'
                    || (character < ' ' && character != '\t')
                    || character == '\u007f'))
            {
                throw new MalformedRequestHeadException("请求头值包含控制字符。");
            }

            if (!headers.TryAdd(name, value))
            {
                // 重复的 Host/Content-Length/Authorization 等字段可能让不同层
                // 作出不同选择，统一拒绝而不是静默覆盖。
                throw new MalformedRequestHeadException("请求头字段重复。");
            }
        }

        var host = headers.TryGetValue("host", out var hostValue) ? hostValue : string.Empty;
        if (headers.TryGetValue("expect", out var expectation)
            && !string.Equals(
                expectation.Trim(' ', '\t'),
                "100-continue",
                StringComparison.OrdinalIgnoreCase))
        {
            // 不支持的 Expect 不能悄悄忽略后再等一个客户端永远不会发送的
            // 请求体；这里直接按坏请求拒绝。
            throw new MalformedRequestHeadException("Expect 不受支持。");
        }

        var bodyPrefixLength = allBytes.Length - headerEnd - 4;
        var bodyPrefix = new byte[bodyPrefixLength];
        if (bodyPrefixLength > 0)
        {
            Array.Copy(allBytes, headerEnd + 4, bodyPrefix, 0, bodyPrefixLength);
        }

        var chunked = false;
        if (headers.TryGetValue("transfer-encoding", out var transferEncoding))
        {
            // 目前只支持单一的 chunked。gzip、逗号列表和 "xchunked"
            // 都不能借子串匹配混入分块路径。
            if (!string.Equals(transferEncoding.Trim(' ', '\t'), "chunked", StringComparison.OrdinalIgnoreCase))
            {
                throw new MalformedRequestHeadException("Transfer-Encoding 不受支持。");
            }

            chunked = true;
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var contentLengthText))
        {
            if (chunked)
            {
                throw new MalformedRequestHeadException("Content-Length 不能与 Transfer-Encoding 同时出现。");
            }

            if (contentLengthText.Length == 0
                || contentLengthText.Any(static character => character is < '0' or > '9')
                || !int.TryParse(
                    contentLengthText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out contentLength))
            {
                throw new MalformedRequestHeadException("Content-Length 无效。");
            }
        }
        else if (!chunked)
        {
            throw new MalformedRequestHeadException("缺少 Content-Length 或 Transfer-Encoding。");
        }

        if (!chunked && contentLength > MaxRequestBytes)
        {
            // 认证前只读取请求头；超限请求体不会进入内存。
            throw new RequestTooLargeException(Math.Max(0, contentLength - bodyPrefix.Length));
        }

        return new HttpRequestHead(
            method,
            target,
            host,
            headers,
            contentLength,
            chunked,
            bodyPrefix);
    }

    /// <summary>
    /// 判断字符串是否是 RFC 9110 的 HTTP token。字段名和方法只允许 token
    /// 字符，不能把空白或分隔符带进后续解析。
    /// </summary>
    private static bool IsHttpToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                || character is '!' or '#' or '$' or '%' or '&' or '\''
                or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static async Task<byte[]> ReadRequestBodyAsync(
        NetworkStream stream,
        HttpRequestHead head,
        CancellationToken cancellationToken)
    {
        if (head.Headers.TryGetValue("expect", out var expect)
            && string.Equals(expect.Trim(' ', '\t'), "100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(continueBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (head.Chunked)
        {
            using var bodyStream = new MemoryStream();
            var reader = new PrefixedStreamReader(stream, head.BodyPrefix);
            await ReadChunkedBodyAsync(reader, bodyStream, cancellationToken).ConfigureAwait(false);
            return bodyStream.ToArray();
        }

        var body = new byte[head.ContentLength];
        var copied = Math.Min(head.BodyPrefix.Length, head.ContentLength);
        if (copied > 0)
        {
            Array.Copy(head.BodyPrefix, body, copied);
        }

        await ReadExactAsync(stream, body.AsMemory(copied), cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static async Task ReadChunkedBodyAsync(
        PrefixedStreamReader reader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        while (destination.Length <= MaxRequestBytes)
        {
            var sizeLine = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
            {
                throw new MalformedChunkedRequestException("分块请求体提前结束。");
            }

            var separator = sizeLine.IndexOf(';');
            var sizeText = separator >= 0 ? sizeLine[..separator] : sizeLine;
            if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)
                || size < 0)
            {
                throw new MalformedChunkedRequestException("分块请求体长度无效。");
            }

            if (size == 0)
            {
                var trailerBytes = 0;
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
                    if (trailer is null)
                    {
                        throw new MalformedChunkedRequestException("分块请求体 trailer 未完整结束。");
                    }

                    if (trailer.Length == 0)
                    {
                        return;
                    }

                    // 单行有 8 KiB 上限，但若不累计，客户端仍可用大量短 trailer
                    // 把连接拖到请求超时。trailer 与请求头一样按硬上限拒绝。
                    if (trailer.Length + 2 > MaxTrailerBytes - trailerBytes)
                    {
                        throw new MalformedChunkedRequestException("分块请求体 trailer 超过上限。");
                    }

                    trailerBytes += trailer.Length + 2;
                }
            }

            if (size > MaxRequestBytes - destination.Length)
            {
                throw new RequestTooLargeException();
            }

            var chunk = new byte[size];
            await reader.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            var crlf = new byte[2];
            await reader.ReadExactlyAsync(crlf, cancellationToken).ConfigureAwait(false);
            if (crlf[0] != '\r' || crlf[1] != '\n')
            {
                throw new MalformedChunkedRequestException("分块请求体缺少分隔符。");
            }
        }

        throw new IOException("分块请求体超过上限。");
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("请求体提前结束。");
            }

            offset += read;
        }
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        int status,
        JsonNode body,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }));
        await WriteResponseAsync(stream, status, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        byte[]? body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        body ??= Array.Empty<byte>();
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            _ => "Error"
        };

        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n")
            .Append(extraHeaders)
            .Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteErrorAsync(NetworkStream stream, int status, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await WriteResponseAsync(stream, status, ErrorBody(message), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接若已经断了，错误响应本身也没有意义。
        }
    }

    private static async Task TryDrainBeforeCloseAsync(
        TcpClient client,
        int? remainingBodyBytes)
    {
        try
        {
            if (!client.Connected)
            {
                return;
            }

            // 提前返回 413/401 时，客户端可能仍在发送已声明的大请求体。
            // 先保留接收方向并丢弃剩余请求体，避免对端上传尚未结束时收到
            // FIN 而中止传输；全部收完后再半关闭发送方向，让已排队的错误
            // 响应完整送达。恶意慢速连接仍受固定时限约束。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ErrorResponseDrainSeconds));
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                var stream = client.GetStream();
                while (remainingBodyBytes is not 0)
                {
                    var count = remainingBodyBytes is > 0
                        ? Math.Min(buffer.Length, remainingBodyBytes.Value)
                        : buffer.Length;
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    idleTimeout.CancelAfter(TimeSpan.FromMilliseconds(ErrorResponseDrainIdleMilliseconds));
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(
                            buffer.AsMemory(0, count),
                            idleTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Error responses must not park a connection slot for a client
                        // that declares a body but never sends it. Stop after a short
                        // idle interval; an actively uploading client keeps draining.
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (remainingBodyBytes is > 0)
                    {
                        remainingBodyBytes -= read;
                    }
                }

                client.Client.Shutdown(SocketShutdown.Send);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or SocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException
                                              or InvalidOperationException)
        {
            // 对端未按 Connection: close 收尾时，超时后直接关闭即可。
        }
    }

    private static byte[] ErrorBody(string message) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));

    private sealed class RequestTooLargeException : IOException
    {
        public RequestTooLargeException()
        {
        }

        public RequestTooLargeException(int remainingBodyBytes)
        {
            RemainingBodyBytes = remainingBodyBytes;
        }

        public int? RemainingBodyBytes { get; }
    }

    private sealed class ResponseDrainPlan
    {
        public int? RemainingBodyBytes { get; set; }
    }

    private sealed class MalformedRequestHeadException : IOException
    {
        public MalformedRequestHeadException(string message)
            : base(message)
        {
        }
    }

    private sealed class MalformedChunkedRequestException : IOException
    {
        public MalformedChunkedRequestException(string message)
            : base(message)
        {
        }
    }

    private readonly record struct HttpRequestHead(
        string Method,
        string Target,
        string Host,
        Dictionary<string, string> Headers,
        int ContentLength,
        bool Chunked,
        byte[] BodyPrefix);

    /// <summary>
    /// 让分块解码器先消费“已跟随请求头读进内存”的字节，再继续读网络流。
    /// </summary>
    private sealed class PrefixedStreamReader
    {
        private readonly Stream _stream;
        private readonly Memory<byte> _prefix;
        private int _offset;

        public PrefixedStreamReader(Stream stream, Memory<byte> prefix)
        {
            _stream = stream;
            _prefix = prefix;
        }

        public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            var one = new byte[1];
            while (line.Length <= maxBytes)
            {
                var read = await ReadAsync(one, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (line.Length == 0)
                    {
                        return null;
                    }

                    throw new MalformedChunkedRequestException("分块请求行未以 CRLF 结束。");
                }

                if (one[0] == '\n')
                {
                    var bytes = line.ToArray();
                    if (bytes.Length == 0 || bytes[^1] != '\r')
                    {
                        throw new MalformedChunkedRequestException("分块请求行未以 CRLF 结束。");
                    }

                    return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
                }

                line.WriteByte(one[0]);
            }

            throw new MalformedChunkedRequestException("分块请求行过长。");
        }

        public async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = await ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("请求体提前结束。");
                }

                offset += read;
            }
        }

        private async Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(destination.Length, _prefix.Length - _offset);
                _prefix.Slice(_offset, count).CopyTo(destination);
                _offset += count;
                return count;
            }

            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
