using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Platform;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public abstract record LocalClip(string? SourceApp);

public sealed record LocalText(string Text, string? SourceApp) : LocalClip(SourceApp);

public sealed record LocalImage(byte[] Png, int Width, int Height, string? SourceApp) : LocalClip(SourceApp);

public sealed record LocalFiles(IReadOnlyList<string> Paths, string? SourceApp) : LocalClip(SourceApp);

public abstract record ReceivedContent(string ItemId, string ContentHash);

public sealed record ReceivedText(string ItemId, string ContentHash, string Text) : ReceivedContent(ItemId, ContentHash);

public sealed record ReceivedImage(string ItemId, string ContentHash, byte[] Png) : ReceivedContent(ItemId, ContentHash);

public sealed record ReceivedFiles(string ItemId, string ContentHash, IReadOnlyList<string> Paths) : ReceivedContent(ItemId, ContentHash);

public interface IClipboardSink
{
    Task WriteAsync(ReceivedContent content, CancellationToken ct);
}

public interface IUserNotifier
{
    void LargeItemAvailable(HistoryEntry entry, string deviceName);

    void ItemReceived(HistoryEntry entry, string deviceName);

    void Problem(string title, string message);
}

public interface IImageTools
{
    Task<byte[]?> MakeJpegThumbnailAsync(byte[] png, int maxSide, int maxBytes, CancellationToken ct);
}

public sealed class NullNotifier : IUserNotifier
{
    public static readonly NullNotifier Instance = new();

    public void LargeItemAvailable(HistoryEntry entry, string deviceName)
    {
    }

    public void ItemReceived(HistoryEntry entry, string deviceName)
    {
    }

    public void Problem(string title, string message)
    {
    }
}

public sealed class NullImageTools : IImageTools
{
    public static readonly NullImageTools Instance = new();

    public Task<byte[]?> MakeJpegThumbnailAsync(byte[] png, int maxSide, int maxBytes, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ReceivedDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TempDir);
    }

    public string Root { get; }

    public string ReceivedDir => Path.Combine(Root, "received");

    public string LogsDir => Path.Combine(Root, "logs");

    public string TempDir => Path.Combine(Root, "tmp");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DatabaseFile => Path.Combine(Root, "history.db");

    public string SecretsDir => Path.Combine(Root, "secrets");
}
