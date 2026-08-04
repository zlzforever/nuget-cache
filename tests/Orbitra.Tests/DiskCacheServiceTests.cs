using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbitra.Configuration;
using Orbitra.Services;
using Orbitra.Tests.Helpers;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="DiskCacheService"/> 单元测试：覆盖磁盘缓存命中/未命中、多上游回退
/// （网络异常/404/5xx）、digest 流式校验、磁盘写失败 503、全部失败 502/透传状态码、
/// 客户端取消 tmp 清理。
/// </summary>
public sealed class DiskCacheServiceTests
{
    /// <summary>blob 内容。</summary>
    private static readonly byte[] BlobContent = Encoding.UTF8.GetBytes("hello-orbitra-blob");

    /// <summary>blob 内容的 sha256 digest。</summary>
    private static readonly string BlobDigest = DigestTestHelper.Of(BlobContent);

    /// <summary>构造被测服务。</summary>
    private static (DiskCacheService Service, FakeHttpMessageHandler Upstream, string CachePath) Create(
        string upstreamUrls, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder, bool blobVerify = true)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "orbitra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var options = TestProxyOptionsFactory.CreateDockerOptions(cachePath, upstreamUrls, blobVerify);
        var upstream = new FakeHttpMessageHandler(responder);
        var factory = new FakeHttpClientFactory(upstream);
        var service = new DiskCacheService(factory, options, NullLogger<DiskCacheService>.Instance);
        return (service, upstream, cachePath);
    }

    /// <summary>构造指定主机的 blob 下载 URL。</summary>
    private static string BlobUrl(string host, string digest) => $"http://{host}/v2/library/nginx/blobs/{digest}";

    /// <summary>构造单上游 blob 下载 URL 列表。</summary>
    private static string[] SingleBlobUrls(string digest) => new[] { BlobUrl("up1.local", digest) };

    /// <summary>构造双上游 blob 下载 URL 列表（up1 优先、up2 回退）。</summary>
    private static string[] DualBlobUrls(string digest) =>
        new[] { BlobUrl("up1.local", digest), BlobUrl("up2.local", digest) };

    /// <summary>目标缓存文件路径。</summary>
    private static string CacheFile(string cachePath, string digest)
    {
        var hex = DockerPathParser.DigestToFileName(digest);
        return Path.Combine(cachePath, "docker", "blobs", "sha256", hex[..2], hex);
    }

    /// <summary>发起一次单上游 blob 下载（默认校验开关与 Content-Type）。</summary>
    private static Task<Microsoft.AspNetCore.Http.IResult> DownloadSingle(
        DiskCacheService service, string cachePath, string digest, CancellationToken token = default)
    {
        return service.DownloadToCacheAsync(
            "Docker",
            SingleBlobUrls(digest),
            CacheFile(cachePath, digest),
            "application/octet-stream",
            token);
    }

    [Fact]
    public async Task Download_FirstDownload_WritesFileToDisk()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local", _ => Task.FromResult(FakeResponses.Bytes(BlobContent)));

        var result = await DownloadSingle(service, cachePath, BlobDigest);

        Assert.NotNull(result);
        Assert.Equal(1, upstream.CountRequests(_ => true));
        var file = CacheFile(cachePath, BlobDigest);
        Assert.True(File.Exists(file));
        Assert.Equal(BlobContent, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Download_SecondCall_DiskHitWithoutTouchingUpstream()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local", _ => Task.FromResult(FakeResponses.Bytes(BlobContent)));

        var first = await DownloadSingle(service, cachePath, BlobDigest);
        var second = await DownloadSingle(service, cachePath, BlobDigest);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Download_Upstream1ConnectionRefused_FallsBackToUpstream2()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(CacheFile(cachePath, BlobDigest)));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up1.local")));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Download_Upstream1NotFound_FallsBackToUpstream2()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(CacheFile(cachePath, BlobDigest)));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Download_Upstream1ServerError_FallsBackToUpstream2()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(File.Exists(CacheFile(cachePath, BlobDigest)));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Download_DigestMismatchUpstream1_FallsBackToUpstream2()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                // up1 返回与期望 digest 不符的毒化内容
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(FakeResponses.Bytes(Encoding.UTF8.GetBytes("poisoned-content")));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None,
            expectedSha256: DockerPathParser.DigestToFileName(BlobDigest));

        Assert.NotNull(result);
        var file = CacheFile(cachePath, BlobDigest);
        Assert.True(File.Exists(file));
        Assert.Equal(BlobContent, File.ReadAllBytes(file));
        // 毒化内容被拒绝：tmp 清理、无残留
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up1.local")));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("up2.local")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public async Task Download_DigestMismatchAllUpstreams_Returns502()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            _ => Task.FromResult(FakeResponses.Bytes(Encoding.UTF8.GetBytes("poisoned-content"))));

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None,
            expectedSha256: DockerPathParser.DigestToFileName(BlobDigest));

        var executed = await ExecuteResultAsync(result);
        Assert.Equal(502, executed.Status);
        Assert.False(File.Exists(CacheFile(cachePath, BlobDigest)));
    }

    [Fact]
    public async Task Download_AllUpstreamsNetworkFail_Returns502()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None);

        var executed = await ExecuteResultAsync(result);
        Assert.Equal(502, executed.Status);
    }

    [Fact]
    public async Task Download_AllUpstreams404_ReturnsLastStatusCode404()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local,http://up2.local",
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await service.DownloadToCacheAsync(
            "Docker", DualBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None);

        var executed = await ExecuteResultAsync(result);
        Assert.Equal(404, executed.Status);
    }

    [Fact]
    public async Task Download_DiskWriteFailure_Returns503()
    {
        // 构造磁盘写失败：父路径存在同名文件，Directory.CreateDirectory 抛 IOException
        var cachePath = Path.Combine(Path.GetTempPath(), "orbitra-tests", Guid.NewGuid().ToString("N"));
        var blockerDir = Path.Combine(cachePath, "docker", "blobs", "sha256");
        Directory.CreateDirectory(blockerDir);
        var blockerFile = Path.Combine(blockerDir, "ab");
        File.WriteAllText(blockerFile, "i-am-a-file-not-a-dir");

        var options = TestProxyOptionsFactory.CreateDockerOptions(cachePath, "http://up1.local");
        var upstream = new FakeHttpMessageHandler(_ => Task.FromResult(FakeResponses.Bytes(BlobContent)));
        var factory = new FakeHttpClientFactory(upstream);
        var service = new DiskCacheService(factory, options, NullLogger<DiskCacheService>.Instance);

        // cacheFile 的父目录是已存在的文件，Directory.CreateDirectory 抛 IOException → 503
        var cacheFile = Path.Combine(blockerDir, "ab", "cd");
        var result = await service.DownloadToCacheAsync(
            "Docker", SingleBlobUrls(BlobDigest), cacheFile,
            "application/octet-stream", CancellationToken.None);

        var executed = await ExecuteResultAsync(result);
        Assert.Equal(503, executed.Status);
    }

    [Fact]
    public async Task Download_ClientCancelDuringCopy_CleansTmpAndThrows()
    {
        var cts = new CancellationTokenSource();
        var (service, upstream, cachePath) = Create(
            "http://up1.local",
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CancellingHttpContent("partial-content", 8, cts),
            }));

        var file = CacheFile(cachePath, BlobDigest);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await DownloadSingle(service, cachePath, BlobDigest, cts.Token);
        });

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public async Task Download_UnauthorizedAllUpstreams_AddsChallengeHeader()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local",
            _ => Task.FromResult(FakeResponses.BearerChallenge401("https://auth.local/token", "reg", "scope")));

        var result = await service.DownloadToCacheAsync(
            "Docker", SingleBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None,
            unauthorizedChallenge: "Basic realm=\"Orbitra\"");

        var executed = await ExecuteResultAsync(result);
        Assert.Equal(401, executed.Status);
        Assert.Equal("Basic realm=\"Orbitra\"", executed.Headers["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task Download_TokenExchangeThenRetry_SameUpstreamSucceeds()
    {
        var (service, upstream, cachePath) = Create(
            "http://up1.local",
            request =>
            {
                if (request.RequestUri!.Host == "auth.local")
                {
                    return Task.FromResult(FakeResponses.Json("""{"token":"tok-abc","expires_in":300}"""));
                }

                var auth = request.Headers.Authorization?.ToString();
                if (auth is not null && auth.StartsWith("Bearer ", StringComparison.Ordinal))
                {
                    return Task.FromResult(FakeResponses.Bytes(BlobContent));
                }

                return Task.FromResult(FakeResponses.BearerChallenge401(
                    "https://auth.local/token", "reg", "repository:library/nginx:pull"));
            });

        var tokenProvider = async (string url, string wwwAuthenticate) =>
        {
            DockerTokenService.TryParseBearerChallenge(
                wwwAuthenticate, out var realm, out var serviceName, out var scope);
            var tokenService = new DockerTokenService(
                new FakeHttpClientFactory(upstream),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<DockerTokenService>.Instance);
            return await tokenService.GetBearerTokenAsync(realm, serviceName, scope, null);
        };

        var result = await service.DownloadToCacheAsync(
            "Docker", SingleBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None,
            unauthorizedTokenProvider: tokenProvider,
            unauthorizedChallenge: "Basic realm=\"Orbitra\"");

        Assert.NotNull(result);
        Assert.True(File.Exists(CacheFile(cachePath, BlobDigest)));
        // 首次 401 + 带 Bearer 重试 = 2 次 blob 请求，1 次 token 请求
        Assert.Equal(2, upstream.CountRequests(r => r.Url.Contains("blobs/")));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("auth.local")));
        Assert.Equal(1, upstream.CountRequests(
            r => r.GetHeader("Authorization")?.StartsWith("Bearer tok-abc") == true));
    }

    [Fact]
    public async Task Download_401Retry_DropsClientAuthorizationHeader()
    {
        // 客户端携带原始凭据访问私有仓库：401 重试仅携带 Bearer token，绝不重复携带客户端原始 Authorization
        const string clientAuthorization = "Basic dXNlcjpwYXNz";
        var (service, upstream, cachePath) = Create(
            "http://up1.local",
            request =>
            {
                if (request.RequestUri!.Host == "auth.local")
                {
                    return Task.FromResult(FakeResponses.Json("""{"token":"tok-abc","expires_in":300}"""));
                }

                if (request.Headers.Authorization?.ToString().StartsWith("Bearer ", StringComparison.Ordinal) == true)
                {
                    return Task.FromResult(FakeResponses.Bytes(BlobContent));
                }

                return Task.FromResult(FakeResponses.BearerChallenge401(
                    "https://auth.local/token", "reg", "repository:library/nginx:pull"));
            });

        var requestHeaders = new Dictionary<string, string> { ["Authorization"] = clientAuthorization };
        var tokenProvider = async (string url, string wwwAuthenticate) =>
        {
            DockerTokenService.TryParseBearerChallenge(
                wwwAuthenticate, out var realm, out var serviceName, out var scope);
            var tokenService = new DockerTokenService(
                new FakeHttpClientFactory(upstream),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<DockerTokenService>.Instance);
            return await tokenService.GetBearerTokenAsync(realm, serviceName, scope, clientAuthorization);
        };

        var result = await service.DownloadToCacheAsync(
            "Docker", SingleBlobUrls(BlobDigest), CacheFile(cachePath, BlobDigest),
            "application/octet-stream", CancellationToken.None,
            requestHeaders: requestHeaders,
            unauthorizedTokenProvider: tokenProvider,
            unauthorizedChallenge: "Basic realm=\"Orbitra\"");

        Assert.NotNull(result);
        Assert.True(File.Exists(CacheFile(cachePath, BlobDigest)));
        // 首次 401（携带客户端 Basic）+ 带 Bearer 重试 = 2 次 blob 请求，1 次 token 请求
        Assert.Equal(2, upstream.CountRequests(r => r.Url.Contains("blobs/")));
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("auth.local")));
        // 初始请求透传客户端原始 Basic 凭据（仅此一路）
        Assert.Equal(1, upstream.CountRequests(
            r => r.Url.Contains("blobs/") && r.GetHeader("Authorization") == clientAuthorization));
        // 带 Bearer 的重试请求只含一路 Authorization，绝不叠加客户端原始 Basic 凭据
        var retries = upstream.Requests.Where(r =>
            r.Url.Contains("blobs/") &&
            r.GetHeader("Authorization")?.StartsWith("Bearer tok-abc", StringComparison.Ordinal) == true);
        Assert.Single(retries);
        Assert.All(retries, r => Assert.DoesNotContain(clientAuthorization, r.GetHeader("Authorization")));
    }

    /// <summary>执行 IResult 并读取状态码。</summary>
    private static async Task<(int Status, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> ExecuteResultAsync(
        Microsoft.AspNetCore.Http.IResult result)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        // IResult 执行依赖 RequestServices（日志器等），提供最小服务集合
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        context.RequestServices = services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers);
    }
}

/// <summary>
/// 模拟客户端中断：写出部分字节后取消外部令牌源并抛出
/// <see cref="OperationCanceledException"/>，使 DiskCacheService 的
/// 「客户端取消 → 清理 tmp」分支可被触发。
/// </summary>
internal sealed class CancellingHttpContent : HttpContent
{
    private readonly string _content;
    private readonly int _prefixLength;
    private readonly CancellationTokenSource _externalCts;

    /// <summary>
    /// 初始化取消型内容。
    /// </summary>
    /// <param name="content">内容文本。</param>
    /// <param name="prefixLength">写出到临时文件的前缀字节数。</param>
    /// <param name="externalCts">
    /// 外部取消令牌源（由被测调用传入，写出前缀后由本内容触发取消）。
    /// </param>
    public CancellingHttpContent(string content, int prefixLength, CancellationTokenSource externalCts)
    {
        _content = content;
        _prefixLength = prefixLength;
        _externalCts = externalCts;
    }

    /// <summary>
    /// 写出前缀字节后取消外部令牌源并抛出取消异常，模拟下载中途客户端断开。
    /// </summary>
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        var prefix = Encoding.UTF8.GetBytes(_content[.._prefixLength]);
        stream.Write(prefix, 0, prefix.Length);
        stream.Flush();
        _externalCts.Cancel();
        throw new OperationCanceledException(_externalCts.Token);
    }

    /// <summary>
    /// 计算内容总长度。
    /// </summary>
    protected override bool TryComputeLength(out long length)
    {
        length = Encoding.UTF8.GetByteCount(_content);
        return true;
    }
}
