using System.Buffers.Binary;
using System.Text;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Content;

public sealed class Ycf1Exception : Exception
{
    public Ycf1Exception(string message) : base(message)
    {
    }
}

public sealed record Ycf1Source(string Name, long Length, Func<Stream> Open);

public sealed record Ycf1Entry(string Name, byte[] Data);

public static class Ycf1
{
    public static ReadOnlySpan<byte> Magic => "YCF1"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "empty";
        }
        if (name == "." || name == "..")
        {
            return "dot name";
        }
        foreach (var c in name)
        {
            if (c == '/' || c == '\\')
            {
                return "contains a path separator";
            }
            if (c < 0x20 || c == 0x7f)
            {
                return "contains a control character";
            }
        }
        if (!name.IsNormalized(NormalizationForm.FormC))
        {
            return "not NFC normalized";
        }
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(name);
        }
        catch (EncoderFallbackException)
        {
            return "not valid Unicode";
        }
        if (byteCount < 1 || byteCount > 255)
        {
            return "longer than 255 UTF-8 bytes";
        }
        return null;
    }

    public static string SanitizeOutgoingName(string rawName)
    {
        var name = rawName.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c == '/' || c == '\\' || c < 0x20 || c == 0x7f)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        var result = sb.ToString();
        if (result.Length == 0 || result == "." || result == "..")
        {
            result = "file";
        }
        while (Encoding.UTF8.GetByteCount(result) > 255)
        {
            var ext = Path.GetExtension(result);
            var stem = Path.GetFileNameWithoutExtension(result);
            if (stem.Length > 1 && ext.Length < 32)
            {
                var cut = stem.Length - 1;
                if (char.IsLowSurrogate(stem[cut]) && cut > 0)
                {
                    cut--;
                }
                result = stem[..cut] + ext;
            }
            else
            {
                var cut = result.Length - 1;
                if (char.IsLowSurrogate(result[cut]) && cut > 0)
                {
                    cut--;
                }
                result = result[..cut];
            }
        }
        return result;
    }

    public static List<string> MakeUniqueNames(IEnumerable<string> rawNames)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in rawNames)
        {
            var name = SanitizeOutgoingName(raw);
            var candidate = name;
            var n = 2;
            while (!used.Add(candidate))
            {
                candidate = SanitizeOutgoingName(Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));
                n++;
            }
            result.Add(candidate);
        }
        return result;
    }

    public static long ArchiveSize(IEnumerable<(string Name, long Length)> files)
    {
        long total = 8;
        foreach (var (name, length) in files)
        {
            total += 4 + Encoding.UTF8.GetByteCount(name) + 8 + length;
        }
        return total;
    }

    public static void Write(Stream output, IReadOnlyList<Ycf1Source> files)
    {
        if (files.Count == 0 || files.Count > ProtocolConstants.MaxFilesPerItem)
        {
            throw new Ycf1Exception("file count must be 1 to 1000");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var reason = ValidateName(f.Name);
            if (reason != null)
            {
                throw new Ycf1Exception("invalid name '" + f.Name + "': " + reason);
            }
            if (!seen.Add(f.Name))
            {
                throw new Ycf1Exception("duplicate name: " + f.Name);
            }
        }
        Span<byte> header = stackalloc byte[8];
        output.Write(Magic);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)files.Count);
        output.Write(header[..4]);
        var buffer = new byte[81920];
        foreach (var f in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(f.Name);
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)nameBytes.Length);
            output.Write(header[..4]);
            output.Write(nameBytes);
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)f.Length);
            output.Write(header);
            using var input = f.Open();
            long remaining = f.Length;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new Ycf1Exception("file changed while reading: " + f.Name);
                }
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    public static byte[] Pack(IReadOnlyList<Ycf1Entry> entries)
    {
        using var ms = new MemoryStream();
        Write(ms, entries.Select(e => new Ycf1Source(e.Name, e.Data.Length, () => new MemoryStream(e.Data, false))).ToList());
        return ms.ToArray();
    }

    public static List<Ycf1Entry> Unpack(byte[] archive)
    {
        var result = new List<Ycf1Entry>();
        using var ms = new MemoryStream(archive, false);
        Read(ms, archive.Length, (name, length, data) =>
        {
            var bytes = new byte[length];
            ReadExactly(data, bytes);
            result.Add(new Ycf1Entry(name, bytes));
        });
        return result;
    }

    public static List<string> ExtractTo(Stream input, long totalLength, string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        Read(input, totalLength, (name, length, data) =>
        {
            var localName = MakeLocalName(name, localNames);
            var path = Path.Combine(directory, localName);
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long remaining = length;
                while (remaining > 0)
                {
                    var read = data.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        throw new Ycf1Exception("archive is truncated");
                    }
                    file.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            paths.Add(path);
        });
        return paths;
    }

    public static List<(string Name, long Length)> ReadIndex(Stream input, long totalLength)
    {
        var list = new List<(string, long)>();
        var buffer = new byte[81920];
        Read(input, totalLength, (name, length, data) =>
        {
            long remaining = length;
            while (remaining > 0)
            {
                var read = data.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new Ycf1Exception("archive is truncated");
                }
                remaining -= read;
            }
            list.Add((name, length));
        });
        return list;
    }

    private delegate void EntryHandler(string name, long length, Stream data);

    private static void Read(Stream input, long totalLength, EntryHandler handler)
    {
        long consumed = 0;
        Span<byte> header = stackalloc byte[8];
        ReadExactly(input, header[..4]);
        consumed += 4;
        if (!header[..4].SequenceEqual(Magic))
        {
            throw new Ycf1Exception("bad magic");
        }
        ReadExactly(input, header[..4]);
        consumed += 4;
        var count = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (count == 0 || count > ProtocolConstants.MaxFilesPerItem)
        {
            throw new Ycf1Exception("file count must be 1 to 1000");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            ReadExactly(input, header[..4]);
            consumed += 4;
            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (nameLength < 1 || nameLength > 255)
            {
                throw new Ycf1Exception("invalid name length");
            }
            var nameBytes = new byte[nameLength];
            ReadExactly(input, nameBytes);
            consumed += nameLength;
            string name;
            try
            {
                name = StrictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                throw new Ycf1Exception("name is not valid UTF-8");
            }
            var reason = ValidateName(name);
            if (reason != null)
            {
                throw new Ycf1Exception("invalid name: " + reason);
            }
            if (!seen.Add(name))
            {
                throw new Ycf1Exception("duplicate name");
            }
            ReadExactly(input, header);
            consumed += 8;
            var dataLength = BinaryPrimitives.ReadUInt64BigEndian(header);
            if (dataLength > (ulong)(totalLength - consumed))
            {
                throw new Ycf1Exception("archive is truncated");
            }
            var bounded = new BoundedReadStream(input, (long)dataLength);
            handler(name, (long)dataLength, bounded);
            bounded.Drain();
            consumed += (long)dataLength;
        }
        if (consumed != totalLength)
        {
            throw new Ycf1Exception("trailing bytes after archive");
        }
        var probe = new byte[1];
        if (input.Read(probe, 0, 1) != 0)
        {
            throw new Ycf1Exception("trailing bytes after archive");
        }
    }

    private static string MakeLocalName(string name, HashSet<string> used)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        }
        var cleaned = sb.ToString().TrimEnd(' ', '.');
        if (cleaned.Length == 0)
        {
            cleaned = "file";
        }
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        var ext = Path.GetExtension(cleaned);
        if (ReservedWindowsNames.Contains(stem))
        {
            stem = "_" + stem;
            cleaned = stem + ext;
        }
        var candidate = cleaned;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = stem + " (" + n + ")" + ext;
            n++;
        }
        return candidate;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0)
            {
                throw new Ycf1Exception("archive is truncated");
            }
            offset += read;
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (read <= 0)
            {
                throw new Ycf1Exception("archive is truncated");
            }
            _remaining -= read;
            return read;
        }

        public void Drain()
        {
            var buffer = new byte[81920];
            while (_remaining > 0)
            {
                if (Read(buffer, 0, buffer.Length) <= 0)
                {
                    break;
                }
            }
        }
    }
}
