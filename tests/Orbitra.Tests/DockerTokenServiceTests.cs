using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orbitra.Services;
using Orbitra.Tests.Helpers;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="DockerTokenService"/> 单元测试：覆盖 Bearer 挑战解析、token 换取成功/失败、
/// 按 (realm,service,scope) 缓存、并发单飞、access_token 字段兼容。
/// </summary>
public sealed class DockerTokenServiceTests
{
    /// <summary>token 换取端点。</summary>
    private const string Realm = "https://auth.example.com/token";

    /// <summary>registry service 标识。</summary>
    private const string Service = "registry.example.com";

    /// <summary>仓库 pull scope。</summary>
    private const string Scope = "repository:library/nginx:pull";

    /// <summary>构造被测服务。</summary>
    private static (DockerTokenService Service, FakeHttpMessageHandler Upstream) Create(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var upstream = new FakeHttpMessageHandler(responder);
        var factory = new FakeHttpClientFactory(upstream);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DockerTokenService(factory, cache, NullLogger<DockerTokenService>.Instance);
        return (service, upstream);
    }

    [Fact]
    public async Task GetBearerTokenAsync_Success_ReturnsToken()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""")));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Equal("tok-abc", token);
        Assert.Equal(1, upstream.CountRequests(r => r.Url.Contains("service=") && r.Url.Contains("scope=")));
    }

    [Fact]
    public async Task GetBearerTokenAsync_UsesAccessTokenField()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"access_token":"tok-def","expires_in":300}""")));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Equal("tok-def", token);
    }

    [Fact]
    public async Task GetBearerTokenAsync_AppendsQueryParams_WithEscaping()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok","expires_in":300}""")));

        await service.GetBearerTokenAsync(Realm, Service, "repository:a/b:pull", null);

        var recorded = upstream.Requests.Single();
        Assert.Contains("service=", recorded.Url);
        Assert.Contains("scope=", recorded.Url);
        Assert.Contains("repository%3Aa%2Fb%3Apull", recorded.Url);
    }

    [Fact]
    public async Task GetBearerTokenAsync_ExpiresInMissing_UsesDefaultAndStillCaches()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc"}""")));

        var first = await service.GetBearerTokenAsync(Realm, Service, Scope, null);
        var second = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Equal("tok-abc", first);
        Assert.Equal("tok-abc", second);
        Assert.Equal(1, upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task GetBearerTokenAsync_SameScope_SecondCallHitsCache()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""")));

        var first = await service.GetBearerTokenAsync(Realm, Service, Scope, null);
        var second = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Equal(first, second);
        Assert.Equal(1, upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task GetBearerTokenAsync_DifferentScope_SeparateCacheEntry()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""")));

        await service.GetBearerTokenAsync(Realm, Service, Scope, null);
        await service.GetBearerTokenAsync(Realm, Service, "repository:library/other:pull", null);

        Assert.Equal(2, upstream.CountRequests(_ => true));
    }

    [Fact]
    public async Task GetBearerTokenAsync_ConcurrentSameScope_SingleFlight()
    {
        var inFlight = 0;
        var (service, upstream) = Create(_ =>
        {
            var current = Interlocked.Increment(ref inFlight);
            return Task.Delay(50).ContinueWith(_ =>
            {
                Interlocked.Decrement(ref inFlight);
                return FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""");
            });
        });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.GetBearerTokenAsync(Realm, Service, Scope, null))
            .ToArray();
        var tokens = await Task.WhenAll(tasks);

        Assert.All(tokens, t => Assert.Equal("tok-abc", t));
        // 并发同 scope 单飞：10 个并发调用只换取一次 token
        Assert.Equal(1, upstream.CountRequests(_ => true));
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task GetBearerTokenAsync_UpstreamNon2xx_ReturnsNull()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetBearerTokenAsync_ResponseWithoutToken_ReturnsNull()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"hello":"world"}""")));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetBearerTokenAsync_InvalidJson_ReturnsNull()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("not-json")));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetBearerTokenAsync_NetworkError_ReturnsNull()
    {
        var (service, upstream) = Create(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        var token = await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetBearerTokenAsync_ClientAuthorization_ForwardedToRealm()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""")));

        await service.GetBearerTokenAsync(Realm, Service, Scope, "Basic dXNlcjpwYXNz");

        var recorded = upstream.Requests.Single();
        Assert.Equal("Basic dXNlcjpwYXNz", recorded.GetHeader("Authorization"));
    }

    [Fact]
    public async Task GetBearerTokenAsync_Anonymous_NoAuthorizationHeaderSent()
    {
        var (service, upstream) = Create(_ => Task.FromResult(
            FakeResponses.Json("""{"token":"tok-abc","expires_in":300}""")));

        await service.GetBearerTokenAsync(Realm, Service, Scope, null);

        Assert.False(upstream.Requests.Single().HasHeader("Authorization"));
    }

    [Theory]
    [InlineData(
        "Bearer realm=\"https://auth.example.com/token\"," +
        "service=\"registry.example.com\",scope=\"repository:library/nginx:pull\"",
        "https://auth.example.com/token", "registry.example.com", "repository:library/nginx:pull")]
    [InlineData(
        "Bearer realm=https://auth.example.com/token,service=reg,scope=sc",
        "https://auth.example.com/token", "reg", "sc")]
    public void TryParseBearerChallenge_ValidChallenge_ParsesParts(
        string challenge, string expectedRealm, string expectedService, string expectedScope)
    {
        var parsed = DockerTokenService.TryParseBearerChallenge(
            challenge, out var realm, out var service, out var scope);

        Assert.True(parsed);
        Assert.Equal(expectedRealm, realm);
        Assert.Equal(expectedService, service);
        Assert.Equal(expectedScope, scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Basic realm=\"upstream\"")]
    [InlineData("Digest realm=\"upstream\"")]
    public void TryParseBearerChallenge_NonBearerOrEmpty_ReturnsFalse(string challenge)
    {
        var parsed = DockerTokenService.TryParseBearerChallenge(
            challenge, out var realm, out var service, out var scope);

        Assert.False(parsed);
        Assert.Equal(string.Empty, realm);
        Assert.Equal(string.Empty, service);
        Assert.Equal(string.Empty, scope);
    }

    [Fact]
    public void TryParseBearerChallenge_MissingRealm_ReturnsFalse()
    {
        var parsed = DockerTokenService.TryParseBearerChallenge(
            "Bearer service=\"reg\",scope=\"sc\"", out var realm, out var service, out var scope);

        Assert.False(parsed);
        Assert.Equal(string.Empty, realm);
    }
}
