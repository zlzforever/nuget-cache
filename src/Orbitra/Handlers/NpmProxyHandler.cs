using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Orbitra.Configuration;
using Orbitra.Services;

namespace Orbitra.Handlers;

/// <summary>
/// npm 代理请求处理器：承载 /npm/{**path} 通配路由（支持 GET/HEAD），路径与原请求 1:1 透传上游，
/// query string 原样透传。tarball（路径含 <c>/-/</c> 或 <c>.tgz</c> 结尾）经 <see cref="DiskCacheService"/>
/// 磁盘永久缓存到 <c>{CACHE_PATH}/npm/{path}</c>；包元数据（<c>/{pkg}</c>、<c>/{pkg}/{version}</c>）
/// 内存短 TTL 缓存（默认 60 秒，<c>NPM_METADATA_TTL</c> 可配），缓存 key 按 Accept 变体
/// （缩写 <c>install-v1+json</c> vs 全量）区分；<c>/-/ping</c> 等以 <c>-/</c> 开头的端点兜底透传不缓存。
/// 元数据内嵌的 tarball 绝对 URL 通过正则定向重写为 <c>{domain}/npm/</c> 前缀（保留编码路径原样）。
/// </summary>
public sealed class NpmProxyHandler
{
    /// <summary>npm 包元数据内存缓存 key 前缀。</summary>
    private const string MetadataCacheKeyPrefix = "npm:metadata:";

    private readonly ProxyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiskCacheService _diskCache;
    private readonly ILogger<NpmProxyHandler> _logger;

    /// <summary>
    /// tarball URL 定向重写正则：匹配 <c>"tarball":"https://{upstream_host}/</c> 前缀，
    /// 匹配后仅替换为 <c>"tarball":"{domain}/npm/</c>，路径部分（含 %2f 等编码）保留原样。
    /// </summary>
    private readonly Regex _tarballUrlPattern;

    /// <summary>
    /// 初始化 npm 代理处理器。
    /// </summary>
    /// <param name="options">代理服务配置（含 npm 上游地址、缓存根目录与元数据 TTL）。</param>
    /// <param name="cache">内存缓存（包元数据）。</param>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"npm" 客户端）。</param>
    /// <param name="diskCache">共享磁盘缓存下载服务。</param>
    /// <param name="logger">结构化日志器。</param>
    public NpmProxyHandler(
        ProxyOptions options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        DiskCacheService diskCache,
        ILogger<NpmProxyHandler> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _diskCache = diskCache;
        _logger = logger;

        // 从 NPM_UPSTREAM_URL 解析出的主机名（含端口）参与正则构造，实现定向替换
        var upstreamHostPattern = Regex.Escape(_options.NpmUpstreamHost);
        _tarballUrlPattern = new Regex(
            $"(?i)(\"tarball\":\\s*\"https?://{upstreamHostPattern})/",
            RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 处理 GET/HEAD /npm/{**path} 通配路由：空路径返回 404；路径安全校验失败返回 400；
    /// tarball 走磁盘永久缓存；包元数据走内存短 TTL 缓存；以 <c>-/</c> 开头的内部端点兜底透传不缓存。
    /// </summary>
    /// <param name="path">通配路由路径段（可为空）。</param>
    /// <param name="httpContext">当前请求上下文（用于读取 query string / Accept 头 / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>按命中/透传/失败分别返回本地文件、内容、状态码。</returns>
    public async Task<IResult> HandleNpmRoute(string path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // 空路径（/npm 或 /npm/）不代理，直接返回 404
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("npm empty path rejected");
            return Results.NotFound();
        }

        // 路径安全校验：逐段校验，拒绝 .. . 空段 控制字符及跨平台非法字符
        var (isValid, reason) = PathSafetyValidator.ValidatePath(path);
        if (!isValid)
        {
            _logger.LogWarning("npm path rejected: {Path} - {Reason}", path, reason);
            return Results.BadRequest();
        }

        // tarball 判定：路径含 /-/（包产物目录分隔）或以 .tgz 结尾 → 磁盘永久缓存
        if (path.Contains("/-/", StringComparison.Ordinal) || path.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return await HandleNpmTarball(path, httpContext, cancellationToken);
        }

        // npm registry 内部端点（/-/ping、/-/v1/search、/-/npm/v1/...）兜底透传不缓存
        if (path.StartsWith("-/", StringComparison.Ordinal))
        {
            return await HandleNpmPassthrough(path, httpContext, cancellationToken);
        }

        // 其余视为包元数据：/{pkg} 或 /{pkg}/{version}（含 scope 包 @scope/name）
        return await HandleNpmMetadata(path, httpContext, cancellationToken);
    }

    /// <summary>
    /// 处理 npm tarball：磁盘永久缓存到 {CACHE_PATH}/npm/{path}，复用共享磁盘缓存服务。
    /// </summary>
    /// <param name="path">npm 产物文件路径。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 query string）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>磁盘命中或下载成功后返回本地文件；上游非 2xx 透传状态码；磁盘写失败返回 503。</returns>
    private async Task<IResult> HandleNpmTarball(string path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(_options.CachePath, "npm", path);
        var targetUrl = BuildUpstreamUrl(path, httpContext);

        return await _diskCache.DownloadToCacheAsync(
            "npm", targetUrl, cacheFile, "application/octet-stream", cancellationToken);
    }

    /// <summary>
    /// 处理 npm 包元数据：仅成功响应写内存缓存，TTL 由 <c>NPM_METADATA_TTL</c> 控制（默认 60 秒），
    /// 缓存 key 按 Accept 变体区分（缩写 install-v1+json vs 全量）；返回前将内嵌 tarball URL 重写为
    /// <c>{domain}/npm/</c> 前缀，并显式设置 Content-Length 保证 HEAD 与 GET 一致。
    /// </summary>
    /// <param name="path">npm 包元数据路径（包名或 包名/版本）。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 Accept / query string / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>内存命中或上游 2xx 时返回 JSON；上游非 2xx 透传状态码。</returns>
    private async Task<IResult> HandleNpmMetadata(string path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var variant = GetAcceptVariant(httpContext);
        var cacheKey = $"{MetadataCacheKeyPrefix}{path}:{variant}";

        if (_cache.TryGetValue(cacheKey, out NpmMetadataCacheValue? cached) && cached != null)
        {
            _logger.LogInformation("npm metadata cache hit: {Path} ({Variant})", path, variant);
            return TextContentResult.Build(httpContext, cached.Content, cached.ContentType);
        }

        var targetUrl = BuildUpstreamUrl(path, httpContext);
        var httpClient = _httpClientFactory.CreateClient("npm");

        // 透传客户端 Accept 头，保证上游按变体返回缩写或全量元数据（与缓存 key 变体一致）
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("npm metadata fetch failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        // URL 重写：将元数据内嵌的 tarball 绝对 URL 前缀定向替换为 {domain}/npm/，路径部分保留原样；
        // 使用 MatchEvaluator 避免替换串中 $ 等字符被正则解释
        var rewrittenJson = _tarballUrlPattern.Replace(
            json, _ => $"\"tarball\":\"{_options.NuGetProxyDomain}npm/");

        var ttl = TimeSpan.FromSeconds(_options.NpmMetadataTtlSeconds);
        _cache.Set(cacheKey, new NpmMetadataCacheValue(rewrittenJson, contentType), ttl);
        _logger.LogInformation("npm metadata cached ({Ttl}s): {Path} ({Variant})",
            _options.NpmMetadataTtlSeconds, path, variant);

        return TextContentResult.Build(httpContext, rewrittenJson, contentType);
    }

    /// <summary>
    /// 处理 npm registry 内部端点（/-/ping、/-/v1/search 等）：兜底透传上游响应，不缓存。
    /// </summary>
    /// <param name="path">npm 内部端点路径。</param>
    /// <param name="httpContext">当前请求上下文（用于透传 query string / 写 Content-Length）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>上游 2xx 时返回响应体；上游非 2xx 透传状态码。</returns>
    private async Task<IResult> HandleNpmPassthrough(string path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var targetUrl = BuildUpstreamUrl(path, httpContext);
        var httpClient = _httpClientFactory.CreateClient("npm");

        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("npm passthrough failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        return TextContentResult.Build(httpContext, body, contentType);
    }

    /// <summary>
    /// 拼接 npm 上游完整 URL：{NPM_UPSTREAM_URL}/{path}，并在存在 query string 时原样透传。
    /// </summary>
    /// <param name="path">npm 路径（与落盘路径一致，scope 包 %40/%2f 解码后一致）。</param>
    /// <param name="httpContext">当前请求上下文（用于读取原始 query string）。</param>
    /// <returns>上游完整请求 URL。</returns>
    private string BuildUpstreamUrl(string path, HttpContext httpContext)
    {
        var queryString = httpContext.Request.QueryString.Value;
        return string.IsNullOrEmpty(queryString)
            ? $"{_options.NpmUpstream}/{path}"
            : $"{_options.NpmUpstream}/{path}{queryString}";
    }

    /// <summary>
    /// 计算 Accept 变体：客户端 Accept 含 <c>install-v1+json</c> 视为缩写变体，否则为全量变体。
    /// </summary>
    /// <param name="httpContext">当前请求上下文（用于读取 Accept 头）。</param>
    /// <returns>变体标识（<c>abbrev</c> 或 <c>full</c>）。</returns>
    private static string GetAcceptVariant(HttpContext httpContext)
    {
        var accept = httpContext.Request.Headers.Accept.ToString();
        return accept.Contains("install-v1+json", StringComparison.OrdinalIgnoreCase)
            ? "abbrev"
            : "full";
    }

    /// <summary>
    /// npm 包元数据内存缓存项：保存重写后的 JSON 内容与上游 Content-Type。
    /// </summary>
    /// <param name="Content">重写后的元数据 JSON 内容。</param>
    /// <param name="ContentType">上游返回的 Content-Type（缓存命中时原样回放）。</param>
    private sealed record NpmMetadataCacheValue(string Content, string ContentType);
}
