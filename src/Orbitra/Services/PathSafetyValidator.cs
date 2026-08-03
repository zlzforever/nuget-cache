namespace Orbitra.Services;

/// <summary>
/// 路径安全校验器：Maven 与 npm 通配路由共用的路径段校验逻辑。
/// 逐段拒绝路径穿越（..）、点段、空段、控制字符及跨平台非法字符，防止目录逃逸。
/// </summary>
public static class PathSafetyValidator
{
    /// <summary>路径总长度上限。</summary>
    private const int MaxTotalPathLength = 4096;

    /// <summary>单个路径段长度上限。</summary>
    private const int MaxSegmentLength = 255;

    /// <summary>
    /// 校验通配路由路径是否安全：先做总长度与 URL 编码归一化校验，再逐段校验
    /// 拒绝 <c>..</c>、<c>.</c>、空段、控制字符及跨平台非法字符，保留大小写原样。
    /// </summary>
    /// <param name="path">待校验的原始路径（未解码，来自 {**path} 路由参数）。</param>
    /// <returns>元组：是否合法，不合法时的拒绝原因（合法时为空字符串）。</returns>
    public static (bool IsValid, string Reason) ValidatePath(string path)
    {
        if (path.Length > MaxTotalPathLength)
        {
            return (false, $"总路径长度超过上限 {MaxTotalPathLength}");
        }

        // URL 编码归一化后再做段校验，防止 %2e%2e / %2F 等编码变体绕过 .. 拒绝逻辑；
        // 仅用于判定，不改动已通过校验的落盘路径内容（大小写保持不变）。
        // 注意：Uri.UnescapeDataString 对非法编码（如 %G0、孤立 %）不抛异常而是原样透传，
        // 非法 % 会被上游 Uri 重新编码为 %25 后转发并 404 透传，不构成崩溃或安全问题；
        // 下方 catch 为防御性保留（实际不会触发），接受该行为由上游 404 兜底
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return (false, "路径包含非法 URL 编码");
        }

        var segments = decodedPath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return (false, "路径包含空段");
            }

            if (segment == "." || segment == "..")
            {
                return (false, $"路径包含非法段: {segment}");
            }

            if (segment.Length > MaxSegmentLength)
            {
                return (false, $"路径段长度超过上限 {MaxSegmentLength}: {segment}");
            }

            foreach (var c in segment)
            {
                // 控制字符及跨平台非法字符（\\ : * ? " < > |）
                if (char.IsControl(c) || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                {
                    return (false, $"路径段包含非法字符: {segment}");
                }
            }
        }

        return (true, string.Empty);
    }
}
