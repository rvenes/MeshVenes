using System.Security.Cryptography;
using System.Text;
using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class DataProtectionTests
{
    [Theory]
    [InlineData("settings")]
    [InlineData("device-profile")]
    public void PortableBackupRoundTripsAndRejectsWrongPassword(string kind)
    {
        var original = Encoding.UTF8.GetBytes("private-key and password fixture");
        var encrypted = EncryptedBackup.Encrypt(original, kind, "long test passphrase");
        Assert.DoesNotContain("private-key", Encoding.UTF8.GetString(encrypted));
        Assert.Equal(original, EncryptedBackup.Decrypt(encrypted, kind, "long test passphrase"));
        Assert.Throws<InvalidDataException>(() => EncryptedBackup.Decrypt(encrypted, kind, "wrong passphrase"));
        var otherKind = kind == "settings" ? "device-profile" : "settings";
        Assert.Throws<InvalidDataException>(() => EncryptedBackup.Decrypt(encrypted, otherKind, "long test passphrase"));
    }

    [Fact]
    public void TamperedBackupCannotDecrypt()
    {
        var data = EncryptedBackup.Encrypt([1, 2, 3], "settings", "long test passphrase");
        var json = System.Text.Json.Nodes.JsonNode.Parse(data)!;
        var ciphertext = Convert.FromBase64String(json["Ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 1;
        json["Ciphertext"] = Convert.ToBase64String(ciphertext);
        Assert.Throws<InvalidDataException>(() => EncryptedBackup.Decrypt(Encoding.UTF8.GetBytes(json.ToJsonString()), "settings", "long test passphrase"));
        Assert.Throws<ArgumentException>(() => EncryptedBackup.Encrypt([1], "settings", "short"));
    }

    [Fact]
    public void LocalArchiveMigratesLegacyContentWithoutExposingNewText()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "meshvenes-protected-test-" + Guid.NewGuid() + ".log");
        try
        {
            File.WriteAllText(path, "legacy message\n");
            Assert.Equal("legacy message\n", LocalProtectedFile.ReadAllText(path));
            LocalProtectedFile.AppendAllText(path, "new private message\n");
            Assert.DoesNotContain("message", File.ReadAllText(path));
            Assert.Equal(new[] { "legacy message", "new private message" }, LocalProtectedFile.ReadAllLines(path));
            LocalProtectedFile.AppendAllText(path, "third message\n");
            Assert.Equal(3, LocalProtectedFile.ReadAllLines(path).Length);
            LocalProtectedFile.WriteAllText(path, "{\"private\":true}");
            Assert.Equal("{\"private\":true}", LocalProtectedFile.ReadAllText(path));
            File.WriteAllText(path, "MV-DPAPI1:AAAA\n");
            Assert.Throws<CryptographicException>(() => { if (OperatingSystem.IsWindows()) LocalProtectedFile.ReadAllText(path); });
            Assert.Throws<CryptographicException>(() => { if (OperatingSystem.IsWindows()) LocalProtectedFile.WriteAllText(path, "replacement"); });
            Assert.Throws<CryptographicException>(() => { if (OperatingSystem.IsWindows()) LocalProtectedFile.AppendAllText(path, "append"); });
            Assert.Equal("MV-DPAPI1:AAAA\n", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ManifestRequiresAnAuthenticSignatureOverExactBytes()
    {
        using var rsa = RSA.Create(3072);
        var data = Encoding.UTF8.GetBytes("{\"version\":\"1.5.2\"}");
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ReleaseManifestSignature.VerifyWithKey(data, signature, rsa.ToXmlString(false));
        data[13] ^= 1;
        Assert.Throws<InvalidDataException>(() => ReleaseManifestSignature.VerifyWithKey(data, signature, rsa.ToXmlString(false)));
        Assert.Throws<InvalidDataException>(() => ReleaseManifestSignature.Verify(data, signature));
    }

    [Fact]
    public async Task MetadataReaderStopsAtLimit()
    {
        using var input = new MemoryStream(new byte[65537]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseManifestSignature.ReadBoundedAsync(input, 65536, default));
    }
}
