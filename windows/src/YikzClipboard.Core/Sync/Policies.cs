using System.Text;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Sync;

public sealed class EchoGuard
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public EchoGuard(int capacity = 32)
    {
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _set.Count;
            }
        }
    }

    public void Remember(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return;
        }
        lock (_gate)
        {
            if (_set.Contains(contentHash))
            {
                _order.Remove(contentHash);
                _order.AddLast(contentHash);
                return;
            }
            _set.Add(contentHash);
            _order.AddLast(contentHash);
            while (_order.Count > _capacity)
            {
                var first = _order.First!.Value;
                _order.RemoveFirst();
                _set.Remove(first);
            }
        }
    }

    public bool Contains(string contentHash)
    {
        lock (_gate)
        {
            return _set.Contains(contentHash);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _set.Clear();
            _order.Clear();
        }
    }
}

public enum OutgoingDecision
{
    Upload,
    SkipRecentHash,
    SkipNormalizedTextHash,
    SkipSameAsNewest,
}

public static class OutgoingPolicy
{
    public static OutgoingDecision Decide(string contentHash, string? textForNormalization, KeyMaterial keys, EchoGuard guard, string? newestCachedHash)
    {
        if (guard.Contains(contentHash))
        {
            return OutgoingDecision.SkipRecentHash;
        }
        if (textForNormalization != null && textForNormalization.Contains("\r\n", StringComparison.Ordinal))
        {
            var normalized = textForNormalization.Replace("\r\n", "\n", StringComparison.Ordinal);
            var normalizedHash = keys.ContentHash(Encoding.UTF8.GetBytes(normalized));
            if (guard.Contains(normalizedHash))
            {
                return OutgoingDecision.SkipNormalizedTextHash;
            }
        }
        if (newestCachedHash != null && string.Equals(newestCachedHash, contentHash, StringComparison.Ordinal))
        {
            return OutgoingDecision.SkipSameAsNewest;
        }
        return OutgoingDecision.Upload;
    }
}

public static class AutoApplyPolicy
{
    public static bool IsEligible(ItemHeader item, string ownDeviceId, long appliedSeq, DateTimeOffset serverNow)
    {
        if (string.Equals(item.DeviceId, ownDeviceId, StringComparison.Ordinal))
        {
            return false;
        }
        if (item.Seq <= appliedSeq)
        {
            return false;
        }
        var age = serverNow - item.CreatedAt;
        return age <= ProtocolConstants.AutoApplyMaxAge;
    }

    public static ItemHeader? SelectAfterCatchUp(IEnumerable<ItemHeader> items, string ownDeviceId, long appliedSeq, DateTimeOffset serverNow)
    {
        ItemHeader? best = null;
        foreach (var item in items)
        {
            if (IsEligible(item, ownDeviceId, appliedSeq, serverNow) && (best == null || item.Seq > best.Seq))
            {
                best = item;
            }
        }
        return best;
    }

    public static long HighestSeq(IEnumerable<ItemHeader> items, long current)
    {
        var max = current;
        foreach (var item in items)
        {
            if (item.Seq > max)
            {
                max = item.Seq;
            }
        }
        return max;
    }
}

public enum StateRevAction
{
    Store,
    Reconcile,
    Ignore,
}

public static class StateRevPolicy
{
    public static StateRevAction OnEvent(long? stored, long eventRev)
    {
        if (!stored.HasValue)
        {
            return StateRevAction.Reconcile;
        }
        if (eventRev == stored.Value + 1)
        {
            return StateRevAction.Store;
        }
        if (eventRev > stored.Value + 1)
        {
            return StateRevAction.Reconcile;
        }
        return StateRevAction.Ignore;
    }
}
