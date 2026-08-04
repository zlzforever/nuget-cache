using Microsoft.Extensions.Logging.Abstractions;
using Orbitra.Configuration;
using Orbitra.Tests.Helpers;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="ProxyOptions.Load"/> 单元测试：覆盖 docker 上游拆分/校验/归一化 fail-fast、
/// 各 TTL 与开关的非法值回退、NuGet 域名双读兼容。
/// </summary>
public sealed class ProxyOptionsTests
{
    /// <summary>测试用缓存根目录（不落盘）。</summary>
    private const string TempCachePath = "/tmp/orbitra-tests-cache";

    /// <summary>合法的 NuGet 代理域名。</summary>
    private const string ValidProxyDomain = "https://proxy.example.com";

    /// <summary>构造最小合法环境（仅 NuGet 域名），其余 docker 配置不设置。</summary>
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

    [Fact]
    public void Load_DockerUpstreamUnset_UsesDefaultSingleUpstream()
    {
        var options = LoadWith((ProxyOptions.DockerUpstreamUrlVariable, null));

        Assert.Single(options.DockerUpstreams);
        Assert.Equal(ProxyOptions.DefaultDockerUpstreamUrl, options.DockerUpstreams[0]);
    }

    [Fact]
    public void Load_DockerUpstreamMultiple_SplitsAndNormalizes()
    {
        var options = LoadWith((
            ProxyOptions.DockerUpstreamUrlVariable, "https://a.example.com/,https://b.example.com/"));

        Assert.Equal(2, options.DockerUpstreams.Count);
        Assert.Equal("https://a.example.com", options.DockerUpstreams[0]);
        Assert.Equal("https://b.example.com", options.DockerUpstreams[1]);
    }

    [Fact]
    public void Load_DockerUpstreamToleratesEmptySegments_FiltersThem()
    {
        var options = LoadWith((
            ProxyOptions.DockerUpstreamUrlVariable, "https://a.example.com/,,https://b.example.com/"));

        Assert.Equal(2, options.DockerUpstreams.Count);
        Assert.Equal("https://a.example.com", options.DockerUpstreams[0]);
        Assert.Equal("https://b.example.com", options.DockerUpstreams[1]);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(" , ")]
    [InlineData(",,")]
    public void Load_DockerUpstreamSplitEmpty_ThrowsArgumentException(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LoadWith((ProxyOptions.DockerUpstreamUrlVariable, value)));

        Assert.Contains(ProxyOptions.DockerUpstreamUrlVariable, exception.Message);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://a.example.com,not-a-url")]
    public void Load_DockerUpstreamInvalidUri_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            LoadWith((ProxyOptions.DockerUpstreamUrlVariable, value)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void Load_DockerTagTtlInvalid_FallsBackToDefault(string value)
    {
        var options = LoadWith((ProxyOptions.DockerTagTtlVariable, value));

        Assert.Equal(ProxyOptions.DefaultDockerTagTtlSeconds, options.DockerTagTtlSeconds);
    }

    [Fact]
    public void Load_DockerTagTtlValid_UsesValue()
    {
        var options = LoadWith((ProxyOptions.DockerTagTtlVariable, "120"));

        Assert.Equal(120, options.DockerTagTtlSeconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void Load_DockerManifestTtlInvalid_FallsBackToDefault(string value)
    {
        var options = LoadWith((ProxyOptions.DockerManifestTtlVariable, value));

        Assert.Equal(ProxyOptions.DefaultDockerManifestTtlSeconds, options.DockerManifestTtlSeconds);
    }

    [Fact]
    public void Load_DockerBlobVerifyInvalid_FallsBackToDefaultTrue()
    {
        var options = LoadWith((ProxyOptions.DockerBlobVerifyVariable, "not-a-bool"));

        Assert.True(options.DockerBlobVerify);
    }

    [Fact]
    public void Load_DockerBlobVerifyFalse_ParsesBoolean()
    {
        var options = LoadWith((ProxyOptions.DockerBlobVerifyVariable, "false"));

        Assert.False(options.DockerBlobVerify);
    }

    [Fact]
    public void Load_NuGetDomainBothMissing_ThrowsArgumentException()
    {
        using var scope = new EnvVarScope(
            (ProxyOptions.NuGetProxyDomainVariable, null),
            (ProxyOptions.LegacyProxyDomainVariable, null));

        Assert.Throws<ArgumentException>(() => ProxyOptions.Load(NullLogger<ProxyOptions>.Instance));
    }

    [Fact]
    public void Load_LegacyProxyDomainOnly_FallsBackAndWarns()
    {
        using var scope = new EnvVarScope(
            (ProxyOptions.NuGetProxyDomainVariable, null),
            (ProxyOptions.LegacyProxyDomainVariable, "https://legacy.example.com"),
            (ProxyOptions.CachePathVariable, TempCachePath));
        var logger = new CapturingLogger<ProxyOptions>();

        var options = ProxyOptions.Load(logger);

        Assert.Equal("https://legacy.example.com", options.NuGetProxyDomain.AbsoluteUri.TrimEnd('/'));
        Assert.Contains(logger.Messages, m => m.Contains(ProxyOptions.LegacyProxyDomainVariable));
    }

    [Fact]
    public void Load_CachePathUnset_UsesBaseDirectoryFallback()
    {
        using var scope = new EnvVarScope(
            (ProxyOptions.NuGetProxyDomainVariable, ValidProxyDomain),
            (ProxyOptions.CachePathVariable, null));

        var options = ProxyOptions.Load(NullLogger<ProxyOptions>.Instance);

        Assert.Equal(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache"), options.CachePath);
    }
}
