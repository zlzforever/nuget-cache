namespace NuGetCache.Configuration;

/// <summary>
/// 代理服务配置：集中从环境变量读取并校验 NuGet/Maven 代理所需的全部配置项。
/// 提供 <c>NUGET_PROXY_DOMAIN</c>（新名）与 <c>PROXY_DOMAIN</c>（旧名，已弃用）的双读兼容，
/// 优先读取新名，未设置时回退旧名并输出弃用警告日志；两者皆缺或非法时启动即抛异常。
/// <c>CACHE_PATH</c> 为 NuGet/Maven 共用磁盘缓存根目录，保持原名不带前缀。
/// <c>MAVEN_UPSTREAM_URL</c> 已有 <c>MAVEN_</c> 前缀，保持原名。
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>NuGet 代理服务外部访问域名环境变量名（新命名，必填）。</summary>
    public const string NuGetProxyDomainVariable = "NUGET_PROXY_DOMAIN";

    /// <summary>NuGet 代理服务外部访问域名环境变量名（旧命名，已弃用，用于向后兼容回退）。</summary>
    public const string LegacyProxyDomainVariable = "PROXY_DOMAIN";

    /// <summary>NuGet/Maven 共用的磁盘缓存根目录环境变量名。</summary>
    public const string CachePathVariable = "CACHE_PATH";

    /// <summary>Maven 上游地址环境变量名。</summary>
    public const string MavenUpstreamUrlVariable = "MAVEN_UPSTREAM_URL";

    /// <summary>Maven 上游默认地址（Maven Central）。</summary>
    public const string DefaultMavenUpstreamUrl = "https://repo.maven.apache.org/maven2";

    /// <summary>
    /// NuGet 代理服务外部访问域名（绝对 URI）。用于 <c>/v3/index.json</c> 的 <c>v3-flatcontainer</c> URL 重写。
    /// </summary>
    public Uri NuGetProxyDomain { get; }

    /// <summary>
    /// NuGet/Maven 共用的磁盘缓存根目录。为空时默认取应用基目录下的 <c>nuget-cache</c>。
    /// </summary>
    public string CachePath { get; }

    /// <summary>
    /// Maven 上游根地址（绝对 URI，去除末尾斜杠）。用于拼接 <c>{MAVEN_UPSTREAM_URL}/{path}</c>。
    /// </summary>
    public string MavenUpstream { get; }

    /// <summary>
    /// 初始化代理配置对象。私有构造，仅允许通过 <see cref="Load"/> 工厂创建。
    /// </summary>
    private ProxyOptions(Uri nuGetProxyDomain, string cachePath, string mavenUpstream)
    {
        NuGetProxyDomain = nuGetProxyDomain;
        CachePath = cachePath;
        MavenUpstream = mavenUpstream;
    }

    /// <summary>
    /// 从环境变量集中读取并校验代理配置：优先 <c>NUGET_PROXY_DOMAIN</c>，
    /// 未设置时回退 <c>PROXY_DOMAIN</c>（打弃用警告）；两者皆缺或非法即抛异常（fail-fast）。
    /// 同时校验 Maven 上游地址并归一化（去除末尾斜杠）。
    /// </summary>
    /// <param name="logger">用于输出弃用警告与缓存根目录信息的日志器。</param>
    /// <returns>校验通过后的代理配置对象。</returns>
    /// <exception cref="ArgumentException">代理域名或 Maven 上游地址缺失/非法时抛出。</exception>
    public static ProxyOptions Load(ILogger logger)
    {
        var proxyDomainValue = Environment.GetEnvironmentVariable(NuGetProxyDomainVariable);

        // 双读兼容：新名未设置时回退旧名 PROXY_DOMAIN，并输出弃用警告日志
        if (string.IsNullOrWhiteSpace(proxyDomainValue))
        {
            var legacyValue = Environment.GetEnvironmentVariable(LegacyProxyDomainVariable);
            if (!string.IsNullOrWhiteSpace(legacyValue))
            {
                logger.LogWarning(
                    "Environment variable {LegacyVar} is deprecated, please use {NewVar} instead.",
                    LegacyProxyDomainVariable, NuGetProxyDomainVariable);
                proxyDomainValue = legacyValue;
            }
        }

        if (!Uri.TryCreate(proxyDomainValue, UriKind.Absolute, out var proxyDomain))
        {
            throw new ArgumentException(
                $"Invalid proxy URI: set either {NuGetProxyDomainVariable} or {LegacyProxyDomainVariable} to a valid absolute URL.");
        }

        var cachePath = Environment.GetEnvironmentVariable(CachePathVariable);
        cachePath = string.IsNullOrWhiteSpace(cachePath)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nuget-cache")
            : cachePath;

        var mavenUpstreamEnv = Environment.GetEnvironmentVariable(MavenUpstreamUrlVariable);
        if (string.IsNullOrWhiteSpace(mavenUpstreamEnv))
        {
            mavenUpstreamEnv = DefaultMavenUpstreamUrl;
        }

        // 启动时校验 Maven 上游地址合法性，失败抛异常（与代理域名校验方式一致）
        if (!Uri.TryCreate(mavenUpstreamEnv, UriKind.Absolute, out var mavenUpstreamUri))
        {
            throw new ArgumentException("Invalid Maven upstream URI.");
        }

        // 归一化上游地址：去除末尾 '/'，保证与 {**path} 拼接时路径正确
        var mavenUpstream = mavenUpstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return new ProxyOptions(proxyDomain, cachePath, mavenUpstream);
    }
}
