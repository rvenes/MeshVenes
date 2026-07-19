using System.Security.Cryptography;
using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class UpdatePackageIntegrityTests
{
    private static readonly byte[] SampleBytes =
    [
        0x4d, 0x65, 0x73, 0x68, 0x56, 0x65, 0x6e, 0x65, 0x73,
        0x00, 0x01, 0x02, 0xfe, 0xff
    ];

    [Fact]
    public async Task VerifyFileAsync_AcceptsExactSizeAndLowercaseHash()
    {
        using var file = TempFile.Create(SampleBytes);
        var expectedHash = ComputeSha256(SampleBytes);

        await UpdatePackageIntegrity.VerifyFileAsync(
            file.Path,
            SampleBytes.LongLength,
            expectedHash);
    }

    [Fact]
    public async Task VerifyFileAsync_AcceptsTrimmedUppercaseHash()
    {
        using var file = TempFile.Create(SampleBytes);
        var expectedHash = $"  {ComputeSha256(SampleBytes).ToUpperInvariant()}  ";

        await UpdatePackageIntegrity.VerifyFileAsync(
            file.Path,
            SampleBytes.LongLength,
            expectedHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task VerifyFileAsync_RejectsNonPositiveExpectedSize(long expectedSize)
    {
        using var file = TempFile.Create(SampleBytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageIntegrity.VerifyFileAsync(
                file.Path,
                expectedSize,
                ComputeSha256(SampleBytes)));

        Assert.Contains("greater than zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef 123456789abcdef0123456789abcdef")]
    public async Task VerifyFileAsync_RejectsMalformedExpectedHash(string? expectedHash)
    {
        using var file = TempFile.Create(SampleBytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageIntegrity.VerifyFileAsync(
                file.Path,
                SampleBytes.LongLength,
                expectedHash));

        Assert.Contains("64 hexadecimal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyFileAsync_RejectsSizeMismatch()
    {
        using var file = TempFile.Create(SampleBytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageIntegrity.VerifyFileAsync(
                file.Path,
                SampleBytes.LongLength + 1,
                ComputeSha256(SampleBytes)));

        Assert.Contains("size mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((SampleBytes.LongLength + 1).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(SampleBytes.LongLength.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyFileAsync_RejectsHashMismatch()
    {
        using var file = TempFile.Create(SampleBytes);
        var differentHash = ComputeSha256([0x00]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageIntegrity.VerifyFileAsync(
                file.Path,
                SampleBytes.LongLength,
                differentHash));

        Assert.Contains("SHA-256 mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(differentHash, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ComputeSha256(SampleBytes), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureDownloadWithinExpectedSize_AcceptsExactSize()
    {
        UpdatePackageIntegrity.EnsureDownloadWithinExpectedSize(
            downloadedSize: 128,
            expectedSize: 128);
    }

    [Fact]
    public void EnsureDownloadWithinExpectedSize_RejectsOversizedDownload()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            UpdatePackageIntegrity.EnsureDownloadWithinExpectedSize(
                downloadedSize: 129,
                expectedSize: 128));

        Assert.Contains("exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("128", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDownloadWithinExpectedSize_RejectsNegativeDownloadedSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UpdatePackageIntegrity.EnsureDownloadWithinExpectedSize(
                downloadedSize: -1,
                expectedSize: 128));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsureDownloadWithinExpectedSize_RejectsInvalidExpectedSize(long expectedSize)
    {
        Assert.Throws<InvalidDataException>(() =>
            UpdatePackageIntegrity.EnsureDownloadWithinExpectedSize(
                downloadedSize: 0,
                expectedSize));
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempFile Create(byte[] bytes)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MeshVenes-update-integrity-{Guid.NewGuid():N}.zip");
            File.WriteAllBytes(path, bytes);
            return new TempFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // Test cleanup is best effort.
            }
        }
    }
}
