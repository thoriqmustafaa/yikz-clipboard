using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace YikzClipboard.Core.Updates;

public enum VerificationResult
{
    Valid,
    SizeMismatch,
    HashMismatch,
    BadSignature,
}

public static class ReleaseSignature
{
    public const string ReleasePublicKeyBase64 = "2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk=";

    public static byte[] ReleasePublicKey => Convert.FromBase64String(ReleasePublicKeyBase64);

    public static bool VerifyDigest(ReadOnlySpan<byte> digest, string? signatureBase64, ReadOnlySpan<byte> publicKey)
    {
        if (digest.Length != 32 || publicKey.Length != 32 || string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64)
        {
            return false;
        }
        try
        {
            var key = new Ed25519PublicKeyParameters(publicKey.ToArray(), 0);
            var signer = new Ed25519Signer();
            signer.Init(false, key);
            var message = digest.ToArray();
            signer.BlockUpdate(message, 0, message.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsValidSha256Hex(string? hex) => hex != null && hex.Length == 64 && hex.All(Uri.IsHexDigit);

    public static VerificationResult VerifyDigestAgainst(byte[] digest, string expectedSha256Hex, string signatureBase64, ReadOnlySpan<byte> publicKey)
    {
        if (!IsValidSha256Hex(expectedSha256Hex))
        {
            return VerificationResult.HashMismatch;
        }
        var expected = Convert.FromHexString(expectedSha256Hex);
        if (!CryptographicOperations.FixedTimeEquals(digest, expected))
        {
            return VerificationResult.HashMismatch;
        }
        return VerifyDigest(digest, signatureBase64, publicKey) ? VerificationResult.Valid : VerificationResult.BadSignature;
    }

    public static VerificationResult VerifyBytes(ReadOnlySpan<byte> data, string expectedSha256Hex, string signatureBase64, ReadOnlySpan<byte> publicKey)
    {
        return VerifyDigestAgainst(SHA256.HashData(data), expectedSha256Hex, signatureBase64, publicKey);
    }

    public static VerificationResult VerifyFile(string path, long expectedSize, string expectedSha256Hex, string signatureBase64, ReadOnlySpan<byte> publicKey)
    {
        byte[] digest;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            if (expectedSize > 0 && stream.Length != expectedSize)
            {
                return VerificationResult.SizeMismatch;
            }
            digest = SHA256.HashData(stream);
        }
        return VerifyDigestAgainst(digest, expectedSha256Hex, signatureBase64, publicKey);
    }
}
