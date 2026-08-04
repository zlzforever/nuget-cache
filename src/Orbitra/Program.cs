using Orbitra.Configuration;
using Orbitra.Handlers;
using Orbitra.Services;

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

// 统一的 SocketsHttpHandler 连接池配置，NuGet/Maven/npm 复用同一套参数
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
builder.Services.AddSingleton<DockerTokenService>();
builder.Services.AddSingleton<NuGetProxyHandler>();
builder.Services.AddSingleton<MavenProxyHandler>();
builder.Services.AddSingleton<NpmProxyHandler>();
builder.Services.AddSingleton<DockerProxyHandler>();

builder.Services.AddHttpClient("NuGet")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

// Maven 专用 HttpClient，复用相同的连接池配置
builder.Services.AddHttpClient("Maven")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

// npm 专用 HttpClient，复用相同的连接池配置
builder.Services.AddHttpClient("npm")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

// Docker 专用 HttpClient：连接池参数与其余一致，但超时放宽至 30 分钟（大 blob 下载）
builder.Services.AddHttpClient("Docker")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(30))
    .ConfigurePrimaryHttpMessageHandler(CreateSocketsHttpHandler);

builder.Services.AddMemoryCache();

var app = builder.Build();

// 请求日志中间件：统一打印每个请求的方法与路径，保证所有代理请求（NuGet/Maven/npm/健康检查/404）实时有迹可循
app.Use(async (context, next) =>
{
    app.Logger.LogInformation("{Method} {Path}", context.Request.Method, context.Request.Path);
    await next(context);
});

// 启动即校验配置（fail-fast），并解析 handler 供路由注册
var proxyOptions = app.Services.GetRequiredService<ProxyOptions>();
var nuGetHandler = app.Services.GetRequiredService<NuGetProxyHandler>();
var mavenHandler = app.Services.GetRequiredService<MavenProxyHandler>();
var npmHandler = app.Services.GetRequiredService<NpmProxyHandler>();
var dockerHandler = app.Services.GetRequiredService<DockerProxyHandler>();

if (!Directory.Exists(proxyOptions.CachePath))
{
    Directory.CreateDirectory(proxyOptions.CachePath);
}
app.Logger.LogInformation("Cache root path: {Path}", proxyOptions.CachePath);
app.Logger.LogInformation("NuGet proxy domain: {Domain}", proxyOptions.NuGetProxyDomain);
app.Logger.LogInformation("Maven upstreams: {Upstreams}", string.Join(", ", proxyOptions.MavenUpstreams));
app.Logger.LogInformation("npm upstream: {Upstream}", proxyOptions.NpmUpstream);
app.Logger.LogInformation("docker upstreams: {Upstreams}", string.Join(", ", proxyOptions.DockerUpstreams));

// NuGet 路由（/nuget 前缀）：服务索引、包版本索引、包文件下载，均支持 GET/HEAD
app.MapMethods("/nuget/v3/index.json", ["GET", "HEAD"], (Delegate)nuGetHandler.GetServiceIndex);
app.MapMethods("/nuget/v3-flatcontainer/{id}/index.json", ["GET", "HEAD"], (Delegate)nuGetHandler.GetPackageIndex);
app.MapMethods("/nuget/v3-flatcontainer/{id}/{version}/{file}", ["GET", "HEAD"], (Delegate)nuGetHandler.GetPackageFile);

// Maven 通配路由：{**path} 与原请求路径 1:1 透传上游，产物磁盘永久缓存，元数据内存缓存
app.MapMethods("/maven/{**path}", ["GET", "HEAD"], (Delegate)mavenHandler.HandleMavenRoute);

// npm 通配路由：{**path} 透传 NPM 上游，tarball 磁盘永久缓存，包元数据内存短 TTL 缓存
app.MapMethods("/npm/{**path}", ["GET", "HEAD"], (Delegate)npmHandler.HandleNpmRoute);

// Docker registry 路由：主路由 /v2 系列（Docker Registry V2 协议必需）。
// 用 {**path} catch-all 一次性覆盖 /v2、/v2/、/v2/{path} 三种形态（catch-all 匹配空段，
// 与 /maven/{**path} 注册方式一致），空路径由 handler 内识别为版本探测。全部 GET/HEAD
app.MapMethods("/v2/{**path}", ["GET", "HEAD"], (Delegate)dockerHandler.HandleDockerRoute);

app.MapMethods("/", ["GET", "HEAD"], (HttpContext httpContext) =>
{
    // 健康检查根路径：显式设置 Content-Length，保证 HEAD 与 GET 一致
    var body = "I am ok: " + DateTimeOffset.UtcNow;
    return TextContentResult.Build(httpContext, body, "text/plain; charset=utf-8");
});
app.MapFallback(() => Results.NotFound());

await app.RunAsync();
