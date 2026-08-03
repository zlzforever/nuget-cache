using NuGetCache.Configuration;
using NuGetCache.Handlers;
using NuGetCache.Services;

// 组合根：集中完成 Kestrel 配置、DI 注册与路由注册，业务逻辑分布在 Handlers / Services / Configuration 中

var builder = WebApplication.CreateBuilder(args);

// 启动即打印版本 banner（组合根最顶部、先于配置 fail-fast），保证配置非法时也能看到 LOGO 与版本便于排障
Console.WriteLine(AppInfo.Banner());

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

// 统一的 SocketsHttpHandler 连接池配置，NuGet 与 Maven 复用同一套参数
static SocketsHttpHandler CreateSocketsHttpHandler() => new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    MaxConnectionsPerServer = 1000,
    ConnectTimeout = TimeSpan.FromSeconds(30),
    EnableMultipleHttp2Connections = true
};

builder.Services.AddSingleton(sp =>
    ProxyOptions.Load(sp.GetRequiredService<ILogger<ProxyOptions>>()));

builder.Services.AddSingleton<DiskCacheService>();
builder.Services.AddSingleton<NuGetProxyHandler>();
builder.Services.AddSingleton<MavenProxyHandler>();

builder.Services.AddHttpClient("NuGet")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

// Maven 专用 HttpClient，复用相同的连接池配置
builder.Services.AddHttpClient("Maven")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

builder.Services.AddMemoryCache();

var app = builder.Build();

// 启动即校验配置（fail-fast），并解析 handler 供路由注册
var proxyOptions = app.Services.GetRequiredService<ProxyOptions>();
var nuGetHandler = app.Services.GetRequiredService<NuGetProxyHandler>();
var mavenHandler = app.Services.GetRequiredService<MavenProxyHandler>();

if (!Directory.Exists(proxyOptions.CachePath))
{
    Directory.CreateDirectory(proxyOptions.CachePath);
}
app.Logger.LogInformation("Cache root path: {Path}", proxyOptions.CachePath);
app.Logger.LogInformation("NuGet proxy domain: {Domain}", proxyOptions.NuGetProxyDomain);
app.Logger.LogInformation("Maven upstream: {Upstream}", proxyOptions.MavenUpstream);

// NuGet 路由：服务索引、包版本索引、包文件下载
app.MapGet("/v3/index.json", nuGetHandler.GetServiceIndex);
app.MapGet("/v3-flatcontainer/{id}/index.json", nuGetHandler.GetPackageIndex);
app.MapGet("/v3-flatcontainer/{id}/{version}/{file}", nuGetHandler.GetPackageFile);

// Maven 通配路由：{**path} 与原请求路径 1:1 透传上游，产物磁盘永久缓存，元数据内存缓存
app.MapGet("/maven/{**path}", mavenHandler.HandleMavenRoute);

app.MapGet("/", () => Results.Text("I am ok: " + DateTimeOffset.UtcNow));
app.MapFallback((HttpContext ctx) =>
{
    app.Logger.LogInformation("[Fallback] {Method} {Path} -> 404", ctx.Request.Method, ctx.Request.Path);
    return Results.NotFound();
});

await app.RunAsync();
