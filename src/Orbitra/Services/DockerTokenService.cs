using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Orbitra.Services;

/// <summary>
/// docker registry token 交换服务：解析上游 401 响应的 <c>WWW-Authenticate: Bearer realm/service/scope</c>，
/// 代理内部向 realm 发起 token 交换（GET <c>{realm}?service&amp;scope</c>，携带客户端 Authorization 头，无则匿名），
/// 成功后返回 Bearer token 供重试同一上游。token 按 <c>(realm,service,scope)</c> 内存缓存，
/// TTL 取响应 <c>expires_in - 60s</c>（最小 30s）；并发同 scope 请求单飞去重，避免打爆 token 服务。
/// 安全约束：Authorization 头与 token 绝不写入任何日志。
/// </summary>
public sealed class DockerTokenService
{
    /// <summary>token 换取失败时默认的最小缓存 TTL（秒），避免频繁重试打爆 realm。</summary>
    private const int MinimumCacheTtlSeconds = 30;

    /// <summary>换取 token 时从 expires_in 中提前扣减的安全余量（秒），防止到期临界处竞态失效。</summary>
    private const int TokenExpiryBufferSeconds = 60;

    /// <summary>Bearer 挑战前缀（不区分大小写匹配）。</summary>
    private const string BearerScheme = "Bearer ";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DockerTokenService> _logger;
    private readonly IMemoryCache _cache;

    /// <summary>单飞去重表：key 为缓存键，值为正在进行的换取任务，完成后立即移除。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    /// <summary>
    /// 初始化 docker token 交换服务。
    /// </summary>
    /// <param name="httpClientFactory">命名 HttpClient 工厂（"Docker" 客户端）。</param>
    /// <param name="cache">内存缓存（按 realm/service/scope 缓存 token）。</param>
    /// <param name="logger">结构化日志器。</param>
    public DockerTokenService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<DockerTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// 解析上游 WWW-Authenticate 头为 Bearer 挑战参数（realm/service/scope）。
    /// 仅支持 <c>Bearer</c> scheme（Basic 等由调用方按「需鉴权」处理，不在此换取）。
    /// </summary>
    /// <param name="wwwAuthenticate">上游返回的 WWW-Authenticate 头原始值。</param>
    /// <param name="realm">解析出的 realm 地址（token 换取端点）。</param>
    /// <param name="service">解析出的 service 标识（可为空）。</param>
    /// <param name="scope">解析出的 scope 声明（可为空）。</param>
    /// <returns>是否为可解析的 Bearer 挑战。</returns>
    public static bool TryParseBearerChallenge(string wwwAuthenticate, out string realm, out string service, out string scope)
    {
        realm = string.Empty;
        service = string.Empty;
        scope = string.Empty;

        if (string.IsNullOrWhiteSpace(wwwAuthenticate))
        {
            return false;
        }

        // 仅处理 Bearer scheme；Basic 等其他 scheme 不进入 token 交换
        var trimmed = wwwAuthenticate.TrimStart();
        if (!trimmed.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 去掉 "Bearer " 前缀后按逗号拆分 k="v" 键值对；值可能带引号也可能不带
        var parameters = trimmed[BearerScheme.Length..];
        foreach (var rawParam in parameters.Split(','))
        {
            var param = rawParam.Trim();
            var equalsIndex = param.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = param[..equalsIndex].Trim().ToLowerInvariant();
            var value = param[(equalsIndex + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "realm":
                    realm = value;
                    break;
                case "service":
                    service = value;
                    break;
                case "scope":
                    scope = value;
                    break;
            }
        }

        // realm 缺失则无法换取 token
        return !string.IsNullOrEmpty(realm);
    }

    /// <summary>
    /// 获取 Bearer token：优先命中内存缓存；未命中则单飞并发去重后向 realm 换取并缓存。
    /// 获取失败（解析失败/上游非 2xx/网络异常/响应无 token 字段）返回 null，由调用方决定降级行为。
    /// 绝不记录 Authorization 头或 token 值。
    /// </summary>
    /// <param name="realm">token 换取端点地址（来自 401 挑战）。</param>
    /// <param name="service">service 参数（可为空）。</param>
    /// <param name="scope">scope 参数（可为空，逗号分隔多 scope 原样透传）。</param>
    /// <param name="clientAuthorization">客户端请求携带的 Authorization 头原始值（可为 null，匿名换取）。</param>
    /// <returns>换取成功的 Bearer token；失败返回 null。</returns>
    public async Task<string?> GetBearerTokenAsync(
        string realm,
        string service,
        string scope,
        string? clientAuthorization)
    {
        var cacheKey = BuildCacheKey(realm, service, scope);

        // 优先命中内存缓存（TTL=expires_in-60s），避免重复换取
        if (_cache.TryGetValue(cacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            return cachedToken;
        }

        // 单飞去重：同一 cacheKey 并发只发起一次换取；任务完成后立即移除，下次过期后重新换取。
        // 换取任务不绑定发起者的取消令牌（传 CancellationToken.None），避免首个请求者断开时
        // 连带取消等待同一 token 的其余并发请求；token 供全体请求共享缓存。
        var lazy = _inflight.GetOrAdd(cacheKey, _ => new Lazy<Task<string?>>(
            () => ExchangeAndCacheAsync(cacheKey, realm, service, scope, clientAuthorization, CancellationToken.None)));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            _inflight.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// 执行 token 换取并写缓存：GET <c>{realm}?service=...&amp;scope=...</c>（携带客户端 Authorization 则原样透传），
    /// 响应体解析 <c>token</c>/<c>access_token</c> 与 <c>expires_in</c>；成功后按 TTL 写内存缓存。
    /// </summary>
    /// <param name="cacheKey">内存缓存键。</param>
    /// <param name="realm">token 换取端点地址。</param>
    /// <param name="service">service 参数。</param>
    /// <param name="scope">scope 参数。</param>
    /// <param name="clientAuthorization">客户端 Authorization 头原始值（可为 null）。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>换取成功的 token；失败返回 null。</returns>
    private async Task<string?> ExchangeAndCacheAsync(
        string cacheKey,
        string realm,
        string service,
        string scope,
        string? clientAuthorization,
        CancellationToken cancellationToken)
    {
        // 拼接换取地址：service/scope 走 query 参数（Uri.EscapeDataString 防注入）
        var separator = realm.Contains('?') ? "&" : "?";
        var tokenUrl = $"{realm}{separator}service={Uri.EscapeDataString(service)}&scope={Uri.EscapeDataString(scope)}";

        var httpClient = _httpClientFactory.CreateClient("Docker");
        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        if (!string.IsNullOrWhiteSpace(clientAuthorization))
        {
            // 携带客户端 Authorization 透传给 token 服务（匿名时省略）；该头不进入任何日志
            request.Headers.TryAddWithoutValidation("Authorization", clientAuthorization);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Docker token exchange failed (network): {Error} - realm={Realm}", ex.Message, realm);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Docker token exchange timed out - realm={Realm}", realm);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 不记录响应体（可能含敏感信息），仅记录状态码与 realm
                _logger.LogWarning("Docker token exchange failed: {StatusCode} - realm={Realm}",
                    (int)response.StatusCode, realm);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!TryParseToken(json, out var token, out var expiresInSeconds))
            {
                _logger.LogWarning("Docker token exchange response has no token field - realm={Realm}", realm);
                return null;
            }

            // TTL = expires_in - 60s，最小 30s：到期前主动过期，避免临界处 401 抖动
            var ttlSeconds = Math.Max(MinimumCacheTtlSeconds, expiresInSeconds - TokenExpiryBufferSeconds);
            _cache.Set(cacheKey, token, TimeSpan.FromSeconds(ttlSeconds));
            _logger.LogInformation("Docker token cached for {Scope} (ttl={Ttl}s)", scope, ttlSeconds);
            return token;
        }
    }

    /// <summary>
    /// 构造 token 内存缓存键：按 <c>(realm,service,scope)</c> 区分，保证不同上游/不同权限范围互不串用。
    /// </summary>
    /// <param name="realm">realm 地址。</param>
    /// <param name="service">service 参数。</param>
    /// <param name="scope">scope 参数。</param>
    /// <returns>缓存键字符串。</returns>
    private static string BuildCacheKey(string realm, string service, string scope)
    {
        return $"docker:token:{realm}|{service}|{scope}";
    }

    /// <summary>
    /// 解析 token 服务 JSON 响应：支持 <c>token</c> 与 <c>access_token</c> 两种字段，
    /// <c>expires_in</c> 缺省按 300 处理。使用 <see cref="JsonDocument"/> 解析（AOT 安全，无反射）。
    /// </summary>
    /// <param name="json">token 服务响应体。</param>
    /// <param name="token">解析出的 token 值（无则空串）。</param>
    /// <param name="expiresInSeconds">token 有效期秒数（缺省 300）。</param>
    /// <returns>是否成功解析出 token。</returns>
    private static bool TryParseToken(string json, out string token, out int expiresInSeconds)
    {
        token = string.Empty;
        expiresInSeconds = 300;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("token", out var tokenElement) &&
                tokenElement.ValueKind == JsonValueKind.String)
            {
                token = tokenElement.GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(token) &&
                root.TryGetProperty("access_token", out var accessTokenElement) &&
                accessTokenElement.ValueKind == JsonValueKind.String)
            {
                token = accessTokenElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("expires_in", out var expiresElement) &&
                expiresElement.ValueKind == JsonValueKind.Number &&
                expiresElement.TryGetInt32(out var parsedExpires) &&
                parsedExpires > 0)
            {
                expiresInSeconds = parsedExpires;
            }
        }
        catch (JsonException)
        {
            // JSON 格式异常：解析失败由调用方统一记录（不记录响应体，可能含敏感信息）
            return false;
        }

        return !string.IsNullOrEmpty(token);
    }
}
