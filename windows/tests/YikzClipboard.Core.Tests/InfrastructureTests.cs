using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using YikzClipboard.Core.Imaging;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class ImagingTests
{
    private static byte[] DecodeSimplePng(byte[] png, out int width, out int height, out int channels)
    {
        Assert.True(PngInfo.TryGetSize(png, out width, out height));
        channels = png[25] == 6 ? 4 : 3;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(crc, PngWriter.Crc32(png.AsSpan(pos + 4, len + 4)));
            if (type == "IDAT")
            {
                idat.Write(png, pos + 8, len);
            }
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        var stride = width * channels;
        var result = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * (stride + 1)]);
            Array.Copy(bytes, y * (stride + 1) + 1, result, y * stride, stride);
        }
        return result;
    }

    private static byte[] Dib24(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel, bool topDown = false)
    {
        var stride = ((w * 24 + 31) / 32) * 4;
        var dib = new byte[40 + stride * h];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), topDown ? -h : h);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        for (var y = 0; y < h; y++)
        {
            var row = topDown ? y : h - 1 - y;
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                var o = 40 + row * stride + x * 3;
                dib[o] = b;
                dib[o + 1] = g;
                dib[o + 2] = r;
            }
        }
        return dib;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dib24ConvertsToOpaquePng(bool topDown)
    {
        var dib = Dib24(5, 3, (x, y) => ((byte)(x * 40), (byte)(y * 80), 7), topDown);
        var png = Dib.ToPng(dib);
        var pixels = DecodeSimplePng(png, out var w, out var h, out var channels);
        Assert.Equal(5, w);
        Assert.Equal(3, h);
        Assert.Equal(3, channels);
        Assert.Equal(new byte[] { 160, 160, 7 }, pixels.AsSpan((2 * 5 + 4) * 3, 3).ToArray());
    }

    [Fact]
    public void Dib32WithAlphaKeepsAlphaAndAllZeroAlphaIsOpaque()
    {
        var bgra = new byte[2 * 2 * 4];
        for (var i = 0; i < 4; i++)
        {
            bgra[i * 4] = 10;
            bgra[i * 4 + 1] = 20;
            bgra[i * 4 + 2] = 30;
            bgra[i * 4 + 3] = (byte)(i * 60);
        }
        var dib = new byte[40 + bgra.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        bgra.CopyTo(dib, 40);
        var decoded = Dib.Decode(dib);
        Assert.True(decoded.HasAlpha);
        Assert.Equal(180, decoded.Bgra[15]);
        var pixels = DecodeSimplePng(Dib.ToPng(dib), out _, out _, out var channels);
        Assert.Equal(4, channels);
        Assert.Equal(new byte[] { 30, 20, 10, 60 }, pixels.AsSpan(4, 4).ToArray());
        for (var i = 0; i < 4; i++)
        {
            dib[40 + i * 4 + 3] = 0;
        }
        var opaque = Dib.Decode(dib);
        Assert.False(opaque.HasAlpha);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(255, opaque.Bgra[i * 4 + 3]));
    }

    [Fact]
    public void FromBgraProducesBottomUpDibCompositedOnWhite()
    {
        var bgra = new byte[] { 0, 0, 255, 255, 0, 0, 0, 0 };
        var dib = Dib.FromBgra(1, 2, bgra);
        Assert.Equal(40 + 8, dib.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dib.AsSpan(40, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, dib.AsSpan(44, 4).ToArray());
        var back = Dib.Decode(dib);
        Assert.Equal(255, back.Bgra[2]);
    }

    [Fact]
    public void PaletteDibConverts()
    {
        var w = 3;
        var h = 1;
        var stride = 4;
        var dib = new byte[40 + 8 + stride * h];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), 2);
        dib[40 + 4] = 255;
        dib[40 + 5] = 255;
        dib[40 + 6] = 255;
        dib[48] = 0b01000000;
        var decoded = Dib.Decode(dib);
        Assert.Equal(0, decoded.Bgra[0]);
        Assert.Equal(255, decoded.Bgra[4]);
        Assert.Equal(0, decoded.Bgra[8]);
    }

    [Fact]
    public void InvalidDibThrows()
    {
        Assert.Throws<FormatException>(() => Dib.Decode(new byte[10]));
        var dib = Dib24(4, 4, (_, _) => (1, 2, 3));
        Assert.Throws<FormatException>(() => Dib.Decode(dib.AsSpan(0, 50)));
    }
}

public class LoggerTests
{
    [Fact]
    public async Task RingBufferKeepsNewestEntries()
    {
        await using var logger = new Logger(null, ringCapacity: 16);
        for (var i = 0; i < 40; i++)
        {
            logger.Info("t", "entry " + i);
        }
        var snapshot = logger.Snapshot();
        Assert.Equal(16, snapshot.Count);
        Assert.Equal("entry 24", snapshot[0].Message);
        Assert.Equal("entry 39", snapshot[^1].Message);
        Assert.True(snapshot.Zip(snapshot.Skip(1)).All(p => p.First.Id < p.Second.Id));
    }

    [Fact]
    public async Task FilesRotateAndTokensAreRedacted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yikz-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var logger = new Logger(dir, maxFileBytes: 2000, maxFiles: 3))
            {
                logger.Info("auth", "token yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw leaked");
                for (var i = 0; i < 200; i++)
                {
                    logger.Info("t", "line " + i + " " + new string('x', 40));
                }
                await logger.FlushAsync();
                Assert.All(logger.Snapshot(), e => Assert.DoesNotContain("WMmC", e.Message));
                var files = logger.LogFiles();
                Assert.Equal(3, files.Count);
                Assert.EndsWith("yikz-clipboard.log", files[^1]);
            }
            var all = string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText));
            Assert.DoesNotContain("WMmC", all);
            Assert.Contains("line 199", all);
            Assert.True(Directory.GetFiles(dir).Length <= 3);
            Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length < 4000));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MinimumLevelFilters()
    {
        await using var logger = new Logger(null) { MinimumLevel = LogLevel.Info };
        logger.Debug("t", "hidden");
        logger.Warn("t", "shown", new InvalidOperationException("why"));
        var s = logger.Snapshot();
        Assert.Single(s);
        Assert.Contains("why", s[0].Message);
        Assert.Contains("WRN [t] shown", s[0].Format());
    }
}

public class ApiClientTests
{
    private static ApiClient Client(FakeHttpHandler h) => new(new HttpClient(h), new Uri("https://clip.test/")) { Token = "yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw" };

    [Fact]
    public async Task ErrorBodiesAreParsed()
    {
        var h = new FakeHttpHandler();
        var example = Vectors.HttpExample("commit_missing_chunks");
        h.On("POST", "/api/items/01a0c36d-10e0-7a56-86e3-483e2f79d9c5/commit", FakeResponse.Json(409, example.GetProperty("response_body").GetRawText()));
        var api = Client(h);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.CommitAsync("01a0c36d-10e0-7a56-86e3-483e2f79d9c5", new CommitRequest()));
        Assert.Equal(409, ex.Status);
        Assert.Equal(ErrorCodes.MissingChunks, ex.Code);
        Assert.Equal(new[] { 1, 2 }, ex.MissingChunks());
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task UnauthorizedRaisesEventButLoginDoesNot()
    {
        var h = new FakeHttpHandler();
        h.On("GET", "/api/me", FakeResponse.Json(401, Vectors.HttpExample("me_unauthorized").GetProperty("response_body").GetRawText()));
        h.On("POST", "/api/login", FakeResponse.Json(401, Vectors.HttpExample("login_invalid_credentials").GetProperty("response_body").GetRawText()));
        var api = Client(h);
        var fired = 0;
        api.Unauthorized += () => fired++;
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetMeAsync());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(1, fired);
        var login = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(new LoginRequest { Username = "u", Password = "p", DeviceName = "d" }));
        Assert.Equal(ErrorCodes.InvalidCredentials, login.Code);
        Assert.Equal(1, fired);
        var (_, _, body, auth) = h.Requests.Last();
        Assert.Null(auth);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("windows", (string?)json["platform"]);
        Assert.Null(json["device_id"]);
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        var h = new FakeHttpHandler();
        h.On("POST", "/api/login", new FakeResponse(429, Encoding.UTF8.GetBytes("""{"code":"rate_limited","message":"slow down"}"""), "application/json", new Dictionary<string, string> { ["Retry-After"] = "600" }));
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client(h).LoginAsync(new LoginRequest()));
        Assert.Equal(TimeSpan.FromSeconds(600), ex.RetryAfter);
    }

    [Fact]
    public async Task DeleteTreats404AsSuccessAndSendsBearer()
    {
        var h = new FakeHttpHandler();
        var api = Client(h);
        await api.DeleteItemAsync("01926f3c-8d2a-7b3e-9f10-0123456789ab");
        var r = h.Requests.Single();
        Assert.Equal("DELETE", r.Method);
        Assert.Equal("Bearer yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw", r.Authorization);
    }

    [Fact]
    public async Task ChunkUploadSendsRawBytes()
    {
        var h = new FakeHttpHandler();
        h.On("PUT", "/api/items/01a0c36d-10e0-7a56-86e3-483e2f79d9c5/chunks/2", FakeResponse.Empty());
        var body = new byte[] { 1, 2, 3, 4 };
        await Client(h).PutChunkAsync("01a0c36d-10e0-7a56-86e3-483e2f79d9c5", 2, body, body.Length);
        Assert.Equal(body, h.Requests.Single().Body);
        await Assert.ThrowsAsync<ArgumentException>(() => Client(h).GetItemAsync("not-a-uuid"));
    }

    [Fact]
    public async Task HistoryQueryStringsAreExact()
    {
        var h = new FakeHttpHandler();
        h.On("GET", "/api/history?after=40&limit=500", FakeResponse.Json(200, Vectors.HttpExample("history_after_catch_up").GetProperty("response_body").GetRawText()));
        h.On("GET", "/api/history?before=42&limit=100", FakeResponse.Json(200, Vectors.HttpExample("history_before").GetProperty("response_body").GetRawText()));
        var api = Client(h);
        Assert.Equal(4, (await api.GetHistoryAsync(after: 40, limit: 500)).Items.Count);
        Assert.Single((await api.GetHistoryAsync(before: 42, limit: 100)).Items);
    }

    [Fact]
    public async Task ServerErrorsAreTransientAndRetried()
    {
        var h = new FakeHttpHandler();
        var calls = 0;
        h.On("GET", "/api/storage", (_, _) => ++calls < 2
            ? FakeResponse.Json(503, "{}")
            : FakeResponse.Json(200, Vectors.HttpExample("storage").GetProperty("response_body").GetRawText()));
        var api = Client(h);
        var storage = await RetryPolicy.RunAsync(t => api.GetStorageAsync(t), 3, CancellationToken.None);
        Assert.Equal(5368709120, storage.LimitBytes);
        Assert.Equal(2, calls);
    }
}

public class ServiceFlowTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), "yikz-svc-" + Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Settings = new SettingsStore(Paths.SettingsFile);
            Http = new FakeHttpHandler();
            Service = new ClipboardSyncService(Paths, Settings, Secrets, new TestLog(), new FakeSink(), new FakeNotifier(), NullImageTools.Instance, "1.0.0",
                http: new HttpClient(Http), transport: Transport, store: HistoryStore.InMemory());
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SettingsStore Settings { get; }
        public FakeHttpHandler Http { get; }
        public InMemorySecureStore Secrets { get; } = new();
        public FakeTransport Transport { get; } = new();
        public ClipboardSyncService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }

    private static string LoginBody(string? keyCheck)
    {
        var node = JsonNode.Parse(Vectors.HttpExample("login_new_device").GetProperty("response_body").GetRawText())!;
        node["key_check"] = keyCheck;
        return node.ToJsonString();
    }

    private static string MeBody(string? keyCheck)
    {
        var node = JsonNode.Parse(Vectors.HttpExample("me").GetProperty("response_body").GetRawText())!;
        node["key_check"] = keyCheck;
        return node.ToJsonString();
    }

    [Fact]
    public async Task FirstDeviceSetsKeyCheckAndConnects()
    {
        await using var h = new Harness();
        h.Http.On("POST", "/api/login", FakeResponse.Json(200, LoginBody(null)));
        h.Http.On("GET", "/api/me", FakeResponse.Json(200, MeBody(null)));
        h.Http.On("PUT", "/api/account/key-check", FakeResponse.Empty());
        h.Http.On("GET", "/api/devices", FakeResponse.Json(200, Vectors.HttpExample("devices_list").GetProperty("response_body").GetRawText()));
        h.Service.Start();
        Assert.Equal(ServiceStatus.SignedOut, h.Service.Status);
        var login = await h.Service.LoginAsync("clip.test", "thoriq", "login-password-example", "Desk");
        Assert.True(login.Success);
        Assert.True(login.NeedsEncryptionPassword);
        Assert.Equal(ServiceStatus.NeedsEncryptionPassword, h.Service.Status);
        Assert.Equal("01a05c00-03e0-7c59-b16c-c1bb2928ae88", h.Settings.Current.DeviceId);
        Assert.Equal("https://clip.test", h.Settings.Current.ServerUrl);
        Assert.NotNull(h.Secrets.Get(ClipboardSyncService.TokenSecret));
        var result = await h.Service.SetEncryptionPasswordAsync("correct horse battery staple");
        Assert.Equal(KeySetupResult.Accepted, result.Result);
        var put = h.Http.Requests.Single(r => r.Method == "PUT");
        Assert.Equal("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93", (string?)JsonNode.Parse(put.Body!)!["key_check"]);
        Assert.Equal(32, h.Secrets.Get(ClipboardSyncService.KeySecret)!.Length);
        var conn = await h.Transport.NextConnectionAsync();
        conn.ServerSend(Vectors.WsMessage("welcome").GetRawText());
        await Wait.UntilAsync(() => h.Service.Status == ServiceStatus.Connected);
        var login2 = JsonNode.Parse(h.Http.Requests.First(r => r.PathAndQuery == "/api/login").Body!)!;
        Assert.Equal("Desk", (string?)login2["device_name"]);
        Assert.DoesNotContain("correct horse", string.Join("", h.Http.Requests.Select(r => r.Body == null ? "" : Encoding.UTF8.GetString(r.Body))));
    }

    [Fact]
    public async Task WrongEncryptionPasswordIsDetected()
    {
        await using var h = new Harness();
        h.Http.On("POST", "/api/login", FakeResponse.Json(200, LoginBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Http.On("GET", "/api/me", FakeResponse.Json(200, MeBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Service.Start();
        await h.Service.LoginAsync("https://clip.test", "thoriq", "pw", "Desk");
        var wrong = await h.Service.SetEncryptionPasswordAsync("not the password");
        Assert.Equal(KeySetupResult.WrongPassword, wrong.Result);
        Assert.Null(h.Secrets.Get(ClipboardSyncService.KeySecret));
        Assert.Equal(ServiceStatus.NeedsEncryptionPassword, h.Service.Status);
        Assert.Equal(0, h.Transport.Attempts);
    }

    [Fact]
    public async Task KeyCheckRaceComparesWithServer()
    {
        await using var h = new Harness();
        h.Http.On("POST", "/api/login", FakeResponse.Json(200, LoginBody(null)));
        var meCalls = 0;
        h.Http.On("GET", "/api/me", (_, _) => FakeResponse.Json(200, MeBody(++meCalls == 1 ? null : "2aa876099cbf0b95a3e2504a603d72e7be1aa270ee2401b7a35594b0096951ab")));
        h.Http.On("PUT", "/api/account/key-check", FakeResponse.Json(409, Vectors.HttpExample("key_check_conflict").GetProperty("response_body").GetRawText()));
        h.Service.Start();
        await h.Service.LoginAsync("https://clip.test", "thoriq", "pw", "Desk");
        var result = await h.Service.SetEncryptionPasswordAsync("correct horse battery staple");
        Assert.Equal(KeySetupResult.WrongPassword, result.Result);
    }

    [Fact]
    public async Task RevokedTokenSignsOutButKeepsKey()
    {
        await using var h = new Harness();
        h.Http.On("POST", "/api/login", FakeResponse.Json(200, LoginBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Http.On("GET", "/api/me", FakeResponse.Json(200, MeBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Http.On("GET", "/api/devices", FakeResponse.Json(401, """{"code":"unauthorized","message":"revoked"}"""));
        h.Service.Start();
        await h.Service.LoginAsync("https://clip.test", "thoriq", "pw", "Desk");
        await h.Service.SetEncryptionPasswordAsync("correct horse battery staple");
        await Wait.UntilAsync(() => h.Service.Status == ServiceStatus.SignedOut);
        Assert.Null(h.Secrets.Get(ClipboardSyncService.TokenSecret));
        Assert.NotNull(h.Secrets.Get(ClipboardSyncService.KeySecret));
        h.Http.On("GET", "/api/devices", FakeResponse.Json(200, """{"devices":[]}"""));
        var again = await h.Service.LoginAsync("https://clip.test", "thoriq", "pw", "Desk");
        Assert.True(again.Success);
        Assert.False(again.NeedsEncryptionPassword);
    }

    [Fact]
    public async Task LocalTextIsUploadedOnceAndEchoesSkipped()
    {
        await using var h = new Harness();
        h.Http.On("POST", "/api/login", FakeResponse.Json(200, LoginBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Http.On("GET", "/api/me", FakeResponse.Json(200, MeBody("41f6d0b7f5e8d491269b90fc8988b2f48757f7a48baaeb9129da7ea9bb427e93")));
        h.Http.On("GET", "/api/devices", FakeResponse.Json(200, """{"devices":[]}"""));
        var created = 0;
        h.Http.On("POST", "/api/items", (_, body) =>
        {
            created++;
            var req = JsonNode.Parse(body!)!;
            var header = JsonNode.Parse(Vectors.HttpExample("item_create_inline").GetProperty("response_body").GetRawText())!;
            header["id"] = (string?)req["id"];
            header["content_hash"] = (string?)req["content_hash"];
            header["size"] = (long)req["size"]!;
            header["meta"] = (string?)req["meta"];
            header["seq"] = 50 + created;
            return FakeResponse.Json(201, header.ToJsonString());
        });
        h.Service.Start();
        await h.Service.LoginAsync("https://clip.test", "thoriq", "pw", "Desk");
        await h.Service.SetEncryptionPasswordAsync("correct horse battery staple");
        var sent = new TaskCompletionSource<HistoryEntry>();
        h.Service.ItemSent += e => sent.TrySetResult(e);
        Assert.True(h.Service.SubmitLocalClip(new LocalText("hello from windows", "Notepad")));
        var entry = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello from windows", entry.Meta!.Preview);
        Assert.Equal("Notepad", entry.Meta.SourceApp);
        Assert.Equal(1, created);
        var request = JsonNode.Parse(h.Http.Requests.Single(r => r.PathAndQuery == "/api/items").Body!)!;
        Assert.Equal(0, (int)request["chunk_count"]!);
        Assert.Equal(18, (long)request["size"]!);
        Assert.Equal(18 + 28, Convert.FromBase64String((string)request["payload"]!).Length);
        h.Service.SubmitLocalClip(new LocalText("hello from windows", "Notepad"));
        h.Service.SubmitLocalClip(new LocalText("hello from windows".Replace("\n", "\r\n"), null));
        await Task.Delay(300);
        Assert.Equal(1, created);
        Assert.NotNull(h.Service.History.Get(entry.Id));
        Assert.False(h.Service.SubmitLocalClip(new LocalText("", null)) && created > 1);
    }
}
