using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YikzClipboard.Core.Protocol;

public static class Timestamps
{
    public static string Format(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new FormatException("invalid RFC 3339 timestamp: " + value);
        }
        return result;
    }

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var normalized = value.Trim();
        if (normalized.Length < 20 || (normalized[10] != 'T' && normalized[10] != 't' && normalized[10] != ' '))
        {
            return false;
        }
        normalized = normalized.Replace('t', 'T').Replace('z', 'Z');
        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }
}

public sealed class TimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("timestamp must be a string");
        }
        var text = reader.GetString();
        if (!Timestamps.TryParse(text, out var value))
        {
            throw new JsonException("invalid timestamp: " + text);
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Timestamps.Format(value));
    }
}
