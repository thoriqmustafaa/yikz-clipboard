namespace YikzClipboard.Core.Protocol;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const string KdfAlgorithm = "pbkdf2-sha256";
    public const int KdfIterations = 600000;
    public const int KeyLength = 32;
    public const int SaltLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int SealOverhead = 28;
    public const long InlineMaxBytes = 262144;
    public const long ChunkSizeBytes = 4194304;
    public const long MaxChunkBodyBytes = 4194332;
    public const int MetaMaxBytes = 65536;
    public const int ThumbMaxBytes = 65536;
    public const int MaxJsonBodyBytes = 1048576;
    public const int MaxFilesPerItem = 1000;
    public const int PreviewMaxCodePoints = 500;
    public const int ThumbnailMaxSide = 320;
    public const int HistoryDefaultLimit = 100;
    public const int HistoryMaxLimit = 500;
    public const int MaxWsClientFrameBytes = 65536;
    public const int MaxWsServerFrameBytes = 1048576;
    public const long DefaultAutoDownloadLimit = 52428800;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DeadConnectionTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan AutoApplyMaxAge = TimeSpan.FromSeconds(300);
    public const string Platform = "windows";

    public const string MimeText = "text/plain; charset=utf-8";
    public const string MimePng = "image/png";
    public const string MimeFiles = "application/x-yikz-files";

    public const string KindText = "text";
    public const string KindImage = "image";
    public const string KindFiles = "files";

    public const string ContentHashLabel = "yikz-clipboard/v1/content-hash";
    public const string KeyCheckLabel = "yikz-clipboard/v1/key-check";
}

public static class WsCloseCodes
{
    public const int Normal = 1000;
    public const int GoingAway = 1001;
    public const int TooLarge = 1009;
    public const int Unauthorized = 4001;
    public const int TooManyConnections = 4002;
    public const int ProtocolUnsupported = 4003;
    public const int HelloRequired = 4004;
    public const int HeartbeatTimeout = 4005;
}

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidId = "invalid_id";
    public const string SizeMismatch = "size_mismatch";
    public const string InvalidCredentials = "invalid_credentials";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string IdConflict = "id_conflict";
    public const string AlreadyCommitted = "already_committed";
    public const string MissingChunks = "missing_chunks";
    public const string KeyCheckMissing = "key_check_missing";
    public const string KeyCheckExists = "key_check_exists";
    public const string ItemsExist = "items_exist";
    public const string PinnedLimit = "pinned_limit";
    public const string BodyTooLarge = "body_too_large";
    public const string ItemTooLarge = "item_too_large";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal";
    public const string DiskLow = "disk_low";
}
