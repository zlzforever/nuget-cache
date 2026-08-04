using System.Text.RegularExpressions;

namespace Orbitra.Services;

/// <summary>
/// docker 路径解析与校验工具：将 <c>{**path}</c> 捕获的相对路径（形如 <c>library/nginx/manifests/latest</c>）
/// 解析为端点类型与仓库名、引用；并提供 digest（<c>sha256:{hex}</c>）与 tag 的格式校验。
/// 路径必须先经 <see cref="PathSafetyValidator"/> 校验（name 段复用），digest 因含冒号须用专用正则单独校验。
/// </summary>
public static class DockerPathParser
{
    /// <summary>docker digest 完整格式：算法 <c>sha256</c> + 64 位小写十六进制。</summary>
    private const string DigestAlgorithm = "sha256";

    /// <summary>digest 校验正则：<c>^sha256:[0-9a-f]{64}$</c>，严格小写（docker digest 规范）。</summary>
    private static readonly Regex DigestRegex = new(
        @"^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    /// <summary>tag 校验正则：首字符字母/数字/下划线，后续允许 <c>.</c> <c>-</c> <c>_</c>，总长 1-128。</summary>
    private static readonly Regex TagRegex = new(
        @"^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析通配路径为 docker 路由信息：按末尾固定段识别端点类型。
    /// 形如 <c>{name}/manifests/{reference}</c> → Manifest；<c>{name}/blobs/{digest}</c> → Blob；
    /// <c>{name}/tags/list</c> → TagsList；空串 → VersionProbe；无法识别 → Unknown。
    /// </summary>
    /// <param name="path">相对路径（{**path} 捕获值，可为空串）。</param>
    /// <returns>解析结果：端点类型 + 仓库名（name，可为空）+ 引用（reference/digest，可为空）。</returns>
    public static DockerRouteInfo Parse(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new DockerRouteInfo(DockerEndpointKind.VersionProbe, string.Empty, string.Empty);
        }

        // 优先匹配 tags/list 固定段
        if (path.EndsWith("/tags/list", StringComparison.Ordinal))
        {
            var name = path[..^"/tags/list".Length];
            return new DockerRouteInfo(DockerEndpointKind.TagsList, name, string.Empty);
        }

        // 匹配 manifests/{reference}：reference 可为 tag 或 digest，因此取最后一个 /manifests/ 段
        var manifestMarker = "/manifests/";
        var manifestIndex = path.LastIndexOf(manifestMarker, StringComparison.Ordinal);
        if (manifestIndex >= 0)
        {
            var name = path[..manifestIndex];
            var reference = path[(manifestIndex + manifestMarker.Length)..];
            if (name.Length > 0 && reference.Length > 0)
            {
                return new DockerRouteInfo(DockerEndpointKind.Manifest, name, reference);
            }
        }

        // 匹配 blobs/{digest}
        var blobMarker = "/blobs/";
        var blobIndex = path.LastIndexOf(blobMarker, StringComparison.Ordinal);
        if (blobIndex >= 0)
        {
            var name = path[..blobIndex];
            var digest = path[(blobIndex + blobMarker.Length)..];
            if (name.Length > 0 && digest.Length > 0)
            {
                return new DockerRouteInfo(DockerEndpointKind.Blob, name, digest);
            }
        }

        return new DockerRouteInfo(DockerEndpointKind.Unknown, string.Empty, string.Empty);
    }

    /// <summary>
    /// 校验引用是否为合法 digest：必须完全匹配 <c>sha256:[0-9a-f]{64}</c>。
    /// </summary>
    /// <param name="reference">待校验的引用字符串（digest 或 tag）。</param>
    /// <returns>是否为合法 digest。</returns>
    public static bool IsValidDigest(string reference)
    {
        return DigestRegex.IsMatch(reference);
    }

    /// <summary>
    /// 校验引用是否为合法 tag：首字符字母/数字/下划线，后续允许 <c>.</c> <c>-</c> <c>_</c>，总长 1-128。
    /// </summary>
    /// <param name="reference">待校验的引用字符串（digest 或 tag）。</param>
    /// <returns>是否为合法 tag。</returns>
    public static bool IsValidTag(string reference)
    {
        return TagRegex.IsMatch(reference);
    }

    /// <summary>
    /// 将 digest 转为用于落盘文件名的十六进制串：剥离 <c>sha256:</c> 前缀。
    /// 调用前应确保引用已通过 <see cref="IsValidDigest"/> 校验。
    /// </summary>
    /// <param name="digest">完整 digest（如 <c>sha256:3b2e...</c>）。</param>
    /// <returns>十六进制摘要串（如 <c>3b2e...</c>）。</returns>
    public static string DigestToFileName(string digest)
    {
        return digest[DigestAlgorithm.Length..].TrimStart(':');
    }
}

/// <summary>
/// docker 路由端点类型。
/// </summary>
public enum DockerEndpointKind
{
    /// <summary>版本探测（<c>/v2</c>、<c>/v2/</c>，空路径）。</summary>
    VersionProbe,

    /// <summary>manifest 端点（<c>{name}/manifests/{reference}</c>）。</summary>
    Manifest,

    /// <summary>blob 端点（<c>{name}/blobs/{digest}</c>）。</summary>
    Blob,

    /// <summary>tags 列表端点（<c>{name}/tags/list</c>）。</summary>
    TagsList,

    /// <summary>无法识别的路径。</summary>
    Unknown
}

/// <summary>
/// docker 路径解析结果：端点类型 + 仓库名 + 引用。
/// </summary>
/// <param name="Kind">端点类型。</param>
/// <param name="Name">仓库名（含多级 <c>/</c>，如 <c>library/nginx</c>；版本探测为空串）。</param>
/// <param name="Reference">引用（manifest 的 tag/digest 或 blob 的 digest；其余为空串）。</param>
public sealed record DockerRouteInfo(DockerEndpointKind Kind, string Name, string Reference);
