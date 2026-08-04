using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Orbitra.Configuration;
using Orbitra.Services;

namespace Orbitra.Handlers;

/// <summary>
/// docker registry 代理请求处理器：承载 <c>/v2/{**path}</c> 主路由与 <c>/docker/v2/{**path}</c> 别名路由
/// （均支持 GET/HEAD）。按 <see cref="DockerPathParser"/> 解析分发到四个端点：
/// 版本探测（<c>/v2</c>/<c>/v2/</c>，回 <c>{}</c> + Docker-Distribution-Api-Version）、
/// blob（按 sha256 digest 磁盘永久缓存，复用 <see cref="DiskCacheService"/>）、
/// manifest（by-digest 磁盘永久 + 内存 TTL + .meta 侧车记录 Content-Type；by-tag 仅内存 TTL）、
/// tags/list（内存短 TTL 透传）。
/// 全部端点支持多上游按配置顺序失败回退（网络异常/超时/非 2xx 换下一个，与 Maven 语义一致）；
/// 遇到上游 401 + Bearer 挑战时内部完成 token 交换（<see cref="DockerTokenService"/>）后重试同一上游，
/// 不透传上游 WWW-Authenticate 给客户端（无法换取时返回 401 + <c>Basic realm="Orbitra"</c>）。
/// </summary>
public sealed class DockerProxyHandler
{
    /// <summary>版本探测响应体（registry 标准 <c>GET /v2/</c> 响应）。</summary>
    private const string VersionProbeBody = "{}";

    /// <summary>版本探测响应头值（registry API 版本标识）。</summary>
    private const string DockerDistributionApiVersion = "registry/2.0";

    /// <summary>digest manifest 内存缓存 key 前缀。</summary>
    private const string ManifestDigestCacheKeyPrefix = "docker:manifest-digest:";

    /// <summary>tag manifest 内存缓存 key 前缀。</summary>
    private const string ManifestTagCacheKeyPrefix = "docker:manifest-tag:";

    /// <summary>tags list 内存缓存 key 前缀。</summary>
    private const string TagsListCacheKeyPrefix = "docker:tags:";

    /// <summary>manifest 磁盘缓存根目录相对 CACHE_PATH 的路径（digest 分片布局）。</summary>
    private const string ManifestCacheRelativeDir = "docker/manifests/sha256";

    /// <summary>blob 磁盘缓存根目录相对 CACHE_PATH 的路径（digest 分片布局）。</summary>
    private const string BlobCacheRelativeDir = "docker/blobs/sha256";

    /// <summary>上游未返回 Content-Type 时 manifest 的回退媒体类型。</summary>
    private const string DefaultManifestContentType = "application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>blob 的 Content-Type（registry 标准为 octet-stream）。</summary>
    private const string BlobContentType = "application/octet-stream";

    /// <summary>全部上游最终 401 时返回给客户端的 WWW-Authenticate 质询值（不透传上游质询）。</summary>
    private const string OrbitraBasicChallenge = "Basic realm=\"Orbitra\"";

    private readonly ProxyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiskCacheService _diskCache;
    private readonly DockerTokenService _tokenService;
    private readonly ILogger<DockerProxyHandler> _logger;

    /// <summary>
    /// 初始化 docker 代理处理器。
    /// </summary>
    /// <param name="options">代理服务配置（含 docker 上游列表、各缓存 TTL 与 blob 校验开关）。</param>
    /// <param name="cache">内存缓存（digest/tag manifest 与 tags list）。</param>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"Docker" 客户端，超时放宽至 30 分钟）。</param>
    /// <param name="diskCache">共享磁盘缓存下载服务（blob 落盘）。</param>
    /// <param name="tokenService">docker token 交换服务（401 → Bearer 换取并缓存）。</param>
    /// <param name="logger">结构化日志器。</param>
    public DockerProxyHandler(
        ProxyOptions options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        DiskCacheService diskCache,
        DockerTokenService tokenService,
        ILogger<DockerProxyHandler> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _diskCache = diskCache;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// 处理 GET/HEAD /v2/{**path} 与 /docker/v2/{**path} 通配路由：空路径 → 版本探测；
    /// 否则按 <see cref="DockerPathParser"/> 分发到 manifest / blob / tags list。
    /// </summary>
    /// <param name="path">通配路由路径段（可为 null/空串，表示版本探测）。</param>
    /// <param name="httpContext">当前请求上下文（读取方法/头/query，写响应头）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消上游请求与落盘）。</param>
    /// <returns>按命中/透传/失败分别返回内容、文件或状态码。</returns>
    public async Task<IResult> HandleDockerRoute(string? path, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return await HandleVersionProbe(httpContext, cancellationToken);
        }

        var routeInfo = DockerPathParser.Parse(path);
        switch (routeInfo.Kind)
        {
            case DockerEndpointKind.Manifest:
                return await HandleManifest(routeInfo.Name, routeInfo.Reference, httpContext, cancellationToken);
            case DockerEndpointKind.Blob:
                return await HandleBlob(routeInfo.Name, routeInfo.Reference, httpContext, cancellationToken);
            case DockerEndpointKind.TagsList:
                return await HandleTagsList(routeInfo.Name, httpContext, cancellationToken);
            default:
                _logger.LogWarning("Docker path not recognized: {Path}", path);
                return Results.NotFound();
        }
    }

    /// <summary>
    /// 处理版本探测（<c>/v2</c>、<c>/v2/</c>）：按多上游顺序透传上游 <c>/v2/</c>，
    /// 首个 2xx 回 <c>{}</c> 并设置 <c>Docker-Distribution-Api-Version: registry/2.0</c>；
    /// 全部失败返回最后非 2xx 状态码（全为网络异常 → 502）。不缓存。
    /// </summary>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>成功返回 <c>{}</c> 文本；失败透传状态码。</returns>
    private async Task<IResult> HandleVersionProbe(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var (response, lastStatusCode) = await FetchWithFallbackAsync(
            string.Empty, HttpMethod.Get, null, null, null, cancellationToken);

        if (response is null)
        {
            return BuildUpstreamFailureResult(lastStatusCode, httpContext);
        }

        using (response)
        {
            httpContext.Response.Headers["Docker-Distribution-Api-Version"] = DockerDistributionApiVersion;
            return TextContentResult.Build(httpContext, VersionProbeBody, "application/json");
        }
    }

    /// <summary>
    /// 处理 manifest 端点（GET/HEAD）：reference 为 digest 时走 <see cref="HandleManifestByDigest"/>，
    /// 为 tag 时走 <see cref="HandleManifestByTag"/>；非法引用返回 400。
    /// </summary>
    /// <param name="name">仓库名（含多级 <c>/</c>，如 <c>library/nginx</c>）。</param>
    /// <param name="reference">manifest 引用（digest 或 tag）。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>按命中/透传/失败返回内容、文件或状态码。</returns>
    private async Task<IResult> HandleManifest(string name, string reference, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // 路径安全校验：name 段复用通用校验器（拒绝 .. . 空段 控制字符及非法字符）
        if (!PathSafetyValidator.ValidatePath(name).IsValid)
        {
            _logger.LogWarning("Docker manifest name rejected: {Name}", name);
            return Results.BadRequest();
        }

        if (DockerPathParser.IsValidDigest(reference))
        {
            return await HandleManifestByDigest(name, reference, httpContext, cancellationToken);
        }

        if (!DockerPathParser.IsValidTag(reference))
        {
            _logger.LogWarning("Docker manifest reference rejected: {Reference}", reference);
            return Results.BadRequest();
        }

        return await HandleManifestByTag(name, reference, httpContext, cancellationToken);
    }

    /// <summary>
    /// 处理 by-digest manifest（GET/HEAD）：磁盘永久缓存 + 内存 TTL（<c>DOCKER_MANIFEST_TTL</c>），
    /// 落盘 <c>{CACHE_PATH}/docker/manifests/sha256/{hex[:2]}/{hex}.json</c> + <c>.meta</c> 侧车记录 Content-Type。
    /// GET 未命中时按多上游回退下载并校验 sha256（与请求 digest 不符回退下一上游，防毒化）；
    /// HEAD 未命中时仅透传上游 HEAD，不触发 GET。Content-Type 严格透传上游（禁止归一化）。
    /// </summary>
    /// <param name="name">仓库名。</param>
    /// <param name="digest">manifest digest（sha256:hex）。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>内存/磁盘命中或下载成功后返回 manifest 内容；失败透传状态码。</returns>
    private async Task<IResult> HandleManifestByDigest(
        string name, string digest, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var memoryKey = $"{ManifestDigestCacheKeyPrefix}{digest}";
        if (_cache.TryGetValue(memoryKey, out ManifestCacheValue? cached) && cached != null)
        {
            return BuildManifestContent(httpContext, cached.Body, cached.ContentType, digest);
        }

        var hex = DockerPathParser.DigestToFileName(digest);
        var cacheFile = Path.Combine(_options.CachePath, ManifestCacheRelativeDir, hex[..2], $"{hex}.json");
        var metaFile = $"{cacheFile}.meta";

        // 磁盘命中（含 .meta 侧车）：回放侧车记录的精确 Content-Type 并回填 Docker-Content-Digest
        if (File.Exists(cacheFile) && File.Exists(metaFile))
        {
            var metaContentType = ReadMetaFile(metaFile);
            if (!string.IsNullOrWhiteSpace(metaContentType))
            {
                _logger.LogInformation("Docker manifest disk cache hit: {File}", cacheFile);
                httpContext.Response.Headers["Docker-Content-Digest"] = digest;
                return Results.File(cacheFile, metaContentType);
            }
        }

        var isHead = HttpMethods.IsHead(httpContext.Request.Method);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        var clientAuthorization = GetClientAuthorization(httpContext);

        // HEAD 未命中：仅透传上游 HEAD（不触发 GET、不落盘）
        if (isHead)
        {
            var (headResponse, headStatusCode) = await FetchWithFallbackAsync(
                $"{name}/manifests/{digest}", HttpMethod.Head, acceptHeader, clientAuthorization, null, cancellationToken);
            if (headResponse is null)
            {
                return BuildUpstreamFailureResult(headStatusCode, httpContext);
            }

            using (headResponse)
            {
                CopyHeadResponse(headResponse, httpContext, digest);
                return Results.StatusCode((int)headResponse.StatusCode);
            }
        }

        // GET：按多上游回退下载，首个 2xx 校验 digest 后写盘 + 写内存缓存
        var (body, contentType, lastStatusCode) = await FetchManifestAsync(
            name, digest, acceptHeader, clientAuthorization, digest, cancellationToken);
        if (body is null)
        {
            return BuildUpstreamFailureResult(lastStatusCode, httpContext);
        }

        try
        {
            WriteManifestToDisk(cacheFile, metaFile, body, contentType ?? DefaultManifestContentType);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Docker manifest disk write failed (IOException): {File}", cacheFile);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Docker manifest disk write failed (UnauthorizedAccess): {File}", cacheFile);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        _cache.Set(memoryKey, new ManifestCacheValue(body, contentType ?? DefaultManifestContentType, digest),
            TimeSpan.FromSeconds(_options.DockerManifestTtlSeconds));
        _logger.LogInformation("Docker manifest cached (disk + memory {Ttl}s): {Digest}",
            _options.DockerManifestTtlSeconds, digest);

        return BuildManifestContent(httpContext, body, contentType ?? DefaultManifestContentType, digest);
    }

    /// <summary>
    /// 处理 by-tag manifest（GET/HEAD）：仅内存短 TTL 缓存（<c>DOCKER_TAG_TTL</c>，不落盘），
    /// 返回时按响应体 sha256 回填 <c>Docker-Content-Digest</c>（格式 <c>sha256:{hex}</c>，
    /// 与 by-digest 路径一致，客户端据此校验 manifest 内容）。
    /// HEAD 未命中时仅透传上游 HEAD，不触发 GET。
    /// </summary>
    /// <param name="name">仓库名。</param>
    /// <param name="tag">manifest tag。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>内存命中或上游 2xx 时返回 manifest 内容；失败透传状态码。</returns>
    private async Task<IResult> HandleManifestByTag(
        string name, string tag, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var memoryKey = $"{ManifestTagCacheKeyPrefix}{name}:{tag}";
        if (_cache.TryGetValue(memoryKey, out ManifestCacheValue? cached) && cached != null)
        {
            _logger.LogInformation("Docker tag manifest cache hit: {Name}:{Tag}", name, tag);
            return BuildManifestContent(httpContext, cached.Body, cached.ContentType, cached.Digest);
        }

        var isHead = HttpMethods.IsHead(httpContext.Request.Method);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        var clientAuthorization = GetClientAuthorization(httpContext);

        // HEAD 未命中：仅透传上游 HEAD
        if (isHead)
        {
            var (headResponse, headStatusCode) = await FetchWithFallbackAsync(
                $"{name}/manifests/{tag}", HttpMethod.Head, acceptHeader, clientAuthorization, null, cancellationToken);
            if (headResponse is null)
            {
                return BuildUpstreamFailureResult(headStatusCode, httpContext);
            }

            using (headResponse)
            {
                CopyHeadResponse(headResponse, httpContext, null);
                return Results.StatusCode((int)headResponse.StatusCode);
            }
        }

        var (body, contentType, lastStatusCode) = await FetchManifestAsync(
            name, tag, acceptHeader, clientAuthorization, null, cancellationToken);
        if (body is null)
        {
            return BuildUpstreamFailureResult(lastStatusCode, httpContext);
        }

        // 回填 Docker-Content-Digest：恒按响应体 sha256 回算（by-tag 不透传上游头，防伪造 digest 干扰客户端校验），
        // 格式统一为 sha256:{hex}，与 by-digest 路径（:579）及缓存回放路径完全一致
        var effectiveContentType = contentType ?? DefaultManifestContentType;
        var digest = $"sha256:{ComputeDigest(body)}";
        _cache.Set(memoryKey, new ManifestCacheValue(body, effectiveContentType, digest),
            TimeSpan.FromSeconds(_options.DockerTagTtlSeconds));
        _logger.LogInformation("Docker tag manifest cached ({Ttl}s): {Name}:{Tag}",
            _options.DockerTagTtlSeconds, name, tag);

        return BuildManifestContent(httpContext, body, effectiveContentType, digest);
    }

    /// <summary>
    /// 处理 blob 端点（GET/HEAD）：<c>{CACHE_PATH}/docker/blobs/sha256/{hex[:2]}/{hex}</c> 磁盘永久缓存。
    /// GET 复用 <see cref="DiskCacheService.DownloadToCacheAsync"/>（401 时内部 token 交换重试同一上游、
    /// <c>DOCKER_BLOB_VERIFY=true</c> 时边写边算 sha256 校验，不符删 tmp 回退下一上游）；
    /// HEAD 磁盘命中回文件头，未命中仅透传上游 HEAD（不触发 GET）。
    /// </summary>
    /// <param name="name">仓库名。</param>
    /// <param name="digest">blob digest（sha256:hex，格式非法返回 400）。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>磁盘命中或下载成功后返回本地文件；失败透传状态码或 503。</returns>
    private async Task<IResult> HandleBlob(string name, string digest, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!DockerPathParser.IsValidDigest(digest))
        {
            _logger.LogWarning("Docker blob digest rejected: {Digest}", digest);
            return Results.BadRequest();
        }

        if (!PathSafetyValidator.ValidatePath(name).IsValid)
        {
            _logger.LogWarning("Docker blob name rejected: {Name}", name);
            return Results.BadRequest();
        }

        var hex = DockerPathParser.DigestToFileName(digest);
        var cacheFile = Path.Combine(_options.CachePath, BlobCacheRelativeDir, hex[..2], hex);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        var clientAuthorization = GetClientAuthorization(httpContext);

        // HEAD：磁盘命中回文件头；未命中透传上游 HEAD（不触发 GET、不落盘）
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            if (File.Exists(cacheFile))
            {
                _logger.LogInformation("Docker blob disk cache hit: {File}", cacheFile);
                httpContext.Response.Headers["Docker-Content-Digest"] = digest;
                return Results.File(cacheFile, BlobContentType);
            }

            var (headResponse, headStatusCode) = await FetchWithFallbackAsync(
                $"{name}/blobs/{digest}", HttpMethod.Head, acceptHeader, clientAuthorization, null, cancellationToken);
            if (headResponse is null)
            {
                return BuildUpstreamFailureResult(headStatusCode, httpContext);
            }

            using (headResponse)
            {
                CopyHeadResponse(headResponse, httpContext, digest);
                return Results.StatusCode((int)headResponse.StatusCode);
            }
        }

        // GET：复用 DiskCacheService 多上游回退落盘；401 token 交换由委托内部完成
        var targetUrls = new List<string>(_options.DockerUpstreams.Count);
        foreach (var upstream in _options.DockerUpstreams)
        {
            targetUrls.Add($"{upstream}/v2/{name}/blobs/{digest}");
        }

        var responseHeaders = new Dictionary<string, string> { ["Docker-Content-Digest"] = digest };
        var expectedSha256 = _options.DockerBlobVerify ? hex : null;

        IReadOnlyDictionary<string, string>? requestHeaders = null;
        if (!string.IsNullOrWhiteSpace(clientAuthorization))
        {
            requestHeaders = new Dictionary<string, string> { ["Authorization"] = clientAuthorization };
        }

        async Task<string?> TokenProvider(string upstreamUrl, string wwwAuthenticate)
        {
            if (!DockerTokenService.TryParseBearerChallenge(wwwAuthenticate, out var realm, out var service, out var scope))
            {
                return null;
            }

            return await _tokenService.GetBearerTokenAsync(realm, service, scope, clientAuthorization);
        }

        return await _diskCache.DownloadToCacheAsync(
            "Docker", targetUrls, cacheFile, BlobContentType, cancellationToken,
            responseHeaders, expectedSha256, requestHeaders, TokenProvider, OrbitraBasicChallenge);
    }

    /// <summary>
    /// 处理 tags/list 端点（GET/HEAD）：内存短 TTL 缓存（同 <c>DOCKER_TAG_TTL</c>），
    /// 透传上游 JSON 并保留 <c>Link</c> 分页头；HEAD 未命中时仅透传上游 HEAD。
    /// </summary>
    /// <param name="name">仓库名。</param>
    /// <param name="httpContext">当前请求上下文（读取 query / Accept 头）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>内存命中或上游 2xx 时返回 JSON 内容；失败透传状态码。</returns>
    private async Task<IResult> HandleTagsList(string name, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!PathSafetyValidator.ValidatePath(name).IsValid)
        {
            _logger.LogWarning("Docker tags list name rejected: {Name}", name);
            return Results.BadRequest();
        }

        // 缓存 key 并入归一化 query（至少 n/last 分页参数）：同一 repo 不同分页页互不串扰；
        // 无 query 时 key 行为与旧版一致（仅 prefix+name）
        var queryKey = NormalizeTagsListQuery(httpContext.Request.Query);
        var memoryKey = queryKey is null
            ? $"{TagsListCacheKeyPrefix}{name}"
            : $"{TagsListCacheKeyPrefix}{name}?{queryKey}";
        if (_cache.TryGetValue(memoryKey, out TagsListCacheValue? cached) && cached != null)
        {
            if (!string.IsNullOrWhiteSpace(cached.Link))
            {
                httpContext.Response.Headers["Link"] = cached.Link;
            }

            return TextContentResult.Build(httpContext, cached.Body, cached.ContentType);
        }

        var isHead = HttpMethods.IsHead(httpContext.Request.Method);
        var acceptHeader = httpContext.Request.Headers.Accept.ToString();
        var clientAuthorization = GetClientAuthorization(httpContext);
        var queryString = httpContext.Request.QueryString.Value;

        // HEAD 未命中：仅透传上游 HEAD
        if (isHead)
        {
            var (headResponse, headStatusCode) = await FetchWithFallbackAsync(
                $"{name}/tags/list", HttpMethod.Head, acceptHeader, clientAuthorization, queryString, cancellationToken);
            if (headResponse is null)
            {
                return BuildUpstreamFailureResult(headStatusCode, httpContext);
            }

            using (headResponse)
            {
                CopyHeadResponse(headResponse, httpContext, null);
                return Results.StatusCode((int)headResponse.StatusCode);
            }
        }

        var (response, lastStatusCode) = await FetchWithFallbackAsync(
            $"{name}/tags/list", HttpMethod.Get, acceptHeader, clientAuthorization, queryString, cancellationToken);
        if (response is null)
        {
            return BuildUpstreamFailureResult(lastStatusCode, httpContext);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            var link = response.Headers.TryGetValues("Link", out var linkValues)
                ? string.Join(",", linkValues)
                : string.Empty;

            _cache.Set(memoryKey, new TagsListCacheValue(body, contentType, link),
                TimeSpan.FromSeconds(_options.DockerTagTtlSeconds));
            _logger.LogInformation("Docker tags list cached ({Ttl}s): {Name}", _options.DockerTagTtlSeconds, name);

            if (!string.IsNullOrWhiteSpace(link))
            {
                httpContext.Response.Headers["Link"] = link;
            }

            return TextContentResult.Build(httpContext, body, contentType);
        }
    }

    /// <summary>
    /// 按多上游顺序发送请求并回退：返回首个 2xx 响应（调用方负责 Dispose）；
    /// 全部失败返回 null 与最后一个非 2xx 状态码（全为网络异常时为 0）。
    /// 请求会附加客户端 Accept 头与 Authorization 头；上游 401 时内部 token 交换后重试同一上游。
    /// </summary>
    /// <param name="relativePath">上游路径（如 <c>library/nginx/manifests/latest</c>；版本探测传空串）。</param>
    /// <param name="method">请求方法（GET/HEAD）。</param>
    /// <param name="acceptHeader">客户端 Accept 头（可为 null）。</param>
    /// <param name="clientAuthorization">客户端 Authorization 头（可为 null，token 交换与上游请求均透传）。</param>
    /// <param name="queryString">原始 query string（含 '?'，可为 null）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>元组：成功响应（或 null）+ 最后失败状态码。</returns>
    private async Task<(HttpResponseMessage? Response, int LastStatusCode)> FetchWithFallbackAsync(
        string relativePath,
        HttpMethod method,
        string? acceptHeader,
        string? clientAuthorization,
        string? queryString,
        CancellationToken cancellationToken)
    {
        var lastStatusCode = 0;
        var httpClient = _httpClientFactory.CreateClient("Docker");

        for (var index = 0; index < _options.DockerUpstreams.Count; index++)
        {
            var upstream = _options.DockerUpstreams[index];
            var targetUrl = BuildUpstreamUrl(upstream, relativePath, queryString);
            var response = await SendSingleAsync(httpClient, targetUrl, method, acceptHeader, clientAuthorization, cancellationToken);

            if (response is null)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                lastStatusCode = (int)response.StatusCode;
                _logger.LogWarning("Docker upstream {Index} failed: {StatusCode} - {Url}",
                    index, lastStatusCode, targetUrl);
                response.Dispose();
                continue;
            }

            return (response, 0);
        }

        return (null, lastStatusCode);
    }

    /// <summary>
    /// 按多上游顺序下载 manifest：返回首个通过 digest 校验（若提供）的 2xx 响应体与 Content-Type。
    /// 由 <see cref="FetchWithFallbackAsync"/> 语义派生，但增加了响应体读取与 sha256 校验；
    /// 全部失败返回 null + 最后失败状态码。
    /// </summary>
    /// <param name="name">仓库名。</param>
    /// <param name="reference">manifest 引用（digest 或 tag）。</param>
    /// <param name="acceptHeader">客户端 Accept 头。</param>
    /// <param name="clientAuthorization">客户端 Authorization 头。</param>
    /// <param name="expectedDigest">期望的完整 digest（如为 by-digest 引用传入，用于校验；tag 传 null）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>元组：响应体（或 null）+ Content-Type（或 null）+ 最后失败状态码。</returns>
    private async Task<(byte[]? Body, string? ContentType, int LastStatusCode)> FetchManifestAsync(
        string name,
        string reference,
        string? acceptHeader,
        string? clientAuthorization,
        string? expectedDigest,
        CancellationToken cancellationToken)
    {
        var lastStatusCode = 0;
        var relativePath = $"{name}/manifests/{reference}";
        var httpClient = _httpClientFactory.CreateClient("Docker");

        for (var index = 0; index < _options.DockerUpstreams.Count; index++)
        {
            var upstream = _options.DockerUpstreams[index];
            var targetUrl = BuildUpstreamUrl(upstream, relativePath, null);
            var response = await SendSingleAsync(httpClient, targetUrl, HttpMethod.Get, acceptHeader, clientAuthorization, cancellationToken);

            if (response is null)
            {
                continue;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    lastStatusCode = (int)response.StatusCode;
                    _logger.LogWarning("Docker manifest upstream {Index} failed: {StatusCode} - {Url}",
                        index, lastStatusCode, targetUrl);
                    continue;
                }

                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                // by-digest 引用：校验响应体 sha256 与请求 digest 一致，不符视为上游毒化，回退下一上游
                if (expectedDigest is not null)
                {
                    var actualDigest = $"sha256:{ComputeDigest(body)}";
                    if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("Docker manifest digest mismatch, expected {Expected} got {Got} - {Url}",
                            expectedDigest, actualDigest, targetUrl);
                        continue;
                    }
                }

                var contentType = response.Content.Headers.ContentType?.ToString();
                return (body, contentType, 0);
            }
        }

        return (null, null, lastStatusCode);
    }

    /// <summary>
    /// 发送单个上游请求：附加 Accept 与客户端 Authorization 头；上游 401 + Bearer 挑战时
    /// 内部 token 交换后带 <c>Authorization: Bearer {token}</c> 重试同一 URL。
    /// 网络异常/超时返回 null（由调用方回退下一上游）。
    /// </summary>
    /// <param name="httpClient">命名 HttpClient（"Docker"）。</param>
    /// <param name="targetUrl">上游完整请求 URL。</param>
    /// <param name="method">请求方法。</param>
    /// <param name="acceptHeader">客户端 Accept 头（可为 null）。</param>
    /// <param name="clientAuthorization">客户端 Authorization 头（可为 null）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>最终响应（含 token 重试后）；网络异常/超时返回 null。</returns>
    private async Task<HttpResponseMessage?> SendSingleAsync(
        HttpClient httpClient,
        string targetUrl,
        HttpMethod method,
        string? acceptHeader,
        string? clientAuthorization,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = BuildRequest(method, targetUrl, acceptHeader, clientAuthorization);
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Docker upstream request failed: {Error} - {Url}", ex.Message, targetUrl);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Docker upstream request timeout - {Url}", targetUrl);
            return null;
        }

        // 401 + Bearer 挑战：内部 token 交换后重试同一上游
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var wwwAuthenticate = response.Headers.WwwAuthenticate.ToString();
            response.Dispose();

            if (!DockerTokenService.TryParseBearerChallenge(wwwAuthenticate, out var realm, out var service, out var scope))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var token = await _tokenService.GetBearerTokenAsync(realm, service, scope, clientAuthorization);
            if (string.IsNullOrEmpty(token))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            try
            {
                using var retryRequest = BuildRequest(method, targetUrl, acceptHeader, null);
                retryRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                return await httpClient.SendAsync(retryRequest, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Docker auth retry failed: {Error} - {Url}", ex.Message, targetUrl);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Docker auth retry timeout - {Url}", targetUrl);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
        }

        return response;
    }

    /// <summary>
    /// 构造上游完整 URL：<c>{upstream}/v2/{relativePath}</c>，并在存在 query string 时原样透传。
    /// </summary>
    /// <param name="upstream">上游根地址（已去除末尾斜杠）。</param>
    /// <param name="relativePath">v2 下的相对路径（版本探测为空串）。</param>
    /// <param name="queryString">原始 query string（可为 null）。</param>
    /// <returns>上游完整请求 URL。</returns>
    private static string BuildUpstreamUrl(string upstream, string relativePath, string? queryString)
    {
        var baseUrl = string.IsNullOrEmpty(relativePath)
            ? $"{upstream}/v2/"
            : $"{upstream}/v2/{relativePath}";
        return string.IsNullOrEmpty(queryString) ? baseUrl : baseUrl + queryString;
    }

    /// <summary>
    /// 构造上游请求对象：附加 Accept 头与可选 Authorization 头（请求头不写入任何日志）。
    /// </summary>
    /// <param name="method">请求方法。</param>
    /// <param name="targetUrl">上游完整 URL。</param>
    /// <param name="acceptHeader">客户端 Accept 头（可为 null）。</param>
    /// <param name="clientAuthorization">客户端 Authorization 头（可为 null）。</param>
    /// <returns>构造完成的请求对象。</returns>
    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string targetUrl, string? acceptHeader, string? clientAuthorization)
    {
        var request = new HttpRequestMessage(method, targetUrl);
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        }

        if (!string.IsNullOrWhiteSpace(clientAuthorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", clientAuthorization);
        }

        return request;
    }

    /// <summary>
    /// 构建 manifest 响应：设置 Content-Length 保证 HEAD 与 GET 一致，回填 Docker-Content-Digest 头。
    /// </summary>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="body">manifest 响应体。</param>
    /// <param name="contentType">Content-Type（上游原值透传）。</param>
    /// <param name="digest">回填的 Docker-Content-Digest。</param>
    /// <returns>内容结果对象。</returns>
    private static IResult BuildManifestContent(HttpContext httpContext, byte[] body, string contentType, string digest)
    {
        httpContext.Response.Headers["Docker-Content-Digest"] = digest;
        httpContext.Response.ContentLength = body.Length;
        return Results.Bytes(body, contentType);
    }

    /// <summary>
    /// 透传上游 HEAD 响应头到当前响应：Content-Length、Content-Type、Docker-Content-Digest（可选回填）、
    /// Link 分页头与 Docker-Distribution-Api-Version。
    /// </summary>
    /// <param name="upstreamResponse">上游 HEAD 响应。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <param name="digestToFill">需要回填的 Docker-Content-Digest（null 表示取上游值或跳过）。</param>
    private static void CopyHeadResponse(HttpResponseMessage upstreamResponse, HttpContext httpContext, string? digestToFill)
    {
        if (upstreamResponse.Content.Headers.ContentLength.HasValue)
        {
            httpContext.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
        }

        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            httpContext.Response.ContentType = contentType;
        }

        if (!string.IsNullOrWhiteSpace(digestToFill))
        {
            httpContext.Response.Headers["Docker-Content-Digest"] = digestToFill;
        }
        else if (upstreamResponse.Headers.TryGetValues("Docker-Content-Digest", out var digestValues))
        {
            httpContext.Response.Headers["Docker-Content-Digest"] = digestValues.ToArray();
        }

        if (upstreamResponse.Headers.TryGetValues("Link", out var linkValues))
        {
            httpContext.Response.Headers["Link"] = linkValues.ToArray();
        }

        if (upstreamResponse.Headers.TryGetValues("Docker-Distribution-Api-Version", out var apiValues))
        {
            httpContext.Response.Headers["Docker-Distribution-Api-Version"] = apiValues.ToArray();
        }
    }

    /// <summary>
    /// 将 manifest 响应体与 Content-Type 侧车原子写盘：先写 <c>{file}.{guid}.tmp</c> 再 rename 为最终文件。
    /// 磁盘写失败由调用方捕获并转为 503。
    /// </summary>
    /// <param name="cacheFile">manifest 缓存文件路径。</param>
    /// <param name="metaFile">Content-Type 侧车路径。</param>
    /// <param name="body">manifest 响应体字节。</param>
    /// <param name="contentType">上游 Content-Type 原值。</param>
    /// <exception cref="IOException">磁盘写入失败时抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">无写权限时抛出。</exception>
    private void WriteManifestToDisk(string cacheFile, string metaFile, byte[] body, string contentType)
    {
        var cacheDir = Path.GetDirectoryName(cacheFile);
        if (!string.IsNullOrEmpty(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        var tmpBody = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
        var tmpMeta = $"{metaFile}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmpBody, body);
        File.WriteAllText(tmpMeta, contentType);
        File.Move(tmpBody, cacheFile, overwrite: true);
        File.Move(tmpMeta, metaFile, overwrite: true);
    }

    /// <summary>
    /// 读取 manifest .meta 侧车中的 Content-Type；文件缺失或读取失败返回 null。
    /// </summary>
    /// <param name="metaFile">侧车文件路径。</param>
    /// <returns>Content-Type 字符串（缺失/失败返回 null）。</returns>
    private string? ReadMetaFile(string metaFile)
    {
        try
        {
            var value = File.ReadAllText(metaFile).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Docker manifest meta read failed: {File}", metaFile);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Docker manifest meta read failed (UnauthorizedAccess): {File}", metaFile);
            return null;
        }
    }

    /// <summary>
    /// 将 tags/list 查询参数归一化为缓存 key 后缀：参数按名称排序、多值以逗号拼接，
    /// 使 <c>?n=10&amp;last=xxx</c> 与 <c>?last=xxx&amp;n=10</c> 命中同一缓存，
    /// 而不同分页（n/last 取值不同）互不串扰；无查询参数时返回 null。
    /// </summary>
    /// <param name="query">当前请求的查询参数集合。</param>
    /// <returns>归一化后的查询 key（无参数返回 null）。</returns>
    private static string? NormalizeTagsListQuery(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(query.Count);
        foreach (var key in query.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var values = string.Join(",", query[key].Select(value => value ?? string.Empty));
            parts.Add($"{key}={values}");
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// 计算响应体的 sha256 十六进制摘要（小写）。
    /// </summary>
    /// <param name="body">响应体字节。</param>
    /// <returns>小写十六进制 sha256 摘要。</returns>
    private static string ComputeDigest(byte[] body)
    {
        return Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
    }

    /// <summary>
    /// 读取客户端请求携带的 Authorization 头原始值（空白返回 null）；该值仅透传/换取 token，绝不写日志。
    /// </summary>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <returns>Authorization 头值（无则 null）。</returns>
    private static string? GetClientAuthorization(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        return string.IsNullOrWhiteSpace(authorization) ? null : authorization;
    }

    /// <summary>
    /// 构建全部上游失败的结果：最终 401 → 回 <c>401 + WWW-Authenticate: Basic realm="Orbitra"</c>
    /// （不透传上游质询）；否则回最后非 2xx 状态码；全为网络异常回 502。
    /// </summary>
    /// <param name="lastStatusCode">最后失败状态码（0 表示全为网络异常）。</param>
    /// <param name="httpContext">当前请求上下文。</param>
    /// <returns>失败状态结果。</returns>
    private IResult BuildUpstreamFailureResult(int lastStatusCode, HttpContext httpContext)
    {
        if (lastStatusCode == (int)HttpStatusCode.Unauthorized)
        {
            httpContext.Response.Headers["WWW-Authenticate"] = OrbitraBasicChallenge;
            return Results.StatusCode((int)HttpStatusCode.Unauthorized);
        }

        if (lastStatusCode != 0)
        {
            _logger.LogError("All docker upstreams failed, last status {StatusCode}", lastStatusCode);
            return Results.StatusCode(lastStatusCode);
        }

        _logger.LogError("All docker upstreams failed, no upstream responded");
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// manifest 内存缓存项：响应体 + Content-Type（上游原值）+ Docker-Content-Digest。
    /// </summary>
    /// <param name="Body">manifest 响应体。</param>
    /// <param name="ContentType">Content-Type（上游原值）。</param>
    /// <param name="Digest">Docker-Content-Digest 值。</param>
    private sealed record ManifestCacheValue(byte[] Body, string ContentType, string Digest);

    /// <summary>
    /// tags/list 内存缓存项：响应体 + Content-Type + Link 分页头。
    /// </summary>
    /// <param name="Body">tags 列表 JSON 内容。</param>
    /// <param name="ContentType">Content-Type。</param>
    /// <param name="Link">Link 分页头（可为空）。</param>
    private sealed record TagsListCacheValue(string Body, string ContentType, string Link);
}
