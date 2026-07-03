using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public class UpdateVersionUtilTests
{
    [Theory]
    [InlineData("1.4.7", "1.4.7")]
    [InlineData("v1.4.7", "1.4.7")]
    [InlineData("V1.4.7", "1.4.7")]
    [InlineData(" 1.4.7 ", "1.4.7")]
    [InlineData("1.4.7-beta", "1.4.7")]
    [InlineData("1.4.7+build5", "1.4.7")]
    [InlineData("1.4.7.2", "1.4.7.2")]
    public void TryParseVersion_ParsesDecoratedVersions(string input, string expected)
    {
        Assert.True(UpdateVersionUtil.TryParseVersion(input, out var version));
        Assert.Equal(System.Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1")]
    public void TryParseVersion_RejectsInvalidInput(string? input)
    {
        Assert.False(UpdateVersionUtil.TryParseVersion(input, out _));
    }

    [Fact]
    public void VersionComparison_DetectsNewerRelease()
    {
        Assert.True(UpdateVersionUtil.TryParseVersion("1.4.7", out var remote));
        Assert.True(UpdateVersionUtil.TryParseVersion("1.4.6", out var current));
        Assert.True(remote > current);
    }

    [Fact]
    public void VersionComparison_SameVersionIsNotNewer()
    {
        Assert.True(UpdateVersionUtil.TryParseVersion("v1.4.6", out var remote));
        Assert.True(UpdateVersionUtil.TryParseVersion("1.4.6", out var current));
        Assert.False(remote > current);
    }

    [Theory]
    [InlineData("v1.4.7", "1.4.7")]
    [InlineData("1.4.7+abc", "1.4.7")]
    [InlineData("1.4.7-rc1", "1.4.7")]
    [InlineData("weird", "weird")]
    public void SanitizeDisplayVersion_StripsDecorations(string input, string expected)
    {
        Assert.Equal(expected, UpdateVersionUtil.SanitizeDisplayVersion(input));
    }
}
