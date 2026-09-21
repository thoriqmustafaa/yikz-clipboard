using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class ItemsVectorTests
{
    public static IEnumerable<object[]> ItemNames() => Vectors.Items("items.json", "items").Select(i => new object[] { i.Str("name") });

    [Theory]
    [MemberData(nameof(ItemNames))]
    public void MetaDecryptsAndSerializesByteForByte(string name)
    {
        var item = Vectors.Named("items.json", "items", name);
        using var keys = new KeyMaterial(Vectors.Hex(item.Str("key_hex")));
        var id = item.Str("id");
        Assert.Equal(item.Str("meta_aad_utf8"), Encoding.UTF8.GetString(Aad.Meta(id)));
        var plain = keys.Open(Aad.Meta(id), StrictBase64.Decode(item.Str("meta_sealed_b64")));
        Assert.Equal(item.Str("meta_plaintext_utf8"), Encoding.UTF8.GetString(plain));
        var meta = MetaCodec.Deserialize(plain);
        Assert.Equal(1, meta.V);
        Assert.Equal(plain, MetaCodec.Serialize(meta));
        var resealed = CryptoBox.SealWithNonce(keys.Key, Vectors.Hex(item.Str("meta_nonce_hex")), Aad.Meta(id), MetaCodec.Serialize(meta));
        Assert.Equal(item.Str("meta_sealed_b64"), StrictBase64.Encode(resealed));
        var expected = item.GetProperty("meta");
        Assert.Equal(expected.Str("mime"), meta.Mime);
        Assert.Equal(expected.Str("preview"), meta.Preview);
        Assert.Equal(item.Str("content_sha256"), meta.Sha256);
        if (expected.TryGetProperty("source_app", out var app))
        {
            Assert.Equal(app.GetString(), meta.SourceApp);
        }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    [InlineData("files")]
    public void InlinePayloadDecryptsAndVerifies(string name)
    {
        var item = Vectors.Named("items.json", "items", name);
        using var keys = new KeyMaterial(Vectors.Hex(item.Str("key_hex")));
        var id = item.Str("id");
        Assert.Equal(item.Str("payload_aad_utf8"), Encoding.UTF8.GetString(Aad.Payload(id)));
        var header = JsonSerializer.Deserialize(item.GetProperty("header").GetRawText(), ProtocolJsonContext.Default.ItemHeader)!;
        Assert.Equal(item.Str("payload_sealed_b64"), header.Payload);
        var content = ItemCodec.OpenInlinePayload(header, StrictBase64.Decode(item.Str("payload_sealed_b64")), keys);
        Assert.Equal(item.Str("content_hex"), Convert.ToHexStringLower(content));
        Assert.Equal(item.Str("content_hash"), keys.ContentHash(content));
        Assert.Equal(item.Str("content_sha256"), CryptoBox.Sha256Hex(content));
        var entry = ItemCodec.Decrypt(header, keys);
        Assert.Equal(MetaState.Ok, entry.MetaState);
        ItemCodec.Verify(content, header, entry.Meta!, keys);
        var resealed = CryptoBox.SealWithNonce(keys.Key, Vectors.Hex(item.Str("payload_nonce_hex")), Aad.Payload(id), content);
        Assert.Equal(item.Str("payload_sealed_b64"), StrictBase64.Encode(resealed));
        Assert.Equal(item.Long("size"), content.Length);
        Assert.Null(entry.Header.Payload);
    }

    [Fact]
    public void TextItemContentMatchesUtf8()
    {
        var item = Vectors.Named("items.json", "items", "text");
        Assert.Equal(item.Str("content_utf8"), Encoding.UTF8.GetString(Vectors.Hex(item.Str("content_hex"))));
    }

    [Fact]
    public void ImageThumbnailDecrypts()
    {
        var item = Vectors.Named("items.json", "items", "image");
        using var keys = new KeyMaterial(Vectors.Hex(item.Str("key_hex")));
        var id = item.Str("id");
        Assert.Equal(item.Str("thumb_aad_utf8"), Encoding.UTF8.GetString(Aad.Thumb(id)));
        var thumb = keys.Open(Aad.Thumb(id), StrictBase64.Decode(item.Str("thumb_sealed_b64")));
        Assert.Equal(item.Str("thumb_plaintext_hex"), Convert.ToHexStringLower(thumb));
        Assert.Equal(0xFF, thumb[0]);
        Assert.Equal(0xD8, thumb[1]);
        var resealed = CryptoBox.SealWithNonce(keys.Key, Vectors.Hex(item.Str("thumb_nonce_hex")), Aad.Thumb(id), thumb);
        Assert.Equal(item.Str("thumb_sealed_b64"), StrictBase64.Encode(resealed));
        Assert.True(Imaging.PngInfo.TryGetSize(Vectors.Hex(item.Str("content_hex")), out var w, out var h));
        var meta = item.GetProperty("meta").GetProperty("image");
        Assert.Equal(meta.Long("width"), w);
        Assert.Equal(meta.Long("height"), h);
    }

    [Fact]
    public void FilesItemArchiveMatches()
    {
        var item = Vectors.Named("items.json", "items", "files");
        var content = Vectors.Hex(item.Str("content_hex"));
        var archive = Vectors.Named("files_archive.json", "cases", "three_files");
        Assert.Equal(archive.Str("archive_hex"), Convert.ToHexStringLower(content));
        var entries = Ycf1.Unpack(content);
        var meta = item.GetProperty("meta");
        Assert.Equal(meta.Str("preview"), TextPreview.Make(string.Join("\n", entries.Select(e => e.Name))));
        var files = meta.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(files.Count, entries.Count);
        for (var i = 0; i < files.Count; i++)
        {
            Assert.Equal(files[i].Str("name"), entries[i].Name);
            Assert.Equal(files[i].Long("size"), entries[i].Data.Length);
        }
    }

    [Fact]
    public void ChunkedItemChunksSealAndOpen()
    {
        var item = Vectors.Named("items.json", "items", "large");
        using var keys = new KeyMaterial(Vectors.Hex(item.Str("key_hex")));
        var id = item.Str("id");
        var content = Vectors.LargeContent();
        Assert.Equal(item.Long("size"), content.Length);
        Assert.Equal(item.Str("content_sha256"), CryptoBox.Sha256Hex(content));
        Assert.Equal(item.Str("content_hash"), keys.ContentHash(content));
        var count = (int)item.Long("chunk_count");
        Assert.Equal(count, Chunking.ChunkCount(content.Length));
        long sealedTotal = 0;
        foreach (var c in item.GetProperty("chunks").EnumerateArray())
        {
            var index = (int)c.Long("index");
            var aad = Aad.Chunk(id, index, count);
            Assert.Equal(c.Str("aad_utf8"), Encoding.UTF8.GetString(aad));
            var length = Chunking.ChunkPlainLength(content.Length, index);
            Assert.Equal(c.Long("plaintext_size"), length);
            var plain = content.AsSpan((int)Chunking.ChunkOffset(index), length);
            Assert.Equal(c.Str("plaintext_sha256"), Convert.ToHexStringLower(SHA256.HashData(plain)));
            var sealedChunk = CryptoBox.SealWithNonce(keys.Key, Vectors.Hex(c.Str("nonce_hex")), aad, plain);
            Assert.Equal(c.Long("sealed_size"), sealedChunk.Length);
            Assert.Equal(c.Str("sealed_sha256"), Convert.ToHexStringLower(SHA256.HashData(sealedChunk)));
            Assert.Equal(c.Str("sealed_prefix_hex"), Convert.ToHexStringLower(sealedChunk.AsSpan(0, 44)));
            Assert.Equal(c.Str("sealed_suffix_hex"), Convert.ToHexStringLower(sealedChunk.AsSpan(sealedChunk.Length - 32)));
            Assert.Equal(plain.ToArray(), keys.Open(aad, sealedChunk));
            sealedTotal += sealedChunk.Length;
        }
        var metaSealed = StrictBase64.Decode(item.Str("meta_sealed_b64"));
        Assert.Equal(item.Long("stored_bytes"), sealedTotal + metaSealed.Length);
    }

    [Theory]
    [MemberData(nameof(ItemNames))]
    public void StoredBytesMatchesSealedSizes(string name)
    {
        var item = Vectors.Named("items.json", "items", name);
        if (item.Long("chunk_count") > 0)
        {
            return;
        }
        long total = StrictBase64.Decode(item.Str("meta_sealed_b64")).Length + StrictBase64.Decode(item.Str("payload_sealed_b64")).Length;
        if (item.Has("thumb_sealed_b64"))
        {
            total += StrictBase64.Decode(item.Str("thumb_sealed_b64")).Length;
        }
        Assert.Equal(item.Long("stored_bytes"), total);
        var header = JsonSerializer.Deserialize(item.GetProperty("header").GetRawText(), ProtocolJsonContext.Default.ItemHeader)!;
        Assert.Equal(total, header.StoredBytes);
    }

    [Fact]
    public void WrongKeyMarksMetaCorrupt()
    {
        var item = Vectors.Named("items.json", "items", "text");
        var header = JsonSerializer.Deserialize(item.GetProperty("header").GetRawText(), ProtocolJsonContext.Default.ItemHeader)!;
        var badKey = Vectors.Hex(item.Str("key_hex"));
        badKey[0] ^= 1;
        using var keys = new KeyMaterial(badKey);
        var entry = ItemCodec.Decrypt(header, keys);
        Assert.Equal(MetaState.Corrupt, entry.MetaState);
        Assert.Equal("Unreadable item", entry.Title);
    }

    [Fact]
    public void UnsupportedMetaVersionIsFlagged()
    {
        using var keys = new KeyMaterial(Vectors.Hex(Vectors.Named("items.json", "items", "text").Str("key_hex")));
        var id = "01a0c368-81e2-7460-8f15-1684608b9fb9";
        var sealedMeta = keys.Seal(Aad.Meta(id), """{"v":2,"mime":"x","preview":"","sha256":""}"""u8);
        var header = new ItemHeader { Id = id, Kind = "text", Size = 1, Meta = StrictBase64.Encode(sealedMeta), ContentHash = new string('0', 64) };
        var entry = ItemCodec.Decrypt(header, keys);
        Assert.Equal(MetaState.Unsupported, entry.MetaState);
        Assert.Equal(EntryKind.Unknown, entry.Kind);
    }

    [Fact]
    public void EntryPresentation()
    {
        var item = Vectors.Named("items.json", "items", "files");
        using var keys = new KeyMaterial(Vectors.Hex(item.Str("key_hex")));
        var header = JsonSerializer.Deserialize(item.GetProperty("header").GetRawText(), ProtocolJsonContext.Default.ItemHeader)!;
        var entry = ItemCodec.Decrypt(header, keys);
        Assert.Equal(EntryKind.Files, entry.Kind);
        Assert.Equal("hello.txt and 2 more", entry.Title);
        Assert.Contains("donn\u00e9es \u00e9.bin", entry.SearchText);
        Assert.Equal("Files", entry.KindLabel);
        Assert.Equal("1.5 KB", HistoryEntry.FormatSize(1536));
        Assert.Equal("43 bytes", HistoryEntry.FormatSize(43));
    }
}
