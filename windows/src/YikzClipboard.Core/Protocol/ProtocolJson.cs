using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YikzClipboard.Core.Protocol;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.Strict,
    WriteIndented = false)]
[JsonSerializable(typeof(ItemHeader))]
[JsonSerializable(typeof(ItemMeta))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(KeyCheckRequest))]
[JsonSerializable(typeof(DevicesResponse))]
[JsonSerializable(typeof(DeviceInfo))]
[JsonSerializable(typeof(RenameDeviceRequest))]
[JsonSerializable(typeof(CreateItemRequest))]
[JsonSerializable(typeof(CommitRequest))]
[JsonSerializable(typeof(PinRequest))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(HistoryIndexResponse))]
[JsonSerializable(typeof(StorageInfo))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(WsEnvelope))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(PresenceMessage))]
[JsonSerializable(typeof(ClipMessage))]
[JsonSerializable(typeof(ClipDeletedMessage))]
[JsonSerializable(typeof(ClipPinnedMessage))]
[JsonSerializable(typeof(StorageWarningMessage))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(PongMessage))]
[JsonSerializable(typeof(WsErrorMessage))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}

public static class MetaCodec
{
    public static byte[] Serialize(ItemMeta meta)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append("\"v\":").Append(meta.V.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"mime\":");
        AppendString(sb, meta.Mime);
        sb.Append(",\"preview\":");
        AppendString(sb, meta.Preview);
        sb.Append(",\"sha256\":");
        AppendString(sb, meta.Sha256);
        if (meta.Image != null)
        {
            sb.Append(",\"image\":{\"width\":")
                .Append(meta.Image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"height\":")
                .Append(meta.Image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('}');
        }
        if (meta.Files != null)
        {
            sb.Append(",\"files\":[");
            for (var i = 0; i < meta.Files.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"name\":");
                AppendString(sb, meta.Files[i].Name);
                sb.Append(",\"size\":").Append(meta.Files[i].Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append(']');
        }
        if (!string.IsNullOrEmpty(meta.SourceApp))
        {
            sb.Append(",\"source_app\":");
            AppendString(sb, meta.SourceApp);
        }
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static ItemMeta Deserialize(ReadOnlySpan<byte> json)
    {
        var meta = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ItemMeta);
        return meta ?? throw new JsonException("meta is null");
    }

    internal static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case (char)0x2028:
                    sb.Append("\\u2028");
                    break;
                case (char)0x2029:
                    sb.Append("\\u2029");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u00").Append(((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append((char)0xFFFD);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}

public static class WsCodec
{
    public static string? PeekType(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.WsEnvelope);
            return envelope?.Type;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Hello(HelloMessage message) => JsonSerializer.Serialize(message, ProtocolJsonContext.Default.HelloMessage);

    public static string Ping(long ts) => JsonSerializer.Serialize(new PingMessage { Ts = ts }, ProtocolJsonContext.Default.PingMessage);

    public static string Pong(long ts) => JsonSerializer.Serialize(new PongMessage { Ts = ts }, ProtocolJsonContext.Default.PongMessage);

    public static long ReadTs(string json)
    {
        try
        {
            var ping = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PingMessage);
            return ping?.Ts ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
