using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class McpProtocolHandlerTests
{
    [Fact]
    public async Task 非字符串字段返回无效请求而不是抛异常()
    {
        var handler = CreateHandler();
        var message = JsonNode.Parse("""
            {"jsonrpc":2,"id":1,"method":"ping","params":{}}
            """);

        var response = await handler.HandleAsync(message, CancellationToken.None);

        Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task 通知不产生响应()
    {
        var handler = CreateHandler();
        var message = JsonNode.Parse("""
            {"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
            """);

        var response = await handler.HandleAsync(message, CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task 初始化协商已知版本并回退未知版本()
    {
        var handler = CreateHandler();
        var known = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}
            """), CancellationToken.None);
        var unknown = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1900-01-01"}}
            """), CancellationToken.None);

        Assert.Equal("2025-03-26", known!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpProtocolHandler.LatestProtocolVersion, unknown!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task 工具清单包含完整桌面控制能力()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            """), CancellationToken.None);
        var tools = response!["result"]!["tools"]!.AsArray();

        Assert.Equal(17, tools.Count);
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_screenshot");
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_keyboard_hotkey");
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_clipboard_write");
    }

    [Fact]
    public async Task 工具调用转发管道和对象参数()
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");

        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"desktop_mouse_move","arguments":{"x":12,"y":34}}}
            """), CancellationToken.None);

        Assert.Equal("DesktopTwin.AgentControl.test", bridge.LastPipeName);
        Assert.Equal("desktop_mouse_move", bridge.LastOperation);
        Assert.Equal(12, bridge.LastArguments.GetProperty("x").GetInt32());
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 非法工具参数返回工具错误()
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");

        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"desktop_mouse_move","arguments":[1,2]}}
            """), CancellationToken.None);

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Null(bridge.LastOperation);
    }

    [Theory]
    [InlineData("""{"x":12}""")]
    [InlineData("""{"x":12,"y":34,"unknown":true}""")]
    [InlineData("""{"x":"12","y":34}""")]
    [InlineData("""{"x":12.5,"y":34}""")]
    [InlineData("""{"x":null,"y":34}""")]
    public async Task 工具参数按输入模式拒绝缺失字段未知字段和错误类型(string argumentsJson)
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "desktop_mouse_move",
                ["arguments"] = JsonNode.Parse(argumentsJson)
            }
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.StartsWith(
            "invalid_arguments:",
            response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(bridge.LastOperation);
    }

    [Fact]
    public async Task 批量请求超过上限时整体拒绝()
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");
        var batch = new JsonArray();
        for (var index = 0; index <= McpProtocolHandler.MaxBatchItems; index++)
        {
            batch.Add(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = index,
                ["method"] = "ping"
            });
        }

        var response = await handler.HandleAsync(batch, CancellationToken.None);

        Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
        Assert.Null(bridge.LastOperation);
    }

    private static McpProtocolHandler CreateHandler()
    {
        return new McpProtocolHandler(new RecordingBridge(), () => null);
    }

    private sealed class RecordingBridge : IMcpAgentBridge
    {
        public string? LastPipeName { get; private set; }

        public string? LastOperation { get; private set; }

        public JsonElement LastArguments { get; private set; }

        public Task<McpAgentResult> ExecuteAsync(
            string pipeName,
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            LastPipeName = pipeName;
            LastOperation = operation;
            LastArguments = arguments.Clone();
            return Task.FromResult(McpAgentResult.Success(
                JsonSerializer.SerializeToElement(new { ok = true })));
        }
    }
}

public sealed class McpSecretStoreTests
{
    [Fact]
    public void 首次启用会生成随机且安全的令牌和管道名()
    {
        var settings = new McpSettings();

        Assert.True(McpSecretStore.Ensure(settings));
        Assert.True(McpSecretStore.IsSafeToken(settings.AccessToken));
        Assert.True(McpSecretStore.IsSafePipeName(settings.PipeName));
        Assert.StartsWith("DesktopTwin.AgentControl.", settings.PipeName);
        Assert.Equal(McpSettings.DefaultPort, settings.Port);
    }

    [Fact]
    public void 已有安全值时不会重复生成()
    {
        var settings = new McpSettings
        {
            AccessToken = McpSecretStore.CreateBase64Url(32),
            PipeName = "DesktopTwin.AgentControl." + McpSecretStore.CreateHex(16)
        };
        var token = settings.AccessToken;
        var pipe = settings.PipeName;

        Assert.False(McpSecretStore.Ensure(settings));
        Assert.Equal(token, settings.AccessToken);
        Assert.Equal(pipe, settings.PipeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces and must not fit in a URL path at all")]
    [InlineData("invalid+base64/token-with-illegal-characters")]
    public void 不安全令牌会被拒绝(string token)
    {
        Assert.False(McpSecretStore.IsSafeToken(token));
    }

    [Fact]
    public void 每次生成的秘密都不同()
    {
        Assert.NotEqual(McpSecretStore.CreateBase64Url(32), McpSecretStore.CreateBase64Url(32));
        Assert.NotEqual(McpSecretStore.CreateHex(16), McpSecretStore.CreateHex(16));
    }

    [Fact]
    public void 分身端参数支持只开控制通道()
    {
        var arguments = SessionAgentService.BuildAgentArguments(
            null,
            "DesktopTwin.AgentControl.0123456789abcdef0123456789abcdef",
            1);

        Assert.Equal(
            "--no-hotkey --control-pipe \"DesktopTwin.AgentControl.0123456789abcdef0123456789abcdef\" --host-session 1",
            arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 控制通道必须携带正整数宿主会话身份(int hostSessionId)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SessionAgentService.BuildAgentArguments(
                null,
                "DesktopTwin.AgentControl.0123456789abcdef0123456789abcdef",
                hostSessionId));

        Assert.Equal("hostSessionId", exception.ParamName);
    }

    [Fact]
    public void 没有控制通道时允许省略宿主会话身份()
    {
        Assert.Equal("--no-hotkey", SessionAgentService.BuildAgentArguments(null, null));
        Assert.Equal(
            "--hotkey \"Ctrl+Alt+Q\"",
            SessionAgentService.BuildAgentArguments("Ctrl+Alt+Q", null));
    }
}

public sealed class McpLoopbackServerTests
{
    [Fact]
    public async Task 带路径令牌的请求可完成初始化()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.PostAsync(
            server.Endpoint,
            JsonContent("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DesktopTwin", body!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.OK)]
    [InlineData("application/json; charset=utf-8", HttpStatusCode.OK)]
    [InlineData("Application/JSON ; charset=utf-8", HttpStatusCode.OK)]
    [InlineData("application/jsonfoo", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json-patch+json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/application/json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("", HttpStatusCode.UnsupportedMediaType)]
    public async Task ContentType必须是完整的JSON媒体类型(string contentType, HttpStatusCode expectedStatus)
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        if (contentType.Length > 0)
        {
            Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", contentType));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task 错误的认证头会被拒绝()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 非本机主机名和外部来源会被拒绝()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var hostRequest = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")
        };
        hostRequest.Headers.Host = "evil.example";

        var hostResponse = await client.SendAsync(hostRequest);

        using var originRequest = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""")
        };
        originRequest.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        var originResponse = await client.SendAsync(originRequest);

        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);
    }

    [Fact]
    public async Task 错误路径返回未找到()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.PostAsync(
            $"http://127.0.0.1:{server.Port}/mcp/wrong-token",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Content-Length: 8388609\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n")]
    public async Task 超限请求体返回413(string framingHeader)
    {
        using var server = CreateServer();
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var chunk = framingHeader.StartsWith("Transfer-Encoding", StringComparison.Ordinal)
            ? "800001\r\n"
            : string.Empty;
        var request = $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Type: application/json\r\n{framingHeader}Connection: close\r\n\r\n{chunk}";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var response = await reader.ReadToEndAsync(timeout.Token);

        Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 超大分块长度不会整数溢出并返回413()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n" +
            "7fffffff\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var response = await reader.ReadToEndAsync(timeout.Token);

        Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未认证请求不读取请求体()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var request = $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Type: application/json\r\nAuthorization: Bearer wrong\r\nContent-Length: {8 * 1024 * 1024}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var response = await reader.ReadToEndAsync(timeout.Token);

        Assert.StartsWith("HTTP/1.1 401", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 认证前超大ContentLength在读取请求体前返回413()
    {
        using var server = CreateServer();
        server.Start();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            "Authorization: Bearer wrong\r\n" +
            $"Content-Length: {(8 * 1024 * 1024) + 1}\r\n" +
            "Connection: close\r\n\r\n";

        var statusLine = await ReadStatusLineAsync(server, request);

        Assert.StartsWith("HTTP/1.1 413", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求头刚好达到32KiB时仍可处理()
    {
        using var server = CreateServer();
        server.Start();
        var json = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var request = BuildRequestWithHeaderSize(server, 32 * 1024, json);

        var statusLine = await ReadStatusLineAsync(server, request);

        Assert.StartsWith("HTTP/1.1 200", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求头超过32KiB时不会因预读越过边界而接受()
    {
        using var server = CreateServer();
        server.Start();
        var json = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var request = BuildRequestWithHeaderSize(server, (32 * 1024) + 512, json);

        // 先写入一段不足 4096 字节的数据，让旧实现的后续缓冲越过 32 KiB 边界。
        var statusLine = await ReadStatusLineAsync(server, request, firstWriteBytes: 1000, delayAfterFirstWrite: true);

        Assert.StartsWith("HTTP/1.1 431", statusLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST {path} HTTP/1.1 extra")]
    [InlineData("POST  {path} HTTP/1.1")]
    [InlineData("POST {path}  HTTP/1.1")]
    [InlineData("POST\t{path}\tHTTP/1.1")]
    [InlineData("POST {path} HTTP/1.0")]
    public async Task 请求行必须严格为三字段且只支持HTTP11(string requestLineTemplate)
    {
        using var server = CreateServer();
        server.Start();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var request =
            $"{requestLineTemplate.Replace("{path}", path, StringComparison.Ordinal)}\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";

        var statusLine = await ReadStatusLineAsync(server, request);

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Bad")]
    [InlineData("Host : 127.0.0.1")]
    [InlineData(" Host: 127.0.0.1")]
    [InlineData("X Bad: value")]
    [InlineData("Content-Length: +0")]
    [InlineData("Content-Length: -1")]
    [InlineData("Content-Length: 0\r\nContent-Length: 0")]
    [InlineData("Transfer-Encoding: xchunked")]
    [InlineData("Transfer-Encoding: gzip, chunked")]
    [InlineData("Content-Length: 0\r\nTransfer-Encoding: chunked")]
    [InlineData("Authorization: Bearer a\r\nAuthorization: Bearer b")]
    [InlineData("Expect: 100-continue-extra")]
    public async Task 不合规请求头或重复头返回400(string extraHeaders)
    {
        using var server = CreateServer();
        server.Start();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var hasFramingHeader =
            extraHeaders.Contains("Content-Length", StringComparison.OrdinalIgnoreCase)
            || extraHeaders.Contains("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            (hasFramingHeader ? string.Empty : "Content-Length: 0\r\n") +
            extraHeaders +
            "\r\nConnection: close\r\n\r\n";

        var statusLine = await ReadStatusLineAsync(server, request);

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Expect100Continue先返回临时响应再读取请求体()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var json = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var body = Encoding.UTF8.GetBytes(json);
        var requestHead =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Expect: 100-continue\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHead));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var continueStatus = await reader.ReadLineAsync(timeout.Token);
        var continueEnd = await reader.ReadLineAsync(timeout.Token);
        await stream.WriteAsync(body);
        var finalStatus = await reader.ReadLineAsync(timeout.Token);

        Assert.Equal("HTTP/1.1 100 Continue", continueStatus);
        Assert.Equal(string.Empty, continueEnd);
        Assert.StartsWith("HTTP/1.1 200", finalStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose会限时等待阻塞中的活跃请求()
    {
        var bridge = new BlockingBridge();
        using var server = CreateServer(bridge);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"desktop_mouse_move","arguments":{"x":1,"y":1}}}""";
        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n" +
            "Connection: close\r\n\r\n" +
            json;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await bridge.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        server.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Dispose 耗时 {stopwatch.Elapsed}。");
        Assert.False(server.IsListening);
        bridge.Release.TrySetResult(McpAgentResult.Success(
            JsonSerializer.SerializeToElement(new { ok = true })));
        await Task.Delay(100);
    }

    [Theory]
    [InlineData("X-Trailer: value")]
    [InlineData("X-Trailer: value\r\n")]
    [InlineData("X-Trailer: value\n")]
    public async Task 未完整结束的分块trailer返回400(string trailer)
    {
        using var server = CreateServer();
        server.Start();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var json = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n" +
            $"{Encoding.UTF8.GetByteCount(json):x}\r\n{json}\r\n0\r\n{trailer}";

        var statusLine = await ReadStatusLineAsync(server, request, halfCloseSend: true);

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分块trailer总量超过32KiB返回400()
    {
        using var server = CreateServer();
        server.Start();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var json = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var trailers = new StringBuilder();
        while (Encoding.ASCII.GetByteCount(trailers.ToString()) <= 32 * 1024)
        {
            trailers.Append("X-Fill: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n");
        }

        var request =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n" +
            $"{Encoding.UTF8.GetByteCount(json):x}\r\n{json}\r\n0\r\n" +
            trailers +
            "\r\n";

        var statusLine = await ReadStatusLineAsync(server, request);

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }

    private static string BuildRequestWithHeaderSize(
        McpLoopbackServer server,
        int headerBytes,
        string json)
    {
        var path = new Uri(server.Endpoint).PathAndQuery;
        var bodyBytes = Encoding.UTF8.GetByteCount(json);
        var beforePadding =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bodyBytes}\r\n" +
            "Connection: close\r\n" +
            "X-Fill: ";
        const string afterPadding = "\r\n\r\n";
        var paddingBytes = headerBytes
            - Encoding.ASCII.GetByteCount(beforePadding)
            - Encoding.ASCII.GetByteCount(afterPadding);
        Assert.True(paddingBytes >= 0);

        return beforePadding + new string('a', paddingBytes) + afterPadding + json;
    }

    private static async Task<string> ReadStatusLineAsync(
        McpLoopbackServer server,
        string request,
        int firstWriteBytes = 0,
        bool delayAfterFirstWrite = false,
        bool halfCloseSend = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        if (firstWriteBytes > 0 && firstWriteBytes < bytes.Length)
        {
            await stream.WriteAsync(bytes.AsMemory(0, firstWriteBytes));
            if (delayAfterFirstWrite)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            await stream.WriteAsync(bytes.AsMemory(firstWriteBytes));
        }
        else
        {
            await stream.WriteAsync(bytes);
        }

        if (halfCloseSend)
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
    }

    private static McpLoopbackServer CreateServer(IMcpAgentBridge? bridge = null)
    {
        return new McpLoopbackServer(
            McpSecretStore.CreateBase64Url(32),
            0,
            () => "DesktopTwin.AgentControl.test",
            bridge ?? new StubBridge());
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class StubBridge : IMcpAgentBridge
    {
        public Task<McpAgentResult> ExecuteAsync(
            string pipeName,
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(McpAgentResult.Success(
                JsonSerializer.SerializeToElement(new { ok = true })));
        }
    }

    private sealed class BlockingBridge : IMcpAgentBridge
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<McpAgentResult> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<McpAgentResult> ExecuteAsync(
            string pipeName,
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Release.Task;
        }
    }
}
