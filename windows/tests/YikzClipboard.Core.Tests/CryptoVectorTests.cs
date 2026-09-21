using System.Text;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class CryptoVectorTests
{
    [Theory]
    [InlineData("fast")]
    [InlineData("unicode_nfd_input")]
    public void KdfFastCasesMatch(string name)
    {
        var c = Vectors.Named("kdf.json", "cases", name);
        var input = c.Str("password_input");
        Assert.Equal(c.Str("password_normalized"), input.Normalize(NormalizationForm.FormC));
        Assert.Equal(c.Str("password_utf8_hex"), Convert.ToHexStringLower(Encoding.UTF8.GetBytes(input.Normalize(NormalizationForm.FormC))));
        var salt = Convert.FromBase64String(c.Str("salt_b64"));
        Assert.Equal(c.Str("salt_hex"), Convert.ToHexStringLower(salt));
        var key = CryptoBox.DeriveKey(input, salt, (int)c.Long("iterations"));
        Assert.Equal(c.Str("key_hex"), Convert.ToHexStringLower(key));
    }

    [Fact]
    public void KdfNfdInputIsActuallyDecomposed()
    {
        var c = Vectors.Named("kdf.json", "cases", "unicode_nfd_input");
        var input = c.Str("password_input");
        Assert.NotEqual(input, c.Str("password_normalized"));
    }

    [Fact]
    public void KdfPrimaryWith600000Iterations()
    {
        var c = Vectors.Named("kdf.json", "cases", "primary");
        Assert.Equal(600000, c.Long("iterations"));
        var key = CryptoBox.DeriveKey(c.Str("password_input"), Convert.FromBase64String(c.Str("salt_b64")));
        Assert.Equal(c.Str("key_hex"), Convert.ToHexStringLower(key));
    }

    [Fact]
    public void KdfMetadataMatchesConstants()
    {
        var kdf = Vectors.Load("kdf.json");
        Assert.Equal(Protocol.ProtocolConstants.KdfAlgorithm, kdf.Str("algorithm"));
        Assert.Equal(Protocol.ProtocolConstants.KeyLength, (int)kdf.Long("key_length"));
    }

    [Fact]
    public void SubkeysAndKeyCheckMatch()
    {
        var keys = Vectors.Load("keys.json");
        Assert.Equal(Protocol.ProtocolConstants.ContentHashLabel, keys.Str("content_hash_label_utf8"));
        Assert.Equal(Protocol.ProtocolConstants.KeyCheckLabel, keys.Str("key_check_label_utf8"));
        foreach (var k in keys.GetProperty("keys").EnumerateArray())
        {
            var key = Vectors.Hex(k.Str("key_hex"));
            using var material = new KeyMaterial(key);
            Assert.Equal(k.Str("content_hash_key_hex"), Convert.ToHexStringLower(material.ContentHashKey));
            Assert.Equal(k.Str("key_check"), material.KeyCheck);
            Assert.Equal(k.Str("key_check"), CryptoBox.KeyCheck(key));
            foreach (var s in k.GetProperty("content_hash_samples").EnumerateArray())
            {
                var content = Vectors.Hex(s.Str("content_hex"));
                Assert.Equal(s.Str("content_utf8"), Encoding.UTF8.GetString(content));
                Assert.Equal(s.Str("content_hash"), material.ContentHash(content));
                Assert.Equal(s.Str("sha256"), CryptoBox.Sha256Hex(content));
                using var digest = new ContentDigest(material.ContentHashKey);
                digest.Append(content.AsSpan(0, content.Length / 2));
                digest.Append(content.AsSpan(content.Length / 2));
                var (hash, sha) = digest.Finish();
                Assert.Equal(s.Str("content_hash"), hash);
                Assert.Equal(s.Str("sha256"), sha);
            }
        }
    }

    [Fact]
    public void AadTemplatesMatch()
    {
        var formats = Vectors.Load("aead.json").GetProperty("aad_formats");
        Assert.Equal("yc1|meta|<item_id>", formats.Str("meta"));
        Assert.Equal("yc1|payload|<item_id>", formats.Str("payload"));
        Assert.Equal("yc1|thumb|<item_id>", formats.Str("thumb"));
        Assert.Equal("yc1|chunk|<item_id>|<index>|<chunk_count>", formats.Str("chunk"));
        Assert.Equal("yc1|chunk|01a0c36d-10e0-7a56-86e3-483e2f79d9c5|2|3", Encoding.UTF8.GetString(Aad.Chunk("01a0c36d-10e0-7a56-86e3-483e2f79d9c5", 2, 3)));
    }

    public static IEnumerable<object[]> PositiveNames() =>
        Vectors.Items("aead.json", "positive").Select(p => new object[] { p.Str("name") });

    public static IEnumerable<object[]> NegativeNames() =>
        Vectors.Items("aead.json", "negative").Select(p => new object[] { p.Str("name") });

    private static byte[] BuildAad(string purpose, string aadUtf8)
    {
        var parts = aadUtf8.Split('|');
        Assert.Equal("yc1", parts[0]);
        Assert.Equal(purpose, parts[1]);
        return purpose switch
        {
            "meta" => Aad.Meta(parts[2]),
            "payload" => Aad.Payload(parts[2]),
            "thumb" => Aad.Thumb(parts[2]),
            "chunk" => Aad.Chunk(parts[2], int.Parse(parts[3]), int.Parse(parts[4])),
            _ => throw new InvalidOperationException(purpose),
        };
    }

    [Theory]
    [MemberData(nameof(PositiveNames))]
    public void AeadPositiveSealAndOpen(string name)
    {
        var p = Vectors.Named("aead.json", "positive", name);
        var key = Vectors.Hex(p.Str("key_hex"));
        var nonce = Vectors.Hex(p.Str("nonce_hex"));
        var aad = BuildAad(p.Str("purpose"), p.Str("aad_utf8"));
        Assert.Equal(p.Str("aad_hex"), Convert.ToHexStringLower(aad));
        var plaintext = Vectors.Hex(p.Str("plaintext_hex"));
        if (p.Has("plaintext_utf8"))
        {
            Assert.Equal(p.Str("plaintext_utf8"), Encoding.UTF8.GetString(plaintext));
        }
        var sealedBox = CryptoBox.SealWithNonce(key, nonce, aad, plaintext);
        Assert.Equal(p.Str("sealed_hex"), Convert.ToHexStringLower(sealedBox));
        Assert.Equal(p.Str("sealed_b64"), StrictBase64.Encode(sealedBox));
        Assert.Equal(p.Long("sealed_length"), sealedBox.Length);
        var opened = CryptoBox.Open(key, aad, StrictBase64.Decode(p.Str("sealed_b64")));
        Assert.Equal(plaintext, opened);
    }

    [Theory]
    [MemberData(nameof(NegativeNames))]
    public void AeadNegativeFails(string name)
    {
        var n = Vectors.Named("aead.json", "negative", name);
        Assert.Equal("decrypt_error", n.Str("expect"));
        var key = Vectors.Hex(n.Str("key_hex"));
        var aad = Encoding.UTF8.GetBytes(n.Str("aad_utf8"));
        Assert.Equal(n.Str("aad_hex"), Convert.ToHexStringLower(aad));
        var sealedBox = Vectors.Hex(n.Str("sealed_hex"));
        Assert.Throws<CryptoException>(() => CryptoBox.Open(key, aad, sealedBox));
    }

    [Fact]
    public void RandomNonceSealRoundTrips()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var aad = Aad.Payload("01a0c368-81e2-7460-8f15-1684608b9fb9");
        var a = CryptoBox.Seal(key, aad, "hello"u8);
        var b = CryptoBox.Seal(key, aad, "hello"u8);
        Assert.NotEqual(a.AsSpan(0, 12).ToArray(), b.AsSpan(0, 12).ToArray());
        Assert.Equal(5 + 28, a.Length);
        Assert.Equal("hello"u8.ToArray(), CryptoBox.Open(key, aad, a));
    }

    [Theory]
    [InlineData("QUJD", true)]
    [InlineData("QUI=", true)]
    [InlineData("QQ==", true)]
    [InlineData("QUJ", false)]
    [InlineData("Q=JD", false)]
    [InlineData("QU JD", false)]
    [InlineData("QU-_", false)]
    [InlineData("QUJD\n", false)]
    public void StrictBase64Rules(string value, bool valid)
    {
        Assert.Equal(valid, StrictBase64.TryDecode(value, out _));
    }
}
