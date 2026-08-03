using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using NuGetCache.Configuration;
using NuGetCache.Services;

namespace NuGetCache.Handlers;

/// <summary>
/// NuGet 代理请求处理器：承载 NuGet 三条路由（服务索引 /v3/index.json、包版本索引、包文件下载）的业务逻辑。
/// 服务索引与包版本索引走内存缓存（TTL 60 分钟）；包文件下载走 <see cref="DiskCacheService"/> 磁盘永久缓存。
/// </summary>
public sealed class NuGetProxyHandler
{
    /// <summary>NuGet 上游服务索引地址。</summary>
    private const string NuGetServiceIndexUrl = "https://api.nuget.org/v3/index.json";

    /// <summary>NuGet 上游 flatcontainer 根地址。</summary>
    private const string NuGetFlatContainerUrlBase = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>服务索引内存缓存 key。</summary>
    private const string ServiceIndexCacheKey = "nuget:index.json";

    /// <summary>包版本索引内存缓存 key 前缀。</summary>
    private const string PackageIndexCacheKeyPrefix = "nuget-package:";

    /// <summary>内存缓存 TTL（60 分钟），与服务索引/包版本索引一致。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    private readonly ProxyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiskCacheService _diskCache;
    private readonly ILogger<NuGetProxyHandler> _logger;

    /// <summary>
    /// 初始化 NuGet 代理处理器。
    /// </summary>
    /// <param name="options">代理服务配置（含代理域名与缓存根目录）。</param>
    /// <param name="cache">内存缓存（服务索引 / 包版本索引）。</param>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"NuGet" 客户端）。</param>
    /// <param name="diskCache">共享磁盘缓存下载服务。</param>
    /// <param name="logger">结构化日志器。</param>
    public NuGetProxyHandler(
        ProxyOptions options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        DiskCacheService diskCache,
        ILogger<NuGetProxyHandler> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _diskCache = diskCache;
        _logger = logger;
    }

    /// <summary>
    /// 处理 GET /v3/index.json：代理上游服务索引，并将响应中所有 v3-flatcontainer 绝对 URL
    /// 重写指向本代理域名（{NUGET_PROXY_DOMAIN}v3-flatcontainer/），结果内存缓存 60 分钟。
    /// </summary>
    /// <returns>内存命中或上游 2xx 时返回 application/json；上游非 2xx 透传状态码。</returns>
    public async Task<IResult> GetServiceIndex()
    {
        // 请求日志统一由 Program.cs 请求日志中间件打印，此处不再重复记录
        if (_cache.TryGetValue(ServiceIndexCacheKey, out string? cachedJson) && cachedJson != null)
        {
            return Results.Content(cachedJson, "application/json");
        }

        var httpClient = _httpClientFactory.CreateClient("NuGet");
        using var response = await httpClient.GetAsync(NuGetServiceIndexUrl);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed: {StatusCode}", (int)response.StatusCode);
            return Results.StatusCode((int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();

        var proxyUrl = $"{_options.NuGetProxyDomain}v3-flatcontainer/";
        json = Regex.Replace(json, @"https?://[^/]+/v3-flatcontainer/", proxyUrl, RegexOptions.IgnoreCase);

        _cache.Set(ServiceIndexCacheKey, json, CacheTtl);
        return Results.Content(json, "application/json");
    }

    /// <summary>
    /// 处理 GET /v3-flatcontainer/{id}/index.json：代理包版本索引，内存缓存 60 分钟。
    /// </summary>
    /// <param name="id">包 ID（路由参数，限长 255 字符，校验失败由框架返回 400）。</param>
    /// <returns>内存命中或上游 2xx 时返回 application/json；上游非 2xx 透传状态码。</returns>
    public async Task<IResult> GetPackageIndex([StringLength(255)] string id)
    {
        var idLower = id.ToLowerInvariant();
        var cacheKey = $"{PackageIndexCacheKeyPrefix}{idLower}:index.json";

        if (_cache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson != null)
        {
            _logger.LogInformation("Index cache hit: {Id}", idLower);
            return Results.Content(cachedJson, "application/json");
        }

        var targetUrl = $"{NuGetFlatContainerUrlBase}/{idLower}/index.json";

        var httpClient = _httpClientFactory.CreateClient("NuGet");
        using var response = await httpClient.GetAsync(targetUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Fetched index failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Fetched index successfully: {Url}", targetUrl);

        _cache.Set(cacheKey, json, CacheTtl);
        return Results.Content(json, "application/json");
    }

    /// <summary>
    /// 处理 GET /v3-flatcontainer/{id}/{version}/{file}：包文件磁盘永久缓存下载。
    /// 磁盘命中直接返回本地文件；未命中则经 <see cref="DiskCacheService"/> 流式落盘后返回。
    /// </summary>
    /// <param name="id">包 ID（路由参数，限长 255 字符）。</param>
    /// <param name="version">包版本（路由参数，限长 255 字符）。</param>
    /// <param name="file">文件名（路由参数，限长 255 字符）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>成功返回本地文件（SendFile）；上游非 2xx 透传状态码；磁盘写失败返回 503。</returns>
    public async Task<IResult> GetPackageFile(
        [StringLength(255)] string id,
        [StringLength(255)] string version,
        [StringLength(255)] string file,
        CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();
        var versionLower = version.ToLowerInvariant();
        var fileLower = file.ToLowerInvariant();

        var cacheDir = Path.Combine(_options.CachePath, idLower, versionLower);
        var cacheFile = Path.Combine(cacheDir, fileLower);

        // Content-Type 回退：与磁盘命中逻辑一致（.nupkg → octet-stream，其余 → json）
        var fallbackContentType = file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream"
            : "application/json";

        var targetUrl = $"{NuGetFlatContainerUrlBase}/{idLower}/{versionLower}/{fileLower}";

        return await _diskCache.DownloadToCacheAsync(
            "NuGet", targetUrl, cacheFile, fallbackContentType, cancellationToken);
    }
}
