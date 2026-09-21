using System.Security.Cryptography;
using System.Text;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Imaging;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Sync;

public static class ItemCodec
{
    public static HistoryEntry Decrypt(ItemHeader header, KeyMaterial keys)
    {
        var h = header.Clone();
        h.Payload = null;
        try
        {
            var sealedMeta = StrictBase64.Decode(header.Meta);
            if (sealedMeta.Length > ProtocolConstants.MetaMaxBytes)
            {
                return new HistoryEntry(h, null, MetaState.Corrupt);
            }
            var plain = keys.Open(Aad.Meta(header.Id), sealedMeta);
            ItemMeta meta;
            try
            {
                meta = MetaCodec.Deserialize(plain);
            }
            catch (Exception)
            {
                return new HistoryEntry(h, null, MetaState.Corrupt);
            }
            if (meta.V != 1)
            {
                return new HistoryEntry(h, null, MetaState.Unsupported);
            }
            return new HistoryEntry(h, meta, MetaState.Ok);
        }
        catch (Exception ex) when (ex is CryptoException or FormatException)
        {
            return new HistoryEntry(h, null, MetaState.Corrupt);
        }
    }

    public static byte[] OpenInlinePayload(ItemHeader header, byte[] sealedPayload, KeyMaterial keys)
    {
        if (sealedPayload.Length != header.Size + ProtocolConstants.SealOverhead)
        {
            throw new CryptoException("payload length does not match size");
        }
        return keys.Open(Aad.Payload(header.Id), sealedPayload);
    }

    public static void Verify(ReadOnlySpan<byte> content, ItemHeader header, ItemMeta meta, KeyMaterial keys)
    {
        var sha = CryptoBox.Sha256Hex(content);
        if (!string.Equals(sha, meta.Sha256, StringComparison.Ordinal))
        {
            throw new CryptoException("sha256 mismatch");
        }
        var hash = keys.ContentHash(content);
        if (!string.Equals(hash, header.ContentHash, StringComparison.Ordinal))
        {
            throw new CryptoException("content_hash mismatch");
        }
    }
}

public sealed class FetchedContent : IDisposable
{
    private FetchedContent(byte[]? bytes, string? tempFile, long length)
    {
        Bytes = bytes;
        TempFile = tempFile;
        Length = length;
    }

    public byte[]? Bytes { get; }

    public string? TempFile { get; }

    public long Length { get; }

    public static FetchedContent InMemory(byte[] bytes) => new(bytes, null, bytes.Length);

    public static FetchedContent OnDisk(string path, long length) => new(null, path, length);

    public Stream OpenRead()
    {
        if (Bytes != null)
        {
            return new MemoryStream(Bytes, false);
        }
        return new FileStream(TempFile!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
    }

    public byte[] ReadAll()
    {
        return Bytes ?? File.ReadAllBytes(TempFile!);
    }

    public void Dispose()
    {
        if (TempFile != null)
        {
            try
            {
                File.Delete(TempFile);
            }
            catch
            {
            }
        }
    }
}

public sealed class ContentFetcher
{
    private readonly ApiClient _api;
    private readonly HistoryStore _store;
    private readonly AppPaths _paths;
    private readonly ILog _log;

    public ContentFetcher(ApiClient api, HistoryStore store, AppPaths paths, ILog log)
    {
        _api = api;
        _store = store;
        _paths = paths;
        _log = log;
    }

    public async Task<FetchedContent> FetchAsync(HistoryEntry entry, KeyMaterial keys, byte[]? sealedPayload, IProgress<double>? progress, CancellationToken ct)
    {
        if (entry.Meta == null)
        {
            throw new InvalidOperationException("item cannot be decrypted");
        }
        var header = entry.Header;
        if (header.ChunkCount == 0)
        {
            var sealedBytes = sealedPayload ?? _store.GetPayload(header.Id);
            if (sealedBytes == null)
            {
                var full = await RetryPolicy.RunAsync(t => _api.GetItemAsync(header.Id, t), 5, ct).ConfigureAwait(false);
                if (full.Payload == null)
                {
                    throw new ApiException(200, "invalid_response", "inline item has no payload");
                }
                sealedBytes = StrictBase64.Decode(full.Payload);
                _store.SetPayload(header.Id, sealedBytes);
            }
            var plain = ItemCodec.OpenInlinePayload(header, sealedBytes, keys);
            ItemCodec.Verify(plain, header, entry.Meta, keys);
            progress?.Report(1);
            return FetchedContent.InMemory(plain);
        }
        var expectedCount = Chunking.ChunkCount(header.Size);
        if (expectedCount != header.ChunkCount)
        {
            throw new CryptoException("chunk_count does not match size");
        }
        var temp = Path.Combine(_paths.TempDir, header.Id + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var digest = new ContentDigest(keys.ContentHashKey);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                for (var i = 0; i < header.ChunkCount; i++)
                {
                    var index = i;
                    var sealedChunk = await RetryPolicy.RunAsync(t => _api.GetChunkAsync(header.Id, index, t), 8, ct,
                        (a, ex) => _log.Warn("download", $"chunk {index} retry {a + 1}: {ex.Message}")).ConfigureAwait(false);
                    var expectedPlain = Chunking.ChunkPlainLength(header.Size, i);
                    if (sealedChunk.Length != expectedPlain + ProtocolConstants.SealOverhead)
                    {
                        throw new CryptoException($"chunk {i} has unexpected length");
                    }
                    var plain = keys.Open(Aad.Chunk(header.Id, i, header.ChunkCount), sealedChunk);
                    digest.Append(plain);
                    await file.WriteAsync(plain, ct).ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(plain);
                    progress?.Report((i + 1) / (double)header.ChunkCount);
                }
            }
            var (hash, sha) = digest.Finish();
            if (!string.Equals(sha, entry.Meta.Sha256, StringComparison.Ordinal))
            {
                throw new CryptoException("sha256 mismatch");
            }
            if (!string.Equals(hash, header.ContentHash, StringComparison.Ordinal))
            {
                throw new CryptoException("content_hash mismatch");
            }
            return FetchedContent.OnDisk(temp, header.Size);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
            throw;
        }
    }

    public ReceivedContent Materialize(HistoryEntry entry, FetchedContent content)
    {
        var header = entry.Header;
        switch (header.Kind)
        {
            case ProtocolConstants.KindText:
                return new ReceivedText(header.Id, header.ContentHash, Encoding.UTF8.GetString(content.ReadAll()));
            case ProtocolConstants.KindImage:
                {
                    var png = content.ReadAll();
                    if (!PngInfo.IsPng(png))
                    {
                        throw new FormatException("image item is not a PNG");
                    }
                    return new ReceivedImage(header.Id, header.ContentHash, png);
                }
            case ProtocolConstants.KindFiles:
                {
                    var dir = Path.Combine(_paths.ReceivedDir, header.Id);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                    try
                    {
                        using var stream = content.OpenRead();
                        var paths = Ycf1.ExtractTo(stream, content.Length, dir);
                        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                        return new ReceivedFiles(header.Id, header.ContentHash, paths);
                    }
                    catch
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch
                        {
                        }
                        throw;
                    }
                }
            default:
                throw new NotSupportedException("unknown kind " + header.Kind);
        }
    }

    public async Task<byte[]?> GetThumbnailAsync(HistoryEntry entry, KeyMaterial keys, CancellationToken ct)
    {
        var cached = _store.GetThumb(entry.Id);
        if (cached != null)
        {
            return cached;
        }
        if (!entry.Header.HasThumb)
        {
            return null;
        }
        var sealedThumb = await _api.GetThumbAsync(entry.Id, ct).ConfigureAwait(false);
        if (sealedThumb == null)
        {
            return null;
        }
        try
        {
            var jpeg = keys.Open(Aad.Thumb(entry.Id), sealedThumb);
            _store.SetThumb(entry.Id, jpeg);
            return jpeg;
        }
        catch (CryptoException ex)
        {
            _log.Error("thumb", "thumbnail failed to decrypt for " + entry.Id, ex);
            return null;
        }
    }

    public void PruneReceived(TimeSpan maxAge, long maxBytes)
    {
        try
        {
            var root = new DirectoryInfo(_paths.ReceivedDir);
            if (!root.Exists)
            {
                return;
            }
            var dirs = root.GetDirectories().Select(d => (Dir: d, Size: DirSize(d), Time: d.LastWriteTimeUtc)).OrderBy(d => d.Time).ToList();
            var cutoff = DateTime.UtcNow - maxAge;
            long total = dirs.Sum(d => d.Size);
            foreach (var d in dirs)
            {
                if (d.Time < cutoff || total > maxBytes)
                {
                    try
                    {
                        d.Dir.Delete(true);
                        total -= d.Size;
                    }
                    catch (Exception ex)
                    {
                        _log.Debug("cache", "could not remove " + d.Dir.Name + ": " + ex.Message);
                    }
                }
            }
            foreach (var f in new DirectoryInfo(_paths.TempDir).GetFiles())
            {
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-6))
                {
                    try
                    {
                        f.Delete();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("cache", "prune failed", ex);
        }
    }

    private static long DirSize(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
