using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

// 配置 Kestrel 并发连接与超时
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // 100M
    serverOptions.Limits.MaxRequestBodySize = 1024288000;
    // 最大并发 TCP 连接（null 表示无限制，生产建议设 5000-50000，依内存而定）
    serverOptions.Limits.MaxConcurrentConnections = 5000;
    // WebSocket 等升级连接的单独限制（默认 100）
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 500;
    // 长连接保活超时（默认 2 分钟，可按需调整）
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    // 请求头超时（默认 30 秒）
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    // 禁用同步 I/O，强制异步，避免线程阻塞
    serverOptions.AllowSynchronousIO = false;
});

// builder.Logging.AddSimpleConsole(options =>
// {
//     options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
//     options.IncludeScopes = false;
//     options.SingleLine = true;
// });

if (!Uri.TryCreate(Environment.GetEnvironmentVariable("PROXY_DOMAIN"), UriKind.Absolute, out var proxyDomain))
{
    throw new ArgumentException("Invalid proxy URI");
}

// Maven 上游地址配置：默认 Maven Central，允许通过环境变量覆盖（如国内镜像）
var mavenUpstreamEnv = Environment.GetEnvironmentVariable("MAVEN_UPSTREAM_URL");
if (string.IsNullOrWhiteSpace(mavenUpstreamEnv))
{
    mavenUpstreamEnv = "https://repo.maven.apache.org/maven2";
}

// 启动时校验 Maven 上游地址合法性，失败抛异常（与 PROXY_DOMAIN 校验方式一致）
if (!Uri.TryCreate(mavenUpstreamEnv, UriKind.Absolute, out var mavenUpstreamUri))
{
    throw new ArgumentException("Invalid Maven upstream URI");
}

// 归一化上游地址：去除末尾 '/'，保证与 {**path} 拼接时路径正确
var mavenUpstream = mavenUpstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

// 统一的 SocketsHttpHandler 连接池配置，NuGet 与 Maven 复用同一套参数
static SocketsHttpHandler CreateSocketsHttpHandler() => new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    MaxConnectionsPerServer = 1000,
    ConnectTimeout = TimeSpan.FromSeconds(30),
    EnableMultipleHttp2Connections = true
};

builder.Services.AddHttpClient("NuGet")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

// Maven 专用 HttpClient，复用相同的连接池配置
builder.Services.AddHttpClient("Maven")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

builder.Services.AddMemoryCache();

var app = builder.Build();
var logger = app.Logger;

var cachePath = Environment.GetEnvironmentVariable("CACHE_PATH");
cachePath = string.IsNullOrWhiteSpace(cachePath)
    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nuget-cache")
    : cachePath;
if (!Directory.Exists(cachePath))
{
    Directory.CreateDirectory(cachePath);
}

logger.LogInformation("Cache root path: {Path}", cachePath);

app.MapGet("/v3/index.json", async (IMemoryCache cache, IHttpClientFactory http) =>
{
    logger.LogInformation("GET /v3/index.json");

    var cacheKey = "nuget:index.json";

    if (cache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson != null)
    {
        return Results.Content(cachedJson, "application/json");
    }

    var httpClient = http.CreateClient("NuGet");
    using var response = await httpClient.GetAsync("https://api.nuget.org/v3/index.json");

    if (!response.IsSuccessStatusCode)
    {
        logger.LogError("Failed: {StatusCode}", (int)response.StatusCode);
        return Results.StatusCode((int)response.StatusCode);
    }

    var json = await response.Content.ReadAsStringAsync();

    var proxyUrl = $"{proxyDomain}v3-flatcontainer/";
    json = Regex.Replace(json, @"https?://[^/]+/v3-flatcontainer/", proxyUrl, RegexOptions.IgnoreCase);

    cache.Set(cacheKey, json, TimeSpan.FromMinutes(60));
    return Results.Content(json, "application/json");
});

app.MapGet("/v3-flatcontainer/{id}/index.json",
    async ([StringLength(255)] string id, IMemoryCache cache, IHttpClientFactory http) =>
    {
        var idLower = id.ToLowerInvariant();

        var cacheKey = $"nuget-package:{idLower}:index.json";

        if (cache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson != null)
        {
            logger.LogInformation("Index cache hit: {Id}", idLower);
            return Results.Content(cachedJson, "application/json");
        }

        var targetUrl = $"https://api.nuget.org/v3-flatcontainer/{idLower}/index.json";

        var httpClient = http.CreateClient("NuGet");
        using var response = await httpClient.GetAsync(targetUrl);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Fetched index failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        logger.LogInformation("Fetched index successfully: {Url}", targetUrl);

        cache.Set(cacheKey, json, TimeSpan.FromMinutes(60));
        return Results.Content(json, "application/json");
    });

app.MapGet("/v3-flatcontainer/{id}/{version}/{file}",
    async ([StringLength(255)] string id, [StringLength(255)] string version, [StringLength(255)] string file,
        IHttpClientFactory http) =>
    {
        var idLower = id.ToLowerInvariant();
        var versionLower = version.ToLowerInvariant();
        var fileLower = file.ToLowerInvariant();

        var cacheDir = Path.Combine(cachePath, idLower, versionLower);
        var cacheFile = Path.Combine(cacheDir, fileLower);

        if (File.Exists(cacheFile))
        {
            logger.LogInformation("Package cache hit: {File}",
                cacheFile);

            var contentType = file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                ? "application/octet-stream"
                : "application/json";

            return Results.File(cacheFile, contentType);
        }

        var targetUrl = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{versionLower}/{fileLower}";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var httpClient = http.CreateClient("NuGet");
        using var response = await httpClient.GetAsync(targetUrl);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Download failed ({Elapsed}ms): {StatusCode} - {Url}", sw.ElapsedMilliseconds,
                (int)response.StatusCode, targetUrl);
            return Results.StatusCode((int)response.StatusCode);
        }

        var content = await response.Content.ReadAsByteArrayAsync();
        sw.Stop();

        Directory.CreateDirectory(cacheDir);
        await File.WriteAllBytesAsync(cacheFile, content);

        logger.LogInformation("Download success ({Elapsed}ms): {File}, Size: {Size} bytes", sw.ElapsedMilliseconds,
            cacheFile,
            content.Length);

        var contentType2 = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return Results.Bytes(content, contentType2);
    });
// Maven 通配路由：{**path} 与原请求路径 1:1 透传上游，产物磁盘永久缓存，元数据内存缓存
app.MapGet("/maven/{**path}", async (string path, IMemoryCache cache, IHttpClientFactory http) =>
{
    // 空路径（/maven 或 /maven/）不代理，直接返回 404
    if (string.IsNullOrWhiteSpace(path))
    {
        logger.LogWarning("Maven empty path rejected");
        return Results.NotFound();
    }

    // 路径安全校验：逐段校验，拒绝 .. . 空段 控制字符及跨平台非法字符，保留大小写
    var (isValid, reason) = ValidateMavenPath(path);
    if (!isValid)
    {
        logger.LogWarning("Maven path rejected: {Path} - {Reason}", path, reason);
        return Results.BadRequest();
    }

    // maven-metadata.xml 精确文件名匹配才走内存缓存（快照 5 分钟 / 非快照 60 分钟），不写盘；
    // 校验和伴生文件（maven-metadata.xml.sha1/.md5/.sha256）含相同子串，但按 PRD 应走磁盘缓存，
    // 因此使用 Path.GetFileName 精确匹配，避免子串匹配误伤
    if (Path.GetFileName(path).Equals("maven-metadata.xml", StringComparison.Ordinal))
    {
        return await HandleMavenMetadataAsync(path, cache, http);
    }

    // 其余产物与校验和文件走磁盘永久缓存
    return await HandleMavenArtifactAsync(path, http);
});

app.MapGet("/", () => Results.Text("I am ok: " + DateTimeOffset.UtcNow));
app.MapFallback((HttpContext ctx) =>
{
    logger.LogInformation("[Fallback] {Method} {Path} -> 404", ctx.Request.Method, ctx.Request.Path);
    return Results.NotFound();
});

// 校验 Maven 路径是否安全：逐段校验，拒绝 ..、.、空段、控制字符及跨平台非法字符，保留大小写
// 返回 (是否合法, 拒绝原因)
static (bool IsValid, string Reason) ValidateMavenPath(string path)
{
    const int maxTotalPathLength = 4096;
    if (path.Length > maxTotalPathLength)
    {
        return (false, $"总路径长度超过上限 {maxTotalPathLength}");
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

        if (segment.Length > 255)
        {
            return (false, $"路径段长度超过上限 255: {segment}");
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

// 处理 maven-metadata.xml：仅成功响应写内存缓存，TTL 快照 5 分钟 / 非快照 60 分钟，不落盘
async Task<IResult> HandleMavenMetadataAsync(string path, IMemoryCache cache, IHttpClientFactory http)
{
    var cacheKey = $"maven:metadata:{path}";

    if (cache.TryGetValue(cacheKey, out string? cachedXml) && cachedXml != null)
    {
        logger.LogInformation("Maven metadata cache hit: {Path}", path);
        return Results.Content(cachedXml, "application/xml");
    }

    var targetUrl = $"{mavenUpstream}/{path}";
    var httpClient = http.CreateClient("Maven");
    using var response = await httpClient.GetAsync(targetUrl);

    if (!response.IsSuccessStatusCode)
    {
        logger.LogWarning("Maven metadata fetch failed: {StatusCode} - {Url}", (int)response.StatusCode, targetUrl);
        return Results.StatusCode((int)response.StatusCode);
    }

    var xml = await response.Content.ReadAsStringAsync();

    var ttl = IsSnapshotMetadata(path) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(60);
    cache.Set(cacheKey, xml, ttl);
    logger.LogInformation("Maven metadata cached ({Ttl}): {Path}", ttl, path);

    var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/xml";
    return Results.Content(xml, contentType);
}

// 判断元数据是否为快照：任一中间段以 -SNAPSHOT 结尾即视为快照元数据
static bool IsSnapshotMetadata(string path)
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

// 处理 Maven 产物与校验和文件：磁盘永久缓存到 {CACHE_PATH}/maven/{path}，上游 2xx 才落盘
async Task<IResult> HandleMavenArtifactAsync(string path, IHttpClientFactory http)
{
    var cacheFile = Path.Combine(cachePath, "maven", path);

    // 磁盘缓存命中直接返回，不产生上游请求
    if (File.Exists(cacheFile))
    {
        logger.LogInformation("Maven cache hit: {File}", cacheFile);
        return Results.File(cacheFile, GetMavenContentType(path));
    }

    var targetUrl = $"{mavenUpstream}/{path}";

    var sw = System.Diagnostics.Stopwatch.StartNew();

    var httpClient = http.CreateClient("Maven");
    using var response = await httpClient.GetAsync(targetUrl);

    // 非 2xx 直接透传状态码，不落盘不缓存
    if (!response.IsSuccessStatusCode)
    {
        logger.LogWarning("Maven download failed ({Elapsed}ms): {StatusCode} - {Url}", sw.ElapsedMilliseconds,
            (int)response.StatusCode, targetUrl);
        return Results.StatusCode((int)response.StatusCode);
    }

    var content = await response.Content.ReadAsByteArrayAsync();
    sw.Stop();

    var cacheDir = Path.GetDirectoryName(cacheFile);

    // 磁盘写失败不静默：目录创建与落盘写入统一捕获 IOException / UnauthorizedAccessException，
    // 结构化日志记录 cacheFile 上下文后返回 503，由框架统一处理会丢失落盘上下文
    try
    {
        if (!string.IsNullOrEmpty(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        await File.WriteAllBytesAsync(cacheFile, content);
    }
    catch (IOException ex)
    {
        logger.LogError(ex, "Maven cache file write failed (IOException): {File}", cacheFile);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (UnauthorizedAccessException ex)
    {
        logger.LogError(ex, "Maven cache file write failed (UnauthorizedAccess): {File}", cacheFile);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    logger.LogInformation("Maven download success ({Elapsed}ms): {File}, Size: {Size} bytes", sw.ElapsedMilliseconds,
        cacheFile, content.Length);

    var contentType = response.Content.Headers.ContentType?.MediaType ?? GetMavenContentType(path);
    return Results.Bytes(content, contentType);
}

// 根据文件扩展名推断 Content-Type（磁盘缓存命中时使用，避免依赖上游响应头）
static string GetMavenContentType(string path)
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

await app.RunAsync();