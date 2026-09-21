using System.Text.Json;
using System.Text.Json.Serialization;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Storage;

public sealed class AppSettings
{
    public const string DefaultServerUrl = "https://clip.yikz.dev";
    public const string DefaultHotkey = "Win+Alt+V";

    [JsonPropertyName("server_url")] public string ServerUrl { get; set; } = DefaultServerUrl;
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("sync_text")] public bool SyncText { get; set; } = true;
    [JsonPropertyName("sync_images")] public bool SyncImages { get; set; } = true;
    [JsonPropertyName("sync_files")] public bool SyncFiles { get; set; } = true;
    [JsonPropertyName("auto_apply")] public bool AutoApply { get; set; } = true;
    [JsonPropertyName("auto_download_limit_bytes")] public long AutoDownloadLimitBytes { get; set; } = ProtocolConstants.DefaultAutoDownloadLimit;
    [JsonPropertyName("max_upload_bytes")] public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    [JsonPropertyName("start_with_windows")] public bool StartWithWindows { get; set; } = true;
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = DefaultHotkey;
    [JsonPropertyName("paused")] public bool Paused { get; set; }
    [JsonPropertyName("notify_received")] public bool NotifyReceived { get; set; }
    [JsonPropertyName("local_history_limit")] public int LocalHistoryLimit { get; set; } = 5000;
    [JsonPropertyName("log_level")] public string LogLevel { get; set; } = "info";

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);
        return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<AppSettings>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            var copy = _current.Clone();
            mutate(copy);
            _current = copy;
            snapshot = copy;
            Save(_path, copy);
        }
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch
        {
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllBytes(path);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        }
        return new AppSettings();
    }

    private static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tmp, path, true);
    }
}

public interface ISecureStore
{
    byte[]? Get(string name);

    void Set(string name, byte[] value);

    void Delete(string name);
}

public sealed class InMemorySecureStore : ISecureStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public byte[]? Get(string name)
    {
        lock (_values)
        {
            return _values.TryGetValue(name, out var v) ? (byte[])v.Clone() : null;
        }
    }

    public void Set(string name, byte[] value)
    {
        lock (_values)
        {
            _values[name] = (byte[])value.Clone();
        }
    }

    public void Delete(string name)
    {
        lock (_values)
        {
            _values.Remove(name);
        }
    }
}
