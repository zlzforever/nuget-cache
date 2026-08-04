using System.Net;
using System.Text;
using Orbitra.Services;
using Orbitra.Tests.Helpers;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="Orbitra.Handlers.DockerProxyHandler"/> 集成测试：通过 <see cref="Helpers.DockerTestHarness"/>
/// 装配真实处理器与假上游，覆盖版本探测、blob/manifest/tags 三类端点、HEAD 支持、
/// token 交换重试、Content-Type 透传、磁盘缓存命中、多上游回退与失败响应。
/// </summary>
public sealed class DockerProxyHandlerTests
{
    /// <summary>blob 内容。</summary>
    private static readonly byte[] BlobContent = Encoding.UTF8.GetBytes("hello-orbitra-blob-body");

    /// <summary>blob 内容的 sha256 digest。</summary>
    private static readonly string BlobDigest = DigestTestHelper.Of(BlobContent);

    /// <summary>OCI manifest 内容。</summary>
    private static readonly byte[] ManifestContent = Encoding.UTF8.GetBytes(
        """{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json"}""");

    /// <summary>manifest 内容的 sha256 digest。</summary>
    private static readonly string ManifestDigest = DigestTestHelper.Of(ManifestContent);

    /// <summary>上游返回 blob 内容的编排函数。</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> BlobResponder() =>
        _ => Task.FromResult(FakeResponses.Bytes(BlobContent, "application/octet-stream"));

    [Fact]
    public async Task VersionProbe_Get_SucceedsWithApiVersionHeader()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local", _ => Task.FromResult(FakeResponses.Json("{}", "application/json")));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal("{}", HttpTestHelper.DecodeBody(body));
        Assert.Equal("registry/2.0", headers["Docker-Distribution-Api-Version"].ToString());
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.EndsWith("/v2/") || r.Url.EndsWith("/v2")));
    }

    [Fact]
    public async Task VersionProbe_Head_ReturnsSameContentLengthAsGet()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local", _ => Task.FromResult(FakeResponses.Json("{}", "application/json")));

        var get = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("", ctx, ct));
        var head = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("", ctx, ct), method: "HEAD");

        Assert.Equal(200, get.Status);
        Assert.Equal(200, head.Status);
        Assert.Equal("registry/2.0", head.Headers["Docker-Distribution-Api-Version"].ToString());
        // HEAD 与 GET 的 Content-Length 一致（响应体由 Kestrel 按 HEAD 抑制）
        Assert.Equal(get.Headers.ContentLength, head.Headers.ContentLength);
        Assert.Equal(Encoding.UTF8.GetByteCount("{}"), head.Headers.ContentLength);
    }

    [Fact]
    public async Task VersionProbe_Upstream404_PassesThroughStatusCode()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("", ctx, ct));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task VersionProbe_AllUpstreamsNetworkFail_Returns502()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("", ctx, ct));

        Assert.Equal(502, status);
    }

    [Fact]
    public async Task Blob_Get_FirstDownloadThenDiskHit()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());
        var path = $"library/nginx/blobs/{BlobDigest}";

        var first = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct));
        Assert.Equal(200, first.Status);
        Assert.Equal(BlobContent, first.Body);
        Assert.Equal(BlobDigest, first.Headers["Docker-Content-Digest"].ToString());
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));

        var second = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct));
        Assert.Equal(200, second.Status);
        Assert.Equal(BlobContent, second.Body);
        // 二次请求走磁盘缓存，不触碰上游
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Blob_InvalidDigest_Returns400()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/blobs/not-a-digest", ctx, ct));

        Assert.Equal(400, status);
        Assert.Equal(0, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Blob_PathTraversalName_Returns400()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"a/../b/blobs/{BlobDigest}", ctx, ct));

        Assert.Equal(400, status);
        Assert.Equal(0, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Blob_HeadDiskMiss_PassesThroughUpstreamHeadWithoutGet()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                // HEAD 响应：带 Content-Length，无响应体
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                response.Content.Headers.ContentLength = 12345;
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", BlobDigest);
                return Task.FromResult(response);
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct),
            method: "HEAD");

        Assert.Equal(200, status);
        Assert.Equal(12345L, headers.ContentLength);
        Assert.Equal(BlobDigest, headers["Docker-Content-Digest"].ToString());
        Assert.Empty(body);
        // 仅 HEAD 透传，不触发 GET，不落盘
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Method == HttpMethod.Head));
        Assert.Equal(0, harness.Upstream.CountRequests(r => r.Method == HttpMethod.Get));
        Assert.False(File.Exists(Path.Combine(harness.CachePath, "docker", "blobs", "sha256")));
    }

    [Fact]
    public async Task Blob_HeadDiskHit_ReturnsFileWithoutUpstream()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());
        var path = $"library/nginx/blobs/{BlobDigest}";

        await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct), method: "HEAD");

        Assert.Equal(200, status);
        Assert.Equal(BlobDigest, headers["Docker-Content-Digest"].ToString());
        Assert.Empty(body);
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Blob_Upstream1Fails_FallsBackToUpstream2()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(BlobContent, body);
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Blob_DigestVerifyEnabled_MismatchFallsBackToNextUpstream()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(FakeResponses.Bytes(Encoding.UTF8.GetBytes("poisoned")));
                }

                return Task.FromResult(FakeResponses.Bytes(BlobContent));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(BlobContent, body);
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("up1.local")));
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Manifest_ByTag_GetCachesAndBackfillsDigest()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            _ => Task.FromResult(FakeResponses.Bytes(ManifestContent, "application/vnd.oci.image.manifest.v1+json")));

        var first = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/latest", ctx, ct));

        Assert.Equal(200, first.Status);
        Assert.Equal(ManifestContent, first.Body);
        // by-tag 无上游 Docker-Content-Digest 头时按响应体 sha256 回填
        // （当前实现为纯 hex，无 sha256: 前缀）
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ManifestContent)).ToLowerInvariant(),
            first.Headers["Docker-Content-Digest"].ToString());
        Assert.Equal("application/vnd.oci.image.manifest.v1+json", first.Headers.ContentType.ToString());
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));

        var second = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/latest", ctx, ct));
        Assert.Equal(200, second.Status);
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Manifest_ByTag_UpstreamDigestHeaderNotPresent_ComputedFromBody()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                var response = FakeResponses.Bytes(ManifestContent, "application/vnd.oci.image.manifest.v1+json");
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:deadbeef");
                return Task.FromResult(response);
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/latest", ctx, ct));

        Assert.Equal(200, status);
        // by-tag 回填的 Docker-Content-Digest 为响应体 sha256（当前实现为纯 hex，无 sha256: 前缀）
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ManifestContent)).ToLowerInvariant(),
            headers["Docker-Content-Digest"].ToString());
    }

    [Fact]
    public async Task Manifest_ByTag_InvalidTag_Returns400()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/bad tag!", ctx, ct));

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Manifest_ByTag_ContentTypePassthroughNotNormalized()
    {
        // 用非标准 content-type（含参数）验证逐字节透传、不归一化
        const string exoticContentType = "application/vnd.docker.distribution.manifest.v2+json; charset=utf-8";
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            _ => Task.FromResult(FakeResponses.Bytes(ManifestContent, exoticContentType)));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/latest", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(exoticContentType, headers.ContentType.ToString());
    }

    [Fact]
    public async Task Manifest_ByDigest_GetWritesDiskAndMetaSidecar()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            _ => Task.FromResult(FakeResponses.Bytes(ManifestContent, "application/vnd.oci.image.manifest.v1+json")));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(ManifestContent, body);
        Assert.Equal(ManifestDigest, headers["Docker-Content-Digest"].ToString());
        Assert.Equal("application/vnd.oci.image.manifest.v1+json", headers.ContentType.ToString());

        var hex = DockerPathParser.DigestToFileName(ManifestDigest);
        var jsonFile = Path.Combine(harness.CachePath, "docker", "manifests", "sha256", hex[..2], $"{hex}.json");
        Assert.True(File.Exists(jsonFile));
        Assert.True(File.Exists($"{jsonFile}.meta"));
        Assert.Equal("application/vnd.oci.image.manifest.v1+json", File.ReadAllText($"{jsonFile}.meta").Trim());
    }

    [Fact]
    public async Task Manifest_ByDigest_ReplayMetaContentTypeOnDiskHit()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            _ => Task.FromResult(FakeResponses.Bytes(ManifestContent, "application/vnd.oci.image.manifest.v1+json")));

        // 首次 GET 落盘
        await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct));
        var requestsBeforeRebuild = harness.Upstream.CountRequests(_ => true);

        // 用全新内存缓存重建（模拟进程重启），仅剩磁盘缓存；断言重建后不再触碰上游
        using var fresh = harness.RebuildFreshMemory();
        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => fresh.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(ManifestContent, body);
        Assert.Equal(ManifestDigest, headers["Docker-Content-Digest"].ToString());
        // .meta 侧车精确回放 Content-Type
        Assert.Equal("application/vnd.oci.image.manifest.v1+json", headers.ContentType.ToString());
        Assert.Equal(requestsBeforeRebuild, fresh.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task Manifest_ByDigest_DigestMismatch_FallsBackToNextUpstream()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local,http://up2.local",
            request =>
            {
                if (request.RequestUri!.Host == "up1.local")
                {
                    return Task.FromResult(FakeResponses.Bytes(Encoding.UTF8.GetBytes("different-manifest")));
                }

                return Task.FromResult(FakeResponses.Bytes(
                    ManifestContent, "application/vnd.oci.image.manifest.v1+json"));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(ManifestContent, body);
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("up1.local")));
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("up2.local")));
    }

    [Fact]
    public async Task Manifest_ByDigest_AllMismatch_Returns502()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local,http://up2.local",
            _ => Task.FromResult(FakeResponses.Bytes(Encoding.UTF8.GetBytes("different-manifest"))));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct));

        Assert.Equal(502, status);
    }

    [Fact]
    public async Task Manifest_ByDigest_HeadMiss_PassesThroughUpstreamHead()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                response.Content.Headers.ContentLength = 128;
                return Task.FromResult(response);
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/manifests/{ManifestDigest}", ctx, ct),
            method: "HEAD");

        Assert.Equal(200, status);
        Assert.Equal(128L, headers.ContentLength);
        Assert.Empty(body);
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Method == HttpMethod.Head));
        Assert.Equal(0, harness.Upstream.CountRequests(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task TagsList_Get_CachesAndPreservesLinkHeader()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                var response = FakeResponses.Json(
                    """{"name":"library/nginx","tags":["latest","1.0"]}""", "application/json");
                response.Headers.TryAddWithoutValidation(
                    "Link", "</v2/library/nginx/tags/list?last=latest>; rel=\"next\"");
                return Task.FromResult(response);
            });

        var first = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/tags/list", ctx, ct),
            queryString: "?n=10");

        Assert.Equal(200, first.Status);
        Assert.Contains("latest", HttpTestHelper.DecodeBody(first.Body));
        Assert.Equal("</v2/library/nginx/tags/list?last=latest>; rel=\"next\"", first.Headers["Link"].ToString());
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));

        var second = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/tags/list", ctx, ct));
        Assert.Equal(200, second.Status);
        Assert.Equal(1, harness.Upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task TagsList_InvalidName_Returns400()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("a/../b/tags/list", ctx, ct));

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Token_Blob401WithChallenge_ExchangesAndRetriesSameUpstream()
    {
        using var harness = DockerTestHarness.Create(
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
                    "https://auth.local/token", "reg.local", "repository:library/nginx:pull"));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(BlobContent, body);
        // 首次 401 + 带 Bearer 重试 = 2 次 blob 请求；token 按 scope 缓存只换一次
        Assert.Equal(2, harness.Upstream.CountRequests(r => r.Url.Contains("blobs/")));
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("auth.local")));
        Assert.Equal(1, harness.Upstream.CountRequests(
            r => r.GetHeader("Authorization")?.StartsWith("Bearer tok-abc") == true));
    }

    [Fact]
    public async Task Token_ExchangeFails_Returns401WithOrbitraBasicChallenge()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                if (request.RequestUri!.Host == "auth.local")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(FakeResponses.BearerChallenge401(
                    "https://auth.local/token", "reg.local", "repository:library/nginx:pull"));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct));

        Assert.Equal(401, status);
        // 不透传上游 Bearer 质询，改用 Orbitra 自身 Basic 质询
        Assert.Equal("Basic realm=\"Orbitra\"", headers["WWW-Authenticate"].ToString());
        Assert.DoesNotContain("Bearer", headers["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task Token_Manifest401WithChallenge_ExchangesAndRetries()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                if (request.RequestUri!.Host == "auth.local")
                {
                    return Task.FromResult(FakeResponses.Json("""{"token":"tok-manifest","expires_in":300}"""));
                }

                if (request.Headers.Authorization?.ToString().StartsWith("Bearer ", StringComparison.Ordinal) == true)
                {
                    return Task.FromResult(FakeResponses.Bytes(
                    ManifestContent, "application/vnd.oci.image.manifest.v1+json"));
                }

                return Task.FromResult(FakeResponses.BearerChallenge401(
                    "https://auth.local/token", "reg.local", "repository:library/nginx:pull"));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("library/nginx/manifests/latest", ctx, ct));

        Assert.Equal(200, status);
        Assert.Equal(ManifestContent, body);
        Assert.Equal(2, harness.Upstream.CountRequests(r => r.Url.Contains("manifests/")));
        Assert.Equal(1, harness.Upstream.CountRequests(r => r.Url.Contains("auth.local")));
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute("some/random/path", ctx, ct));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ClientAuthorization_ForwardedToUpstreamNotLogged()
    {
        using var harness = DockerTestHarness.Create(
            "http://up1.local",
            request =>
            {
                // 未带 Authorization 则 401，验证客户端头被透传
                if (request.Headers.Authorization?.ToString() == "Basic dXNlcjpwYXNz")
                {
                    return Task.FromResult(FakeResponses.Bytes(BlobContent));
                }

                return Task.FromResult(FakeResponses.BearerChallenge401(
                    "https://auth.local/token", "reg.local", "repository:library/nginx:pull"));
            });

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute($"library/nginx/blobs/{BlobDigest}", ctx, ct),
            authorization: "Basic dXNlcjpwYXNz");

        Assert.Equal(200, status);
        // Authorization 头与 token 绝不进入日志
        var allLogs = harness.Logger.AllText();
        Assert.DoesNotContain("Basic dXNlcjpwYXNz", allLogs);
        Assert.DoesNotContain("tok-", allLogs);
    }

    [Fact]
    public async Task Blob_GetSetsExplicitContentLength_ForHeadConsistency()
    {
        using var harness = DockerTestHarness.Create("http://up1.local", BlobResponder());
        var path = $"library/nginx/blobs/{BlobDigest}";

        var get = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct));
        var head = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandleDockerRoute(path, ctx, ct), method: "HEAD");

        // 磁盘命中后 HEAD 与 GET 的 Content-Length 一致（均为文件长度）
        Assert.Equal(get.Headers.ContentLength, head.Headers.ContentLength);
        Assert.Equal(BlobContent.Length, get.Headers.ContentLength);
    }
}
