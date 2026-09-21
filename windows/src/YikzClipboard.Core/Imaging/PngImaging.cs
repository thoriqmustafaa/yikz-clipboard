using System.Buffers.Binary;
using System.IO.Compression;

namespace YikzClipboard.Core.Imaging;

public static class PngInfo
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static bool TryGetSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24 || !IsPng(data))
        {
            return false;
        }
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }
        var w = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }
        width = (int)w;
        height = (int)h;
        return true;
    }
}

public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> bgraTopDown, bool keepAlpha)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (bgraTopDown.Length < (long)width * height * 4)
        {
            throw new ArgumentException("pixel buffer too small", nameof(bgraTopDown));
        }
        var channels = keepAlpha ? 4 : 3;
        var rowBytes = width * channels;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
        {
            var row = new byte[rowBytes + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                var src = bgraTopDown.Slice(y * width * 4, width * 4);
                var o = 1;
                for (var x = 0; x < width; x++)
                {
                    var p = x * 4;
                    row[o++] = src[p + 2];
                    row[o++] = src[p + 1];
                    row[o++] = src[p];
                    if (keepAlpha)
                    {
                        row[o++] = src[p + 3];
                    }
                }
                z.Write(row, 0, row.Length);
            }
        }
        var idat = raw.ToArray();
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(keepAlpha ? 6 : 2);
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, idat);
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFFu);
        s.Write(len);
    }

    internal static uint Crc32(ReadOnlySpan<byte> data) => UpdateCrc(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}

public sealed class DecodedBitmap
{
    public DecodedBitmap(int width, int height, byte[] bgra, bool hasAlpha)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
        HasAlpha = hasAlpha;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public bool HasAlpha { get; }
}

public static class Dib
{
    private const int BiRgb = 0;
    private const int BiBitfields = 3;
    private const int BiAlphaBitfields = 6;

    public static byte[] ToPng(ReadOnlySpan<byte> dib)
    {
        var bmp = Decode(dib);
        return PngWriter.EncodeBgra(bmp.Width, bmp.Height, bmp.Bgra, bmp.HasAlpha);
    }

    public static DecodedBitmap Decode(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            throw new FormatException("DIB too short");
        }
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            throw new FormatException("unsupported DIB header");
        }
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var clrUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        if (width <= 0 || rawHeight == 0 || width > 32768 || Math.Abs(rawHeight) > 32768)
        {
            throw new FormatException("invalid DIB dimensions");
        }
        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        var offset = headerSize;
        if (compression == BiBitfields || compression == BiAlphaBitfields)
        {
            if (headerSize == 40)
            {
                var maskCount = compression == BiAlphaBitfields ? 4 : 3;
                if (dib.Length < 40 + maskCount * 4)
                {
                    throw new FormatException("DIB masks missing");
                }
                rMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                gMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                bMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
                if (maskCount == 4)
                {
                    aMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
                }
                offset += maskCount * 4;
            }
            else
            {
                rMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                gMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                bMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
                aMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            }
        }
        else if (compression != BiRgb)
        {
            throw new FormatException("compressed DIB is not supported");
        }
        else if (headerSize >= 56 && bitCount == 32)
        {
            aMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
        }
        if (bitCount == 16 && compression == BiRgb)
        {
            rMask = 0x7C00;
            gMask = 0x03E0;
            bMask = 0x001F;
        }
        var paletteCount = 0;
        if (bitCount <= 8)
        {
            paletteCount = clrUsed > 0 ? clrUsed : 1 << bitCount;
        }
        var palette = dib.Slice(offset, Math.Min(paletteCount * 4, Math.Max(0, dib.Length - offset)));
        offset += paletteCount * 4;
        var stride = ((width * bitCount + 31) / 32) * 4;
        if ((long)offset + (long)stride * height > dib.Length)
        {
            throw new FormatException("DIB pixel data truncated");
        }
        var pixels = dib[offset..];
        var output = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var srcRow = topDown ? y : height - 1 - y;
            var row = pixels.Slice(srcRow * stride, stride);
            var dst = output.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (bitCount)
                {
                    case 32:
                        {
                            var v = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
                            if (compression == BiRgb)
                            {
                                b = row[x * 4];
                                g = row[x * 4 + 1];
                                r = row[x * 4 + 2];
                                a = row[x * 4 + 3];
                            }
                            else
                            {
                                r = Extract(v, rMask);
                                g = Extract(v, gMask);
                                b = Extract(v, bMask);
                                a = aMask != 0 ? Extract(v, aMask) : (byte)255;
                            }
                            break;
                        }
                    case 24:
                        b = row[x * 3];
                        g = row[x * 3 + 1];
                        r = row[x * 3 + 2];
                        break;
                    case 16:
                        {
                            var v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                            r = Extract(v, rMask);
                            g = Extract(v, gMask);
                            b = Extract(v, bMask);
                            break;
                        }
                    case 8:
                    case 4:
                    case 1:
                        {
                            int index;
                            if (bitCount == 8)
                            {
                                index = row[x];
                            }
                            else if (bitCount == 4)
                            {
                                var v = row[x / 2];
                                index = (x % 2 == 0) ? v >> 4 : v & 0x0f;
                            }
                            else
                            {
                                var v = row[x / 8];
                                index = (v >> (7 - x % 8)) & 1;
                            }
                            if (index * 4 + 3 < palette.Length)
                            {
                                b = palette[index * 4];
                                g = palette[index * 4 + 1];
                                r = palette[index * 4 + 2];
                            }
                            else
                            {
                                r = g = b = 0;
                            }
                            break;
                        }
                    default:
                        throw new FormatException("unsupported DIB bit depth " + bitCount);
                }
                dst[x * 4] = b;
                dst[x * 4 + 1] = g;
                dst[x * 4 + 2] = r;
                dst[x * 4 + 3] = a;
                if (a != 0)
                {
                    anyAlpha = true;
                }
            }
        }
        var hasAlpha = bitCount == 32 && anyAlpha && (compression == BiRgb || aMask != 0);
        if (bitCount == 32 && hasAlpha)
        {
            var allOpaque = true;
            for (var i = 3; i < output.Length; i += 4)
            {
                if (output[i] != 255)
                {
                    allOpaque = false;
                    break;
                }
            }
            if (allOpaque)
            {
                hasAlpha = false;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < output.Length; i += 4)
            {
                output[i] = 255;
            }
        }
        return new DecodedBitmap(width, height, output, hasAlpha);
    }

    public static byte[] FromBgra(int width, int height, ReadOnlySpan<byte> bgraTopDown)
    {
        var stride = width * 4;
        var dib = new byte[40 + stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), BiRgb);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), stride * height);
        for (var y = 0; y < height; y++)
        {
            var src = bgraTopDown.Slice(y * stride, stride);
            var dst = dib.AsSpan(40 + (height - 1 - y) * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var p = x * 4;
                var a = src[p + 3];
                dst[p] = Blend(src[p], a);
                dst[p + 1] = Blend(src[p + 1], a);
                dst[p + 2] = Blend(src[p + 2], a);
                dst[p + 3] = 255;
            }
        }
        return dib;
    }

    private static byte Blend(byte c, byte a) => (byte)((c * a + 255 * (255 - a) + 127) / 255);

    private static byte Extract(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var bits = System.Numerics.BitOperations.PopCount(mask);
        var v = (value & mask) >> shift;
        if (bits >= 8)
        {
            return (byte)(v >> (bits - 8));
        }
        var max = (1u << bits) - 1;
        return (byte)((v * 255 + max / 2) / max);
    }
}
