using System.Text;

namespace Orbitra.Services;

/// <summary>
/// 文本内容结果构建辅助类：统一使用 UTF-8 字节长度显式设置响应 <c>Content-Length</c>，
/// 再返回 <c>Results.Content</c>，保证 HEAD 与 GET 响应的 Content-Length 完全一致
/// （HEAD 时 Kestrel 自动抑制响应体，仅保留头部）。
/// </summary>
public static class TextContentResult
{
    /// <summary>
    /// 构建文本内容结果：按 UTF-8 计算字节数并显式设置 Content-Length 后返回内容结果。
    /// </summary>
    /// <param name="httpContext">当前请求上下文（用于写入 Content-Length 响应头）。</param>
    /// <param name="content">响应正文文本（UTF-8 编码）。</param>
    /// <param name="contentType">响应 Content-Type。</param>
    /// <returns>内容结果对象（由框架负责写出）。</returns>
    public static IResult Build(HttpContext httpContext, string content, string contentType)
    {
        httpContext.Response.ContentLength = Encoding.UTF8.GetByteCount(content);
        return Results.Content(content, contentType);
    }
}
