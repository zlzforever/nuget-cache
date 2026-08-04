using Orbitra.Services;
using Xunit;

namespace Orbitra.Tests;

/// <summary>
/// <see cref="DockerPathParser"/> 单元测试：覆盖版本探测、blob/manifests/tags-list 三类路径解析、
/// digest 与 tag 校验、digest 文件名转换。
/// </summary>
public sealed class DockerPathParserTests
{
    /// <summary>合法 digest（64 位小写十六进制）。</summary>
    private const string ValidDigest = "sha256:f3fd4e663acf47fe7285cf7ffb4de8ecd6bdd1a4d1c06f650ff93778ff6009f7";

    [Fact]
    public void Parse_EmptyOrNull_ReturnsVersionProbe()
    {
        // /v2、/v2/ 的 {**path} 捕获值均为空，
        // 路由后由处理器判空触发版本探测
        Assert.Equal(DockerEndpointKind.VersionProbe, DockerPathParser.Parse("").Kind);
        Assert.Equal(DockerEndpointKind.VersionProbe, DockerPathParser.Parse(string.Empty).Kind);
    }

    [Fact]
    public void Parse_VersionProbeWhitespacePath_ReturnsUnknown()
    {
        // 处理器在调用 Parse 前先做 IsNullOrWhiteSpace 判定，故 Parse 仅对空串返回 VersionProbe
        Assert.Equal(DockerEndpointKind.Unknown, DockerPathParser.Parse(" ").Kind);
    }

    [Fact]
    public void Parse_ManifestTag_ExtractsNameAndReference()
    {
        var result = DockerPathParser.Parse("library/nginx/manifests/latest");

        Assert.Equal(DockerEndpointKind.Manifest, result.Kind);
        Assert.Equal("library/nginx", result.Name);
        Assert.Equal("latest", result.Reference);
    }

    [Fact]
    public void Parse_ManifestDigest_ExtractsNameAndDigest()
    {
        var result = DockerPathParser.Parse($"library/nginx/manifests/{ValidDigest}");

        Assert.Equal(DockerEndpointKind.Manifest, result.Kind);
        Assert.Equal("library/nginx", result.Name);
        Assert.Equal(ValidDigest, result.Reference);
    }

    [Fact]
    public void Parse_ManifestMultiLevelName_KeepsSlashInName()
    {
        var result = DockerPathParser.Parse("a/b/c/repo/manifests/v1.2.3");

        Assert.Equal(DockerEndpointKind.Manifest, result.Kind);
        Assert.Equal("a/b/c/repo", result.Name);
        Assert.Equal("v1.2.3", result.Reference);
    }

    [Fact]
    public void Parse_Blob_ExtractsNameAndDigest()
    {
        var result = DockerPathParser.Parse($"library/nginx/blobs/{ValidDigest}");

        Assert.Equal(DockerEndpointKind.Blob, result.Kind);
        Assert.Equal("library/nginx", result.Name);
        Assert.Equal(ValidDigest, result.Reference);
    }

    [Fact]
    public void Parse_TagsList_ExtractsName()
    {
        var result = DockerPathParser.Parse("library/nginx/tags/list");

        Assert.Equal(DockerEndpointKind.TagsList, result.Kind);
        Assert.Equal("library/nginx", result.Name);
        Assert.Equal(string.Empty, result.Reference);
    }

    [Theory]
    [InlineData("library/nginx")]
    [InlineData("library/nginx/foo")]
    [InlineData("manifests/only")]
    [InlineData("blobs/only")]
    [InlineData("tags/list")]
    public void Parse_Unrecognized_ReturnsUnknown(string path)
    {
        var result = DockerPathParser.Parse(path);

        Assert.Equal(DockerEndpointKind.Unknown, result.Kind);
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("sha256:f3fd4e663acf47fe7285cf7ffb4de8ecd6bdd1a4d1c06f650ff93778ff6009f7", true)]
    [InlineData("sha256:F3FD4E663ACF47FE7285CF7FFB4DE8ECD6BDD1A4D1C06F650FF93778FF6009F7", false)]
    [InlineData("sha256:short", false)]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0", false)]
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    public void IsValidDigest_VariousFormats_ReturnsExpected(string digest, bool expected)
    {
        Assert.Equal(expected, DockerPathParser.IsValidDigest(digest));
    }

    [Theory]
    [InlineData("latest", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("v1.2.3-beta", true)]
    [InlineData("release_2024", true)]
    [InlineData("A.b-1", true)]
    [InlineData("", false)]
    [InlineData("1.0.0", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("a:latest", false)]
    [InlineData("a@latest", false)]
    public void IsValidTag_VariousFormats_ReturnsExpected(string tag, bool expected)
    {
        Assert.Equal(expected, DockerPathParser.IsValidTag(tag));
    }

    [Fact]
    public void DigestToFileName_StripsSha256Prefix()
    {
        Assert.Equal(
            "f3fd4e663acf47fe7285cf7ffb4de8ecd6bdd1a4d1c06f650ff93778ff6009f7",
            DockerPathParser.DigestToFileName(ValidDigest));
    }
}
