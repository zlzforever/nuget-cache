using Orbitra.Configuration;

namespace Orbitra.Services;

/// <summary>
/// 磁盘缓存下载服务：NuGet / Maven / npm 共用的「下载 → 流式落盘 → 原子 rename → 本地文件返回」链路。
/// 支持多上游顺序回退：按 URL 列表顺序逐一尝试，首个 2xx 落盘返回，网络异常/非 2xx 记录后换下一个，
/// 全部失败返回最后一个非 2xx 状态码（全为网络异常时 502）。
/// 采用临时文件（同目录 tmp + 原子 rename）保证并发同路径下载不产生半成品文件；
/// 客户端中途断开时取消写入并清理临时文件；磁盘写失败（IOException/UnauthorizedAccessException）
/// 统一转为 503 并输出结构化日志。
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
    /// <param name="clientName">IHttpClientFactory 命名客户端名称（如 "NuGet" / "Maven" / "npm"）。</param>
    /// <param name="targetUrls">上游完整下载地址列表，顺序即回退顺序；第一个 2xx 即成功并落盘。</param>
    /// <param name="cacheFile">磁盘缓存最终文件完整路径。</param>
    /// <param name="fallbackContentType">Content-Type 回退值（上游未提供时使用，与磁盘命中逻辑一致）。</param>
    /// <param name="cancellationToken">请求取消令牌（客户端断开时取消写入并清理临时文件）。</param>
    /// <returns>
    /// 成功返回 <see cref="IResult"/>（磁盘命中或下载成功后为本地文件）；全部失败时返回最后一个非 2xx 状态码，
    /// 若全为网络异常（无任何响应）返回 502 Bad Gateway；磁盘写失败返回 503（本地故障，不触发换源）。
    /// </returns>
    public async Task<IResult> DownloadToCacheAsync(
        string clientName,
        IReadOnlyList<string> targetUrls,
        string cacheFile,
        string fallbackContentType,
        CancellationToken cancellationToken)
    {
        // 循环前先做磁盘命中检查：命中直接返回本地文件，不触碰任何上游
        if (File.Exists(cacheFile))
        {
            _logger.LogInformation("Cache hit: {File}", cacheFile);
            return Results.File(cacheFile, fallbackContentType);
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
                // 头一到即返回，body 不预缓冲，交由下方 CopyToAsync 流式落盘，避免整包进内存
                response = await httpClient.GetAsync(
                    targetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                    // 流式落盘：64KB 异步缓冲写入临时文件，不整包入内存
                    await using (var fileStream = new FileStream(
                        tmpFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true))
                    {
                        await response.Content.CopyToAsync(fileStream, cancellationToken);
                        await fileStream.FlushAsync(cancellationToken);
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

                return Results.File(cacheFile, contentType);
            }
        }

        // 全部失败：最后一个非 2xx 状态码；全为网络异常（无任何响应）→ 502 Bad Gateway
        if (lastStatusCode != 0)
        {
            _logger.LogError("All {Client} upstreams failed, last status {StatusCode}",
                clientName, lastStatusCode);
            return Results.StatusCode(lastStatusCode);
        }

        _logger.LogError("All {Client} upstreams failed, no upstream responded", clientName);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
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
}
