using System.Net;
using System.Security.Cryptography;
using Orbitra.Configuration;

namespace Orbitra.Services;

/// <summary>
/// 磁盘缓存下载服务：NuGet / Maven / npm / docker 共用的「下载 → 流式落盘 → 原子 rename → 本地文件返回」链路。
/// 支持多上游顺序回退：按 URL 列表顺序逐一尝试，首个 2xx 落盘返回，网络异常/非 2xx 记录后换下一个，
/// 全部失败返回最后一个非 2xx 状态码（全为网络异常时 502）。
/// 采用临时文件（同目录 tmp + 原子 rename）保证并发同路径下载不产生半成品文件；
/// 客户端中途断开时取消写入并清理临时文件；磁盘写失败（IOException/UnauthorizedAccessException）
/// 统一转为 503 并输出结构化日志。
/// 可选能力（docker 场景使用，全部向后兼容）：<c>responseHeaders</c> 在成功响应上透传自定义头；
/// <c>requestHeaders</c> 附加到上游 GET 请求；<c>expectedSha256</c> 流式校验落盘内容与期望摘要一致，
/// 不符则删除临时文件并回退下一上游；<c>unauthorizedTokenProvider</c> 在遇到 401 时换取 token 并重试同一上游。
/// </summary>
public sealed class DiskCacheService
{
    /// <summary>流式写入缓冲区大小（64KB），兼顾吞吐与内存占用。</summary>
    private const int StreamBufferSize = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyOptions _options;
    private readonly ILogger<DiskCacheService> _logger;

    /// <summary>
    /// 初始化磁盘缓存下载服务。
    /// </summary>
    /// <param name="httpClientFactory">命名 HttpClient 工厂，用于创建上游下载客户端。</param>
    /// <param name="options">代理服务配置（含磁盘缓存根目录）。</param>
    /// <param name="logger">结构化日志器。</param>
    public DiskCacheService(IHttpClientFactory httpClientFactory, ProxyOptions options, ILogger<DiskCacheService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 下载上游文件并落盘到缓存目录：磁盘命中直接返回本地文件（不触碰上游）；未命中则按传入的
    /// URL 列表顺序逐一尝试（多上游失败回退），首个 2xx 流式落盘（先写同目录
    /// <c>{fileName}.{guid}.tmp</c>，成功后再原子 rename 为最终文件）并通过 <c>Results.File</c>
    /// （SendFile 零拷贝）返回本地文件。
    /// </summary>
    /// <param name="clientName">IHttpClientFactory 命名客户端名称（如 "NuGet" / "Maven" / "npm" / "Docker"）。</param>
    /// <param name="targetUrls">上游完整下载地址列表，顺序即回退顺序；第一个 2xx 即成功并落盘。</param>
    /// <param name="cacheFile">磁盘缓存最终文件完整路径。</param>
    /// <param name="fallbackContentType">Content-Type 回退值（上游未提供时使用，与磁盘命中逻辑一致）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <param name="responseHeaders">成功响应需附加的自定义响应头（如 Docker-Content-Digest），可为 null。</param>
    /// <param name="expectedSha256">期望的 sha256 十六进制摘要（小写）；非 null 时边写边算流式哈希，
    /// 落盘内容摘要与期望不符则删除临时文件并回退下一上游（防上游毒化/损坏）。</param>
    /// <param name="requestHeaders">附加到上游 GET 请求的自定义请求头（如 Authorization），可为 null。</param>
    /// <param name="unauthorizedTokenProvider">401 处理委托：入参为上游 URL 与 WWW-Authenticate 原始值，
    /// 返回换取成功的 Bearer token；返回 null 表示无法换取（该上游按 401 失败处理，回退下一上游）。</param>
    /// <param name="unauthorizedChallenge">全部上游最终为 401 时，需要附加到响应的 WWW-Authenticate 质询值
    /// （不透传上游质询时使用，如 <c>Basic realm="Orbitra"</c>），可为 null。</param>
    /// <returns>
    /// 成功返回 <see cref="IResult"/>（磁盘命中或下载成功后为本地文件）；全部失败时返回最后一个非 2xx 状态码，
    /// 若全为网络异常（无任何响应）返回 502 Bad Gateway；磁盘写失败返回 503（本地故障，不触发换源）。
    /// </returns>
    public async Task<IResult> DownloadToCacheAsync(
        string clientName,
        IReadOnlyList<string> targetUrls,
        string cacheFile,
        string fallbackContentType,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? responseHeaders = null,
        string? expectedSha256 = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        Func<string, string, Task<string?>>? unauthorizedTokenProvider = null,
        string? unauthorizedChallenge = null)
    {
        // 循环前先做磁盘命中检查：命中直接返回本地文件，不触碰任何上游
        if (File.Exists(cacheFile))
        {
            _logger.LogInformation("Cache hit: {File}", cacheFile);
            return WrapWithHeaders(Results.File(cacheFile, fallbackContentType), responseHeaders);
        }

        var httpClient = _httpClientFactory.CreateClient(clientName);
        // 记录最后一个非 2xx 状态码；0 表示尚未收到任何上游响应（全部为网络异常）
        var lastStatusCode = 0;

        for (var index = 0; index < targetUrls.Count; index++)
        {
            var targetUrl = targetUrls[index];
            HttpResponseMessage response;

            try
            {
                using var request = BuildRequest(HttpMethod.Get, targetUrl, requestHeaders);
                // 头一到即返回，body 不预缓冲，交由下方 CopyToAsync 流式落盘，避免整包进内存
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                // 网络异常（连接失败/DNS/拒绝等）：结构化记录后回退下一个上游
                _logger.LogWarning("{Client} upstream {Index} failed: {Error} - {Url}",
                    clientName, index, ex.Message, targetUrl);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 客户端未取消但请求超时（HttpClient.Timeout 触发）：视为上游失败，回退下一个
                _logger.LogWarning("{Client} upstream {Index} failed: timeout - {Url}",
                    clientName, index, targetUrl);
                continue;
            }

            // 401 鉴权处理：提供 token 委托时内部完成 token 交换并带 Authorization 重试同一上游
            if (response.StatusCode == HttpStatusCode.Unauthorized && unauthorizedTokenProvider != null)
            {
                var wwwAuthenticate = response.Headers.WwwAuthenticate.ToString();
                response.Dispose();
                try
                {
                    var token = await unauthorizedTokenProvider(targetUrl, wwwAuthenticate);
                    if (string.IsNullOrEmpty(token))
                    {
                        lastStatusCode = (int)HttpStatusCode.Unauthorized;
                        _logger.LogWarning("{Client} upstream {Index} unauthorized, token exchange failed - {Url}",
                            clientName, index, targetUrl);
                        continue;
                    }

                    // 重试请求丢弃客户端原始 Authorization（避免与 Bearer token 两路叠加导致上游歧义），
                    // 仅附交换成功的 Bearer token，与 DockerProxyHandler.SendSingleAsync 重试语义一致
                    using var retryRequest = BuildRequest(HttpMethod.Get, targetUrl, StripAuthorizationHeader(requestHeaders));
                    retryRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    response = await httpClient.SendAsync(
                        retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("{Client} upstream {Index} auth retry failed: {Error} - {Url}",
                        clientName, index, ex.Message, targetUrl);
                    continue;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{Client} upstream {Index} auth retry timeout - {Url}",
                        clientName, index, targetUrl);
                    continue;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                lastStatusCode = (int)response.StatusCode;
                _logger.LogWarning("{Client} upstream {Index} failed: {StatusCode} - {Url}",
                    clientName, index, lastStatusCode, targetUrl);
                response.Dispose();
                continue;
            }

            // 首个 2xx：流式落盘 + 原子 rename 后返回；磁盘写失败为本地故障，不触发换源
            using (response)
            {
                var cacheDir = Path.GetDirectoryName(cacheFile);
                var tmpFile = $"{cacheFile}.{Guid.NewGuid():N}.tmp";

                try
                {
                    if (!string.IsNullOrEmpty(cacheDir))
                    {
                        Directory.CreateDirectory(cacheDir);
                    }

                    // 流式落盘：64KB 异步缓冲写入临时文件，不整包入内存；期望摘要存在时边写边算哈希
                    string? actualDigestHex = null;
                    await using (var fileStream = new FileStream(
                        tmpFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true))
                    {
                        if (expectedSha256 is null)
                        {
                            await response.Content.CopyToAsync(fileStream, cancellationToken);
                            await fileStream.FlushAsync(cancellationToken);
                        }
                        else
                        {
                            // 边写边算：HashingWriteStream 包裹 FileStream，CopyToAsync 流式写入同时累计 SHA256
                            using (var hashingStream = new HashingWriteStream(fileStream))
                            {
                                await response.Content.CopyToAsync(hashingStream, cancellationToken);
                                await fileStream.FlushAsync(cancellationToken);
                                actualDigestHex = hashingStream.GetDigestHex();
                            }
                        }
                    }

                    // 期望摘要校验：不一致说明上游内容被毒化/损坏，删除临时文件后回退下一上游
                    if (expectedSha256 is not null &&
                        !string.Equals(actualDigestHex, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteTempFile(tmpFile);
                        _logger.LogWarning(
                            "{Client} upstream {Index} digest mismatch, expected {Expected} got {Got} - {Url}",
                            clientName, index, expectedSha256, actualDigestHex, targetUrl);
                        continue;
                    }

                    // 原子 rename：并发同路径下载各写各的 tmp，先完成的 rename 生效，杜绝半成品文件被读到
                    File.Move(tmpFile, cacheFile, overwrite: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 客户端断开：清理临时文件后向上抛，由框架按请求取消处理
                    TryDeleteTempFile(tmpFile);
                    throw;
                }
                catch (IOException ex)
                {
                    TryDeleteTempFile(tmpFile);
                    _logger.LogError(ex, "Cache file write failed (IOException): {File}", cacheFile);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                catch (UnauthorizedAccessException ex)
                {
                    TryDeleteTempFile(tmpFile);
                    _logger.LogError(ex, "Cache file write failed (UnauthorizedAccess): {File}", cacheFile);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? fallbackContentType;
                _logger.LogInformation("{Client} served from upstream {Index}: {Url}",
                    clientName, index, targetUrl);
                _logger.LogInformation("Download success: {File}, Size: {Size} bytes",
                    cacheFile, new FileInfo(cacheFile).Length);

                return WrapWithHeaders(Results.File(cacheFile, contentType), responseHeaders);
            }
        }

        // 全部失败：最后一个非 2xx 状态码；全为网络异常（无任何响应）→ 502 Bad Gateway
        if (lastStatusCode != 0)
        {
            _logger.LogError("All {Client} upstreams failed, last status {StatusCode}",
                clientName, lastStatusCode);

            // 最终 401 且配置了自定义质询：附加 Orbitra 自己的 WWW-Authenticate，不透传上游质询
            if (lastStatusCode == (int)HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(unauthorizedChallenge))
            {
                var headers = new Dictionary<string, string> { ["WWW-Authenticate"] = unauthorizedChallenge };
                return WrapWithHeaders(Results.StatusCode(lastStatusCode), headers);
            }

            return Results.StatusCode(lastStatusCode);
        }

        _logger.LogError("All {Client} upstreams failed, no upstream responded", clientName);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// 构建上游请求：统一方法 + 可选附加请求头（如 Authorization），请求头不写入任何日志。
    /// </summary>
    /// <param name="method">请求方法（GET）。</param>
    /// <param name="targetUrl">上游完整 URL。</param>
    /// <param name="requestHeaders">附加请求头集合，可为 null。</param>
    /// <returns>构造完成的请求对象。</returns>
    private static HttpRequestMessage BuildRequest(HttpMethod method, string targetUrl, IReadOnlyDictionary<string, string>? requestHeaders)
    {
        var request = new HttpRequestMessage(method, targetUrl);
        if (requestHeaders != null)
        {
            foreach (var (headerName, headerValue) in requestHeaders)
            {
                request.Headers.TryAddWithoutValidation(headerName, headerValue);
            }
        }

        return request;
    }

    /// <summary>
    /// 返回去除 Authorization 键后的请求头集合：供 401 重试请求使用（统一改用 Bearer token 头，
    /// 避免与客户端原始 Authorization 头重复携带触发上游歧义）；无 Authorization 或为空时原样返回。
    /// </summary>
    /// <param name="requestHeaders">原始请求头集合（可为 null）。</param>
    /// <returns>去除 Authorization 后的请求头集合（原集合为 null 时返回 null）。</returns>
    private static IReadOnlyDictionary<string, string>? StripAuthorizationHeader(
        IReadOnlyDictionary<string, string>? requestHeaders)
    {
        if (requestHeaders is null)
        {
            return null;
        }

        Dictionary<string, string>? filtered = null;
        foreach (var (headerName, headerValue) in requestHeaders)
        {
            if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered ??= new Dictionary<string, string>();
            filtered[headerName] = headerValue;
        }

        return filtered;
    }

    /// <summary>
    /// 用 IResult 包装成功响应并附加自定义响应头（如 Docker-Content-Digest）；无附加头时原样返回。
    /// </summary>
    /// <param name="inner">被包装的结果对象。</param>
    /// <param name="responseHeaders">需附加的响应头集合，可为 null。</param>
    /// <returns>包装后的结果对象。</returns>
    private static IResult WrapWithHeaders(IResult inner, IReadOnlyDictionary<string, string>? responseHeaders)
    {
        if (responseHeaders is null || responseHeaders.Count == 0)
        {
            return inner;
        }

        return new HeaderEnrichedResult(inner, responseHeaders);
    }

    /// <summary>
    /// 删除残留的临时文件；删除失败仅记录 Debug 日志，不阻断主流程。
    /// </summary>
    /// <param name="tmpFile">临时文件完整路径。</param>
    private void TryDeleteTempFile(string tmpFile)
    {
        try
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file: {File}", tmpFile);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file: {File}", tmpFile);
        }
    }

    /// <summary>
    /// 在结果写出时先附加自定义响应头再执行被包装结果：用于在不改各调用方签名的情况下透传头（如 Docker-Content-Digest）。
    /// </summary>
    /// <param name="Inner">被包装的结果对象。</param>
    /// <param name="Headers">需要附加的响应头集合。</param>
    private sealed record HeaderEnrichedResult(IResult Inner, IReadOnlyDictionary<string, string> Headers) : IResult
    {
        /// <summary>
        /// 执行结果：先写附加响应头，再执行被包装结果。
        /// </summary>
        /// <param name="httpContext">当前 HTTP 上下文。</param>
        /// <returns>执行完成的任务。</returns>
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            foreach (var (headerName, headerValue) in Headers)
            {
                httpContext.Response.Headers[headerName] = headerValue;
            }

            await Inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// 流式哈希写入流：包裹目标写入流，所有写入同时喂给 IncrementalHash（SHA256），
    /// 支持边写边算落盘内容的摘要，避免下载完成后整文件重读校验。
    /// </summary>
    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;

        /// <summary>
        /// 初始化哈希写入流。
        /// </summary>
        /// <param name="inner">被包裹的目标写入流（如 FileStream）。</param>
        public HashingWriteStream(Stream inner)
        {
            _inner = inner;
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        /// <summary>是否可读（仅写流，恒为 false）。</summary>
        public override bool CanRead => false;

        /// <summary>是否可写（恒为 true）。</summary>
        public override bool CanWrite => true;

        /// <summary>是否可定位（恒为 false）。</summary>
        public override bool CanSeek => false;

        /// <summary>流长度（不支持，抛异常）。</summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>流位置（不支持，抛异常）。</summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 同步写入并更新哈希。
        /// </summary>
        /// <param name="buffer">数据缓冲。</param>
        /// <param name="offset">写入起始偏移。</param>
        /// <param name="count">写入字节数。</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _hash.AppendData(buffer, offset, count);
        }

        /// <summary>
        /// 异步写入并更新哈希。
        /// </summary>
        /// <param name="buffer">数据缓冲。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            _hash.AppendData(buffer.Span);
        }

        /// <summary>
        /// 计算已写入内容的 sha256 十六进制摘要（小写），计算后哈希器复位。
        /// </summary>
        /// <returns>小写十六进制 sha256 摘要。</returns>
        public string GetDigestHex()
        {
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>
        /// 刷新内层流（同步）。
        /// </summary>
        public override void Flush()
        {
            _inner.Flush();
        }

        /// <summary>
        /// 异步刷新内层流。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// 读取（不支持，抛异常）。
        /// </summary>
        /// <param name="buffer">数据缓冲。</param>
        /// <param name="offset">读取起始偏移。</param>
        /// <param name="count">读取字节数。</param>
        /// <returns>读取字节数。</returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 定位（不支持，抛异常）。
        /// </summary>
        /// <param name="offset">偏移量。</param>
        /// <param name="origin">定位起点。</param>
        /// <returns>新位置。</returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 设置长度（不支持，抛异常）。
        /// </summary>
        /// <param name="value">新长度。</param>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 释放资源：释放哈希器并刷新内层流。
        /// </summary>
        /// <param name="disposing">是否释放托管资源。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
