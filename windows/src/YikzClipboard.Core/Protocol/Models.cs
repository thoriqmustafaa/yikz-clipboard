using System.Text.Json;
using System.Text.Json.Serialization;

namespace YikzClipboard.Core.Protocol;

public sealed class ItemHeader
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("chunk_count")] public int ChunkCount { get; set; }
    [JsonPropertyName("created_at")][JsonConverter(typeof(TimestampJsonConverter))] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("has_thumb")] public bool HasThumb { get; set; }
    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; set; }
    [JsonPropertyName("meta")] public string Meta { get; set; } = "";
    [JsonPropertyName("payload")] public string? Payload { get; set; }

    public ItemHeader Clone()
    {
        return (ItemHeader)MemberwiseClone();
    }
}

public sealed class ImageInfo
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class FileInfoEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ItemMeta
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("mime")] public string Mime { get; set; } = "";
    [JsonPropertyName("preview")] public string Preview { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("image")] public ImageInfo? Image { get; set; }
    [JsonPropertyName("files")] public List<FileInfoEntry>? Files { get; set; }
    [JsonPropertyName("source_app")] public string? SourceApp { get; set; }
}

public sealed class KdfInfo
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("iterations")] public int Iterations { get; set; }
    [JsonPropertyName("key_length")] public int KeyLength { get; set; }
}

public sealed class LoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = ProtocolConstants.Platform;
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
}

public sealed class LoginResponse
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("kdf")] public KdfInfo Kdf { get; set; } = new();
    [JsonPropertyName("key_check")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? KeyCheck { get; set; }
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = "";
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
}

public sealed class ServerLimits
{
    [JsonPropertyName("inline_max_bytes")] public long InlineMaxBytes { get; set; } = ProtocolConstants.InlineMaxBytes;
    [JsonPropertyName("chunk_size_bytes")] public long ChunkSizeBytes { get; set; } = ProtocolConstants.ChunkSizeBytes;
    [JsonPropertyName("max_chunk_body_bytes")] public long MaxChunkBodyBytes { get; set; } = ProtocolConstants.MaxChunkBodyBytes;
    [JsonPropertyName("thumb_max_bytes")] public int ThumbMaxBytes { get; set; } = ProtocolConstants.ThumbMaxBytes;
    [JsonPropertyName("meta_max_bytes")] public int MetaMaxBytes { get; set; } = ProtocolConstants.MetaMaxBytes;
    [JsonPropertyName("max_json_body_bytes")] public int MaxJsonBodyBytes { get; set; } = ProtocolConstants.MaxJsonBodyBytes;
    [JsonPropertyName("max_files_per_item")] public int MaxFilesPerItem { get; set; } = ProtocolConstants.MaxFilesPerItem;
    [JsonPropertyName("history_default_limit")] public int HistoryDefaultLimit { get; set; } = ProtocolConstants.HistoryDefaultLimit;
    [JsonPropertyName("history_max_limit")] public int HistoryMaxLimit { get; set; } = ProtocolConstants.HistoryMaxLimit;
    [JsonPropertyName("max_ws_client_frame_bytes")] public int MaxWsClientFrameBytes { get; set; } = ProtocolConstants.MaxWsClientFrameBytes;
    [JsonPropertyName("max_ws_server_frame_bytes")] public int MaxWsServerFrameBytes { get; set; } = ProtocolConstants.MaxWsServerFrameBytes;
    [JsonPropertyName("max_connections_per_device")] public int MaxConnectionsPerDevice { get; set; } = 8;
    [JsonPropertyName("upload_ttl_seconds")] public int UploadTtlSeconds { get; set; } = 3600;
    [JsonPropertyName("disk_low_max_upload_bytes")] public long DiskLowMaxUploadBytes { get; set; } = 1048576;
}

public sealed class DeviceInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("created_at")][JsonConverter(typeof(TimestampJsonConverter))] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("last_seen_at")][JsonConverter(typeof(TimestampJsonConverter))] public DateTimeOffset LastSeenAt { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("revoked")] public bool Revoked { get; set; }
    [JsonPropertyName("current")] public bool Current { get; set; }
}

public sealed class MeResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("device")] public DeviceInfo Device { get; set; } = new();
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("kdf")] public KdfInfo Kdf { get; set; } = new();
    [JsonPropertyName("key_check")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? KeyCheck { get; set; }
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = "";
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("limits")] public ServerLimits? Limits { get; set; }
}

public sealed class KeyCheckRequest
{
    [JsonPropertyName("key_check")] public string KeyCheck { get; set; } = "";
}

public sealed class DevicesResponse
{
    [JsonPropertyName("devices")] public List<DeviceInfo> Devices { get; set; } = new();
}

public sealed class RenameDeviceRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class CreateItemRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("chunk_count")] public int ChunkCount { get; set; }
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("meta")] public string Meta { get; set; } = "";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
}

public sealed class CommitRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("chunk_count")] public int ChunkCount { get; set; }
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("meta")] public string Meta { get; set; } = "";
}

public sealed class PinRequest
{
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
}

public sealed class HistoryResponse
{
    [JsonPropertyName("items")] public List<ItemHeader> Items { get; set; } = new();
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
}

public sealed class HistoryIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
}

public sealed class HistoryIndexResponse
{
    [JsonPropertyName("current_seq")] public long CurrentSeq { get; set; }
    [JsonPropertyName("state_rev")] public long StateRev { get; set; }
    [JsonPropertyName("items")] public List<HistoryIndexEntry> Items { get; set; } = new();
}

public sealed class StorageInfo
{
    [JsonPropertyName("used_bytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("limit_bytes")] public long LimitBytes { get; set; }
    [JsonPropertyName("pinned_bytes")] public long PinnedBytes { get; set; }
    [JsonPropertyName("pinned_limit_bytes")] public long PinnedLimitBytes { get; set; }
    [JsonPropertyName("item_count")] public long ItemCount { get; set; }
    [JsonPropertyName("free_disk_bytes")] public long FreeDiskBytes { get; set; }
    [JsonPropertyName("min_free_disk_bytes")] public long MinFreeDiskBytes { get; set; }
    [JsonPropertyName("retention_days")] public int RetentionDays { get; set; }
    [JsonPropertyName("disk_low")] public bool DiskLow { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("details")] public JsonElement? Details { get; set; }
}

public static class WsTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Presence = "presence";
    public const string DevicesChanged = "devices_changed";
    public const string Clip = "clip";
    public const string ClipDeleted = "clip_deleted";
    public const string ClipPinned = "clip_pinned";
    public const string StorageWarning = "storage_warning";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Error = "error";
    public const string ReleaseAvailable = "release_available";
}

public sealed class WsEnvelope
{
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class HelloMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Hello;
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("last_seq")] public long LastSeq { get; set; }
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = ProtocolConstants.Platform;
}

public sealed class OnlineDevice
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

public sealed class WelcomeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Welcome;
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = "";
    [JsonPropertyName("server_time")][JsonConverter(typeof(TimestampJsonConverter))] public DateTimeOffset ServerTime { get; set; }
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("current_seq")] public long CurrentSeq { get; set; }
    [JsonPropertyName("state_rev")] public long StateRev { get; set; }
    [JsonPropertyName("online_devices")] public List<OnlineDevice> OnlineDevices { get; set; } = new();
}

public sealed class PresenceMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Presence;
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("online")] public bool Online { get; set; }
}

public sealed class ClipMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Clip;
    [JsonPropertyName("item")] public ItemHeader Item { get; set; } = new();
}

public sealed class ClipDeletedMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.ClipDeleted;
    [JsonPropertyName("ids")] public List<string> Ids { get; set; } = new();
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("state_rev")] public long StateRev { get; set; }
}

public sealed class ClipPinnedMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.ClipPinned;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("state_rev")] public long StateRev { get; set; }
}

public sealed class ReleaseAvailableMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.ReleaseAvailable;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class StorageWarningMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.StorageWarning;
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("free_disk_bytes")] public long FreeDiskBytes { get; set; }
    [JsonPropertyName("min_free_disk_bytes")] public long MinFreeDiskBytes { get; set; }
}

public sealed class PingMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Ping;
    [JsonPropertyName("ts")] public long Ts { get; set; }
}

public sealed class PongMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Pong;
    [JsonPropertyName("ts")] public long Ts { get; set; }
}

public sealed class WsErrorMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = WsTypes.Error;
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("details")] public JsonElement? Details { get; set; }
}
