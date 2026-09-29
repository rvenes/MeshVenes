using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public static class ReleaseManifestSignature
{
    public const string PublicKeyXml = "<RSAKeyValue><Modulus>uDMXeybP0llPLe9X9AFYAulekHrQQs+H6kaJmyOn7SQQBwGu+m4o3exFedskOu6RCnIscj5bzDTEByPBiSRndAFD1PZRf7O4fKh1xPIdmsItwa3MAiMzvPvmLNtXxTb7vrMygUcyDwmc/uX+k4qHu+YVQlGfdnNiBInd7b4KLGn/ABRHrYW5ZLruhHqpqVFYgK5VkWvlicFuASRwNCv9C95QtLvmFXTMg8gThZB8eR+nUaL+OTMTnWTuKP0hQA0V9OJ6tAKyQ/Yg2LpQMNv4qSjjk/UVxjCtbc4Dzi1zkOVzjGMEHwcAcGqGrZp0MfwOjC4qGdpUJ0yprByawdPSsUVCaJV9nY3MAcYjuwgkZoN1J+GyKuendUaI3YghGpZbHv4/6sekdDHUtmcQY/g+t7Lvb48A0U5iYZP8G++5ooX9j8CT0UV7ENWrMf48fYqAdFtHqE9J2hSmzmrvJdNqN1TguU7wr4gu09NY+/fxLVw9r6JjvYG7rx3o4Big//xt</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    public static void Verify(byte[] manifest, byte[] signature) => VerifyWithKey(manifest, signature, PublicKeyXml);

    internal static void VerifyWithKey(byte[] manifest, byte[] signature, string publicKey)
    {
        if (manifest.Length == 0 || manifest.Length > 65536 || signature.Length != 384)
            throw new InvalidDataException("Invalid signed release metadata size.");
        using var rsa = RSA.Create();
        rsa.FromXmlString(publicKey);
        if (rsa.KeySize != 3072 || !rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("The release manifest signature is invalid.");
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken ct)
    {
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (target.Length + read > maximumBytes) throw new InvalidDataException("Release metadata exceeds its size limit.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }
}
