namespace Orbitra.Configuration;

/// <summary>
/// 代理服务配置：集中从环境变量读取并校验 NuGet/Maven/npm 代理所需的全部配置项。
/// 提供 <c>NUGET_PROXY_DOMAIN</c>（新名）与 <c>PROXY_DOMAIN</c>（旧名，已弃用）的双读兼容，
/// 优先读取新名，未设置时回退旧名并输出弃用警告日志；两者皆缺或非法时启动即抛异常。
/// <c>CACHE_PATH</c> 为 NuGet/Maven/npm 共用磁盘缓存根目录，保持原名不带前缀；
/// 各仓库在根目录下按 <c>nuget/</c>、<c>maven/</c>、<c>npm/</c> 子目录隔离。
/// <c>MAVEN_UPSTREAM_URL</c> 已有 <c>MAVEN_</c> 前缀，保持原名，且支持逗号分隔多值
/// （<c>https://a/,https://b/,https://c/</c>），顺序即失败回退顺序；
/// npm 新增 <c>NPM_UPSTREAM_URL</c> 与 <c>NPM_METADATA_TTL</c>。
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>NuGet 代理服务外部访问域名环境变量名（新命名，必填）。</summary>
    public const string NuGetProxyDomainVariable = "NUGET_PROXY_DOMAIN";

    /// <summary>NuGet 代理服务外部访问域名环境变量名（旧命名，已弃用，用于向后兼容回退）。</summary>
    public const string LegacyProxyDomainVariable = "PROXY_DOMAIN";

    /// <summary>NuGet/Maven/npm 共用的磁盘缓存根目录环境变量名。</summary>
    public const string CachePathVariable = "CACHE_PATH";

    /// <summary>Maven 上游地址环境变量名。</summary>
    public const string MavenUpstreamUrlVariable = "MAVEN_UPSTREAM_URL";

    /// <summary>npm 上游地址环境变量名。</summary>
    public const string NpmUpstreamUrlVariable = "NPM_UPSTREAM_URL";

    /// <summary>npm 包元数据内存缓存 TTL（秒）环境变量名。</summary>
    public const string NpmMetadataTtlVariable = "NPM_METADATA_TTL";

    /// <summary>Maven 上游默认地址（Maven Central）。</summary>
    public const string DefaultMavenUpstreamUrl = "https://repo.maven.apache.org/maven2";

    /// <summary>npm 上游默认地址（npm 官方 registry）。</summary>
    public const string DefaultNpmUpstreamUrl = "https://registry.npmjs.org";

    /// <summary>npm 包元数据内存缓存默认 TTL（秒）。</summary>
    public const int DefaultNpmMetadataTtlSeconds = 60;

    /// <summary>
    /// NuGet 代理服务外部访问域名（绝对 URI）。用于 <c>/nuget/v3/index.json</c> 的
    /// <c>v3-flatcontainer</c> URL 重写。
    /// </summary>
    public Uri NuGetProxyDomain { get; }

    /// <summary>
    /// NuGet/Maven/npm 共用的磁盘缓存根目录。为空时默认取应用基目录下的 <c>cache</c>。
    /// </summary>
    public string CachePath { get; }

    /// <summary>
    /// Maven 上游有序列表（每项均为绝对 URI，去除末尾斜杠），顺序即失败回退顺序。
    /// 用于拼接 <c>{upstream}/{path}</c>；源自 <c>MAVEN_UPSTREAM_URL</c> 逗号分隔配置，
    /// 未设置时默认单元素列表（Maven Central）。
    /// </summary>
    public IReadOnlyList<string> MavenUpstreams { get; }

    /// <summary>
    /// npm 上游根地址（绝对 URI，去除末尾斜杠）。用于拼接 <c>{NPM_UPSTREAM_URL}/{path}</c>。
    /// </summary>
    public string NpmUpstream { get; }

    /// <summary>
    /// npm 上游主机名（含端口，如 <c>registry.npmjs.org</c>）。用于解析 tarball URL 重写目标。
    /// </summary>
    public string NpmUpstreamHost { get; }

    /// <summary>
    /// npm 包元数据内存缓存 TTL（秒），默认 60。
    /// </summary>
    public int NpmMetadataTtlSeconds { get; }

    /// <summary>
    /// 初始化代理配置对象。私有构造，仅允许通过 <see cref="Load"/> 工厂创建。
    /// </summary>
    private ProxyOptions(
        Uri nuGetProxyDomain,
        string cachePath,
        IReadOnlyList<string> mavenUpstreams,
        string npmUpstream,
        string npmUpstreamHost,
        int npmMetadataTtlSeconds)
    {
        NuGetProxyDomain = nuGetProxyDomain;
        CachePath = cachePath;
        MavenUpstreams = mavenUpstreams;
        NpmUpstream = npmUpstream;
        NpmUpstreamHost = npmUpstreamHost;
        NpmMetadataTtlSeconds = npmMetadataTtlSeconds;
    }

    /// <summary>
    /// 从环境变量集中读取并校验代理配置：优先 <c>NUGET_PROXY_DOMAIN</c>，
    /// 未设置时回退 <c>PROXY_DOMAIN</c>（打弃用警告）；两者皆缺或非法即抛异常（fail-fast）。
    /// 同时校验 Maven/npm 上游地址并归一化（去除末尾斜杠），读取 npm 元数据缓存 TTL。
    /// </summary>
    /// <param name="logger">用于输出弃用警告与缓存根目录信息的日志器。</param>
    /// <returns>校验通过后的代理配置对象。</returns>
    /// <exception cref="ArgumentException">代理域名或 Maven/npm 上游地址缺失/非法时抛出。</exception>
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
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache")
            : cachePath;

        var mavenUpstreamEnv = Environment.GetEnvironmentVariable(MavenUpstreamUrlVariable);
        IReadOnlyList<string> mavenUpstreams;
        if (string.IsNullOrWhiteSpace(mavenUpstreamEnv))
        {
            // 未设置 → 默认 Maven Central（单元素列表）
            mavenUpstreams = new[] { DefaultMavenUpstreamUrl };
        }
        else
        {
            // 逗号分隔多上游：Split(',') + Trim + 过滤空串，容忍 "a/,"、"a/,,b/" 等写法
            var segments = mavenUpstreamEnv
                .Split(',')
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToArray();

            // 设置了但拆分后为空（全空白/全逗号）→ 配置错误，启动即抛异常（fail-fast）
            if (segments.Length == 0)
            {
                throw new ArgumentException(
                    $"{MavenUpstreamUrlVariable} is set but contains no valid upstream URL after splitting by ','.");
            }

            // 逐个校验合法性并归一化（去除末尾 '/'）；任一非法即抛异常，避免运行期静默跳过某上游
            mavenUpstreams = segments.Select(segment =>
            {
                if (!Uri.TryCreate(segment, UriKind.Absolute, out var upstreamUri))
                {
                    throw new ArgumentException($"Invalid Maven upstream URI: {segment}");
                }

                return upstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            }).ToArray();
        }

        var npmUpstreamEnv = Environment.GetEnvironmentVariable(NpmUpstreamUrlVariable);
        if (string.IsNullOrWhiteSpace(npmUpstreamEnv))
        {
            npmUpstreamEnv = DefaultNpmUpstreamUrl;
        }

        // 启动时校验 npm 上游地址合法性，失败抛异常
        if (!Uri.TryCreate(npmUpstreamEnv, UriKind.Absolute, out var npmUpstreamUri))
        {
            throw new ArgumentException("Invalid npm upstream URI.");
        }

        // 归一化 npm 上游地址：去除末尾 '/'，保证与 {**path} 拼接时路径正确
        var npmUpstream = npmUpstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        // 提取主机名（含端口），用于 npm 元数据内嵌 tarball 绝对 URL 的定向重写
        var npmUpstreamHost = npmUpstreamUri.Authority;

        var npmTtlEnv = Environment.GetEnvironmentVariable(NpmMetadataTtlVariable);
        if (!int.TryParse(npmTtlEnv, out var npmMetadataTtlSeconds) || npmMetadataTtlSeconds <= 0)
        {
            npmMetadataTtlSeconds = DefaultNpmMetadataTtlSeconds;
        }

        return new ProxyOptions(proxyDomain, cachePath, mavenUpstreams, npmUpstream, npmUpstreamHost, npmMetadataTtlSeconds);
    }
}
