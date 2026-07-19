using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public static class UpdatePackageIntegrity
{
    private const int Sha256HexLength = 64;

    public static void EnsureDownloadWithinExpectedSize(
        long downloadedSize,
        long expectedSize)
    {
        if (expectedSize <= 0)
            throw new InvalidDataException("Expected update size must be greater than zero.");
        if (downloadedSize < 0)
            throw new ArgumentOutOfRangeException(
                nameof(downloadedSize),
                "Downloaded update size must not be negative.");
        if (downloadedSize > expectedSize)
        {
            throw new InvalidDataException(
                $"Update download exceeded the expected size of {expectedSize} bytes.");
        }
    }

    public static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        EnsureDownloadWithinExpectedSize(downloadedSize: 0, expectedSize);

        var normalizedExpectedHash = NormalizeExpectedSha256(expectedSha256);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Update package path must not be empty.", nameof(path));

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        EnsureExpectedSize(stream.Length, expectedSize);

        using var sha256 = SHA256.Create();
        var actualHashBytes = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        // Recheck the length from the same stream in case the file changed while
        // it was being verified.
        EnsureExpectedSize(stream.Length, expectedSize);

        var actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();
        if (!string.Equals(actualHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Update package SHA-256 mismatch. Expected {normalizedExpectedHash}, but found {actualHash}.");
        }
    }

    private static string NormalizeExpectedSha256(string? expectedSha256)
    {
        var normalized = expectedSha256?.Trim() ?? "";
        if (normalized.Length != Sha256HexLength)
        {
            throw new InvalidDataException(
                "Expected update SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        foreach (var character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                throw new InvalidDataException(
                    "Expected update SHA-256 must contain exactly 64 hexadecimal characters.");
            }
        }

        return normalized.ToLowerInvariant();
    }

    private static void EnsureExpectedSize(long actualSize, long expectedSize)
    {
        if (actualSize != expectedSize)
        {
            throw new InvalidDataException(
                $"Update package size mismatch. Expected {expectedSize} bytes, but found {actualSize} bytes.");
        }
    }
}
