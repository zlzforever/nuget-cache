using Microsoft.Extensions.Caching.Memory;
using NuGetCache.Configuration;
using NuGetCache.Services;

namespace NuGetCache.Handlers;

/// <summary>
/// Maven 代理请求处理器：承载 /maven/{**path} 通配路由。路径与原请求 1:1 透传上游。
/// maven-metadata.xml 走内存缓存（快照 5 分钟 / 非快照 60 分钟），其余产物与校验和文件
/// 经 <see cref="DiskCacheService"/> 磁盘永久缓存。
/// </summary>
public sealed class MavenProxyHandler
{
    /// <summary>Maven 元数据内存缓存 key 前缀。</summary>
    private const string MetadataCacheKeyPrefix = "maven:metadata:";

    /// <summary>快照元数据内存缓存 TTL（5 分钟，快照版本变化频繁缩短 TTL）。</summary>
    private static readonly TimeSpan SnapshotMetadataTtl = TimeSpan.FromMinutes(5);

    /// <summary>非快照元数据内存缓存 TTL（60 分钟）。</summary>
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromMinutes(60);

    /// <summary>路径总长度上限。</summary>
    private const int MaxTotalPathLength = 4096;

    /// <summary>单个路径段长度上限。</summary>
    private const int MaxSegmentLength = 255;

    private readonly ProxyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiskCacheService _diskCache;
    private readonly ILogger<MavenProxyHandler> _logger;

    /// <summary>
    /// 初始化 Maven 代理处理器。
    /// </summary>
    /// <param name="options">代理服务配置（含 Maven 上游地址与缓存根目录）。</param>
    /// <param name="cache">内存缓存（maven-metadata.xml）。</param>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"Maven" 客户端）。</param>
    /// <param name="diskCache">共享磁盘缓存下载服务。</param>
    /// <param name="logger">结构化日志器。</param>
    public MavenProxyHandler(
        ProxyOptions options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        DiskCacheService diskCache,
        ILogger<MavenProxyHandler> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _diskCache = diskCache;
        _logger = logger;
    }

    /// <summary>
    /// 处理 GET /maven/{**path} 通配路由：空路径返回 404；路径安全校验失败返回 400；
    /// maven-metadata.xml 走内存缓存，其余产物走磁盘永久缓存。
    /// </summary>
    /// <param name="path">通配路由路径段（可为空）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>按命中/透传/失败分别返回本地文件、内容、状态码。</returns>
    public async Task<IResult> HandleMavenRoute(string path, CancellationToken cancellationToken)
    {
        // 空路径（/maven 或 /maven/）不代理，直接返回 404
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Maven empty path rejected");
            return Results.NotFound();
        }

        // 路径安全校验：逐段校验，拒绝 .. . 空段 控制字符及跨平台非法字符，保留大小写
        var (isValid, reason) = ValidateMavenPath(path);
        if (!isValid)
        {
            _logger.LogWarning("Maven path rejected: {Path} - {Reason}", path, reason);
            return Results.BadRequest();
        }

        // maven-metadata.xml 精确文件名匹配才走内存缓存（快照 5 分钟 / 非快照 60 分钟），不写盘；
        // 校验和伴生文件（maven-metadata.xml.sha1/.md5/.sha256）含相同子串，但按 PRD 应走磁盘缓存，
        // 因此使用 Path.GetFileName 精确匹配，避免子串匹配误伤
        if (Path.GetFileName(path).Equals("maven-metadata.xml", StringComparison.Ordinal))
        {
            return await HandleMavenMetadata(path);
        }

        // 其余产物与校验和文件走磁盘永久缓存
        return await HandleMavenArtifact(path, cancellationToken);
    }

    /// <summary>
    /// 处理 maven-metadata.xml：仅成功响应写内存缓存，TTL 快照 5 分钟 / 非快照 60 分钟，不落盘。
    /// </summary>
    /// <param name="path">Maven 元数据文件路径。</param>
    /// <returns>内存命中或上游 2xx 时返回 application/xml（上游 Content-Type 优先）；上游非 2xx 透传状态码。</returns>
    private async Task<IResult> HandleMavenMetadata(string path)
    {
        var cacheKey = $"{MetadataCacheKeyPrefix}{path}";

        if (_cache.TryGetValue(cacheKey, out string? cachedXml) && cachedXml != null)
        {
            _logger.LogInformation("Maven metadata cache hit: {Path}", path);
            return Results.Content(cachedXml, "application/xml");
        }

        var targetUrl = $"{_options.MavenUpstream}/{path}";
        var httpClient = _httpClientFactory.CreateClient("Maven");
        using var response = await httpClient.GetAsync(targetUrl);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Maven metadata fetch failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var xml = await response.Content.ReadAsStringAsync();

        var ttl = IsSnapshotMetadata(path) ? SnapshotMetadataTtl : MetadataTtl;
        _cache.Set(cacheKey, xml, ttl);
        _logger.LogInformation("Maven metadata cached ({Ttl}): {Path}", ttl, path);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/xml";
        return Results.Content(xml, contentType);
    }

    /// <summary>
    /// 处理 Maven 产物与校验和文件：磁盘永久缓存到 {CACHE_PATH}/maven/{path}，上游 2xx 才落盘。
    /// </summary>
    /// <param name="path">Maven 产物文件路径。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>磁盘命中或下载成功后返回本地文件；上游非 2xx 透传状态码；磁盘写失败返回 503。</returns>
    private async Task<IResult> HandleMavenArtifact(string path, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(_options.CachePath, "maven", path);
        var fallbackContentType = GetMavenContentType(path);
        var targetUrl = $"{_options.MavenUpstream}/{path}";

        return await _diskCache.DownloadToCacheAsync("Maven", targetUrl, cacheFile, fallbackContentType, cancellationToken);
    }

    /// <summary>
    /// 校验 Maven 路径是否安全：逐段校验，拒绝 ..、.、空段、控制字符及跨平台非法字符，保留大小写。
    /// </summary>
    /// <param name="path">待校验的 Maven 路径。</param>
    /// <returns>元组：是否合法，不合法时的拒绝原因（合法时为空字符串）。</returns>
    private static (bool IsValid, string Reason) ValidateMavenPath(string path)
    {
        if (path.Length > MaxTotalPathLength)
        {
            return (false, $"总路径长度超过上限 {MaxTotalPathLength}");
        }

        // URL 编码归一化后再做段校验，防止 %2e%2e / %2F 等编码变体绕过 .. 拒绝逻辑；
        // 仅用于判定，不改动已通过校验的落盘路径内容（大小写保持不变）。
        // 注意：Uri.UnescapeDataString 对非法编码（如 %G0、孤立 %）不抛异常而是原样透传，
        // 非法 % 会被上游 Uri 重新编码为 %25 后转发并 404 透传，不构成崩溃或安全问题；
        // 下方 catch 为防御性保留（实际不会触发），接受该行为由上游 404 兜底
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return (false, "路径包含非法 URL 编码");
        }

        var segments = decodedPath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return (false, "路径包含空段");
            }

            if (segment == "." || segment == "..")
            {
                return (false, $"路径包含非法段: {segment}");
            }

            if (segment.Length > MaxSegmentLength)
            {
                return (false, $"路径段长度超过上限 {MaxSegmentLength}: {segment}");
            }

            foreach (var c in segment)
            {
                // 控制字符及跨平台非法字符（\\ : * ? " < > |）
                if (char.IsControl(c) || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                {
                    return (false, $"路径段包含非法字符: {segment}");
                }
            }
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// 判断元数据是否为快照：任一中间段以 -SNAPSHOT 结尾即视为快照元数据。
    /// </summary>
    /// <param name="path">Maven 元数据文件路径。</param>
    /// <returns>是否为快照元数据。</returns>
    private static bool IsSnapshotMetadata(string path)
    {
        var segments = path.Split('/');
        // 中间段 = 去掉最后一段（文件名）之前的全部段
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].EndsWith("-SNAPSHOT", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 根据文件扩展名推断 Content-Type（磁盘缓存命中时使用，避免依赖上游响应头）。
    /// </summary>
    /// <param name="path">Maven 产物文件路径。</param>
    /// <returns>推断出的 MIME 类型。</returns>
    private static string GetMavenContentType(string path)
    {
        if (path.EndsWith(".pom", StringComparison.Ordinal) || path.EndsWith(".xml", StringComparison.Ordinal))
        {
            return "application/xml";
        }

        if (path.EndsWith(".jar", StringComparison.Ordinal) || path.EndsWith(".war", StringComparison.Ordinal) ||
            path.EndsWith(".aar", StringComparison.Ordinal) || path.EndsWith(".zip", StringComparison.Ordinal))
        {
            return "application/octet-stream";
        }

        if (path.EndsWith(".sha1", StringComparison.Ordinal) || path.EndsWith(".sha256", StringComparison.Ordinal) ||
            path.EndsWith(".md5", StringComparison.Ordinal) || path.EndsWith(".sha512", StringComparison.Ordinal))
        {
            return "application/octet-stream";
        }

        return "application/octet-stream";
    }
}
