using System.Text.Json;
using System.Text.Json.Nodes;

namespace YikzClipboard.Core.Tests.Support;

public static class Vectors
{
    private static readonly Lazy<string> Dir = new(FindDirectory);

    public static string Directory => Dir.Value;

    public static JsonElement Load(string file)
    {
        var path = Path.Combine(Directory, file);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.Clone();
    }

    public static JsonNode LoadNode(string file)
    {
        return JsonNode.Parse(File.ReadAllBytes(Path.Combine(Directory, file)))!;
    }

    public static IEnumerable<JsonElement> Items(string file, string property)
    {
        return Load(file).GetProperty(property).EnumerateArray();
    }

    public static JsonElement Named(string file, string property, string name)
    {
        foreach (var e in Items(file, property))
        {
            if (e.GetProperty("name").GetString() == name)
            {
                return e;
            }
        }
        throw new InvalidOperationException($"vector {name} not found in {file}");
    }

    public static JsonElement HttpExample(string name) => Named("http.json", "examples", name);

    public static JsonElement WsMessage(string name) => Named("ws.json", "messages", name).GetProperty("message");

    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    public static string Str(this JsonElement e, string property) => e.GetProperty(property).GetString()!;

    public static long Long(this JsonElement e, string property) => e.GetProperty(property).GetInt64();

    public static bool Has(this JsonElement e, string property) => e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null;

    private static string FindDirectory()
    {
        var env = Environment.GetEnvironmentVariable("YIKZ_VECTORS_DIR");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
        {
            return env;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "protocol", "vectors");
            if (System.IO.Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "kdf.json")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("protocol/vectors not found above " + AppContext.BaseDirectory);
    }

    public static byte[] LargeContent()
    {
        var header = Named("files_archive.json", "cases", "single_large_file_header");
        var prefix = Hex(header.Str("archive_prefix_hex"));
        var size = header.Long("archive_size");
        var data = new byte[size];
        prefix.CopyTo(data, 0);
        var fileSize = header.GetProperty("files")[0].Long("size");
        for (long i = 0; i < fileSize; i++)
        {
            data[prefix.Length + i] = (byte)(i % 251);
        }
        return data;
    }
}
