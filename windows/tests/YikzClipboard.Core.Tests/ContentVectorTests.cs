using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class ContentVectorTests
{
    [Fact]
    public void UuidV7GenerateMatchesVectors()
    {
        var v = Vectors.Load("uuidv7.json");
        Assert.Equal(UuidV7.Pattern, v.Str("regex"));
        foreach (var g in v.GetProperty("generate").EnumerateArray())
        {
            var id = UuidV7.Create(g.Long("unix_ms"), Vectors.Hex(g.Str("random_hex")));
            Assert.Equal(g.Str("expected"), id);
        }
    }

    [Fact]
    public void UuidV7ValidationMatchesVectors()
    {
        var v = Vectors.Load("uuidv7.json");
        foreach (var s in v.GetProperty("valid").EnumerateArray())
        {
            Assert.True(UuidV7.IsValid(s.GetString()), s.GetString());
        }
        foreach (var s in v.GetProperty("invalid").EnumerateArray())
        {
            Assert.False(UuidV7.IsValid(s.Str("value")), s.Str("reason"));
        }
    }

    [Fact]
    public void UuidV7NewIsValidAndCarriesTime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = UuidV7.New();
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(UuidV7.IsValid(id));
        var ms = Convert.ToInt64(id.Replace("-", "")[..12], 16);
        Assert.InRange(ms, before, after);
        Assert.NotEqual(id, UuidV7.New());
    }

    [Fact]
    public void TokenVectors()
    {
        var v = Vectors.Load("token.json");
        var regex = new Regex(v.Str("regex"));
        foreach (var c in v.GetProperty("cases").EnumerateArray())
        {
            var token = DeviceToken.FromRandom(Vectors.Hex(c.Str("random_hex")));
            Assert.Equal(c.Str("token"), token);
            Assert.Equal(46, token.Length);
            Assert.Matches(regex, token);
            Assert.True(DeviceToken.IsValid(token));
            Assert.Equal(c.Str("token_hash"), DeviceToken.Hash(token));
        }
        Assert.False(DeviceToken.IsValid("yc_short"));
    }

    [Fact]
    public void Ycf1ThreeFilesPackAndUnpack()
    {
        var c = Vectors.Named("files_archive.json", "cases", "three_files");
        var entries = c.GetProperty("files").EnumerateArray().Select(f =>
        {
            Assert.Equal(f.Str("name_utf8_hex"), Convert.ToHexStringLower(Encoding.UTF8.GetBytes(f.Str("name"))));
            return new Ycf1Entry(f.Str("name"), Vectors.Hex(f.Str("data_hex")));
        }).ToList();
        var archive = Ycf1.Pack(entries);
        Assert.Equal(c.Str("archive_hex"), Convert.ToHexStringLower(archive));
        Assert.Equal(c.Long("archive_size"), archive.Length);
        Assert.Equal(c.Str("archive_sha256"), Convert.ToHexStringLower(SHA256.HashData(archive)));
        Assert.Equal(archive.Length, Ycf1.ArchiveSize(entries.Select(e => (e.Name, (long)e.Data.Length))));
        var back = Ycf1.Unpack(archive);
        Assert.Equal(entries.Count, back.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Name, back[i].Name);
            Assert.Equal(entries[i].Data, back[i].Data);
        }
    }

    [Fact]
    public void Ycf1LargeFileHeaderAndHash()
    {
        var c = Vectors.Named("files_archive.json", "cases", "single_large_file_header");
        var file = c.GetProperty("files")[0];
        var size = file.Long("size");
        var data = new byte[size];
        for (long i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 251);
        }
        using var ms = new MemoryStream();
        Ycf1.Write(ms, new[] { new Ycf1Source(file.Str("name"), size, () => new MemoryStream(data)) });
        var archive = ms.ToArray();
        var prefix = Vectors.Hex(c.Str("archive_prefix_hex"));
        Assert.Equal(prefix, archive.AsSpan(0, prefix.Length).ToArray());
        Assert.Equal(c.Long("archive_size"), archive.Length);
        Assert.Equal(c.Str("archive_sha256"), Convert.ToHexStringLower(SHA256.HashData(archive)));
        Assert.Equal(archive, Vectors.LargeContent());
        using var read = new MemoryStream(archive);
        var index = Ycf1.ReadIndex(read, archive.Length);
        Assert.Single(index);
        Assert.Equal(("pattern.bin", size), index[0]);
    }

    [Fact]
    public void Ycf1InvalidNamesAreRejected()
    {
        foreach (var n in Vectors.Items("files_archive.json", "invalid_names"))
        {
            Assert.NotNull(Ycf1.ValidateName(n.Str("name")));
        }
        Assert.Null(Ycf1.ValidateName("donn\u00e9es \u00e9.bin"));
        Assert.NotNull(Ycf1.ValidateName("e\u0301.txt"));
        Assert.NotNull(Ycf1.ValidateName("tab\there"));
        Assert.NotNull(Ycf1.ValidateName("del\u007f"));
    }

    [Fact]
    public void Ycf1ReaderRejectsMalformedArchives()
    {
        var good = Ycf1.Pack(new[] { new Ycf1Entry("a.txt", "hi"u8.ToArray()) });
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(good.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(good[..^1]));
        var zero = new byte[] { 0x59, 0x43, 0x46, 0x31, 0, 0, 0, 0 };
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(zero));
        var badMagic = (byte[])good.Clone();
        badMagic[0] = 0x58;
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(badMagic));
        var tooMany = new byte[] { 0x59, 0x43, 0x46, 0x31, 0, 0, 0x03, 0xE9 };
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(tooMany));
        var slash = (byte[])good.Clone();
        slash[12] = (byte)'/';
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(slash));
        var dup = BuildRaw(("x", "1"u8.ToArray()), ("x", "2"u8.ToArray()));
        Assert.Throws<Ycf1Exception>(() => Ycf1.Unpack(dup));
        Assert.Throws<Ycf1Exception>(() => Ycf1.Pack(new[] { new Ycf1Entry("x", new byte[1]), new Ycf1Entry("x", new byte[1]) }));
    }

    private static byte[] BuildRaw(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        ms.Write("YCF1"u8);
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)files.Length);
        ms.Write(b, 0, 4);
        foreach (var (name, data) in files)
        {
            var nb = Encoding.UTF8.GetBytes(name);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)nb.Length);
            ms.Write(b, 0, 4);
            ms.Write(nb);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)data.Length);
            ms.Write(b, 0, 8);
            ms.Write(data);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Ycf1ExtractResolvesCaseInsensitiveCollisionsAndReservedNames()
    {
        var archive = Ycf1.Pack(new[]
        {
            new Ycf1Entry("Readme.txt", "1"u8.ToArray()),
            new Ycf1Entry("README.txt", "2"u8.ToArray()),
            new Ycf1Entry("con.txt", "3"u8.ToArray()),
            new Ycf1Entry("what?.txt", "4"u8.ToArray()),
        });
        var dir = Path.Combine(Path.GetTempPath(), "ycf1-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ms = new MemoryStream(archive);
            var paths = Ycf1.ExtractTo(ms, archive.Length, dir);
            var names = paths.Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "Readme.txt", "README (2).txt", "_con.txt", "what_.txt" }, names);
            Assert.Equal("2", File.ReadAllText(paths[1]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OutgoingNamesAreMadeUniqueAndValid()
    {
        var names = Ycf1.MakeUniqueNames(new[] { "a.txt", "a.txt", "e\u0301.txt", new string('x', 300) + ".log" });
        Assert.Equal("a.txt", names[0]);
        Assert.Equal("a (2).txt", names[1]);
        Assert.Equal("\u00e9.txt", names[2]);
        Assert.True(Encoding.UTF8.GetByteCount(names[3]) <= 255);
        Assert.EndsWith(".log", names[3]);
        Assert.All(names, n => Assert.Null(Ycf1.ValidateName(n)));
    }

    [Fact]
    public void ChunkingMatchesVectors()
    {
        var v = Vectors.Load("chunking.json");
        Assert.Equal(ProtocolConstants.InlineMaxBytes, v.Long("inline_max_bytes"));
        Assert.Equal(ProtocolConstants.ChunkSizeBytes, v.Long("chunk_size_bytes"));
        Assert.Equal(ProtocolConstants.SealOverhead, v.Long("seal_overhead_bytes"));
        foreach (var c in v.GetProperty("cases").EnumerateArray())
        {
            var size = c.Long("size");
            Assert.Equal(c.GetProperty("inline").GetBoolean(), Chunking.IsInline(size));
            Assert.Equal(c.Long("chunk_count"), Chunking.ChunkCount(size));
            if (Chunking.IsInline(size))
            {
                Assert.Equal(c.Long("payload_sealed_length"), size + ProtocolConstants.SealOverhead);
                continue;
            }
            var last = (int)c.Long("last_chunk_index");
            Assert.Equal(ProtocolConstants.ChunkSizeBytes, c.Long("full_chunk_plaintext_size"));
            Assert.Equal(c.Long("full_chunk_sealed_length"), Chunking.SealedLength((int)ProtocolConstants.ChunkSizeBytes));
            Assert.Equal(c.Long("last_chunk_plaintext_size"), Chunking.ChunkPlainLength(size, last));
            Assert.Equal(c.Long("last_chunk_sealed_length"), Chunking.SealedLength(Chunking.ChunkPlainLength(size, last)));
            Assert.Equal(c.Long("total_chunk_sealed_bytes"), Chunking.TotalSealedChunkBytes(size));
            if (last > 0)
            {
                Assert.Equal(4194304, Chunking.ChunkPlainLength(size, 0));
            }
        }
    }

    [Fact]
    public void PreviewMatchesVectors()
    {
        var v = Vectors.Load("preview.json");
        Assert.Equal(ProtocolConstants.PreviewMaxCodePoints, (int)v.Long("max_code_points"));
        foreach (var c in v.GetProperty("cases").EnumerateArray())
        {
            var input = c.Str("input");
            Assert.Equal(c.Long("input_code_points"), TextPreview.CountCodePoints(input));
            Assert.Equal(c.Long("input_utf16_units"), input.Length);
            var preview = TextPreview.Make(input);
            Assert.Equal(c.Str("expected"), preview);
            Assert.Equal(c.Long("expected_code_points"), TextPreview.CountCodePoints(preview));
        }
    }

    [Theory]
    [InlineData("https://yikz.dev/", true)]
    [InlineData("  http://example.com/a?b=c  ", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("see https://yikz.dev/", false)]
    [InlineData("https://a b", false)]
    [InlineData("plain text", false)]
    public void LinkDetection(string text, bool isLink)
    {
        Assert.Equal(isLink, TextPreview.IsLink(text));
    }
}
