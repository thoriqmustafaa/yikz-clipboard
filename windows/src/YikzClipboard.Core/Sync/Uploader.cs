using System.Text;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Sync;

public sealed class PreparedContent : IDisposable
{
    public required string Kind { get; init; }
    public required long Size { get; init; }
    public required string ContentHash { get; init; }
    public required ItemMeta Meta { get; init; }
    public byte[]? Bytes { get; init; }
    public string? TempFile { get; init; }
    public string? Text { get; init; }
    public byte[]? PngForThumb { get; init; }

    public Stream OpenRead()
    {
        if (Bytes != null)
        {
            return new MemoryStream(Bytes, false);
        }
        return new FileStream(TempFile!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
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

public sealed class UploadException : Exception
{
    public UploadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public static class ContentPreparer
{
    public static PreparedContent? Prepare(LocalClip clip, KeyMaterial keys, AppPaths paths, ILog log, long maxBytes)
    {
        switch (clip)
        {
            case LocalText t:
                {
                    if (string.IsNullOrEmpty(t.Text))
                    {
                        return null;
                    }
                    var bytes = Encoding.UTF8.GetBytes(t.Text);
                    if (bytes.Length > maxBytes)
                    {
                        log.Warn("send", "text is larger than the upload limit, skipped");
                        return null;
                    }
                    return new PreparedContent
                    {
                        Kind = ProtocolConstants.KindText,
                        Size = bytes.Length,
                        ContentHash = keys.ContentHash(bytes),
                        Meta = new ItemMeta
                        {
                            V = 1,
                            Mime = ProtocolConstants.MimeText,
                            Preview = TextPreview.Make(t.Text),
                            Sha256 = CryptoBox.Sha256Hex(bytes),
                            SourceApp = t.SourceApp,
                        },
                        Bytes = bytes,
                        Text = t.Text,
                    };
                }
            case LocalImage img:
                {
                    if (img.Png.Length == 0 || img.Png.Length > maxBytes)
                    {
                        return null;
                    }
                    var w = img.Width;
                    var h = img.Height;
                    if (Imaging.PngInfo.TryGetSize(img.Png, out var pw, out var ph))
                    {
                        w = pw;
                        h = ph;
                    }
                    return new PreparedContent
                    {
                        Kind = ProtocolConstants.KindImage,
                        Size = img.Png.Length,
                        ContentHash = keys.ContentHash(img.Png),
                        Meta = new ItemMeta
                        {
                            V = 1,
                            Mime = ProtocolConstants.MimePng,
                            Preview = "",
                            Sha256 = CryptoBox.Sha256Hex(img.Png),
                            Image = new ImageInfo { Width = w, Height = h },
                            SourceApp = img.SourceApp,
                        },
                        Bytes = img.Png,
                        PngForThumb = img.Png,
                    };
                }
            case LocalFiles files:
                return PrepareFiles(files, keys, paths, log, maxBytes);
            default:
                return null;
        }
    }

    private static PreparedContent? PrepareFiles(LocalFiles clip, KeyMaterial keys, AppPaths paths, ILog log, long maxBytes)
    {
        var sources = new List<(string Path, long Length, string RawName)>();
        foreach (var p in clip.Paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    log.Info("send", "skipping directory (folders are not synced): " + System.IO.Path.GetFileName(p.TrimEnd('\\', '/')));
                    continue;
                }
                var info = new FileInfo(p);
                if (!info.Exists)
                {
                    log.Warn("send", "file not found: " + info.Name);
                    continue;
                }
                var resolved = info;
                if (info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(true);
                    if (target is not FileInfo fi || !fi.Exists)
                    {
                        log.Info("send", "skipping link that does not point to a file: " + info.Name);
                        continue;
                    }
                    resolved = fi;
                }
                sources.Add((resolved.FullName, resolved.Length, info.Name));
            }
            catch (Exception ex)
            {
                log.Warn("send", "cannot read " + System.IO.Path.GetFileName(p), ex);
            }
        }
        if (sources.Count == 0)
        {
            return null;
        }
        if (sources.Count > ProtocolConstants.MaxFilesPerItem)
        {
            log.Warn("send", "more than 1000 files, only the first 1000 are sent");
            sources = sources.Take(ProtocolConstants.MaxFilesPerItem).ToList();
        }
        var names = Ycf1.MakeUniqueNames(sources.Select(s => s.RawName));
        var archiveSize = Ycf1.ArchiveSize(names.Select((n, i) => (n, sources[i].Length)));
        if (archiveSize > maxBytes)
        {
            log.Warn("send", "files are larger than the upload limit, skipped");
            return null;
        }
        var ycfSources = sources.Select((s, i) => new Ycf1Source(names[i], s.Length, () => new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))).ToList();
        var meta = new ItemMeta
        {
            V = 1,
            Mime = ProtocolConstants.MimeFiles,
            Preview = TextPreview.Make(string.Join("\n", names)),
            Sha256 = "",
            Files = names.Select((n, i) => new FileInfoEntry { Name = n, Size = sources[i].Length }).ToList(),
            SourceApp = clip.SourceApp,
        };
        if (Chunking.IsInline(archiveSize))
        {
            using var ms = new MemoryStream((int)archiveSize);
            Ycf1.Write(ms, ycfSources);
            var bytes = ms.ToArray();
            meta.Sha256 = CryptoBox.Sha256Hex(bytes);
            return new PreparedContent
            {
                Kind = ProtocolConstants.KindFiles,
                Size = bytes.Length,
                ContentHash = keys.ContentHash(bytes),
                Meta = meta,
                Bytes = bytes,
            };
        }
        var temp = System.IO.Path.Combine(paths.TempDir, "upload." + Guid.NewGuid().ToString("N") + ".ycf1");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                Ycf1.Write(fs, ycfSources);
            }
            using var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            var (hash, sha, length) = ContentDigest.Compute(keys.ContentHashKey, read);
            meta.Sha256 = sha;
            return new PreparedContent
            {
                Kind = ProtocolConstants.KindFiles,
                Size = length,
                ContentHash = hash,
                Meta = meta,
                TempFile = temp,
            };
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
}

public sealed class Uploader
{
    private readonly ApiClient _api;
    private readonly IImageTools _imageTools;
    private readonly ILog _log;
    private readonly Func<CancellationToken, Task<bool>> _ensureKeyCheck;

    public Uploader(ApiClient api, IImageTools imageTools, ILog log, Func<CancellationToken, Task<bool>> ensureKeyCheck)
    {
        _api = api;
        _imageTools = imageTools;
        _log = log;
        _ensureKeyCheck = ensureKeyCheck;
    }

    public event Action<string, double>? Progress;

    public async Task<(ItemHeader Header, byte[]? SealedPayload)> UploadAsync(PreparedContent content, KeyMaterial keys, CancellationToken ct)
    {
        var keyCheckRetried = false;
        var idRetried = false;
        while (true)
        {
            var id = UuidV7.New();
            try
            {
                return await UploadOnceAsync(id, content, keys, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.KeyCheckMissing && !keyCheckRetried)
            {
                keyCheckRetried = true;
                _log.Warn("send", "server has no key check, setting it");
                if (!await _ensureKeyCheck(ct).ConfigureAwait(false))
                {
                    throw new UploadException("encryption key was rejected by the server", ex);
                }
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.IdConflict && !idRetried)
            {
                idRetried = true;
                _log.Warn("send", "id conflict, retrying with a new id");
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.ItemTooLarge)
            {
                await TryCancelAsync(id).ConfigureAwait(false);
                throw new UploadException("item does not fit in server storage", ex);
            }
        }
    }

    private async Task TryCancelAsync(string id)
    {
        try
        {
            await _api.DeleteItemAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("send", "cancel pending upload failed: " + ex.Message);
        }
    }

    private async Task<(ItemHeader, byte[]?)> UploadOnceAsync(string id, PreparedContent content, KeyMaterial keys, CancellationToken ct)
    {
        var metaPlain = MetaCodec.Serialize(content.Meta);
        var sealedMeta = keys.Seal(Aad.Meta(id), metaPlain);
        if (sealedMeta.Length > ProtocolConstants.MetaMaxBytes)
        {
            throw new UploadException("item metadata is too large");
        }
        var metaB64 = StrictBase64.Encode(sealedMeta);
        if (content.PngForThumb != null)
        {
            try
            {
                var jpeg = await _imageTools.MakeJpegThumbnailAsync(content.PngForThumb, ProtocolConstants.ThumbnailMaxSide, ProtocolConstants.ThumbMaxBytes - ProtocolConstants.SealOverhead, ct).ConfigureAwait(false);
                if (jpeg != null && jpeg.Length + ProtocolConstants.SealOverhead <= ProtocolConstants.ThumbMaxBytes)
                {
                    var sealedThumb = keys.Seal(Aad.Thumb(id), jpeg);
                    await RetryPolicy.RunAsync(t => _api.PutThumbAsync(id, sealedThumb, t), 5, ct).ConfigureAwait(false);
                }
            }
            catch (ApiException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("send", "thumbnail skipped", ex);
            }
        }
        if (Chunking.IsInline(content.Size))
        {
            byte[] plain;
            using (var s = content.OpenRead())
            using (var ms = new MemoryStream((int)content.Size))
            {
                s.CopyTo(ms);
                plain = ms.ToArray();
            }
            var sealedPayload = keys.Seal(Aad.Payload(id), plain);
            var request = new CreateItemRequest
            {
                Id = id,
                Kind = content.Kind,
                Size = content.Size,
                ChunkCount = 0,
                ContentHash = content.ContentHash,
                Meta = metaB64,
                Payload = StrictBase64.Encode(sealedPayload),
            };
            var header = await RetryPolicy.RunAsync(t => _api.CreateItemAsync(request, t), 6, ct,
                (a, ex) => _log.Warn("send", $"create retry {a + 1}: {ex.Message}")).ConfigureAwait(false);
            Progress?.Invoke(id, 1);
            return (header, sealedPayload);
        }
        var count = Chunking.ChunkCount(content.Size);
        await UploadChunksAsync(id, content, keys, Enumerable.Range(0, count).ToList(), count, ct).ConfigureAwait(false);
        var commit = new CommitRequest
        {
            Kind = content.Kind,
            Size = content.Size,
            ChunkCount = count,
            ContentHash = content.ContentHash,
            Meta = metaB64,
        };
        for (var round = 0; ; round++)
        {
            try
            {
                var header = await RetryPolicy.RunAsync(t => _api.CommitAsync(id, commit, t), 6, ct,
                    (a, ex) => _log.Warn("send", $"commit retry {a + 1}: {ex.Message}")).ConfigureAwait(false);
                Progress?.Invoke(id, 1);
                return (header, null);
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.MissingChunks && round < 3)
            {
                var missing = ex.MissingChunks().Where(i => i >= 0 && i < count).ToList();
                if (missing.Count == 0)
                {
                    throw;
                }
                _log.Warn("send", "server is missing " + missing.Count + " chunks, uploading again");
                await UploadChunksAsync(id, content, keys, missing, count, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task UploadChunksAsync(string id, PreparedContent content, KeyMaterial keys, IReadOnlyList<int> indices, int count, CancellationToken ct)
    {
        var done = 0;
        using var gate = new SemaphoreSlim(3, 3);
        var tasks = indices.Select(async index =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var length = Chunking.ChunkPlainLength(content.Size, index);
                var plain = new byte[length];
                using (var s = content.OpenRead())
                {
                    s.Seek(Chunking.ChunkOffset(index), SeekOrigin.Begin);
                    s.ReadExactly(plain, 0, length);
                }
                var sealedChunk = keys.Seal(Aad.Chunk(id, index, count), plain);
                await RetryPolicy.RunAsync(t => _api.PutChunkAsync(id, index, sealedChunk, sealedChunk.Length, t), 8, ct,
                    (a, ex) => _log.Warn("send", $"chunk {index} retry {a + 1}: {ex.Message}")).ConfigureAwait(false);
                var n = Interlocked.Increment(ref done);
                Progress?.Invoke(id, n / (double)indices.Count);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
