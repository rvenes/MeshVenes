using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshVenes.Services;

public static class EncryptedBackup
{
    public const int MaximumPayloadBytes = 10 * 1024 * 1024;
    private const int Iterations = 600000;

    public static byte[] Encrypt(byte[] payload, string kind, string password)
    {
        ValidateKind(kind);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidDataException("Backup is too large.");
        if (password.Length < 12) throw new ArgumentException("Use a password with at least 12 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, payload, ciphertext, tag, Encoding.UTF8.GetBytes("MeshVenesBackup:1:" + kind));
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope("MeshVenesBackup", 1, kind, salt, nonce, ciphertext, tag));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(byte[] data, string expectedKind, string password)
    {
        ValidateKind(expectedKind);
        if (data.Length > MaximumPayloadBytes * 2) throw new InvalidDataException("Backup is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(data) ?? throw new InvalidDataException("Invalid encrypted backup.");
        if (envelope.Format != "MeshVenesBackup" || envelope.Version != 1 || envelope.Kind != expectedKind ||
            envelope.Salt?.Length != 16 || envelope.Nonce?.Length != 12 || envelope.Tag?.Length != 16 ||
            envelope.Ciphertext is null || envelope.Ciphertext.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Invalid backup format or wrong import category.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, envelope.Salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Encoding.UTF8.GetBytes("MeshVenesBackup:1:" + expectedKind));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Incorrect password or damaged encrypted backup.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("settings" or "device-profile")) throw new InvalidDataException("Unknown backup category.");
    }

    private sealed record Envelope(string Format, int Version, string Kind, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
