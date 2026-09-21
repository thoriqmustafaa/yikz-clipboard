using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YikzClipboard.Core.Updates;

public sealed class ReleaseAsset
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class LatestRelease
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
    [JsonPropertyName("notes_md")] public string? NotesMd { get; set; }
    [JsonPropertyName("asset")] public ReleaseAsset? Asset { get; set; }

    [JsonIgnore]
    public DateTimeOffset? PublishedTime => DateTimeOffset.TryParse(PublishedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LatestRelease))]
[JsonSerializable(typeof(ReleaseAsset))]
public sealed partial class UpdateJsonContext : JsonSerializerContext
{
}

public static class LatestReleaseParser
{
    public static LatestRelease? Parse(HttpStatusCode status, ReadOnlySpan<byte> body)
    {
        if (status == HttpStatusCode.NoContent)
        {
            return null;
        }
        if (status != HttpStatusCode.OK)
        {
            throw new UpdateException("unexpected status " + (int)status + " from the update server");
        }
        if (body.IsEmpty)
        {
            return null;
        }
        LatestRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(body, UpdateJsonContext.Default.LatestRelease);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("invalid release information: " + ex.Message, ex);
        }
        if (release == null || string.IsNullOrEmpty(release.Version))
        {
            throw new UpdateException("release information has no version");
        }
        return release;
    }
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
