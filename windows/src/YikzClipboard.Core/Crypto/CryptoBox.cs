using System.Security.Cryptography;
using System.Text;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Crypto;

public sealed class CryptoException : Exception
{
    public CryptoException(string message) : base(message)
    {
    }

    public CryptoException(string message, Exception inner) : base(message, inner)
    {
    }
}

public static class Aad
{
    public static byte[] Meta(string itemId) => Encoding.UTF8.GetBytes("yc1|meta|" + itemId);

    public static byte[] Payload(string itemId) => Encoding.UTF8.GetBytes("yc1|payload|" + itemId);

    public static byte[] Thumb(string itemId) => Encoding.UTF8.GetBytes("yc1|thumb|" + itemId);

    public static byte[] Chunk(string itemId, int index, int chunkCount)
    {
        return Encoding.UTF8.GetBytes(
            "yc1|chunk|" + itemId + "|" +
            index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
            chunkCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public static class CryptoBox
{
    public static byte[] DeriveKey(string encryptionPassword, ReadOnlySpan<byte> salt, int iterations = ProtocolConstants.KdfIterations)
    {
        var normalized = encryptionPassword.Normalize(NormalizationForm.FormC);
        var passwordBytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, ProtocolConstants.KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static byte[] ContentHashKey(ReadOnlySpan<byte> key)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ProtocolConstants.ContentHashLabel));
    }

    public static string KeyCheck(ReadOnlySpan<byte> key)
    {
        return Hex.Lower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ProtocolConstants.KeyCheckLabel)));
    }

    public static string ContentHash(ReadOnlySpan<byte> contentHashKey, ReadOnlySpan<byte> content)
    {
        return Hex.Lower(HMACSHA256.HashData(contentHashKey, content));
    }

    public static string Sha256Hex(ReadOnlySpan<byte> content)
    {
        return Hex.Lower(SHA256.HashData(content));
    }

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[ProtocolConstants.NonceLength];
        RandomNumberGenerator.Fill(nonce);
        return SealWithNonce(key, nonce, aad, plaintext);
    }

    public static byte[] SealWithNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        if (key.Length != ProtocolConstants.KeyLength)
        {
            throw new ArgumentException("key must be 32 bytes", nameof(key));
        }
        if (nonce.Length != ProtocolConstants.NonceLength)
        {
            throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
        }
        var output = new byte[plaintext.Length + ProtocolConstants.SealOverhead];
        nonce.CopyTo(output);
        using var gcm = new AesGcm(key, ProtocolConstants.TagLength);
        gcm.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(ProtocolConstants.NonceLength, plaintext.Length),
            output.AsSpan(ProtocolConstants.NonceLength + plaintext.Length, ProtocolConstants.TagLength),
            aad);
        return output;
    }

    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> sealedBox)
    {
        if (key.Length != ProtocolConstants.KeyLength)
        {
            throw new CryptoException("key must be 32 bytes");
        }
        if (sealedBox.Length < ProtocolConstants.SealOverhead)
        {
            throw new CryptoException("sealed box is shorter than 28 bytes");
        }
        var plainLength = sealedBox.Length - ProtocolConstants.SealOverhead;
        var plaintext = new byte[plainLength];
        try
        {
            using var gcm = new AesGcm(key, ProtocolConstants.TagLength);
            gcm.Decrypt(
                sealedBox[..ProtocolConstants.NonceLength],
                sealedBox.Slice(ProtocolConstants.NonceLength, plainLength),
                sealedBox.Slice(ProtocolConstants.NonceLength + plainLength, ProtocolConstants.TagLength),
                plaintext,
                aad);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptoException("authentication failed", ex);
        }
    }
}

public sealed class KeyMaterial : IDisposable
{
    public KeyMaterial(byte[] key)
    {
        if (key.Length != ProtocolConstants.KeyLength)
        {
            throw new ArgumentException("key must be 32 bytes", nameof(key));
        }
        Key = (byte[])key.Clone();
        ContentHashKey = CryptoBox.ContentHashKey(Key);
        KeyCheck = CryptoBox.KeyCheck(Key);
    }

    public byte[] Key { get; }

    public byte[] ContentHashKey { get; }

    public string KeyCheck { get; }

    public string ContentHash(ReadOnlySpan<byte> content) => CryptoBox.ContentHash(ContentHashKey, content);

    public byte[] Seal(byte[] aad, ReadOnlySpan<byte> plaintext) => CryptoBox.Seal(Key, aad, plaintext);

    public byte[] Open(byte[] aad, ReadOnlySpan<byte> sealedBox) => CryptoBox.Open(Key, aad, sealedBox);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Key);
        CryptographicOperations.ZeroMemory(ContentHashKey);
    }
}

public sealed class ContentDigest : IDisposable
{
    private readonly IncrementalHash _hmac;
    private readonly IncrementalHash _sha;

    public ContentDigest(ReadOnlySpan<byte> contentHashKey)
    {
        _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, contentHashKey);
        _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public long Length { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        _hmac.AppendData(data);
        _sha.AppendData(data);
        Length += data.Length;
    }

    public (string ContentHash, string Sha256) Finish()
    {
        return (Hex.Lower(_hmac.GetHashAndReset()), Hex.Lower(_sha.GetHashAndReset()));
    }

    public static (string ContentHash, string Sha256, long Length) Compute(ReadOnlySpan<byte> contentHashKey, Stream stream)
    {
        using var digest = new ContentDigest(contentHashKey);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.Append(buffer.AsSpan(0, read));
        }
        var (hash, sha) = digest.Finish();
        return (hash, sha, digest.Length);
    }

    public void Dispose()
    {
        _hmac.Dispose();
        _sha.Dispose();
    }
}

public static class Hex
{
    public static string Lower(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    public static byte[] Decode(string hex) => Convert.FromHexString(hex);

    public static bool IsLowerHex(string? value, int length)
    {
        if (value == null || value.Length != length)
        {
            return false;
        }
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
}

public static class StrictBase64
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data);

    public static byte[] Decode(string? value)
    {
        if (!TryDecode(value, out var bytes))
        {
            throw new FormatException("invalid base64");
        }
        return bytes;
    }

    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value == null || value.Length % 4 != 0)
        {
            return false;
        }
        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
            if (!ok)
            {
                return false;
            }
        }
        var padStart = value.IndexOf('=');
        if (padStart >= 0 && (padStart < value.Length - 2 || value.AsSpan(padStart).ContainsAnyExcept('=')))
        {
            return false;
        }
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
