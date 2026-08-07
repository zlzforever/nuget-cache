namespace Orbitra.Configuration;

/// <summary>
/// 代理服务配置：集中从环境变量读取并校验 NuGet/Maven/npm/docker/pip 代理所需的全部配置项。
/// 提供 <c>NUGET_PROXY_DOMAIN</c>（新名）与 <c>PROXY_DOMAIN</c>（旧名，已弃用）的双读兼容，
/// 优先读取新名，未设置时回退旧名并输出弃用警告日志；两者皆缺或非法时启动即抛异常。
/// <c>CACHE_PATH</c> 为 NuGet/Maven/npm/pip/docker 共用磁盘缓存根目录，保持原名不带前缀；
/// 各仓库在根目录下按 <c>nuget/</c>、<c>maven/</c>、<c>npm/</c>、<c>pip/</c>、<c>docker/</c> 子目录隔离。
/// <c>MAVEN_UPSTREAM_URL</c> 与 <c>DOCKER_UPSTREAM_URL</c> 均支持逗号分隔多值
/// （<c>https://a/,https://b/,https://c/</c>），顺序即失败回退顺序，拆分/校验/归一化逻辑一致；
/// npm 新增 <c>NPM_UPSTREAM_URL</c> 与 <c>NPM_METADATA_TTL</c>；
/// pip 新增 <c>PIP_UPSTREAM_URL</c>（单上游，含 userinfo 启动即抛异常，避免凭据进日志）与
/// <c>PIP_SIMPLE_TTL</c>，并派生「伴生文件主机」（上游为 pypi.org 时自动映射 files.pythonhosted.org）；
/// docker 新增 <c>DOCKER_TAG_TTL</c>、<c>DOCKER_MANIFEST_TTL</c>、<c>DOCKER_BLOB_VERIFY</c>、<c>DOCKER_ENABLE_PUSH</c>。
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>NuGet 代理服务外部访问域名环境变量名（新命名，必填）。</summary>
    public const string NuGetProxyDomainVariable = "NUGET_PROXY_DOMAIN";

    /// <summary>NuGet 代理服务外部访问域名环境变量名（旧命名，已弃用，用于向后兼容回退）。</summary>
    public const string LegacyProxyDomainVariable = "PROXY_DOMAIN";

    /// <summary>NuGet/Maven/npm/pip/docker 共用的磁盘缓存根目录环境变量名。</summary>
    public const string CachePathVariable = "CACHE_PATH";

    /// <summary>Maven 上游地址环境变量名。</summary>
    public const string MavenUpstreamUrlVariable = "MAVEN_UPSTREAM_URL";

    /// <summary>npm 上游地址环境变量名。</summary>
    public const string NpmUpstreamUrlVariable = "NPM_UPSTREAM_URL";

    /// <summary>npm 包元数据内存缓存 TTL（秒）环境变量名。</summary>
    public const string NpmMetadataTtlVariable = "NPM_METADATA_TTL";

    /// <summary>pip 上游索引基址环境变量名。</summary>
    public const string PipUpstreamUrlVariable = "PIP_UPSTREAM_URL";

    /// <summary>pip simple 项目页内存缓存 TTL（秒）环境变量名。</summary>
    public const string PipSimpleTtlVariable = "PIP_SIMPLE_TTL";

    /// <summary>docker 上游地址环境变量名。</summary>
    public const string DockerUpstreamUrlVariable = "DOCKER_UPSTREAM_URL";

    /// <summary>docker tag manifest 内存缓存 TTL（秒）环境变量名。</summary>
    public const string DockerTagTtlVariable = "DOCKER_TAG_TTL";

    /// <summary>docker digest manifest 内存缓存 TTL（秒）环境变量名。</summary>
    public const string DockerManifestTtlVariable = "DOCKER_MANIFEST_TTL";

    /// <summary>docker blob 下载时是否校验 sha256 digest 的环境变量名。</summary>
    public const string DockerBlobVerifyVariable = "DOCKER_BLOB_VERIFY";

    /// <summary>docker push 支持开关的环境变量名（本阶段仅配置项，尚未实现 push）。</summary>
    public const string DockerEnablePushVariable = "DOCKER_ENABLE_PUSH";

    /// <summary>Maven 上游默认地址（Maven Central）。</summary>
    public const string DefaultMavenUpstreamUrl = "https://repo.maven.apache.org/maven2";

    /// <summary>npm 上游默认地址（npm 官方 registry）。</summary>
    public const string DefaultNpmUpstreamUrl = "https://registry.npmjs.org";

    /// <summary>npm 包元数据内存缓存默认 TTL（秒）。</summary>
    public const int DefaultNpmMetadataTtlSeconds = 60;

    /// <summary>pip 上游默认索引基址（PyPI Simple API）。</summary>
    public const string DefaultPipUpstreamUrl = "https://pypi.org/simple";

    /// <summary>pip 上游为 pypi.org 时对应的伴生文件主机（wheel/sdist 等文件所在地）。</summary>
    public const string DefaultPipCompanionHost = "files.pythonhosted.org";

    /// <summary>pip simple 项目页内存缓存默认 TTL（秒），与 PyPI 自身缓存语义对齐。</summary>
    public const int DefaultPipSimpleTtlSeconds = 600;

    /// <summary>docker 上游默认地址（Docker Hub registry-1）。</summary>
    public const string DefaultDockerUpstreamUrl = "https://registry-1.docker.io";

    /// <summary>docker tag manifest 内存缓存默认 TTL（秒）。</summary>
    public const int DefaultDockerTagTtlSeconds = 60;

    /// <summary>docker digest manifest 内存缓存默认 TTL（秒）。</summary>
    public const int DefaultDockerManifestTtlSeconds = 3600;

    /// <summary>docker blob 下载时默认开启 sha256 digest 校验。</summary>
    public const bool DefaultDockerBlobVerify = true;

    /// <summary>docker push 支持默认关闭（本阶段仅 pull-only 核心链路）。</summary>
    public const bool DefaultDockerEnablePush = false;

    /// <summary>
    /// NuGet 代理服务外部访问域名（绝对 URI）。用于 <c>/nuget/v3/index.json</c> 的
    /// <c>v3-flatcontainer</c> URL 重写。
    /// </summary>
    public Uri NuGetProxyDomain { get; }

    /// <summary>
    /// NuGet/Maven/npm/pip/docker 共用的磁盘缓存根目录。为空时默认取应用基目录下的 <c>cache</c>。
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
    /// pip 上游索引基址（绝对 URI，去除末尾斜杠，如 <c>https://pypi.org/simple</c>）。
    /// 用于拼接 <c>{PIP_UPSTREAM_URL}/{规范化项目名}/</c> 与索引根透传；单上游 v1。
    /// </summary>
    public string PipUpstream { get; }

    /// <summary>
    /// pip 上游主机名（含端口，如 <c>pypi.org</c>）。参与 simple 项目页内嵌文件 URL 的
    /// 定向重写白名单匹配。
    /// </summary>
    public string PipUpstreamHost { get; }

    /// <summary>
    /// pip 伴生文件主机名（含端口）：上游为 pypi.org 时为 <c>files.pythonhosted.org</c>，
    /// 其余上游为 null（镜像通常与上游同主机）。同样参与文件 URL 重写白名单匹配。
    /// </summary>
    public string? PipCompanionHost { get; }

    /// <summary>
    /// pip 文件下载基址（绝对 URI，去除末尾斜杠）：上游为 pypi.org 时为
    /// <c>https://files.pythonhosted.org</c>，否则为 <c>{上游 scheme}://{上游 authority}</c>。
    /// 用于拼接 files 路由的上游 URL <c>{PipFileBaseUrl}/{路径}</c>。
    /// </summary>
    public string PipFileBaseUrl { get; }

    /// <summary>
    /// pip simple 项目页内存缓存 TTL（秒），默认 600；按 Accept 变体（HTML / PEP 691 JSON）分 key。
    /// </summary>
    public int PipSimpleTtlSeconds { get; }

    /// <summary>
    /// docker 上游有序列表（每项均为绝对 URI，去除末尾斜杠），顺序即失败回退顺序。
    /// 用于拼接 <c>{upstream}/v2/{path}</c>；源自 <c>DOCKER_UPSTREAM_URL</c> 逗号分隔配置，
    /// 未设置时默认单元素列表（Docker Hub）。
    /// </summary>
    public IReadOnlyList<string> DockerUpstreams { get; }

    /// <summary>
    /// docker tag manifest（含 tags/list）内存缓存 TTL（秒），默认 60。
    /// tag 可变，故使用短 TTL 控制跨上游 tag 漂移的一致窗口。
    /// </summary>
    public int DockerTagTtlSeconds { get; }

    /// <summary>
    /// docker digest manifest 内存缓存 TTL（秒），默认 3600。
    /// digest 不可变，磁盘永久缓存之上叠加内存 TTL 加速命中。
    /// </summary>
    public int DockerManifestTtlSeconds { get; }

    /// <summary>
    /// docker blob 下载时是否校验 sha256 digest（默认 true）：边写边算 IncrementalHash，
    /// 与请求 digest 不符则删除临时文件并回退下一上游，防上游毒化/损坏。
    /// </summary>
    public bool DockerBlobVerify { get; }

    /// <summary>
    /// docker push 支持开关（默认 false）。本阶段仅保留配置项，PUT/POST/PATCH 上传链路尚未实现。
    /// </summary>
    public bool DockerEnablePush { get; }

    /// <summary>
    /// 初始化代理配置对象。私有构造，仅允许通过 <see cref="Load"/> 工厂创建。
    /// </summary>
    private ProxyOptions(
        Uri nuGetProxyDomain,
        string cachePath,
        IReadOnlyList<string> mavenUpstreams,
        string npmUpstream,
        string npmUpstreamHost,
        int npmMetadataTtlSeconds,
        string pipUpstream,
        string pipUpstreamHost,
        string? pipCompanionHost,
        string pipFileBaseUrl,
        int pipSimpleTtlSeconds,
        IReadOnlyList<string> dockerUpstreams,
        int dockerTagTtlSeconds,
        int dockerManifestTtlSeconds,
        bool dockerBlobVerify,
        bool dockerEnablePush)
    {
        NuGetProxyDomain = nuGetProxyDomain;
        CachePath = cachePath;
        MavenUpstreams = mavenUpstreams;
        NpmUpstream = npmUpstream;
        NpmUpstreamHost = npmUpstreamHost;
        NpmMetadataTtlSeconds = npmMetadataTtlSeconds;
        PipUpstream = pipUpstream;
        PipUpstreamHost = pipUpstreamHost;
        PipCompanionHost = pipCompanionHost;
        PipFileBaseUrl = pipFileBaseUrl;
        PipSimpleTtlSeconds = pipSimpleTtlSeconds;
        DockerUpstreams = dockerUpstreams;
        DockerTagTtlSeconds = dockerTagTtlSeconds;
        DockerManifestTtlSeconds = dockerManifestTtlSeconds;
        DockerBlobVerify = dockerBlobVerify;
        DockerEnablePush = dockerEnablePush;
    }

    /// <summary>
    /// 从环境变量集中读取并校验代理配置：优先 <c>NUGET_PROXY_DOMAIN</c>，
    /// 未设置时回退 <c>PROXY_DOMAIN</c>（打弃用警告）；两者皆缺或非法即抛异常（fail-fast）。
    /// 同时校验 Maven/npm/docker/pip 上游地址并归一化（去除末尾斜杠），读取各仓库内存缓存 TTL 与开关项；
    /// pip 上游含 userinfo（<c>user:pass@</c>）时启动即抛异常（避免凭据进日志）。
    /// </summary>
    /// <param name="logger">用于输出弃用警告与缓存根目录信息的日志器。</param>
    /// <returns>校验通过后的代理配置对象。</returns>
    /// <exception cref="ArgumentException">代理域名或各仓库上游地址缺失/非法、pip 上游含 userinfo 时抛出。</exception>
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

        // Maven 多上游：未设置 → 默认单元素；设置后拆分空或非法 → 启动即抛异常（fail-fast）
        var mavenUpstreamEnv = Environment.GetEnvironmentVariable(MavenUpstreamUrlVariable);
        var mavenUpstreams = ParseUpstreamList(
            MavenUpstreamUrlVariable, mavenUpstreamEnv, DefaultMavenUpstreamUrl, "Maven");

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

        // pip 单上游：未设置 → 默认 PyPI Simple；设置后校验绝对 URI 与 userinfo（fail-fast）
        var pipUpstreamEnv = Environment.GetEnvironmentVariable(PipUpstreamUrlVariable);
        if (string.IsNullOrWhiteSpace(pipUpstreamEnv))
        {
            pipUpstreamEnv = DefaultPipUpstreamUrl;
        }

        if (!Uri.TryCreate(pipUpstreamEnv, UriKind.Absolute, out var pipUpstreamUri))
        {
            throw new ArgumentException("Invalid pip upstream URI.");
        }

        // 拒绝含 userinfo 的上游地址（旧式 Basic 鉴权形态），避免凭据进入日志；私有源鉴权本期不做
        if (!string.IsNullOrEmpty(pipUpstreamUri.UserInfo))
        {
            throw new ArgumentException(
                $"{PipUpstreamUrlVariable} must not contain userinfo (user:pass@); " +
                "credentials in the upstream URL are not supported.");
        }

        // 归一化 pip 上游地址：去除末尾 '/'，保证与 {规范化项目名}/ 拼接时路径正确
        var pipUpstream = pipUpstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        // 提取主机名（含端口），用于 simple 项目页内嵌文件绝对 URL 的定向重写白名单
        var pipUpstreamHost = pipUpstreamUri.Authority;

        // 伴生文件主机：上游为 pypi.org 时映射 files.pythonhosted.org（wheel/sdist 实际所在地），
        // 其余上游（国内镜像等）文件与页面同主机，无需映射
        string? pipCompanionHost = null;
        string pipFileBaseUrl;
        if (string.Equals(pipUpstreamUri.Host, "pypi.org", StringComparison.OrdinalIgnoreCase))
        {
            pipCompanionHost = DefaultPipCompanionHost;
            pipFileBaseUrl = $"https://{DefaultPipCompanionHost}";
        }
        else
        {
            pipFileBaseUrl = $"{pipUpstreamUri.Scheme}://{pipUpstreamUri.Authority}";
        }

        var pipSimpleTtlSeconds = ReadPositiveInt(PipSimpleTtlVariable, DefaultPipSimpleTtlSeconds);

        // docker 多上游：拆分/校验/归一化与 Maven 完全一致（同一辅助方法）
        var dockerUpstreamEnv = Environment.GetEnvironmentVariable(DockerUpstreamUrlVariable);
        var dockerUpstreams = ParseUpstreamList(
            DockerUpstreamUrlVariable, dockerUpstreamEnv, DefaultDockerUpstreamUrl, "docker");

        var dockerTagTtl = ReadPositiveInt(DockerTagTtlVariable, DefaultDockerTagTtlSeconds);
        var dockerManifestTtl = ReadPositiveInt(DockerManifestTtlVariable, DefaultDockerManifestTtlSeconds);
        var dockerBlobVerify = ReadBoolean(DockerBlobVerifyVariable, DefaultDockerBlobVerify);
        var dockerEnablePush = ReadBoolean(DockerEnablePushVariable, DefaultDockerEnablePush);

        return new ProxyOptions(
            proxyDomain, cachePath, mavenUpstreams, npmUpstream, npmUpstreamHost, npmMetadataTtlSeconds,
            pipUpstream, pipUpstreamHost, pipCompanionHost, pipFileBaseUrl, pipSimpleTtlSeconds,
            dockerUpstreams, dockerTagTtl, dockerManifestTtl, dockerBlobVerify, dockerEnablePush);
    }

    /// <summary>
    /// 解析逗号分隔的多上游配置：未设置或空白时返回默认单元素列表；设置了则按逗号拆分、
    /// 去空白、过滤空串，拆分后为空 → 抛异常（fail-fast），逐项校验绝对 URI 并归一化
    /// （去除末尾斜杠）。Maven 与 docker 共用，保证多上游语义完全一致。
    /// </summary>
    /// <param name="variableName">环境变量名（用于错误信息）。</param>
    /// <param name="envValue">环境变量原始值（可为空）。</param>
    /// <param name="defaultUrl">未设置时的默认上游地址。</param>
    /// <param name="repoName">仓库类型名（Maven / docker，用于错误信息）。</param>
    /// <returns>归一化后的上游有序列表（去除末尾斜杠的绝对 URI）。</returns>
    /// <exception cref="ArgumentException">配置值拆分后为空或某项非绝对 URI 时抛出。</exception>
    private static IReadOnlyList<string> ParseUpstreamList(
        string variableName, string? envValue, string defaultUrl, string repoName)
    {
        if (string.IsNullOrWhiteSpace(envValue))
        {
            // 未设置 → 默认上游（单元素列表）
            return new[] { defaultUrl };
        }

        // 逗号分隔多上游：Split(',') + Trim + 过滤空串，容忍 "a/,"、"a/,,b/" 等写法
        var segments = envValue
            .Split(',')
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();

        // 设置了但拆分后为空（全空白/全逗号）→ 配置错误，启动即抛异常（fail-fast）
        if (segments.Length == 0)
        {
            throw new ArgumentException(
                $"{variableName} is set but contains no valid upstream URL after splitting by ','.");
        }

        // 逐个校验合法性并归一化（去除末尾 '/'）；任一非法即抛异常，避免运行期静默跳过某上游
        return segments.Select(segment =>
        {
            if (!Uri.TryCreate(segment, UriKind.Absolute, out var upstreamUri))
            {
                throw new ArgumentException($"Invalid {repoName} upstream URI: {segment}");
            }

            return upstreamUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }).ToArray();
    }

    /// <summary>
    /// 读取正整数型环境变量：未设置或非法（非整数/非正数）时回退默认值。
    /// </summary>
    /// <param name="variableName">环境变量名。</param>
    /// <param name="defaultValue">默认值。</param>
    /// <returns>解析出的 TTL 秒数（非法时默认值）。</returns>
    private static int ReadPositiveInt(string variableName, int defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(variableName);
        if (int.TryParse(rawValue, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// 读取布尔型环境变量：未设置或非法时回退默认值。
    /// </summary>
    /// <param name="variableName">环境变量名。</param>
    /// <param name="defaultValue">默认值。</param>
    /// <returns>解析出的布尔值（非法时默认值）。</returns>
    private static bool ReadBoolean(string variableName, bool defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(variableName);
        if (bool.TryParse(rawValue, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }
}
