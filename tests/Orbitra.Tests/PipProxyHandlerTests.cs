using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orbitra.Configuration;
using Orbitra.Handlers;
using Orbitra.Services;
using Orbitra.Tests.Helpers;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="PipProxyHandler"/> 单元测试：覆盖 PEP 503 项目名规范化、HTML/JSON/属性 URL 重写
/// （保留 #sha256= 片段、白名单外主机不重写）、Accept 变体缓存分离、simple 根透传不缓存、
/// files 磁盘缓存、路径安全校验与 HEAD Content-Length 一致性。
/// </summary>
public sealed class PipProxyHandlerTests
{
    /// <summary>PyPI 上游默认地址。</summary>
    private const string DefaultUpstream = "https://pypi.org/simple";

    /// <summary>测试用固定代理域名（与 TestProxyOptionsFactory 一致）。</summary>
    private const string ProxyDomain = "https://proxy.example.com";

    /// <summary>模拟的 PyPI simple 项目页 HTML（含 whl 链接、PEP 658/714 属性与白名单外主机链接）。</summary>
    private const string SampleHtml = """
        <!DOCTYPE html><html><head><meta name="pypi:repository-version" content="1.0"><title>Links for requests</title></head><body><h1>Links for requests</h1>
        <a href="https://files.pythonhosted.org/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl#sha256=942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8" data-requires-python="&gt;=3.7" data-dist-info-metadata="true" data-core-metadata="https://files.pythonhosted.org/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl.metadata">requests-2.31.0-py3-none-any.whl</a>
        <a href="https://example.com/foreign/other.whl#sha256=ffffffff">other</a>
        </body></html>
        """;

    /// <summary>模拟的 PEP 691 JSON 项目页（含 files[].url 与 core-metadata.url）。</summary>
    private const string SampleJson = """
        {"meta":{"api-version":"1.0"},"name":"requests","files":[{"filename":"requests-2.31.0-py3-none-any.whl","url":"https://files.pythonhosted.org/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl","hashes":{"sha256":"942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8"},"core-metadata":{"url":"https://files.pythonhosted.org/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl.metadata","dist-info-metadata":true}}]}
        """;

    /// <summary>模拟 pip 客户端的 PEP 691 JSON 协商 Accept 头。</summary>
    private const string JsonAccept =
        "application/vnd.pypi.simple.v1.2+json, application/vnd.pypi.simple.v1.1+json, application/vnd.pypi.simple.v1+json";

    /// <summary>重写后的 whl 链接期望值（代理域名前缀 + 原路径 + #sha256= 片段保留）。</summary>
    private const string RewrittenWhlUrl =
        "https://proxy.example.com/pip/files/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl#sha256=942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8";

    /// <summary>重写后的 metadata 链接期望值。</summary>
    private const string RewrittenMetadataUrl =
        "https://proxy.example.com/pip/files/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl.metadata";

    /// <summary>重写后的 PEP 691 JSON whl 链接期望值（JSON 变体无 #sha256= 片段）。</summary>
    private const string RewrittenJsonWhlUrl =
        "https://proxy.example.com/pip/files/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl";

    [Theory]
    [InlineData("Django", "django")]
    [InlineData("django", "django")]
    [InlineData("my.pkg", "my-pkg")]
    [InlineData("Foo__bar", "foo-bar")]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("a--b__c.d", "a-b-c-d")]
    [InlineData("Requests", "requests")]
    [InlineData("scikit-learn", "scikit-learn")]
    public void NormalizeProjectName_Pep503Rules_ReturnsCanonicalName(string input, string expected)
    {
        Assert.Equal(expected, PipProxyHandler.NormalizeProjectName(input));
    }

    [Fact]
    public void NormalizeProjectName_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PipProxyHandler.NormalizeProjectName(null!));
    }

    /// <summary>空路径（/pip、/pip/）返回 404。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(null)]
    public async Task HandlePipRoute_EmptyPath_Returns404(string? path)
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Json("{}")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute(path, ctx, ct));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>未知前缀（非 files/、非 simple/）返回 404。</summary>
    [Fact]
    public async Task HandlePipRoute_UnknownPrefix_Returns404()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Json("{}")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("docker/foo", ctx, ct));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>路径穿越（..）与空段等不安全路径返回 400，不触碰上游。</summary>
    [Theory]
    [InlineData("files/../secret")]
    [InlineData("simple/../django")]
    [InlineData("simple/a//b")]
    [InlineData("files/a/./b")]
    public async Task HandlePipRoute_UnsafePath_Returns400(string path)
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Json("{}")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute(path, ctx, ct));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>项目页：按 PEP 503 规范化名请求上游，HTML href/data-* 属性绝对 URL 重写、片段保留、白名单外原样。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageHtml_NormalizesNameAndRewritesUrls()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/Django/", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("text/html", headers.ContentType.ToString());
        Assert.Equal("https://pypi.org/simple/django/", harness.Upstream.Requests[0].Url);
        Assert.Null(harness.Upstream.Requests[0].GetHeader("Accept"));

        var responseBody = HttpTestHelper.DecodeBody(body);
        Assert.Contains(RewrittenWhlUrl, responseBody);
        Assert.Contains(RewrittenMetadataUrl, responseBody);
        Assert.Contains("https://example.com/foreign/other.whl#sha256=ffffffff", responseBody);
        Assert.DoesNotContain("https://files.pythonhosted.org/packages/a8/57/942c5a3aed2a5097c36ea4825339a6a3bd10f02e77f1ecf11bea72fd57de61a8/requests-2.31.0-py3-none-any.whl#sha256=", responseBody);
    }

    /// <summary>缓存 key 用规范化名：Django 与 django 两次请求命中同一缓存，仅 1 次上游请求。</summary>
    [Fact]
    public async Task HandlePipRoute_SameProjectDifferentCase_SharesMemoryCache()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/Django/", ctx, ct));
        var (secondStatus, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/django/", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, secondStatus);
        Assert.Single(harness.Upstream.Requests);
        Assert.Equal("https://pypi.org/simple/django/", harness.Upstream.Requests[0].Url);
    }

    /// <summary>同变体二次请求命中内存缓存，不再请求上游。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageSameVariant_CacheHitSkipsUpstream()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));
        var (secondStatus, _, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, secondStatus);
        Assert.Single(harness.Upstream.Requests);
        Assert.Contains(RewrittenWhlUrl, HttpTestHelper.DecodeBody(body));
    }

    /// <summary>PEP 691 JSON 变体：Accept 头原样透传，files[].url 与 core-metadata.url 均重写。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageJson_ForwardsAcceptAndRewritesUrls()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Json(SampleJson)));

        var (status, headers, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct),
            accept: JsonAccept);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("application/json", headers.ContentType.ToString());
        // 多值 Accept 头经请求解析后以逗号拼接（无空格），与原始单值文本仅在空白上有差异
        Assert.Equal(JsonAccept.Replace(", ", ","), harness.Upstream.Requests[0].GetHeader("Accept"));

        var responseBody = HttpTestHelper.DecodeBody(body);
        Assert.Contains(RewrittenJsonWhlUrl, responseBody);
        Assert.Contains(RewrittenMetadataUrl, responseBody);
        Assert.DoesNotContain("https://files.pythonhosted.org", responseBody);
    }

    /// <summary>HTML 与 JSON 变体按 Accept 分离缓存：两种变体各 1 次上游请求，重复同变体命中缓存。</summary>
    [Fact]
    public async Task HandlePipRoute_AcceptVariants_CachedSeparately()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            req => Task.FromResult(
                (req.Headers.Accept.ToString() ?? string.Empty).Contains("+json")
                    ? FakeResponses.Json(SampleJson)
                    : Html(SampleHtml)));

        await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));
        await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct), accept: JsonAccept);
        await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));

        Assert.Equal(2, harness.Upstream.Requests.Count);
    }

    /// <summary>索引根 /simple/ 兜底透传不缓存：连续两次请求各打上游一次。</summary>
    [Fact]
    public async Task HandlePipRoute_SimpleRoot_PassthroughNotCached()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html("<html><body><a href=\"../django/\">django</a></body></html>")));

        var first = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct));
        var second = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, first.Status);
        Assert.Equal(StatusCodes.Status200OK, second.Status);
        Assert.Equal(2, harness.Upstream.Requests.Count);
        Assert.All(harness.Upstream.Requests, r => Assert.Equal("https://pypi.org/simple", r.Url));
    }

    /// <summary>files 文件下载：上游 URL 使用伴生文件主机基址，首次落盘、二次磁盘命中不再请求上游。</summary>
    [Fact]
    public async Task HandlePipRoute_FileDownload_DiskCached()
    {
        var wheelBytes = Encoding.UTF8.GetBytes("wheel-content");
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Bytes(wheelBytes)));

        var first = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct));
        var second = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, first.Status);
        Assert.Equal(StatusCodes.Status200OK, second.Status);
        Assert.Single(harness.Upstream.Requests);
        Assert.Equal("https://files.pythonhosted.org/packages/a8/57/foo-1.0-py3-none-any.whl", harness.Upstream.Requests[0].Url);
        Assert.True(File.Exists(Path.Combine(harness.CachePath, "pip", "files", "packages", "a8", "57", "foo-1.0-py3-none-any.whl")));
        Assert.Equal(wheelBytes, second.Body);
    }

    /// <summary>files 路径也可承载 PEP 658/714 元数据文件（.whl.metadata），同样磁盘缓存。</summary>
    [Fact]
    public async Task HandlePipRoute_MetadataFile_DiskCached()
    {
        var metadataBytes = Encoding.UTF8.GetBytes("Metadata-Version: 2.1");
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Bytes(metadataBytes)));

        await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl.metadata", ctx, ct));
        await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl.metadata", ctx, ct));

        Assert.Single(harness.Upstream.Requests);
        Assert.Equal("https://files.pythonhosted.org/packages/a8/57/foo-1.0-py3-none-any.whl.metadata", harness.Upstream.Requests[0].Url);
    }

    /// <summary>镜像上游（与页面同主机）：重写白名单与文件基址均使用镜像主机，无伴生主机映射。</summary>
    [Fact]
    public async Task HandlePipRoute_MirrorUpstream_FilesUseSameHost()
    {
        const string mirrorUpstream = "https://mirror.example.com/simple";
        const string mirrorHtml = """
            <a href="https://mirror.example.com/packages/a8/57/foo-1.0-py3-none-any.whl#sha256=abc123">foo-1.0-py3-none-any.whl</a>
            """;
        using var harness = PipTestHarness.Create(mirrorUpstream, _ => Task.FromResult(Html(mirrorHtml)));

        var page = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/foo/", ctx, ct));
        var file = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, page.Status);
        Assert.Equal(StatusCodes.Status200OK, file.Status);
        Assert.Contains("https://proxy.example.com/pip/files/packages/a8/57/foo-1.0-py3-none-any.whl#sha256=abc123", HttpTestHelper.DecodeBody(page.Body));
        Assert.Equal("https://mirror.example.com/packages/a8/57/foo-1.0-py3-none-any.whl", harness.Upstream.Requests[1].Url);
    }

    /// <summary>HEAD 项目页：Content-Length 与 GET 完全一致，状态 200。</summary>
    [Fact]
    public async Task HandlePipRoute_Head_ContentLengthMatchesGet()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        var get = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));
        var head = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct), method: "HEAD");

        Assert.Equal(StatusCodes.Status200OK, head.Status);
        Assert.Equal(get.Body.Length.ToString(), head.Headers.ContentLength.ToString());
    }

    /// <summary>上游非 2xx：项目页与文件均透传上游状态码。</summary>
    [Fact]
    public async Task HandlePipRoute_UpstreamNonSuccess_PassthroughStatus()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            _ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var page = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/missing/", ctx, ct));
        var file = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/ab/missing.whl", ctx, ct));

        Assert.Equal(StatusCodes.Status404NotFound, page.Status);
        Assert.Equal(StatusCodes.Status404NotFound, file.Status);
    }

    /// <summary>files 文件下载：磁盘命中后 HEAD 与 GET 的 Content-Length 一致（均为文件长度）。</summary>
    [Fact]
    public async Task HandlePipRoute_FileHead_ContentLengthMatchesGet()
    {
        var wheelBytes = Encoding.UTF8.GetBytes("wheel-content");
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Bytes(wheelBytes)));

        var get = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct));
        var head = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct),
            method: "HEAD");

        Assert.Equal(StatusCodes.Status200OK, get.Status);
        Assert.Equal(StatusCodes.Status200OK, head.Status);
        Assert.Equal(get.Headers.ContentLength, head.Headers.ContentLength);
        Assert.Equal(wheelBytes.Length, get.Headers.ContentLength);
    }

    /// <summary>simple 索引根：HEAD 与 GET 的 Content-Length 一致（透传不缓存）。</summary>
    [Fact]
    public async Task HandlePipRoute_SimpleRootHead_ContentLengthMatchesGet()
    {
        const string rootHtml = "<html><body><a href=\"../django/\">django</a></body></html>";
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(rootHtml)));

        var get = await HttpTestHelper.ExecuteAsync((ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct));
        var head = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct), method: "HEAD");

        Assert.Equal(StatusCodes.Status200OK, get.Status);
        Assert.Equal(StatusCodes.Status200OK, head.Status);
        Assert.Equal(get.Headers.ContentLength, head.Headers.ContentLength);
    }

    /// <summary>项目页：query string 原样透传上游（拼接在规范化名之后）。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPage_QueryStringForwardedToUpstream()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct),
            queryString: "?foo=bar&baz=1");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("https://pypi.org/simple/requests/?foo=bar&baz=1", harness.Upstream.Requests[0].Url);
    }

    /// <summary>simple 索引根：query string 原样透传上游。</summary>
    [Fact]
    public async Task HandlePipRoute_SimpleRoot_QueryStringForwardedToUpstream()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html("<html></html>")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct),
            queryString: "?format=json");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("https://pypi.org/simple?format=json", harness.Upstream.Requests[0].Url);
    }

    /// <summary>files 下载：query string 原样透传上游（拼接在文件 URL 之后）。</summary>
    [Fact]
    public async Task HandlePipRoute_FileDownload_QueryStringForwardedToUpstream()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(FakeResponses.Bytes(new byte[] { 1, 2, 3 })));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct),
            queryString: "?expires=1700000000");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(
            "https://files.pythonhosted.org/packages/a8/57/foo-1.0-py3-none-any.whl?expires=1700000000",
            harness.Upstream.Requests[0].Url);
    }

    /// <summary>files 下载：上游网络异常（连接拒绝）→ 502 Bad Gateway（共享磁盘缓存服务语义）。</summary>
    [Fact]
    public async Task HandlePipRoute_FileUpstreamNetworkError_Returns502()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("files/packages/a8/57/foo-1.0-py3-none-any.whl", ctx, ct));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
    }

    /// <summary>项目页：上游网络异常（连接拒绝）→ 502 Bad Gateway（与 files 路由语义一致）。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageUpstreamNetworkError_Returns502()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
    }

    /// <summary>simple 索引根：上游网络异常（连接拒绝）→ 502 Bad Gateway（与 files 路由语义一致）。</summary>
    [Fact]
    public async Task HandlePipRoute_SimpleRootUpstreamNetworkError_Returns502()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct));

        Assert.Equal(StatusCodes.Status502BadGateway, status);
    }

    /// <summary>URL 重写：主机大小写不敏感（(?i) 标志），href 中 query 与 #sha256= 片段均保留。</summary>
    [Fact]
    public async Task HandlePipRoute_Rewrite_CaseInsensitiveHostKeepsQueryAndFragment()
    {
        const string htmlWithQuery = """
            <a href="HTTPS://Files.Pythonhosted.org/packages/a8/57/foo-1.0-py3-none-any.whl?download=1#sha256=abc123">foo-1.0-py3-none-any.whl</a>
            """;
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(htmlWithQuery)));

        var (status, _, body) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/foo/", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(
            "https://proxy.example.com/pip/files/packages/a8/57/foo-1.0-py3-none-any.whl?download=1#sha256=abc123",
            HttpTestHelper.DecodeBody(body));
    }

    /// <summary>项目页不带尾斜杠（simple/{name}）：上游请求仍拼接规范尾斜杠（客户端兜底形态）。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageWithoutTrailingSlash_UpstreamGetsTrailingSlash()
    {
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(Html(SampleHtml)));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests", ctx, ct));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("https://pypi.org/simple/requests/", harness.Upstream.Requests[0].Url);
    }

    /// <summary>PEP 691 JSON 变体：上游 vnd.pypi.simple Content-Type 原样回放（uv/pip 依赖该类型协商）。</summary>
    [Fact]
    public async Task HandlePipRoute_ProjectPageJson_VndContentTypeEchoed()
    {
        var jsonResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(SampleJson, Encoding.UTF8, "application/vnd.pypi.simple.v1+json"),
        };
        using var harness = PipTestHarness.Create(DefaultUpstream, _ => Task.FromResult(jsonResponse));

        var (status, headers, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/requests/", ctx, ct),
            accept: JsonAccept);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("application/vnd.pypi.simple.v1+json", headers.ContentType.ToString());
    }

    /// <summary>simple 索引根：Accept 头原样透传上游（JSON 协商对索引根同样生效）。</summary>
    [Fact]
    public async Task HandlePipRoute_SimpleRoot_ForwardsAcceptHeader()
    {
        using var harness = PipTestHarness.Create(
            DefaultUpstream,
            req => Task.FromResult(
                (req.Headers.Accept.ToString() ?? string.Empty).Contains("+json")
                    ? FakeResponses.Json("{\"projects\":[]}")
                    : Html("<html></html>")));

        var (status, _, _) = await HttpTestHelper.ExecuteAsync(
            (ctx, ct) => harness.Handler.HandlePipRoute("simple/", ctx, ct),
            accept: JsonAccept);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(JsonAccept.Replace(", ", ","), harness.Upstream.Requests[0].GetHeader("Accept"));
    }

    /// <summary>
    /// 构造 HTML 文本响应（Content-Type: text/html）。
    /// </summary>
    /// <param name="body">HTML 响应体。</param>
    /// <returns>构造完成的响应对象。</returns>
    private static HttpResponseMessage Html(string body)
    {
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }

    /// <summary>
    /// pip 处理器测试基座：装配 ProxyOptions / 内存缓存 / 假上游 / 磁盘缓存与处理器，
    /// 提供缓存目录生命周期管理。
    /// </summary>
    private sealed class PipTestHarness : IDisposable
    {
        /// <summary>测试用磁盘缓存根目录。</summary>
        public string CachePath { get; }

        /// <summary>可编排的假上游处理器。</summary>
        public FakeHttpMessageHandler Upstream { get; }

        /// <summary>当前装配的 pip 处理器。</summary>
        public PipProxyHandler Handler { get; }

        /// <summary>是否已释放。</summary>
        private bool _disposed;

        /// <summary>
        /// 初始化测试基座（私有构造，经 <see cref="Create"/> 创建）。
        /// </summary>
        /// <param name="cachePath">磁盘缓存根目录。</param>
        /// <param name="upstream">假上游处理器。</param>
        /// <param name="handler">pip 处理器。</param>
        private PipTestHarness(string cachePath, FakeHttpMessageHandler upstream, PipProxyHandler handler)
        {
            CachePath = cachePath;
            Upstream = upstream;
            Handler = handler;
        }

        /// <summary>
        /// 创建测试基座。
        /// </summary>
        /// <param name="upstreamUrl">PIP_UPSTREAM_URL 原值。</param>
        /// <param name="responder">假上游的响应编排函数。</param>
        /// <param name="simpleTtl">PIP_SIMPLE_TTL（null 表示不设置）。</param>
        /// <returns>装配完成的测试基座。</returns>
        public static PipTestHarness Create(
            string upstreamUrl,
            Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
            string? simpleTtl = null)
        {
            var cachePath = Path.Combine(Path.GetTempPath(), "orbitra-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cachePath);
            var options = TestProxyOptionsFactory.CreatePipOptions(cachePath, upstreamUrl, simpleTtl);
            var upstream = new FakeHttpMessageHandler(responder);
            var factory = new FakeHttpClientFactory(upstream);
            var memory = new MemoryCache(new MemoryCacheOptions());
            var diskCache = new DiskCacheService(factory, options, NullLogger<DiskCacheService>.Instance);
            var handler = new PipProxyHandler(options, memory, factory, diskCache, NullLogger<PipProxyHandler>.Instance);
            return new PipTestHarness(cachePath, upstream, handler);
        }

        /// <summary>
        /// 清理测试缓存目录。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (Directory.Exists(CachePath))
                {
                    Directory.Delete(CachePath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不阻断后续用例
            }
            catch (UnauthorizedAccessException)
            {
                // 测试清理失败不阻断后续用例
            }
        }
    }
}

/// <summary>
/// pip 配置项单元测试：覆盖 <c>PIP_UPSTREAM_URL</c> 默认值/归一化/userinfo 拒绝、
/// 伴生主机与文件基址派生、<c>PIP_SIMPLE_TTL</c> 非法值回退。
/// </summary>
public sealed class PipOptionsTests
{
    /// <summary>测试用缓存根目录（不落盘）。</summary>
    private const string TempCachePath = "/tmp/orbitra-tests-cache";

    /// <summary>合法的 NuGet 代理域名。</summary>
    private const string ValidProxyDomain = "https://proxy.example.com";

    /// <summary>
    /// 构造最小合法环境（仅 NuGet 域名 + 缓存根目录），叠加指定环境变量后加载配置。
    /// </summary>
    /// <param name="variables">待设置的额外环境变量。</param>
    /// <returns>校验通过后的配置对象。</returns>
    private static ProxyOptions LoadWith(params (string Key, string? Value)[] variables)
    {
        var all = new List<(string, string?)>
        {
            (ProxyOptions.NuGetProxyDomainVariable, ValidProxyDomain),
            (ProxyOptions.CachePathVariable, TempCachePath),
        };
        all.AddRange(variables);
        using var scope = new EnvVarScope(all.ToArray());
        return ProxyOptions.Load(NullLogger<ProxyOptions>.Instance);
    }

    /// <summary>未设置 PIP_UPSTREAM_URL：使用 PyPI Simple 默认值，派生伴生主机与文件基址，TTL 默认 600。</summary>
    [Fact]
    public void Load_PipUpstreamUnset_UsesPyPiDefaultAndDerivesCompanion()
    {
        var options = LoadWith((ProxyOptions.PipUpstreamUrlVariable, null));

        Assert.Equal("https://pypi.org/simple", options.PipUpstream);
        Assert.Equal("pypi.org", options.PipUpstreamHost);
        Assert.Equal("files.pythonhosted.org", options.PipCompanionHost);
        Assert.Equal("https://files.pythonhosted.org", options.PipFileBaseUrl);
        Assert.Equal(ProxyOptions.DefaultPipSimpleTtlSeconds, options.PipSimpleTtlSeconds);
    }

    /// <summary>PIP_UPSTREAM_URL 含 userinfo（user:pass@）：启动即抛异常，拒绝凭据进日志。</summary>
    [Theory]
    [InlineData("https://user:pass@pypi.org/simple")]
    [InlineData("https://token@mirror.example.com/simple")]
    public void Load_PipUpstreamWithUserInfo_ThrowsArgumentException(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LoadWith((ProxyOptions.PipUpstreamUrlVariable, value)));

        Assert.Contains(ProxyOptions.PipUpstreamUrlVariable, exception.Message);
    }

    /// <summary>PIP_UPSTREAM_URL 非法 URI：启动即抛异常。</summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://exa mple.com/simple")]
    public void Load_PipUpstreamInvalidUri_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            LoadWith((ProxyOptions.PipUpstreamUrlVariable, value)));
    }

    /// <summary>PIP_UPSTREAM_URL 末尾斜杠归一化去除。</summary>
    [Fact]
    public void Load_PipUpstreamTrailingSlash_Normalized()
    {
        var options = LoadWith((ProxyOptions.PipUpstreamUrlVariable, "https://pypi.org/simple/"));

        Assert.Equal("https://pypi.org/simple", options.PipUpstream);
        Assert.Equal("pypi.org", options.PipUpstreamHost);
    }

    /// <summary>镜像上游：无伴生主机映射，文件基址与上游同主机同 scheme。</summary>
    [Fact]
    public void Load_PipUpstreamMirror_NoCompanionAndSameFileBase()
    {
        var options = LoadWith((ProxyOptions.PipUpstreamUrlVariable, "https://mirror.example.com/simple"));

        Assert.Equal("mirror.example.com", options.PipUpstreamHost);
        Assert.Null(options.PipCompanionHost);
        Assert.Equal("https://mirror.example.com", options.PipFileBaseUrl);
    }

    /// <summary>PIP_SIMPLE_TTL 非法值回退默认 600。</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void Load_PipSimpleTtlInvalid_FallsBackToDefault(string value)
    {
        var options = LoadWith((ProxyOptions.PipSimpleTtlVariable, value));

        Assert.Equal(ProxyOptions.DefaultPipSimpleTtlSeconds, options.PipSimpleTtlSeconds);
    }

    /// <summary>PIP_SIMPLE_TTL 合法值生效。</summary>
    [Fact]
    public void Load_PipSimpleTtlValid_UsesValue()
    {
        var options = LoadWith((ProxyOptions.PipSimpleTtlVariable, "120"));

        Assert.Equal(120, options.PipSimpleTtlSeconds);
    }
}
