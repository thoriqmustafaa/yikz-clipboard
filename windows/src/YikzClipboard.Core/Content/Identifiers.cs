using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Content;

public static partial class UuidV7
{
    public const string Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        return Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), random);
    }

    public static string Create(long unixMs, ReadOnlySpan<byte> random10)
    {
        if (random10.Length != 10)
        {
            throw new ArgumentException("random part must be 10 bytes", nameof(random10));
        }
        Span<byte> b = stackalloc byte[16];
        b[0] = (byte)(unixMs >> 40);
        b[1] = (byte)(unixMs >> 32);
        b[2] = (byte)(unixMs >> 24);
        b[3] = (byte)(unixMs >> 16);
        b[4] = (byte)(unixMs >> 8);
        b[5] = (byte)unixMs;
        random10.CopyTo(b[6..]);
        b[6] = (byte)(0x70 | (b[6] & 0x0f));
        b[8] = (byte)(0x80 | (b[8] & 0x3f));
        var hex = Convert.ToHexStringLower(b);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static bool IsValid(string? value) => value != null && IdRegex().IsMatch(value);
}

public static partial class DeviceToken
{
    [GeneratedRegex("^yc_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static bool IsValid(string? value) => value != null && TokenRegex().IsMatch(value);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    public static string FromRandom(ReadOnlySpan<byte> random32)
    {
        return "yc_" + Convert.ToBase64String(random32).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public static class Chunking
{
    public static bool IsInline(long size) => size <= ProtocolConstants.InlineMaxBytes;

    public static int ChunkCount(long size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        if (IsInline(size))
        {
            return 0;
        }
        return (int)((size + ProtocolConstants.ChunkSizeBytes - 1) / ProtocolConstants.ChunkSizeBytes);
    }

    public static long ChunkOffset(int index) => index * ProtocolConstants.ChunkSizeBytes;

    public static int ChunkPlainLength(long size, int index)
    {
        var count = (int)((size + ProtocolConstants.ChunkSizeBytes - 1) / ProtocolConstants.ChunkSizeBytes);
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        var start = ChunkOffset(index);
        return (int)Math.Min(ProtocolConstants.ChunkSizeBytes, size - start);
    }

    public static int SealedLength(int plainLength) => plainLength + ProtocolConstants.SealOverhead;

    public static long TotalSealedChunkBytes(long size)
    {
        var count = (size + ProtocolConstants.ChunkSizeBytes - 1) / ProtocolConstants.ChunkSizeBytes;
        return size + count * ProtocolConstants.SealOverhead;
    }
}

public static partial class TextPreview
{
    public static string Make(string text, int maxCodePoints = ProtocolConstants.PreviewMaxCodePoints)
    {
        var count = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (count == maxCodePoints)
            {
                return text[..i];
            }
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i += 2;
            }
            else
            {
                i += 1;
            }
            count++;
        }
        return text;
    }

    public static int CountCodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    [GeneratedRegex(@"^(https?|ftp)://[^\s/$.?#][^\s]*$|^mailto:[^\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    public static bool IsLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var trimmed = text.Trim();
        if (trimmed.Length > 2048)
        {
            return false;
        }
        return LinkRegex().IsMatch(trimmed);
    }

    public static string FirstLine(string text, int max = 200)
    {
        var span = text.AsSpan().TrimStart();
        var nl = span.IndexOfAny('\r', '\n');
        if (nl >= 0)
        {
            span = span[..nl];
        }
        var line = span.ToString().Trim();
        return line.Length > max ? line[..max] : line;
    }
}
