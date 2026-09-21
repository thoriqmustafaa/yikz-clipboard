using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public sealed class EngineHarness : IDisposable
{
    public const string MacBook = "01a05c00-03e0-7c59-b16c-c1bb2928ae88";
    public const string Pixel = "01a05c04-97c0-7c1c-bdb7-1933ae6945c4";
    public const string Desktop = "01a06158-ba80-7326-af5c-c9255f9309c8";
    public const string ServerId = "01a05bfb-7000-7691-98e4-301030971d0c";
    public const string LargeId = "01a0c36d-10e0-7a56-86e3-483e2f79d9c5";

    public EngineHarness(string ownDevice)
    {
        Root = Path.Combine(Path.GetTempPath(), "yikz-test-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Store = HistoryStore.InMemory();
        Http = new FakeHttpHandler();
        Api = new ApiClient(new HttpClient(Http), new Uri("https://clip.test/")) { Token = "yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw" };
        Keys = new KeyMaterial(Vectors.Hex("445b4a046c63fd1e1aa196088b1b75665ecba3d831addb5bf5a9908236767294"));
        Clock = new FixedClock(new DateTimeOffset(2026, 9, 21, 10, 10, 0, TimeSpan.Zero));
        Sink = new FakeSink();
        Notifier = new FakeNotifier();
        Guard = new EchoGuard();
        Log = new TestLog();
        Settings = new AppSettings();
        Fetcher = new ContentFetcher(Api, Store, Paths, Log);
        OwnDevice = ownDevice;
        Engine = new SyncEngine(Api, Store, Fetcher, Sink, Notifier, Guard, Clock, Log, () => new SyncContext
        {
            Keys = Keys,
            DeviceId = OwnDevice,
            SaltB64 = "XB6PKps9R+agxPgdLmuacw==",
            Settings = () => Settings,
        });
        RouteVectors();
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public HistoryStore Store { get; }
    public FakeHttpHandler Http { get; }
    public ApiClient Api { get; }
    public KeyMaterial Keys { get; }
    public FixedClock Clock { get; }
    public FakeSink Sink { get; }
    public FakeNotifier Notifier { get; }
    public EchoGuard Guard { get; }
    public TestLog Log { get; }
    public AppSettings Settings { get; }
    public ContentFetcher Fetcher { get; }
    public SyncEngine Engine { get; }
    public string OwnDevice { get; }

    public static string Body(string example) => Vectors.HttpExample(example).GetProperty("response_body").GetRawText();

    private void RouteVectors()
    {
        Http.On("GET", "/api/history?after=40&limit=500", FakeResponse.Json(200, Body("history_after_catch_up")));
        Http.On("GET", "/api/history/index", FakeResponse.Json(200, Body("history_index")));
        Http.On("GET", "/api/me", FakeResponse.Json(200, Body("me")));
        var large = Vectors.Named("items.json", "items", "large");
        var content = Vectors.LargeContent();
        foreach (var c in large.GetProperty("chunks").EnumerateArray())
        {
            var index = (int)c.Long("index");
            var length = Content.Chunking.ChunkPlainLength(content.Length, index);
            var sealedChunk = CryptoBox.SealWithNonce(Keys.Key, Vectors.Hex(c.Str("nonce_hex")), Aad.Chunk(LargeId, index, 3), content.AsSpan((int)Content.Chunking.ChunkOffset(index), length));
            Http.On("GET", $"/api/items/{LargeId}/chunks/{index}", FakeResponse.Binary(sealedChunk));
        }
    }

    public void Seed(long lastSeq, long? stateRev, long appliedSeq, string? serverId = ServerId)
    {
        Store.SaveSyncState(new SyncState { ServerId = serverId, LastSeq = lastSeq, StateRev = stateRev, AppliedSeq = appliedSeq });
    }

    public SyncEngine Rebuild()
    {
        return new SyncEngine(Api, Store, Fetcher, Sink, Notifier, Guard, Clock, Log, () => new SyncContext
        {
            Keys = Keys,
            DeviceId = OwnDevice,
            SaltB64 = "XB6PKps9R+agxPgdLmuacw==",
            Settings = () => Settings,
        });
    }

    public static string WelcomeJson(long currentSeq = 44, long stateRev = 17, string serverId = ServerId)
    {
        var node = JsonNode.Parse(Vectors.WsMessage("welcome").GetRawText())!;
        node["current_seq"] = currentSeq;
        node["state_rev"] = stateRev;
        node["server_id"] = serverId;
        return node.ToJsonString();
    }

    public static string FreshTextClip(long seq, string device, DateTimeOffset createdAt)
    {
        var node = JsonNode.Parse(Vectors.WsMessage("clip_inline_text").GetRawText())!;
        node["item"]!["seq"] = seq;
        node["item"]!["device_id"] = device;
        node["item"]!["created_at"] = Timestamps.Format(createdAt);
        return node.ToJsonString();
    }

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
        }
    }
}

public class SyncEngineTests
{
    [Fact]
    public async Task CatchUpAppliesNewestEligibleChunkedItem()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: null, appliedSeq: 40);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        var written = await h.Sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var files = Assert.IsType<ReceivedFiles>(written);
        Assert.Equal(EngineHarness.LargeId, files.ItemId);
        Assert.Single(files.Paths);
        Assert.Equal("pattern.bin", Path.GetFileName(files.Paths[0]));
        var data = File.ReadAllBytes(files.Paths[0]);
        Assert.Equal(8389608, data.Length);
        Assert.Equal((byte)(1000 % 251), data[1000]);
        Assert.StartsWith(h.Paths.ReceivedDir, files.Paths[0]);
        var state = engine.StateSnapshot;
        Assert.Equal(44, state.LastSeq);
        Assert.Equal(44, state.AppliedSeq);
        Assert.Equal(17, state.StateRev);
        Assert.Equal(4, h.Store.Count());
        Assert.Single(h.Sink.Written);
        Assert.True(h.Guard.Contains("e4684803f912ef0a936551a983a63a2681784ef3e5c83f53359f4a3ecb4ee4cf"));
        Assert.Equal(1, h.Http.Count("GET", "/api/history?after=40&limit=500"));
        Assert.Equal(1, h.Http.Count("GET", "/api/history/index"));
        Assert.All(h.Http.Requests, r => Assert.Equal("Bearer yc_WMmC_xkE6-ZpL_QDaSgkhu0mZPAO1jhw3PEkbkI4Ipw", r.Authorization));
    }

    [Fact]
    public async Task OwnItemsAndOldItemsAreNotApplied()
    {
        using var h = new EngineHarness(EngineHarness.MacBook);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        await Task.Delay(100);
        Assert.Empty(h.Sink.Written);
        var state = engine.StateSnapshot;
        Assert.Equal(44, state.AppliedSeq);
        Assert.Equal(44, state.LastSeq);
        Assert.Equal(0, h.Http.Count("GET", "/api/history/index"));
        var persisted = h.Store.LoadSyncState();
        Assert.Equal(44, persisted.LastSeq);
        Assert.Equal(EngineHarness.ServerId, persisted.ServerId);
    }

    [Fact]
    public async Task LargeItemAboveLimitNotifiesInsteadOfDownloading()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Settings.AutoDownloadLimitBytes = 1024 * 1024;
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        await Wait.UntilAsync(() => h.Notifier.Large.Count == 1);
        Assert.Empty(h.Sink.Written);
        Assert.Equal(0, h.Http.Count("GET", $"/api/items/{EngineHarness.LargeId}/chunks/0"));
        Assert.Equal(44, engine.StateSnapshot.AppliedSeq);
        var entry = h.Store.Get(EngineHarness.LargeId)!;
        Assert.True(await engine.ApplyAsync(entry, null, true, CancellationToken.None));
        Assert.Single(h.Sink.Written);
    }

    [Fact]
    public async Task LiveClipAfterCatchUpIsAppliedOnce()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 44, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow.AddSeconds(-1)));
        var written = await h.Sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var text = Assert.IsType<ReceivedText>(written);
        Assert.Equal("Hello from yikz-clipboard \U0001F44B\nSecond line.", text.Text);
        Assert.Equal(45, engine.StateSnapshot.LastSeq);
        Assert.Equal(45, engine.StateSnapshot.AppliedSeq);
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow));
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(46, EngineHarness.Pixel, h.Clock.UtcNow));
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(47, EngineHarness.Desktop, h.Clock.UtcNow.AddMinutes(-6)));
        await Task.Delay(200);
        Assert.Single(h.Sink.Written);
        Assert.Equal(47, engine.StateSnapshot.AppliedSeq);
        Assert.NotNull(h.Store.Get("01a0c368-81e2-7460-8f15-1684608b9fb9"));
        Assert.NotNull(h.Store.GetPayload("01a0c368-81e2-7460-8f15-1684608b9fb9"));
    }

    [Fact]
    public async Task LiveClipsDuringCatchUpAreDeferredAndOnlyNewestApplied()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40);
        using var gate = new ManualResetEventSlim(false);
        var body = EngineHarness.Body("history_after_catch_up");
        h.Http.On("GET", "/api/history?after=40&limit=500", (_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return FakeResponse.Json(200, body);
        });
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson(currentSeq: 44));
        await Wait.UntilAsync(() => h.Http.Requests.Any(r => r.PathAndQuery.StartsWith("/api/history?after=40")));
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow));
        await Task.Delay(100);
        Assert.Empty(h.Sink.Written);
        Assert.NotNull(h.Store.Get("01a0c368-81e2-7460-8f15-1684608b9fb9"));
        Assert.True(engine.IsCatchingUp);
        gate.Set();
        await engine.WelcomeTask;
        var written = await h.Sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<ReceivedText>(written);
        await Task.Delay(200);
        Assert.Single(h.Sink.Written);
        Assert.Equal(0, h.Http.Count("GET", $"/api/items/{EngineHarness.LargeId}/chunks/0"));
        Assert.Equal(45, engine.StateSnapshot.LastSeq);
        Assert.Equal(45, engine.StateSnapshot.AppliedSeq);
        Assert.False(engine.IsCatchingUp);
    }

    [Fact]
    public async Task InterruptedCatchUpDoesNotAdvanceLastSeq()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40);
        h.Http.On("GET", "/api/history?after=40&limit=500", FakeResponse.Json(500, """{"code":"internal","message":"boom"}"""));
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await Wait.UntilAsync(() => h.Http.Count("GET", "/api/history?after=40&limit=500") >= 1);
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow));
        await Task.Delay(200);
        Assert.Equal(40, engine.StateSnapshot.LastSeq);
        Assert.Equal(40, h.Store.LoadSyncState().LastSeq);
        engine.CancelPending();
    }

    [Fact]
    public async Task InitialSyncPagesBackwardsAndAppliesNothing()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 0, stateRev: null, appliedSeq: 0, serverId: null);
        var newest = JsonNode.Parse(EngineHarness.Body("history_newest"))!;
        h.Http.On("GET", "/api/history?before=45&limit=500", FakeResponse.Json(200, newest.ToJsonString()));
        var lastSeq = newest["items"]!.AsArray().Min(i => (long)i!["seq"]!);
        h.Http.On("GET", $"/api/history?before={lastSeq}&limit=500", FakeResponse.Json(200, EngineHarness.Body("history_before")));
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        await Task.Delay(100);
        Assert.Empty(h.Sink.Written);
        Assert.Equal(4, h.Store.Count());
        var state = engine.StateSnapshot;
        Assert.Equal(44, state.LastSeq);
        Assert.Equal(44, state.AppliedSeq);
        Assert.Equal(17, state.StateRev);
        Assert.Equal(EngineHarness.ServerId, state.ServerId);
    }

    [Fact]
    public async Task ServerIdChangeClearsCacheAndResyncs()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40, serverId: "01a05bfb-7000-7691-98e4-000000000000");
        var stale = new ItemHeader { Id = "01926f3c-8d2a-7b3e-9f10-0123456789ab", Seq = 3, DeviceId = "x", Kind = "text", Size = 1, Meta = "AAAA", ContentHash = new string('0', 64) };
        h.Store.Upsert(new HistoryEntry(stale, null, MetaState.Corrupt));
        var page = JsonNode.Parse(EngineHarness.Body("history_after_catch_up"))!;
        page["has_more"] = false;
        h.Http.On("GET", "/api/history?before=45&limit=500", FakeResponse.Json(200, page.ToJsonString()));
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        Assert.Null(h.Store.Get(stale.Id));
        Assert.Equal(1, h.Http.Count("GET", "/api/me"));
        Assert.Equal(EngineHarness.ServerId, engine.StateSnapshot.ServerId);
        Assert.Equal(44, engine.StateSnapshot.LastSeq);
        Assert.Empty(h.Sink.Written);
    }

    [Fact]
    public async Task ServerIdChangeWithDifferentSaltInvalidatesKey()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 40, serverId: "01a05bfb-7000-7691-98e4-000000000000");
        var me = JsonNode.Parse(EngineHarness.Body("me"))!;
        me["salt"] = "ABEiM0RVZneImaq7zN3u/w==";
        h.Http.On("GET", "/api/me", FakeResponse.Json(200, me.ToJsonString()));
        var engine = h.Rebuild();
        var invalid = new TaskCompletionSource<string>();
        engine.KeyInvalid += m => invalid.TrySetResult(m);
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await invalid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.Store.Count());
    }

    [Fact]
    public async Task ReconcileRemovesMissingAndUpdatesPins()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 40, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        Assert.Equal(4, h.Store.Count());
        var index = JsonNode.Parse(EngineHarness.Body("history_index"))!;
        var items = index["items"]!.AsArray();
        var removed = (string)items[1]!["id"]!;
        items.RemoveAt(1);
        items[0]!["pinned"] = true;
        index["state_rev"] = 30;
        h.Http.On("GET", "/api/history/index", FakeResponse.Json(200, index.ToJsonString()));
        engine.HandleMessage("clip_deleted", """{"type":"clip_deleted","ids":["01a0c36b-3c20-7558-a621-6f947a818022"],"reason":"user","state_rev":25}""");
        await Wait.UntilAsync(() => engine.StateSnapshot.StateRev == 30);
        Assert.Null(h.Store.Get(removed));
        Assert.Null(h.Store.Get("01a0c36b-3c20-7558-a621-6f947a818022"));
        Assert.True(h.Store.Get((string)items[0]!["id"]!)!.Pinned);
    }

    [Fact]
    public async Task SequentialStateEventsAreStoredWithoutReconcile()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 44, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        engine.HandleMessage("clip_pinned", """{"type":"clip_pinned","id":"01a0c368-81e2-7460-8f15-1684608b9fb9","pinned":true,"state_rev":18}""");
        engine.HandleMessage("clip_deleted", Vectors.WsMessage("clip_deleted_retention").GetRawText());
        engine.HandleMessage("clip_deleted", Vectors.WsMessage("clip_deleted_user").GetRawText());
        await Task.Delay(100);
        Assert.Equal(19, engine.StateSnapshot.StateRev);
        Assert.Equal(0, h.Http.Count("GET", "/api/history/index"));
    }

    [Fact]
    public async Task TamperedPayloadIsNeverWritten()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 44, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        var node = JsonNode.Parse(EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow))!;
        var payload = Convert.FromBase64String((string)node["item"]!["payload"]!);
        payload[20] ^= 0xFF;
        node["item"]!["payload"] = Convert.ToBase64String(payload);
        engine.HandleMessage("clip", node.ToJsonString());
        await Task.Delay(300);
        Assert.Empty(h.Sink.Written);
        Assert.Contains(h.Log.Lines, l => l.Contains("verification"));
    }

    [Fact]
    public async Task ContentHashMismatchIsRejected()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Seed(lastSeq: 44, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        var node = JsonNode.Parse(EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow))!;
        node["item"]!["content_hash"] = new string('a', 64);
        engine.HandleMessage("clip", node.ToJsonString());
        await Task.Delay(300);
        Assert.Empty(h.Sink.Written);
    }

    [Fact]
    public async Task ReceivingKindCanBeDisabled()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Settings.SyncText = false;
        h.Seed(lastSeq: 44, stateRev: 17, appliedSeq: 44);
        var engine = h.Rebuild();
        engine.HandleMessage("welcome", EngineHarness.WelcomeJson());
        await engine.WelcomeTask;
        engine.HandleMessage("clip", EngineHarness.FreshTextClip(45, EngineHarness.MacBook, h.Clock.UtcNow));
        await Task.Delay(200);
        Assert.Empty(h.Sink.Written);
        Assert.Equal(45, engine.StateSnapshot.AppliedSeq);
        Assert.NotNull(h.Store.Get("01a0c368-81e2-7460-8f15-1684608b9fb9"));
    }

    [Fact]
    public void UnknownMessageTypesAreIgnored()
    {
        using var h = new EngineHarness(EngineHarness.Pixel);
        h.Engine.HandleMessage("future_type", """{"type":"future_type","x":1}""");
        h.Engine.HandleMessage("clip", "{not json");
        Assert.Empty(h.Sink.Written);
    }
}
