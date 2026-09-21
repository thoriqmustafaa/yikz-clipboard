using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class JsonVectorTests
{
    private static void AssertRoundTrip<T>(JsonElement source, JsonTypeInfo<T> info)
    {
        var value = JsonSerializer.Deserialize(source.GetRawText(), info);
        Assert.NotNull(value);
        var back = JsonNode.Parse(JsonSerializer.Serialize(value!, info));
        var expected = JsonNode.Parse(source.GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, back), $"round trip differs\nexpected: {expected!.ToJsonString()}\nactual:   {back!.ToJsonString()}");
    }

    public static IEnumerable<object[]> WsNames() => Vectors.Items("ws.json", "messages").Select(m => new object[] { m.Str("name") });

    [Theory]
    [MemberData(nameof(WsNames))]
    public void WebSocketMessagesRoundTrip(string name)
    {
        var message = Vectors.WsMessage(name);
        var type = message.Str("type");
        Assert.Equal(type, WsCodec.PeekType(message.GetRawText()));
        var ctx = ProtocolJsonContext.Default;
        switch (type)
        {
            case WsTypes.Hello: AssertRoundTrip(message, ctx.HelloMessage); break;
            case WsTypes.Welcome: AssertRoundTrip(message, ctx.WelcomeMessage); break;
            case WsTypes.Presence: AssertRoundTrip(message, ctx.PresenceMessage); break;
            case WsTypes.DevicesChanged: AssertRoundTrip(message, ctx.WsEnvelope); break;
            case WsTypes.Clip: AssertRoundTrip(message, ctx.ClipMessage); break;
            case WsTypes.ClipDeleted: AssertRoundTrip(message, ctx.ClipDeletedMessage); break;
            case WsTypes.ClipPinned: AssertRoundTrip(message, ctx.ClipPinnedMessage); break;
            case WsTypes.StorageWarning: AssertRoundTrip(message, ctx.StorageWarningMessage); break;
            case WsTypes.Ping: AssertRoundTrip(message, ctx.PingMessage); break;
            case WsTypes.Pong: AssertRoundTrip(message, ctx.PongMessage); break;
            case WsTypes.Error: AssertRoundTrip(message, ctx.WsErrorMessage); break;
            default: throw new InvalidOperationException("unexpected type " + type);
        }
    }

    [Fact]
    public void HelloBuiltByClientMatchesVectorShape()
    {
        var expected = JsonNode.Parse(Vectors.WsMessage("hello").GetRawText())!;
        var json = WsCodec.Hello(new HelloMessage
        {
            DeviceId = "01a05c00-03e0-7c59-b16c-c1bb2928ae88",
            LastSeq = 40,
            AppVersion = "1.0.0",
            Platform = "macos",
        });
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(json)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Vectors.WsMessage("ping_from_client").GetRawText()), JsonNode.Parse(WsCodec.Ping(1789985400000))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Vectors.WsMessage("pong_from_client").GetRawText()), JsonNode.Parse(WsCodec.Pong(1789985420000))));
        Assert.Equal(1789985420000, WsCodec.ReadTs(Vectors.WsMessage("ping_from_server").GetRawText()));
    }

    [Fact]
    public void WelcomeParsesTimestampsAndDevices()
    {
        var welcome = JsonSerializer.Deserialize(Vectors.WsMessage("welcome").GetRawText(), ProtocolJsonContext.Default.WelcomeMessage)!;
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 10, 10, 0, TimeSpan.Zero), welcome.ServerTime);
        Assert.Equal(44, welcome.CurrentSeq);
        Assert.Equal(17, welcome.StateRev);
        Assert.Equal(2, welcome.OnlineDevices.Count);
        Assert.Equal("android", welcome.OnlineDevices[1].Platform);
    }

    [Fact]
    public void CloseCodesMatchConstants()
    {
        var codes = Vectors.Items("ws.json", "close_codes").Select(c => (int)c.Long("code")).ToHashSet();
        Assert.Contains(WsCloseCodes.Normal, codes);
        Assert.Contains(WsCloseCodes.GoingAway, codes);
        Assert.Contains(WsCloseCodes.Unauthorized, codes);
        Assert.Contains(WsCloseCodes.TooManyConnections, codes);
        Assert.Contains(WsCloseCodes.ProtocolUnsupported, codes);
        Assert.Contains(WsCloseCodes.HelloRequired, codes);
        Assert.Contains(WsCloseCodes.HeartbeatTimeout, codes);
    }

    [Fact]
    public void UnknownFieldsAndTypesAreTolerated()
    {
        var json = """{"type":"clip_pinned","id":"01a0c368-81e2-7460-8f15-1684608b9fb9","pinned":true,"state_rev":3,"future":{"x":1}}""";
        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ClipPinnedMessage)!;
        Assert.True(msg.Pinned);
        Assert.Equal("brand_new", WsCodec.PeekType("""{"type":"brand_new","a":1}"""));
        Assert.Null(WsCodec.PeekType("not json"));
        Assert.Null(WsCodec.PeekType("[1,2]"));
    }

    public static IEnumerable<object[]> HttpNames() => Vectors.Items("http.json", "examples").Select(m => new object[] { m.Str("name") });

    [Theory]
    [MemberData(nameof(HttpNames))]
    public void HttpBodiesRoundTrip(string name)
    {
        var example = Vectors.HttpExample(name);
        var status = (int)example.Long("status");
        var path = example.Str("path");
        var method = example.Str("method");
        var ctx = ProtocolJsonContext.Default;
        if (example.TryGetProperty("request_body", out var req) && req.ValueKind == JsonValueKind.Object && status < 300)
        {
            switch (path)
            {
                case "/api/login": AssertRoundTrip(req, ctx.LoginRequest); break;
                case "/api/account/key-check": AssertRoundTrip(req, ctx.KeyCheckRequest); break;
                case "/api/items": AssertRoundTrip(req, ctx.CreateItemRequest); break;
                default:
                    if (path.EndsWith("/commit")) AssertRoundTrip(req, ctx.CommitRequest);
                    else if (path.EndsWith("/pin")) AssertRoundTrip(req, ctx.PinRequest);
                    else if (path.StartsWith("/api/devices/")) AssertRoundTrip(req, ctx.RenameDeviceRequest);
                    else throw new InvalidOperationException("unmapped request " + name);
                    break;
            }
        }
        if (!example.TryGetProperty("response_body", out var res) || res.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (status >= 400)
        {
            AssertRoundTrip(res, ctx.ErrorResponse);
            var err = JsonSerializer.Deserialize(res.GetRawText(), ctx.ErrorResponse)!;
            Assert.False(string.IsNullOrEmpty(err.Code));
            return;
        }
        var basePath = path.Split('?')[0];
        if (basePath == "/api/login") AssertRoundTrip(res, ctx.LoginResponse);
        else if (basePath == "/api/me") AssertRoundTrip(res, ctx.MeResponse);
        else if (basePath == "/api/devices") AssertRoundTrip(res, ctx.DevicesResponse);
        else if (basePath.StartsWith("/api/devices/")) AssertRoundTrip(res, ctx.DeviceInfo);
        else if (basePath == "/api/history") AssertRoundTrip(res, ctx.HistoryResponse);
        else if (basePath == "/api/history/index") AssertRoundTrip(res, ctx.HistoryIndexResponse);
        else if (basePath == "/api/storage") AssertRoundTrip(res, ctx.StorageInfo);
        else if (basePath == "/healthz") AssertRoundTrip(res, ctx.HealthResponse);
        else if (basePath.StartsWith("/api/items")) AssertRoundTrip(res, ctx.ItemHeader);
        else throw new InvalidOperationException("unmapped response " + name + " " + method);
    }

    [Fact]
    public void MeLimitsParse()
    {
        var me = JsonSerializer.Deserialize(Vectors.HttpExample("me").GetProperty("response_body").GetRawText(), ProtocolJsonContext.Default.MeResponse)!;
        Assert.NotNull(me.Limits);
        Assert.Equal(ProtocolConstants.InlineMaxBytes, me.Limits!.InlineMaxBytes);
        Assert.Equal(ProtocolConstants.ChunkSizeBytes, me.Limits.ChunkSizeBytes);
        Assert.True(me.Device.Current);
        Assert.Equal(1, me.ProtocolVersion);
        Assert.Equal("pbkdf2-sha256", me.Kdf.Algorithm);
    }

    [Fact]
    public void HistoryResponsesHaveNoPayload()
    {
        foreach (var name in new[] { "history_newest", "history_before", "history_after_catch_up" })
        {
            var res = JsonSerializer.Deserialize(Vectors.HttpExample(name).GetProperty("response_body").GetRawText(), ProtocolJsonContext.Default.HistoryResponse)!;
            Assert.NotEmpty(res.Items);
            Assert.All(res.Items, i => Assert.Null(i.Payload));
        }
        var after = JsonSerializer.Deserialize(Vectors.HttpExample("history_after_catch_up").GetProperty("response_body").GetRawText(), ProtocolJsonContext.Default.HistoryResponse)!;
        Assert.True(after.Items.Zip(after.Items.Skip(1)).All(p => p.First.Seq < p.Second.Seq));
    }

    [Theory]
    [InlineData("2026-09-21T10:00:01.250Z", "2026-09-21T10:00:01.250Z")]
    [InlineData("2026-09-21T10:00:01Z", "2026-09-21T10:00:01.000Z")]
    [InlineData("2026-09-21T12:00:01.5+02:00", "2026-09-21T10:00:01.500Z")]
    [InlineData("2026-09-21t10:00:01.123456z", "2026-09-21T10:00:01.123Z")]
    public void TimestampsParseAnyRfc3339AndFormatExactly(string input, string formatted)
    {
        Assert.Equal(formatted, Timestamps.Format(Timestamps.Parse(input)));
    }

    [Fact]
    public void InvalidTimestampRejected()
    {
        Assert.False(Timestamps.TryParse("yesterday", out _));
        Assert.False(Timestamps.TryParse("2026-09-21", out _));
    }
}
