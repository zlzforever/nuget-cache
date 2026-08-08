using System.Reflection;

namespace Orbitra.Configuration;

/// <summary>
/// 应用启动信息：集中承载产品展示名、定位标语、版本号与 ASCII LOGO，
/// 供组合根启动时打印启动 banner 使用。未来产品改名或新增仓库支持时，
/// 只需修改本类的常量即可全局生效，避免散落在各处硬编码。
/// 本类全部为静态常量/属性，无反射扫描与动态生成，完全兼容 NativeAOT 裁剪。
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// 产品展示名（与仓库名、镜像名保持一致，作为品牌资产在本次改造中由 NuGetCache 更名为 Orbitra）。
    /// </summary>
    public const string Name = "Orbitra";

    /// <summary>
    /// 产品定位标语（多仓库包缓存代理），用于 banner 定位行展示。
    /// </summary>
    public const string Tagline = "Multi-Repo Package Cache Proxy";

    /// <summary>
    /// 当前已支持的仓库列表（竖线分隔），用于 banner 定位行展示。
    /// </summary>
    public const string SupportedRepos = "nuget | maven | npm | docker | pip";

    /// <summary>
    /// 规划支持的仓库列表（竖线分隔），用于 banner 定位行展示；当前已全部落地，为空串。
    /// </summary>
    public const string PlannedRepos = "";

    /// <summary>
    /// 产品版本号（含 git commit 短哈希，如 <c>1.0.0+fa2058b</c>；无 git 上下文时退化为纯 <c>1.0.0</c>）。
    /// 在 git 仓库内构建时，SDK 自动将 SourceRevisionId 附加到 AssemblyInformationalVersion 属性。
    /// </summary>
    public static string Version { get; } = BuildVersion();

    /// <summary>
    /// ASCII 艺术 LOGO（raw string 常量，纯 ASCII，避免终端/日志采集器渲染变形），
    /// 主体为产品名 Orbitra，用于 banner 首屏展示。
    /// </summary>
    public const string Logo = """
      ___      _    _ _
     / _ \ _ _| |__(_) |_ _ _ __ _
    | (_) | '_| '_ \ |  _| '_/ _` |
     \___/|_| |_.__/_|\__|_| \__,_|

    """;

    /// <summary>
    /// 组装启动 banner：LOGO + 版本行 + 定位行，供组合根启动时整块打印。
    /// 规划列表为空时省略 "Next:" 段（当前全部仓库已支持）。
    /// </summary>
    /// <returns>完整 banner 文本（含末尾换行，可直接 Console.WriteLine 输出）。</returns>
    public static string Banner() => string.Join(
        Environment.NewLine,
        Logo.TrimEnd('\r', '\n'),
        string.Empty,
        $"  {Name} v{Version}  |  {Tagline}",
        string.IsNullOrEmpty(PlannedRepos)
            ? $"  Repos: {SupportedRepos}"
            : $"  Repos: {SupportedRepos}                 Next: {PlannedRepos} (规划中)");

    /// <summary>
    /// 读取当前程序集的 <see cref="AssemblyInformationalVersionAttribute"/> 生成展示版本号：
    /// git 内构建时形如 <c>1.0.0+&lt;full commit hash&gt;</c>，截断为短哈希（7 位）展示；
    /// 无 git 上下文或元数据缺失时退化为纯版本号 <c>1.0.0</c>。
    /// 该方法经 NativeAOT 实测可正常读取，不触发裁剪问题。
    /// </summary>
    /// <returns>用于展示的版本号字符串。</returns>
    private static string BuildVersion()
    {
        var informationalVersion = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "1.0.0";
        }

        // git 内构建时 InformationalVersion 形如 "1.0.0+<full commit hash>"，截断为短哈希展示
        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex > 0 && plusIndex < informationalVersion.Length - 1)
        {
            var shortHash = informationalVersion[(plusIndex + 1)..];
            return $"{informationalVersion[..plusIndex]}+{shortHash[..Math.Min(7, shortHash.Length)]}";
        }

        return informationalVersion;
    }
}
