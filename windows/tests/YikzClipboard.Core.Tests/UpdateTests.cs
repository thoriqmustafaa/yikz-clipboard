using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Tests.Support;
using YikzClipboard.Core.Updates;

namespace YikzClipboard.Core.Tests;

public class ReleaseSignatureTests
{
    private static JsonVector V => new(Vectors.Load("release_signature.json"));

    private sealed record JsonVector(System.Text.Json.JsonElement E)
    {
        public byte[] File => Convert.FromBase64String(E.Str("file_b64"));
        public byte[] PublicKey => Convert.FromBase64String(E.Str("public_key_b64"));
        public string Sha256 => E.Str("sha256_hex");
        public string Signature => E.Str("signature_b64");
        public byte[] Seed => Vectors.Hex(E.Str("private_key_seed_hex"));
    }

    [Fact]
    public void VectorVerifies()
    {
        var v = V;
        Assert.Equal(v.Sha256, Convert.ToHexStringLower(SHA256.HashData(v.File)));
        Assert.Equal(VerificationResult.Valid, ReleaseSignature.VerifyBytes(v.File, v.Sha256, v.Signature, v.PublicKey));
        Assert.True(ReleaseSignature.VerifyDigest(SHA256.HashData(v.File), v.Signature, v.PublicKey));
    }

    [Fact]
    public void VectorPublicKeyMatchesSeed()
    {
        var v = V;
        var priv = new Ed25519PrivateKeyParameters(v.Seed, 0);
        Assert.Equal(v.PublicKey, priv.GeneratePublicKey().GetEncoded());
    }

    [Fact]
    public void SignatureIsOverRawDigestNotFileOrHex()
    {
        var v = V;
        var signature = Convert.FromBase64String(v.Signature);
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(v.PublicKey, 0));
        var digest = SHA256.HashData(v.File);
        verifier.BlockUpdate(digest, 0, digest.Length);
        Assert.True(verifier.VerifySignature(signature));
        var hex = Encoding.ASCII.GetBytes(v.Sha256);
        verifier.Reset();
        verifier.BlockUpdate(hex, 0, hex.Length);
        Assert.False(verifier.VerifySignature(signature));
    }

    [Fact]
    public void TamperedFileFails()
    {
        var v = V;
        for (var i = 0; i < v.File.Length; i += 7)
        {
            var file = v.File;
            file[i] ^= 0x01;
            Assert.Equal(VerificationResult.HashMismatch, ReleaseSignature.VerifyBytes(file, v.Sha256, v.Signature, v.PublicKey));
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(file));
            Assert.Equal(VerificationResult.BadSignature, ReleaseSignature.VerifyBytes(file, actualHash, v.Signature, v.PublicKey));
        }
    }

    [Fact]
    public void TamperedSignatureFails()
    {
        var v = V;
        var original = Convert.FromBase64String(v.Signature);
        Assert.Equal(64, original.Length);
        for (var i = 0; i < original.Length; i++)
        {
            var sig = (byte[])original.Clone();
            sig[i] ^= 0x01;
            Assert.Equal(VerificationResult.BadSignature, ReleaseSignature.VerifyBytes(v.File, v.Sha256, Convert.ToBase64String(sig), v.PublicKey));
        }
        Assert.Equal(VerificationResult.BadSignature, ReleaseSignature.VerifyBytes(v.File, v.Sha256, "not base64!", v.PublicKey));
        Assert.Equal(VerificationResult.BadSignature, ReleaseSignature.VerifyBytes(v.File, v.Sha256, Convert.ToBase64String(original[..63]), v.PublicKey));
    }

    [Fact]
    public void WrongKeyFails()
    {
        var v = V;
        Assert.Equal(VerificationResult.BadSignature, ReleaseSignature.VerifyBytes(v.File, v.Sha256, v.Signature, ReleaseSignature.ReleasePublicKey));
    }

    [Fact]
    public void VerifyFileChecksSizeHashAndSignature()
    {
        var v = V;
        var path = Path.Combine(Path.GetTempPath(), "yikz-sig-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, v.File);
            Assert.Equal(VerificationResult.Valid, ReleaseSignature.VerifyFile(path, v.File.Length, v.Sha256, v.Signature, v.PublicKey));
            Assert.Equal(VerificationResult.SizeMismatch, ReleaseSignature.VerifyFile(path, v.File.Length + 1, v.Sha256, v.Signature, v.PublicKey));
            var tampered = v.File;
            tampered[^1] ^= 0x20;
            File.WriteAllBytes(path, tampered);
            Assert.Equal(VerificationResult.HashMismatch, ReleaseSignature.VerifyFile(path, v.File.Length, v.Sha256, v.Signature, v.PublicKey));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmbeddedReleaseKeyIs32Bytes()
    {
        Assert.Equal(32, ReleaseSignature.ReleasePublicKey.Length);
        Assert.Equal("2Ve8Uwt53+AwbuAiK08Xf2gB5EU7pguqXL7I9yeqGGk=", ReleaseSignature.ReleasePublicKeyBase64);
    }
}

public class SemVerTests
{
    [Theory]
    [InlineData("1.1.0", 1, 1, 0)]
    [InlineData("v2.10.3", 2, 10, 3)]
    [InlineData(" 0.0.1 ", 0, 0, 1)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    public void Parses(string text, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(new SemVer(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    public void RejectsInvalid(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.10", "1.0.2", 1)]
    [InlineData("1.1.0", "1.1.0", 0)]
    [InlineData("0.9.0", "1.0.0", -1)]
    public void ComparesNumerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));
    }

    [Fact]
    public void FromAssemblyVersion()
    {
        Assert.Equal(new SemVer(1, 1, 0), SemVer.FromVersion(new Version(1, 1, 0, 0)));
        Assert.Equal(new SemVer(1, 1, 0), SemVer.FromVersion(new Version(1, 1)));
        Assert.Equal("1.1.0", new SemVer(1, 1, 0).ToString());
    }
}

public class UpdateDecisionTests
{
    private static LatestRelease Release(string version, string platform = "windows-x64") => new()
    {
        Version = version,
        PublishedAt = "2026-09-22T10:00:00.000Z",
        NotesMd = "### Windows\n- Auto update\n",
        Asset = new ReleaseAsset
        {
            Platform = platform,
            File = "YikzClipboard-" + version + "-win-x64.zip",
            Size = 1234,
            Sha256 = new string('a', 64),
            Signature = Convert.ToBase64String(new byte[64]),
            Url = "/api/releases/" + version + "/assets/YikzClipboard-" + version + "-win-x64.zip",
        },
    };

    private static readonly SemVer Current = new(1, 1, 0);

    [Fact]
    public void NoReleaseMeansNothingToDo()
    {
        Assert.Equal(UpdateAction.NoRelease, UpdatePolicy.Decide(Current, null, "windows-x64").Action);
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.9")]
    [InlineData("0.9.0")]
    public void NeverDowngradesOrReinstalls(string version)
    {
        Assert.Equal(UpdateAction.UpToDate, UpdatePolicy.Decide(Current, Release(version), "windows-x64").Action);
    }

    [Theory]
    [InlineData("1.1.1")]
    [InlineData("1.2.0")]
    [InlineData("2.0.0")]
    public void UpdatesToNewer(string version)
    {
        var d = UpdatePolicy.Decide(Current, Release(version), "windows-x64");
        Assert.Equal(UpdateAction.Update, d.Action);
        Assert.Equal(SemVer.Parse(version), d.Version!.Value);
    }

    [Fact]
    public void RejectsOtherPlatformAsset()
    {
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, Release("1.2.0", "windows-arm64"), "windows-x64").Action);
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, Release("1.2.0", "macos"), "windows-x64").Action);
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, Release("1.2.0"), null).Action);
    }

    [Fact]
    public void RejectsMalformedAssets()
    {
        var r = Release("1.2.0");
        r.Asset!.File = "..\\evil.zip";
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, r, "windows-x64").Action);
        r = Release("1.2.0");
        r.Asset!.Sha256 = "abc";
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, r, "windows-x64").Action);
        r = Release("1.2.0");
        r.Asset!.Signature = "";
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, r, "windows-x64").Action);
        r = Release("1.2.0");
        r.Asset = null;
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, r, "windows-x64").Action);
        r = Release("banana");
        Assert.Equal(UpdateAction.Invalid, UpdatePolicy.Decide(Current, r, "windows-x64").Action);
    }

    [Fact]
    public void PlatformKeys()
    {
        Assert.Equal("windows-x64", UpdatePolicy.PlatformKey(Architecture.X64));
        Assert.Equal("windows-arm64", UpdatePolicy.PlatformKey(Architecture.Arm64));
        Assert.Null(UpdatePolicy.PlatformKey(Architecture.X86));
    }
}

public class LatestReleaseTests
{
    private const string LatestJson = """
        {
          "version": "1.2.0",
          "published_at": "2026-09-22T10:00:00.000Z",
          "notes_md": "### Windows\n- Taskbar option\n",
          "asset": {
            "platform": "windows-x64",
            "file": "YikzClipboard-1.2.0-win-x64.zip",
            "size": 2400000,
            "sha256": "979c9f8f4fa29fe42adad7a79ccba083a4ee7faf2d9b8d22a26b0097dc08df60",
            "signature": "GQaoiK5wfcLPxW5w9GLFrOODSd0KRdG3ga9/iT0jQX0LXdPXcr1S3Ud231ebMyxNSTWlh6PCPyFq/F5BSU5PAg==",
            "url": "/api/releases/1.2.0/assets/YikzClipboard-1.2.0-win-x64.zip"
          }
        }
        """;

    [Fact]
    public void Parses200()
    {
        var r = LatestReleaseParser.Parse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(LatestJson));
        Assert.NotNull(r);
        Assert.Equal("1.2.0", r!.Version);
        Assert.Equal("### Windows\n- Taskbar option\n", r.NotesMd);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero), r.PublishedTime);
        Assert.Equal("windows-x64", r.Asset!.Platform);
        Assert.Equal(2400000, r.Asset.Size);
        Assert.Equal("/api/releases/1.2.0/assets/YikzClipboard-1.2.0-win-x64.zip", r.Asset.Url);
    }

    [Fact]
    public void Parses204AsNoRelease()
    {
        Assert.Null(LatestReleaseParser.Parse(HttpStatusCode.NoContent, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Throws<UpdateException>(() => LatestReleaseParser.Parse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{not json")));
        Assert.Throws<UpdateException>(() => LatestReleaseParser.Parse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<UpdateException>(() => LatestReleaseParser.Parse(HttpStatusCode.InternalServerError, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task ClientRequestsLatestWithTokenAndHandles204()
    {
        var handler = new FakeHttpHandler();
        handler.On("GET", "/api/releases/latest?platform=windows-x64", FakeResponse.Json(200, LatestJson));
        handler.On("GET", "/api/releases/latest?platform=windows-arm64", FakeResponse.Empty(204));
        var client = new UpdateClient(new HttpClient(handler), () => new UpdateEndpoint(new Uri("https://clip.test"), "yc_token"));
        var latest = await client.GetLatestAsync("windows-x64", CancellationToken.None);
        Assert.Equal("1.2.0", latest!.Version);
        Assert.Null(await client.GetLatestAsync("windows-arm64", CancellationToken.None));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer yc_token", r.Authorization));
    }

    [Fact]
    public async Task ClientRequiresSignIn()
    {
        var client = new UpdateClient(new HttpClient(new FakeHttpHandler()), () => null);
        await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync("windows-x64", CancellationToken.None));
    }

    [Fact]
    public void AssetUrlResolvesAgainstServerOnly()
    {
        var asset = new ReleaseAsset { File = "a.zip", Url = "/api/releases/1.2.0/assets/a.zip" };
        Assert.Equal("https://clip.test/api/releases/1.2.0/assets/a.zip", UpdateClient.ResolveAssetUri(new Uri("https://clip.test"), asset, "1.2.0").ToString());
        asset.Url = null;
        Assert.Equal("https://clip.test/api/releases/1.2.0/assets/a.zip", UpdateClient.ResolveAssetUri(new Uri("https://clip.test/"), asset, "1.2.0").ToString());
        asset.Url = "https://evil.test/a.zip";
        Assert.Throws<UpdateException>(() => UpdateClient.ResolveAssetUri(new Uri("https://clip.test"), asset, "1.2.0"));
    }
}

public class UpdaterFlowTests
{
    private static byte[] Seed => Vectors.Hex(Vectors.Load("release_signature.json").Str("private_key_seed_hex"));

    private static string Sign(byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Seed, 0));
        var digest = SHA256.HashData(data);
        signer.BlockUpdate(digest, 0, digest.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private static byte[] PublicKey => new Ed25519PrivateKeyParameters(Seed, 0).GeneratePublicKey().GetEncoded();

    private static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var exe = zip.CreateEntry("YikzClipboard.exe");
            using (var s = exe.Open())
            {
                s.Write(Encoding.ASCII.GetBytes("MZ fake exe"));
            }
            var dll = zip.CreateEntry("lib/Some.dll");
            using (var s = dll.Open())
            {
                s.Write(new byte[] { 1, 2, 3 });
            }
        }
        return ms.ToArray();
    }

    private static string Latest(byte[] zip, string signature) => $$"""
        {"version":"1.2.0","published_at":"2026-09-22T10:00:00.000Z","notes_md":"### Windows\n- New\n",
         "asset":{"platform":"windows-x64","file":"YikzClipboard-1.2.0-win-x64.zip","size":{{zip.Length}},
         "sha256":"{{Convert.ToHexStringLower(SHA256.HashData(zip))}}","signature":"{{signature}}",
         "url":"/api/releases/1.2.0/assets/YikzClipboard-1.2.0-win-x64.zip"}}
        """;

    private static (Updater Updater, string Root) Create(byte[] announced, byte[] served, string signature)
    {
        var handler = new FakeHttpHandler();
        handler.On("GET", "/api/releases/latest?platform=windows-x64", FakeResponse.Json(200, Latest(announced, signature)));
        handler.On("GET", "/api/releases/1.2.0/assets/YikzClipboard-1.2.0-win-x64.zip", FakeResponse.Binary(served));
        var client = new UpdateClient(new HttpClient(handler), () => new UpdateEndpoint(new Uri("https://clip.test"), "yc_token"));
        var root = Path.Combine(Path.GetTempPath(), "yikz-upd-" + Guid.NewGuid().ToString("N"));
        return (new Updater(client, new SemVer(1, 1, 0), "windows-x64", root, NullLog.Instance, null, PublicKey), root);
    }

    [Fact]
    public async Task DownloadsVerifiesAndExtracts()
    {
        var zip = MakeZip();
        var (updater, root) = Create(zip, zip, Sign(zip));
        try
        {
            DateTimeOffset? checkedAt = null;
            updater.Checked += t => checkedAt = t;
            await updater.CheckAsync(true);
            var status = updater.Status;
            Assert.Equal(UpdateStage.Ready, status.Stage);
            Assert.NotNull(checkedAt);
            Assert.Equal(new SemVer(1, 2, 0), status.Prepared!.Version);
            Assert.True(File.Exists(Path.Combine(status.Prepared.AppDir, "YikzClipboard.exe")));
            Assert.True(File.Exists(Path.Combine(status.Prepared.AppDir, "lib", "Some.dll")));
            Assert.False(File.Exists(Path.Combine(root, "1.2.0", "YikzClipboard-1.2.0-win-x64.zip")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TamperedDownloadIsDeleted()
    {
        var zip = MakeZip();
        var signature = Sign(zip);
        var tampered = (byte[])zip.Clone();
        tampered[^5] ^= 0xFF;
        var (updater, root) = Create(zip, tampered, signature);
        try
        {
            await updater.CheckAsync(false);
            Assert.Equal(UpdateStage.Failed, updater.Status.Stage);
            Assert.Null(updater.Status.Prepared);
            Assert.False(File.Exists(Path.Combine(root, "1.2.0", "YikzClipboard-1.2.0-win-x64.zip")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task BadSignatureIsRejected()
    {
        var zip = MakeZip();
        var (updater, root) = Create(zip, zip, Sign(Encoding.ASCII.GetBytes("something else")));
        try
        {
            await updater.CheckAsync(false);
            Assert.Equal(UpdateStage.Failed, updater.Status.Stage);
            Assert.Null(updater.Status.Prepared);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void ExtractRejectsZipSlip()
    {
        var root = Path.Combine(Path.GetTempPath(), "yikz-slip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zipPath = Path.Combine(root, "evil.zip");
            using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntry("../outside.txt");
            }
            Assert.Throws<UpdateException>(() => UpdatePackage.Extract(zipPath, Path.Combine(root, "app")));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class UpdateScriptTests
{
    [Theory]
    [InlineData("C:\\Apps\\Yikz", "'C:\\Apps\\Yikz'")]
    [InlineData("C:\\Users\\O'Brien\\Yikz", "'C:\\Users\\O''Brien\\Yikz'")]
    [InlineData("C:\\a$b`c\"d", "'C:\\a$b`c\"d'")]
    [InlineData("C:\\x\u2019y", "'C:\\x\u2019\u2019y'")]
    [InlineData("", "''")]
    public void QuotesForPowerShellSingleQuotedStrings(string input, string expected)
    {
        Assert.Equal(expected, UpdateScript.Quote(input));
    }

    private static UpdateScriptOptions Options(string install) => new(
        4242,
        "C:\\Users\\O'Brien\\AppData\\Local\\YikzClipboard\\updates\\1.2.0\\app",
        install,
        install + "\\YikzClipboard.exe",
        "C:\\Users\\O'Brien\\AppData\\Local\\YikzClipboard\\updates\\1.2.0\\backup",
        "C:\\Users\\O'Brien\\AppData\\Local\\YikzClipboard\\updates\\1.2.0\\install.log",
        "1.2.0");

    [Fact]
    public void ScriptEmbedsQuotedPathsAndPid()
    {
        var install = "D:\\Tools\\Yikz $(Remove-Item x) 'quoted'\\";
        var script = UpdateScript.Build(Options(install));
        Assert.Contains("$waitPid = 4242\r\n", script);
        Assert.Contains("$source = 'C:\\Users\\O''Brien\\AppData\\Local\\YikzClipboard\\updates\\1.2.0\\app'\r\n", script);
        Assert.Contains("$target = 'D:\\Tools\\Yikz $(Remove-Item x) ''quoted'''\r\n", script);
        Assert.Contains("$exe = 'D:\\Tools\\Yikz $(Remove-Item x) ''quoted''\\\\YikzClipboard.exe'\r\n", script);
        Assert.Contains("$backup = 'C:\\Users\\O''Brien\\AppData\\Local\\YikzClipboard\\updates\\1.2.0\\backup'\r\n", script);
        Assert.Contains("Get-Process -Id $waitPid", script);
        Assert.Contains("Start-Process -FilePath $exe", script);
        Assert.Contains("rolling back", script);
    }

    [Fact]
    public void ScriptHasNoUnescapedUserText()
    {
        var install = "E:\\It's \u2018here\u2019";
        var script = UpdateScript.Build(Options(install));
        foreach (var line in script.Split("\r\n"))
        {
            if (!line.StartsWith("$target = ", StringComparison.Ordinal))
            {
                continue;
            }
            var literal = line["$target = ".Length..];
            Assert.StartsWith("'", literal);
            Assert.EndsWith("'", literal);
            var inner = literal[1..^1];
            var i = 0;
            while (i < inner.Length)
            {
                if ("'\u2018\u2019\u201A\u201B".Contains(inner[i]))
                {
                    Assert.True(i + 1 < inner.Length && inner[i + 1] == inner[i], "unescaped quote at " + i);
                    i += 2;
                    continue;
                }
                i++;
            }
        }
    }

    [Fact]
    public void ScriptFileEncodingHasBom()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, UpdateScript.FileEncoding.GetPreamble());
    }
}

public class NotesMarkdownTests
{
    [Fact]
    public void ParsesSubset()
    {
        var blocks = NotesMarkdown.Parse("### Windows\n- **Taskbar** option, see `Settings`\n- [Docs](https://clip.yikz.dev/docs)\n\nPlain line\r\n- [bad](javascript:alert(1))");
        Assert.Equal(5, blocks.Count);
        Assert.Equal(NotesBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Windows", blocks[0].Spans[0].Text);
        Assert.Equal(NotesBlockKind.Bullet, blocks[1].Kind);
        Assert.Equal(new NotesSpan(NotesSpanKind.Bold, "Taskbar"), blocks[1].Spans[0]);
        Assert.Equal(new NotesSpan(NotesSpanKind.Text, " option, see "), blocks[1].Spans[1]);
        Assert.Equal(new NotesSpan(NotesSpanKind.Code, "Settings"), blocks[1].Spans[2]);
        Assert.Equal(new NotesSpan(NotesSpanKind.Link, "Docs", "https://clip.yikz.dev/docs"), blocks[2].Spans[0]);
        Assert.Equal(NotesBlockKind.Paragraph, blocks[3].Kind);
        Assert.Equal(new NotesSpan(NotesSpanKind.Text, "bad"), blocks[4].Spans[0]);
    }

    [Fact]
    public void UnclosedMarkersStayText()
    {
        var spans = NotesMarkdown.ParseInline("a ** b ` c [d");
        Assert.Single(spans);
        Assert.Equal("a ** b ` c [d", spans[0].Text);
    }
}

public class ReleaseAvailableMessageTests
{
    [Fact]
    public void EngineRaisesReleaseAvailable()
    {
        var json = """{"type":"release_available","version":"1.1.0"}""";
        Assert.Equal(WsTypes.ReleaseAvailable, WsCodec.PeekType(json));
        var msg = System.Text.Json.JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ReleaseAvailableMessage);
        Assert.Equal("1.1.0", msg!.Version);
    }
}
