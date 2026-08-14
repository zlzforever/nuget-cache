using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Orbitra.Configuration;
using Orbitra.Services;

namespace Orbitra.Handlers;

/// <summary>
/// pip 代理请求处理器：承载 <c>/pip/{**path}</c> 通配路由（支持 GET/HEAD），仿 <see cref="NpmProxyHandler"/>
/// 的单 catch-all + 路径分类写法。路径分三类：<c>files/</c> 前缀为文件下载（wheel / sdist / PEP 658
/// 元数据，磁盘永久缓存到 <c>{CACHE_PATH}/pip/files/{path}</c>）；<c>simple/</c> 前缀下项目页
/// （<c>/simple/{name}/</c>）走内存短 TTL 缓存（TTL 由 <c>PIP_SIMPLE_TTL</c> 控制，按 Accept 变体
/// HTML / PEP 691 JSON 分 key），索引根（<c>/simple/</c>）兜底透传不缓存；其余路径 404。
/// 项目名按 PEP 503 规范化（小写 + <c>[-_.]+</c> 折叠为 <c>-</c>），缓存 key 与上游请求均用规范化名；
/// 返回前将内嵌的「配置上游主机 + 伴生文件主机」绝对文件 URL 重写为 <c>{代理域名}/pip/files/</c> 前缀，
/// 保留 <c>#sha256=</c> 片段，白名单外的绝对 URL 原样保留。
/// </summary>
public sealed class PipProxyHandler
{
    /// <summary>pip simple 项目页内存缓存 key 前缀。</summary>
    private const string SimpleCacheKeyPrefix = "pip:simple:";

    /// <summary>files 路由前缀（含尾斜杠）。</summary>
    private const string FilesPrefix = "files/";

    /// <summary>simple 路由前缀（含尾斜杠）。</summary>
    private const string SimplePrefix = "simple/";

    /// <summary>文件下载兜底 Content-Type（上游未提供时使用）。</summary>
    private const string FallbackFileContentType = "application/octet-stream";

    /// <summary>PEP 503 项目名规范化折叠正则：匹配一个或多个 <c>-</c>/<c>_</c>/<c>.</c> 连续字符。</summary>
    private static readonly Regex NormalizeNamePattern = new("[-_.]+", RegexOptions.CultureInvariant);

    private readonly ProxyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiskCacheService _diskCache;
    private readonly ILogger<PipProxyHandler> _logger;

    /// <summary>
    /// 绝对文件 URL 定向重写正则：匹配 <c>https?://{配置上游主机或伴生文件主机}/</c> 前缀，
    /// 匹配后仅替换为 <c>{代理域名}/pip/files/</c>，路径/查询/<c>#sha256=</c> 片段部分保留原样；
    /// 同时覆盖 HTML <c>href</c>/<c>data-core-metadata</c>/<c>data-dist-info-metadata</c> 属性
    /// 与 PEP 691 JSON <c>files[].url</c>/<c>core-metadata.url</c> 字段中的绝对 URL。
    /// </summary>
    private readonly Regex _rewriteUrlPattern;

    /// <summary>
    /// 初始化 pip 代理处理器。
    /// </summary>
    /// <param name="options">代理服务配置（含 pip 上游基址、伴生文件主机、simple TTL 与缓存根目录）。</param>
    /// <param name="cache">内存缓存（simple 项目页）。</param>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"PIP" 客户端）。</param>
    /// <param name="diskCache">共享磁盘缓存下载服务。</param>
    /// <param name="logger">结构化日志器。</param>
    public PipProxyHandler(
        ProxyOptions options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        DiskCacheService diskCache,
        ILogger<PipProxyHandler> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _diskCache = diskCache;
        _logger = logger;

        // 重写白名单：配置上游主机 + 伴生文件主机（pypi.org → files.pythonhosted.org），
        // 其余主机的绝对 URL 原样保留（客户端直连上游文件主机，功能不破坏）
        var hosts = new List<string> { Regex.Escape(options.PipUpstreamHost) };
        if (!string.IsNullOrEmpty(options.PipCompanionHost))
        {
            hosts.Add(Regex.Escape(options.PipCompanionHost));
        }

        _rewriteUrlPattern = new Regex(
            $"(?i)https?://(?:{string.Join("|", hosts)})/(?<rest>[^\\s\"'<>]*)",
            RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 处理 GET/HEAD /pip/{**path} 通配路由：空路径或未知前缀返回 404；路径安全校验失败返回 400；
    /// <c>files/</c> 前缀走磁盘永久缓存；<c>simple/</c> 前缀下项目页走内存短 TTL 缓存、索引根兜底透传。
    /// </summary>
    /// <param name="path">通配路由路径段（可为空，客户端项目页请求恒带尾斜杠）。</param>
    /// <param name="httpContext">当前请求上下文（用于读取 query string / Accept 头 / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>按命中/透传/失败分别返回本地文件、内容、状态码。</returns>
    public async Task<IResult> HandlePipRoute(string? path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var rawPath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            _logger.LogWarning("pip empty path rejected");
            return Results.NotFound();
        }

        // 客户端请求项目页恒带尾斜杠（/simple/{name}/），先去除末尾斜杠再做分类与安全校验；
        // files 路径无尾斜杠，simple 项目页上游拼接时补回规范尾斜杠
        var normalizedPath = rawPath.TrimEnd('/');
        if (normalizedPath.Length == 0)
        {
            _logger.LogWarning("pip empty path rejected");
            return Results.NotFound();
        }

        var (isValid, reason) = PathSafetyValidator.ValidatePath(normalizedPath);
        if (!isValid)
        {
            _logger.LogWarning("pip path rejected: {Path} - {Reason}", path, reason);
            return Results.BadRequest();
        }

        // simple 前缀（含根 /pip/simple 与 /pip/simple/）→ 项目页或索引根；
        // files 前缀要求后接具体文件路径，单独 /pip/files 不代理
        if (normalizedPath == "simple" || normalizedPath.StartsWith(SimplePrefix, StringComparison.Ordinal))
        {
            return await HandleSimple(rawPath, httpContext, cancellationToken);
        }

        if (normalizedPath.StartsWith(FilesPrefix, StringComparison.Ordinal))
        {
            return await HandlePipFile(normalizedPath, httpContext, cancellationToken);
        }

        _logger.LogWarning("pip unknown path: {Path}", path);
        return Results.NotFound();
    }

    /// <summary>
    /// 处理 pip 文件下载（/files/{**path}）：磁盘永久缓存到 <c>{CACHE_PATH}/pip/files/{path}</c>，
    /// 复用共享磁盘缓存服务（流式落盘 + 原子 rename）；上游 URL 由配置的文件主机基址
    /// （伴生主机或上游同主机）+ 相对路径拼接。
    /// </summary>
    /// <param name="path">去尾斜杠后的完整文件路径（<c>files/...</c>，与落盘路径一致）。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 query string）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>磁盘命中或下载成功后返回本地文件；上游非 2xx 透传状态码；磁盘写失败返回 503。</returns>
    private async Task<IResult> HandlePipFile(string path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(_options.CachePath, "pip", path);
        var fileRelPath = path.Substring(FilesPrefix.Length);
        var queryString = httpContext.Request.QueryString.Value;
        var targetUrl = string.IsNullOrEmpty(queryString)
            ? $"{_options.PipFileBaseUrl}/{fileRelPath}"
            : $"{_options.PipFileBaseUrl}/{fileRelPath}{queryString}";

        return await _diskCache.DownloadToCacheAsync(
            "PIP", new[] { targetUrl }, cacheFile, FallbackFileContentType, cancellationToken);
    }

    /// <summary>
    /// 处理 simple 路由分类：项目页（simple/{name}/）走内存短 TTL 缓存 + URL 重写；
    /// 索引根（simple/）兜底透传不缓存。
    /// </summary>
    /// <param name="rawPath">原始请求路径（可能带尾斜杠）。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 Accept / query string / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>项目页内存命中或上游 2xx 时返回重写后内容；索引根透传上游响应；上游非 2xx 透传状态码。</returns>
    private async Task<IResult> HandleSimple(string rawPath, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // 项目名 = simple/ 之后的剩余路径（去尾斜杠）；"simple" 与 "simple/" 均视为索引根
        var projectName = rawPath.Length > SimplePrefix.Length
            ? rawPath.Substring(SimplePrefix.Length).TrimEnd('/')
            : string.Empty;
        if (string.IsNullOrEmpty(projectName))
        {
            // 索引根 /simple/：全量项目列表体量大且客户端不依赖其缓存，透传不缓存
            return await HandleSimpleRoot(httpContext, cancellationToken);
        }

        // PEP 503 规范化：Django 与 django 命中同一缓存，且省去上游 301 重定向往返
        var normalizedName = NormalizeProjectName(projectName);
        return await HandleProjectPage(normalizedName, httpContext, cancellationToken);
    }

    /// <summary>
    /// 处理 simple 项目页：仅成功响应写内存缓存（TTL 由 <c>PIP_SIMPLE_TTL</c> 控制，默认 600 秒，
    /// 发布新版本后需尽快可见，绝不落盘），缓存 key 按 Accept 变体区分（HTML / PEP 691 JSON）；
    /// 返回前将内嵌的绝对文件 URL 重写为 <c>{代理域名}/pip/files/</c> 前缀，并显式设置
    /// Content-Length 保证 HEAD 与 GET 一致。
    /// </summary>
    /// <param name="normalizedName">PEP 503 规范化后的项目名。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 Accept / query string / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>内存命中或上游 2xx 时返回重写后内容；上游非 2xx 透传状态码；上游网络异常/超时返回 502。</returns>
    private async Task<IResult> HandleProjectPage(string normalizedName, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var variant = GetAcceptVariant(httpContext);
        var cacheKey = $"{SimpleCacheKeyPrefix}{normalizedName}:{variant}";

        if (_cache.TryGetValue(cacheKey, out PipSimpleCacheValue? cached) && cached != null)
        {
            _logger.LogInformation("pip simple cache hit: {Name} ({Variant})", normalizedName, variant);
            return TextContentResult.Build(httpContext, cached.Content, cached.ContentType);
        }

        var targetUrl = BuildSimpleUpstreamUrl(normalizedName, httpContext);
        var httpClient = _httpClientFactory.CreateClient("PIP");

        // 透传客户端 Accept 头，保证上游按 PEP 691 协商返回 HTML 或 JSON 变体（与缓存 key 变体一致）
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // 上游网络异常（连接失败/DNS/拒绝等）：与 files 路由及 maven-metadata 一致返回 502
            _logger.LogWarning("pip simple upstream failed: {Error} - {Url}", ex.Message, targetUrl);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 客户端未取消但请求超时（HttpClient.Timeout 触发）：视为上游失败返回 502
            _logger.LogWarning("pip simple upstream failed: timeout - {Url}", targetUrl);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("pip simple fetch failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
                return Results.StatusCode((int)response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html; charset=utf-8";

            // URL 重写：白名单主机（配置上游主机 + 伴生文件主机）的绝对 URL 前缀替换为
            // {domain}/pip/files/，路径/查询/#sha256= 片段保留原样（正则中 / 已被前缀消费，
            // 替换串需补回）；使用 MatchEvaluator 避免替换串中 $ 等字符被正则解释
            var rewrittenBody = _rewriteUrlPattern.Replace(
                body,
                match => $"{_options.NuGetProxyDomain.AbsoluteUri.TrimEnd('/')}/pip/files/{match.Groups["rest"].Value}");

            var ttl = TimeSpan.FromSeconds(_options.PipSimpleTtlSeconds);
            _cache.Set(cacheKey, new PipSimpleCacheValue(rewrittenBody, contentType), ttl);
            _logger.LogInformation("pip simple cached ({Ttl}s): {Name} ({Variant})",
                _options.PipSimpleTtlSeconds, normalizedName, variant);

            return TextContentResult.Build(httpContext, rewrittenBody, contentType);
        }
    }

    /// <summary>
    /// 处理 simple 索引根（/simple/）：兜底透传上游响应，不缓存。
    /// </summary>
    /// <param name="httpContext">当前请求上下文（用于透传 Accept / query string / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>上游 2xx 时返回响应体；上游非 2xx 透传状态码；上游网络异常/超时返回 502。</returns>
    private async Task<IResult> HandleSimpleRoot(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var queryString = httpContext.Request.QueryString.Value;
        var targetUrl = string.IsNullOrEmpty(queryString)
            ? _options.PipUpstream
            : _options.PipUpstream + queryString;

        var httpClient = _httpClientFactory.CreateClient("PIP");
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // 上游网络异常（连接失败/DNS/拒绝等）：与 files 路由及 maven-metadata 一致返回 502
            _logger.LogWarning("pip simple root upstream failed: {Error} - {Url}", ex.Message, targetUrl);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 客户端未取消但请求超时（HttpClient.Timeout 触发）：视为上游失败返回 502
            _logger.LogWarning("pip simple root upstream failed: timeout - {Url}", targetUrl);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("pip simple root fetch failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
                return Results.StatusCode((int)response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html; charset=utf-8";
            return TextContentResult.Build(httpContext, body, contentType);
        }
    }

    /// <summary>
    /// 拼接 simple 项目页上游 URL：<c>{PIP_UPSTREAM_URL}/{规范化名}/</c>，存在 query string 时原样透传。
    /// </summary>
    /// <param name="normalizedName">PEP 503 规范化后的项目名。</param>
    /// <param name="httpContext">当前请求上下文（用于读取原始 query string）。</param>
    /// <returns>上游完整请求 URL。</returns>
    private string BuildSimpleUpstreamUrl(string normalizedName, HttpContext httpContext)
    {
        var queryString = httpContext.Request.QueryString.Value;
        return string.IsNullOrEmpty(queryString)
            ? $"{_options.PipUpstream}/{normalizedName}/"
            : $"{_options.PipUpstream}/{normalizedName}/{queryString}";
    }

    /// <summary>
    /// 计算 Accept 变体：客户端 Accept 含 <c>vnd.pypi.simple</c> 且含 <c>+json</c> 视为
    /// PEP 691 JSON 变体，否则为 HTML 变体；两种变体分别缓存（与 npm 的元数据变体模式一致）。
    /// </summary>
    /// <param name="httpContext">当前请求上下文（用于读取 Accept 头）。</param>
    /// <returns>变体标识（<c>json</c> 或 <c>html</c>）。</returns>
    private static string GetAcceptVariant(HttpContext httpContext)
    {
        var accept = httpContext.Request.Headers.Accept.ToString();
        return accept.Contains("vnd.pypi.simple", StringComparison.OrdinalIgnoreCase) &&
               accept.Contains("+json", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "html";
    }

    /// <summary>
    /// PEP 503 项目名规范化：先小写，再将连续的 <c>-</c>/<c>_</c>/<c>.</c> 折叠为单个 <c>-</c>
    /// （如 <c>Django</c> → <c>django</c>、<c>my.pkg</c> → <c>my-pkg</c>），与 pip 客户端的
    /// canonicalize_name 行为一致；缓存 key 与上游请求均使用规范化名。
    /// </summary>
    /// <param name="name">原始项目名（来自请求路径段，非空）。</param>
    /// <returns>规范化后的项目名。</returns>
    /// <exception cref="ArgumentNullException">项目名为 null 时抛出。</exception>
    public static string NormalizeProjectName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NormalizeNamePattern.Replace(name, "-").ToLowerInvariant();
    }

    /// <summary>
    /// pip simple 项目页内存缓存项：保存重写后的页面内容与上游 Content-Type。
    /// </summary>
    /// <param name="Content">重写后的项目页内容（HTML 或 PEP 691 JSON）。</param>
    /// <param name="ContentType">上游返回的 Content-Type（缓存命中时原样回放）。</param>
    private sealed record PipSimpleCacheValue(string Content, string ContentType);
}
