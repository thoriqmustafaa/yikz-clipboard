using System.Text;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class PolicyTests
{
    private sealed class MaxRandom : Random
    {
        public override double NextDouble() => 0.999999999;
    }

    [Fact]
    public void BackoffCeilingDoublesFromHalfSecondToThirty()
    {
        var b = new ReconnectBackoff();
        var expected = new[] { 0.5, 1, 2, 4, 8, 16, 30, 30, 30 };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], b.Ceiling(i).TotalSeconds, 6);
        }
    }

    [Fact]
    public void BackoffIsFullJitterAndIncrementsAttempt()
    {
        var b = new ReconnectBackoff(new MaxRandom());
        var delays = Enumerable.Range(0, 8).Select(_ => b.NextDelay().TotalSeconds).ToList();
        Assert.Equal(8, b.Attempt);
        Assert.InRange(delays[0], 0.49, 0.5);
        Assert.InRange(delays[3], 3.99, 4.0);
        Assert.InRange(delays[7], 29.99, 30.0);
        b.Reset();
        Assert.Equal(0, b.Attempt);
        Assert.InRange(b.NextDelay().TotalSeconds, 0.49, 0.5);
        var random = new ReconnectBackoff(new Random(42));
        for (var i = 0; i < 200; i++)
        {
            var ceiling = random.Ceiling(random.Attempt).TotalSeconds;
            var d = random.NextDelay().TotalSeconds;
            Assert.InRange(d, 0, ceiling);
        }
    }

    [Fact]
    public void HttpRetryDelayCapsAtSixty()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.InRange(RetryPolicy.HttpDelay(i).TotalSeconds, 0, Math.Min(60, Math.Pow(2, i)));
        }
    }

    private static ItemHeader Item(long seq, string device, DateTimeOffset created) => new()
    {
        Id = $"01a0c368-81e2-7460-8f15-{seq:D12}",
        Seq = seq,
        DeviceId = device,
        CreatedAt = created,
        Kind = "text",
    };

    [Fact]
    public void AutoApplyEligibility()
    {
        var now = new DateTimeOffset(2026, 9, 21, 10, 10, 0, TimeSpan.Zero);
        Assert.True(AutoApplyPolicy.IsEligible(Item(5, "other", now.AddSeconds(-300)), "me", 4, now));
        Assert.False(AutoApplyPolicy.IsEligible(Item(5, "other", now.AddSeconds(-300.001)), "me", 4, now));
        Assert.False(AutoApplyPolicy.IsEligible(Item(5, "me", now), "me", 4, now));
        Assert.False(AutoApplyPolicy.IsEligible(Item(4, "other", now), "me", 4, now));
        Assert.True(AutoApplyPolicy.IsEligible(Item(5, "other", now.AddSeconds(30)), "me", 4, now));
    }

    [Fact]
    public void AfterCatchUpOnlyNewestEligibleIsApplied()
    {
        var now = new DateTimeOffset(2026, 9, 21, 10, 10, 0, TimeSpan.Zero);
        var items = new[]
        {
            Item(10, "a", now.AddSeconds(-10)),
            Item(11, "b", now.AddSeconds(-5)),
            Item(12, "me", now.AddSeconds(-1)),
            Item(9, "a", now.AddHours(-3)),
        };
        var chosen = AutoApplyPolicy.SelectAfterCatchUp(items, "me", 8, now);
        Assert.Equal(11, chosen!.Seq);
        Assert.Equal(12, AutoApplyPolicy.HighestSeq(items, 8));
        Assert.Null(AutoApplyPolicy.SelectAfterCatchUp(items, "me", 12, now));
        Assert.Null(AutoApplyPolicy.SelectAfterCatchUp(new[] { Item(9, "a", now.AddHours(-3)) }, "me", 0, now));
    }

    [Fact]
    public void StateRevPolicyFollowsSpec()
    {
        Assert.Equal(StateRevAction.Store, StateRevPolicy.OnEvent(17, 18));
        Assert.Equal(StateRevAction.Reconcile, StateRevPolicy.OnEvent(17, 19));
        Assert.Equal(StateRevAction.Ignore, StateRevPolicy.OnEvent(17, 17));
        Assert.Equal(StateRevAction.Ignore, StateRevPolicy.OnEvent(17, 3));
        Assert.Equal(StateRevAction.Reconcile, StateRevPolicy.OnEvent(null, 1));
    }

    [Fact]
    public void EchoGuardKeepsLast32()
    {
        var g = new EchoGuard();
        for (var i = 0; i < 40; i++)
        {
            g.Remember("h" + i);
        }
        Assert.Equal(32, g.Count);
        Assert.False(g.Contains("h0"));
        Assert.False(g.Contains("h7"));
        Assert.True(g.Contains("h8"));
        Assert.True(g.Contains("h39"));
        g.Remember("h8");
        g.Remember("new");
        Assert.True(g.Contains("h8"));
        Assert.False(g.Contains("h9"));
    }

    [Fact]
    public void OutgoingPolicySkipsEchoesAndNewest()
    {
        using var keys = new KeyMaterial(Vectors.Hex("445b4a046c63fd1e1aa196088b1b75665ecba3d831addb5bf5a9908236767294"));
        var guard = new EchoGuard();
        var lf = "line one\nline two";
        var crlf = "line one\r\nline two";
        var lfHash = keys.ContentHash(Encoding.UTF8.GetBytes(lf));
        var crlfHash = keys.ContentHash(Encoding.UTF8.GetBytes(crlf));
        Assert.Equal(OutgoingDecision.Upload, OutgoingPolicy.Decide(crlfHash, crlf, keys, guard, null));
        guard.Remember(lfHash);
        Assert.Equal(OutgoingDecision.SkipNormalizedTextHash, OutgoingPolicy.Decide(crlfHash, crlf, keys, guard, null));
        Assert.Equal(OutgoingDecision.SkipRecentHash, OutgoingPolicy.Decide(lfHash, lf, keys, guard, null));
        Assert.Equal(OutgoingDecision.SkipSameAsNewest, OutgoingPolicy.Decide("abc", null, keys, guard, "abc"));
        Assert.Equal(OutgoingDecision.Upload, OutgoingPolicy.Decide("abc", null, keys, guard, "def"));
    }

    [Theory]
    [InlineData("clip.yikz.dev", "https://clip.yikz.dev/", "wss://clip.yikz.dev/ws")]
    [InlineData("https://clip.yikz.dev", "https://clip.yikz.dev/", "wss://clip.yikz.dev/ws")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/", "ws://localhost:8080/ws")]
    [InlineData("https://example.com/sub", "https://example.com/sub/", "wss://example.com/sub/ws")]
    public void ServerUrlParsing(string input, string baseUri, string wsUri)
    {
        Assert.True(ApiClient.TryParseServerUrl(input, out var uri));
        Assert.Equal(baseUri, uri.ToString());
        var api = new ApiClient(new HttpClient(), uri);
        Assert.Equal(wsUri, api.WebSocketUri().ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("   ")]
    public void InvalidServerUrls(string input)
    {
        Assert.False(ApiClient.TryParseServerUrl(input, out _));
    }
}
