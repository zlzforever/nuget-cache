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

builder.Services.AddHttpClient("NuGet")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 1000,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        EnableMultipleHttp2Connections = true
    });

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
app.MapGet("/", () => Results.Text("I am ok: " + DateTimeOffset.UtcNow));
app.MapFallback((HttpContext ctx) =>
{
    logger.LogInformation("[Fallback] {Method} {Path} -> 404", ctx.Request.Method, ctx.Request.Path);
    return Results.NotFound();
});

await app.RunAsync();