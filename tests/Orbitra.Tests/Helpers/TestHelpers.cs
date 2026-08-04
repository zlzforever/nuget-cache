using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbitra.Configuration;
using Orbitra.Handlers;
using Orbitra.Services;

namespace Orbitra.Tests.Helpers;

/// <summary>
/// 环境变量作用域：构造时记录并设置一组环境变量，释放时恢复原值，
/// 用于隔离 <see cref="ProxyOptions.Load"/> 对环境变量读数的测试副作用。
/// </summary>
public sealed class EnvVarScope : IDisposable
{
    /// <summary>被修改环境变量的原值快照（null 表示原本未设置）。</summary>
    private readonly Dictionary<string, string?> _originalValues = new();

    /// <summary>
    /// 记录并覆盖指定环境变量。
    /// </summary>
    /// <param name="variables">待设置的环境变量键值对（值为 null 表示清除）。</param>
    public EnvVarScope(params (string Key, string? Value)[] variables)
    {
        foreach (var (key, value) in variables)
        {
            _originalValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// 恢复全部被修改的环境变量原值。
    /// </summary>
    public void Dispose()
    {
        foreach (var (key, original) in _originalValues)
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }
}

/// <summary>
/// 记录的请求快照：方法 + 完整 URL + 请求头集合，避免持有已释放的
/// <see cref="HttpRequestMessage"/>。
/// </summary>
/// <param name="Method">HTTP 方法。</param>
/// <param name="Url">完整请求 URL。</param>
/// <param name="Headers">请求头快照（键为头名，值为逗号拼接值）。</param>
public sealed record RecordedRequest(HttpMethod Method, string Url, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>是否存在指定请求头。</summary>
    public bool HasHeader(string name) => Headers.ContainsKey(name);

    /// <summary>读取指定请求头的值；不存在时返回 null。</summary>
    public string? GetHeader(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// 可编排的假 HTTP 上游处理器：按请求返回脚本化响应，或抛出异常模拟网络故障，
/// 同时记录全部收到的请求供断言使用。
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>
    /// 初始化假上游处理器。
    /// </summary>
    /// <param name="responder">请求到响应的映射函数（可抛异常模拟网络故障）。</param>
    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    /// <summary>已收到的全部请求快照（按到达顺序）。</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>
    /// 统计满足条件的请求数量。
    /// </summary>
    /// <param name="predicate">请求过滤条件。</param>
    /// <returns>符合条件的请求数。</returns>
    public int CountRequests(Predicate<RecordedRequest> predicate) => _requests.Count(r => predicate(r));

    /// <summary>
    /// 将请求转发给响应函数并记录快照；响应函数抛出 <see cref="HttpRequestException"/>
    /// 时模拟连接拒绝/网络故障。
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = new RecordedRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
        _requests.Enqueue(snapshot);
        return _responder(request);
    }
}

/// <summary>
/// 假 <see cref="IHttpClientFactory"/>：对任意命名客户端返回同一个底层处理器创建的 HttpClient，
/// 使测试可以复用单一可编排处理器观测全部上游请求。
/// </summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    /// <summary>
    /// 初始化假工厂。
    /// </summary>
    /// <param name="handler">所有命名客户端共享的底层处理器。</param>
    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// 创建共享底层处理器的 HttpClient（不接管处理器生命周期）。
    /// </summary>
    /// <param name="name">命名客户端名称（本实现忽略）。</param>
    /// <returns>复用同一处理器的 HttpClient。</returns>
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// 捕获日志消息的日志器：将所有日志通过格式化函数转为纯文本收集，
/// 用于断言 Authorization 头与 token 等敏感信息绝不落日志。
/// </summary>
/// <typeparam name="T">日志类别名称。</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>已收集的日志消息文本（按到达顺序）。</summary>
    public List<string> Messages { get; } = new();

    /// <summary>
    /// 创建日志作用域（测试中返回 null，不启用作用域）。
    /// </summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// 日志开关恒为启用。
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <summary>
    /// 将日志项格式化为纯文本后收集。
    /// </summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += " | " + exception.Message;
        }

        Messages.Add(message);
    }

    /// <summary>全部日志合并为单个字符串，便于一次性断言。</summary>
    public string AllText() => string.Join(Environment.NewLine, Messages);
}

/// <summary>
/// HTTP 测试执行辅助：构造 <see cref="DefaultHttpContext"/> 并执行处理器返回的
/// <see cref="IResult"/>。
/// </summary>
public static class HttpTestHelper
{
    /// <summary>
    /// 执行 handler 动作并返回响应快照。
    /// </summary>
    /// <param name="action">接收上下文并返回 IResult 的处理器动作。</param>
    /// <param name="method">请求方法（GET/HEAD 等）。</param>
    /// <param name="accept">Accept 请求头（可为 null）。</param>
    /// <param name="authorization">Authorization 请求头（可为 null）。</param>
    /// <param name="queryString">原始 query string（含 '?'，可为 null）。</param>
    /// <returns>元组：状态码 + 响应头 + 响应体字节。</returns>
    public static async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> ExecuteAsync(
        Func<HttpContext, CancellationToken, Task<IResult>> action,
        string method = "GET",
        string? accept = null,
        string? authorization = null,
        string? queryString = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        // IResult 执行依赖 RequestServices（日志器等），提供最小服务集合
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        context.RequestServices = services.BuildServiceProvider();

        var body = new MemoryStream();
        context.Response.Body = body;
        var result = await action(context, CancellationToken.None);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers, body.ToArray());
    }

    /// <summary>将响应体字节按 UTF-8 解码为字符串。</summary>
    public static string DecodeBody(byte[] body) => Encoding.UTF8.GetString(body);
}

/// <summary>
/// 测试用 docker 配置工厂：通过环境变量作用域构造 <see cref="ProxyOptions"/>，
/// 使配置校验（fail-fast）与归一化逻辑走与生产完全一致的路径。
/// </summary>
public static class TestProxyOptionsFactory
{
    /// <summary>
    /// 构造 docker 专用配置：固定 NuGet 代理域名，指定缓存目录与上游列表。
    /// </summary>
    /// <param name="cachePath">磁盘缓存根目录。</param>
    /// <param name="upstreamUrl">DOCKER_UPSTREAM_URL 原值（逗号分隔多上游）。</param>
    /// <param name="blobVerify">DOCKER_BLOB_VERIFY 开关（null 表示不设置）。</param>
    /// <param name="tagTtl">DOCKER_TAG_TTL 秒数（null 表示不设置）。</param>
    /// <param name="manifestTtl">DOCKER_MANIFEST_TTL 秒数（null 表示不设置）。</param>
    /// <returns>校验并归一化后的配置对象。</returns>
    public static ProxyOptions CreateDockerOptions(
        string cachePath,
        string upstreamUrl,
        bool? blobVerify = null,
        string? tagTtl = null,
        string? manifestTtl = null)
    {
        using var scope = new EnvVarScope(
            (ProxyOptions.NuGetProxyDomainVariable, "https://proxy.example.com"),
            (ProxyOptions.CachePathVariable, cachePath),
            (ProxyOptions.DockerUpstreamUrlVariable, upstreamUrl),
            (ProxyOptions.DockerBlobVerifyVariable, blobVerify?.ToString()),
            (ProxyOptions.DockerTagTtlVariable, tagTtl),
            (ProxyOptions.DockerManifestTtlVariable, manifestTtl));
        return ProxyOptions.Load(NullLogger<ProxyOptions>.Instance);
    }
}

/// <summary>
/// 测试脚本化响应工厂：提供 JSON / 二进制 / 401 挑战等常见上游响应构造。
/// </summary>
public static class FakeResponses
{
    /// <summary>
    /// 构造 JSON 文本响应。
    /// </summary>
    /// <param name="body">响应体文本。</param>
    /// <param name="contentType">Content-Type（可为 null，默认 application/json）。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <returns>构造完成的响应对象。</returns>
    public static HttpResponseMessage Json(
        string body, string? contentType = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>
    /// 构造二进制响应（可指定 Content-Type）。
    /// </summary>
    /// <param name="bytes">响应体字节。</param>
    /// <param name="contentType">Content-Type（可为 null，默认 octet-stream）。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <returns>构造完成的响应对象。</returns>
    public static HttpResponseMessage Bytes(
        byte[] bytes, string? contentType = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>
    /// 构造带 Bearer 挑战的 401 响应（Docker Hub 鉴权质询格式）。
    /// </summary>
    /// <param name="realm">token 换取端点。</param>
    /// <param name="service">service 标识。</param>
    /// <param name="scope">scope 声明。</param>
    /// <returns>401 + WWW-Authenticate: Bearer 挑战。</returns>
    public static HttpResponseMessage BearerChallenge401(string realm, string service, string scope)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
            "Bearer",
            $"realm=\"{realm}\",service=\"{service}\",scope=\"{scope}\""));
        return response;
    }
}

/// <summary>
/// docker 处理器测试基座：装配 ProxyOptions / 内存缓存 / 假上游 / 各服务，
/// 提供缓存目录生命周期管理与按需重建处理器（模拟进程重启后的磁盘命中）。
/// </summary>
public sealed class DockerTestHarness : IDisposable
{
    /// <summary>测试用磁盘缓存根目录。</summary>
    public string CachePath { get; }

    /// <summary>测试用代理配置。</summary>
    public ProxyOptions Options { get; }

    /// <summary>可编排的假上游处理器。</summary>
    public FakeHttpMessageHandler Upstream { get; }

    /// <summary>共享内存缓存实例。</summary>
    public IMemoryCache MemoryCache { get; }

    /// <summary>捕获日志的处理器日志器。</summary>
    public CapturingLogger<DockerProxyHandler> Logger { get; }

    /// <summary>当前装配的 docker 处理器。</summary>
    public DockerProxyHandler Handler { get; private set; }

    /// <summary>假 HttpClient 工厂。</summary>
    private readonly FakeHttpClientFactory _factory;

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 初始化测试基座（私有构造，经 <see cref="Create"/> 创建）。
    /// </summary>
    private DockerTestHarness(
        string cachePath,
        ProxyOptions options,
        FakeHttpMessageHandler upstream,
        IMemoryCache memoryCache,
        CapturingLogger<DockerProxyHandler> logger,
        FakeHttpClientFactory factory,
        DockerProxyHandler handler)
    {
        CachePath = cachePath;
        Options = options;
        Upstream = upstream;
        MemoryCache = memoryCache;
        Logger = logger;
        _factory = factory;
        Handler = handler;
    }

    /// <summary>
    /// 创建测试基座。
    /// </summary>
    /// <param name="upstreamUrl">DOCKER_UPSTREAM_URL 原值（逗号分隔多上游）。</param>
    /// <param name="responder">假上游的响应编排函数。</param>
    /// <param name="blobVerify">DOCKER_BLOB_VERIFY 开关（null 表示不设置）。</param>
    /// <param name="tagTtl">DOCKER_TAG_TTL（null 表示不设置）。</param>
    /// <param name="manifestTtl">DOCKER_MANIFEST_TTL（null 表示不设置）。</param>
    /// <returns>装配完成的测试基座。</returns>
    public static DockerTestHarness Create(
        string upstreamUrl,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        bool? blobVerify = null,
        string? tagTtl = null,
        string? manifestTtl = null)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "orbitra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var options = TestProxyOptionsFactory.CreateDockerOptions(
            cachePath, upstreamUrl, blobVerify, tagTtl, manifestTtl);
        var upstream = new FakeHttpMessageHandler(responder);
        var factory = new FakeHttpClientFactory(upstream);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<DockerProxyHandler>();
        var diskCache = new DiskCacheService(factory, options, NullLogger<DiskCacheService>.Instance);
        var tokenService = new DockerTokenService(factory, memory, NullLogger<DockerTokenService>.Instance);
        var handler = new DockerProxyHandler(options, memory, factory, diskCache, tokenService, logger);
        return new DockerTestHarness(cachePath, options, upstream, memory, logger, factory, handler);
    }

    /// <summary>
    /// <summary>
    /// 使用全新内存缓存与全新处理器重建（模拟进程重启后仅剩磁盘缓存），
    /// 共享同一缓存目录与假上游。
    /// </summary>
    /// <returns>重建后的测试基座。</returns>
    public DockerTestHarness RebuildFreshMemory()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var logger = new CapturingLogger<DockerProxyHandler>();
        var diskCache = new DiskCacheService(_factory, Options, NullLogger<DiskCacheService>.Instance);
        var tokenService = new DockerTokenService(_factory, memory, NullLogger<DockerTokenService>.Instance);
        var handler = new DockerProxyHandler(Options, memory, _factory, diskCache, tokenService, logger);
        var harness = new DockerTestHarness(CachePath, Options, Upstream, memory, logger, _factory, handler);
        return harness;
    }

    /// <summary>
    /// 清理测试缓存目录。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(CachePath))
            {
                Directory.Delete(CachePath, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不阻断后续用例
        }
        catch (UnauthorizedAccessException)
        {
            // 测试清理失败不阻断后续用例
        }
    }
}

/// <summary>
/// digest 计算辅助：为测试构造与内容匹配的 sha256 digest。
/// </summary>
public static class DigestTestHelper
{
    /// <summary>
    /// 计算字节内容的 sha256 digest（形如 sha256:hex）。
    /// </summary>
    /// <param name="content">内容字节。</param>
    /// <returns>完整 digest 字符串。</returns>
    public static string Of(byte[] content) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
}
