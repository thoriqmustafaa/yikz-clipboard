using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Net;

public sealed class ApiException : Exception
{
    public ApiException(int status, string code, string message, JsonElement? details = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
        Details = details;
        RetryAfter = retryAfter;
    }

    public int Status { get; }

    public string Code { get; }

    public JsonElement? Details { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsNetworkError => Status == 0;

    public bool IsTransient => Status == 0 || Status == 500 || Status == 502 || Status == 503 || Status == 504;

    public IReadOnlyList<int> MissingChunks()
    {
        var list = new List<int>();
        if (Details is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in missing.EnumerateArray())
            {
                if (m.TryGetInt32(out var i))
                {
                    list.Add(i);
                }
            }
        }
        return list;
    }

    public override string ToString() => $"HTTP {Status} {Code}: {Message}";
}

public sealed class ApiClient
{
    private readonly HttpClient _http;
    private Uri _baseUri;

    public ApiClient(HttpClient http, Uri baseUri)
    {
        _http = http;
        _baseUri = NormalizeBase(baseUri);
    }

    public Uri BaseUri
    {
        get => _baseUri;
        set => _baseUri = NormalizeBase(value);
    }

    public string? Token { get; set; }

    public event Action? Unauthorized;

    public static Uri NormalizeBase(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path);
        if (!text.EndsWith('/'))
        {
            text += "/";
        }
        return new Uri(text, UriKind.Absolute);
    }

    public static bool TryParseServerUrl(string? input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        uri = NormalizeBase(parsed);
        return true;
    }

    public Uri WebSocketUri()
    {
        var builder = new UriBuilder(new Uri(_baseUri, "ws"))
        {
            Scheme = _baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
        };
        return builder.Uri;
    }

    public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, "api/login", request, ProtocolJsonContext.Default.LoginRequest, ProtocolJsonContext.Default.LoginResponse, false, ct);

    public Task<MeResponse> GetMeAsync(CancellationToken ct = default)
        => SendJsonAsync<object, MeResponse>(HttpMethod.Get, "api/me", null, null, ProtocolJsonContext.Default.MeResponse, true, ct);

    public Task LogoutAsync(CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Post, "api/logout", null, ct);

    public Task PutKeyCheckAsync(string keyCheck, CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Put, "api/account/key-check", JsonContent(new KeyCheckRequest { KeyCheck = keyCheck }, ProtocolJsonContext.Default.KeyCheckRequest), ct);

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var response = await SendJsonAsync<object, DevicesResponse>(HttpMethod.Get, "api/devices", null, null, ProtocolJsonContext.Default.DevicesResponse, true, ct).ConfigureAwait(false);
        return response.Devices;
    }

    public Task<DeviceInfo> RenameDeviceAsync(string id, string name, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Patch, "api/devices/" + Uri.EscapeDataString(id), new RenameDeviceRequest { Name = name }, ProtocolJsonContext.Default.RenameDeviceRequest, ProtocolJsonContext.Default.DeviceInfo, true, ct);

    public Task DeleteDeviceAsync(string id, CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Delete, "api/devices/" + Uri.EscapeDataString(id), null, ct);

    public Task<ItemHeader> CreateItemAsync(CreateItemRequest request, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, "api/items", request, ProtocolJsonContext.Default.CreateItemRequest, ProtocolJsonContext.Default.ItemHeader, true, ct);

    public Task PutThumbAsync(string id, byte[] sealedThumb, CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Put, ItemPath(id) + "/thumb", Binary(sealedThumb, 0, sealedThumb.Length), ct);

    public Task PutChunkAsync(string id, int index, byte[] sealedChunk, int length, CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Put, ItemPath(id) + "/chunks/" + index.ToString(CultureInfo.InvariantCulture), Binary(sealedChunk, 0, length), ct);

    public Task<ItemHeader> CommitAsync(string id, CommitRequest request, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, ItemPath(id) + "/commit", request, ProtocolJsonContext.Default.CommitRequest, ProtocolJsonContext.Default.ItemHeader, true, ct);

    public Task<ItemHeader> GetItemAsync(string id, CancellationToken ct = default)
        => SendJsonAsync<object, ItemHeader>(HttpMethod.Get, ItemPath(id), null, null, ProtocolJsonContext.Default.ItemHeader, true, ct);

    public Task<byte[]> GetChunkAsync(string id, int index, CancellationToken ct = default)
        => SendBinaryAsync(ItemPath(id) + "/chunks/" + index.ToString(CultureInfo.InvariantCulture), ProtocolConstants.MaxChunkBodyBytes, ct);

    public async Task<byte[]?> GetThumbAsync(string id, CancellationToken ct = default)
    {
        try
        {
            return await SendBinaryAsync(ItemPath(id) + "/thumb", ProtocolConstants.ThumbMaxBytes, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task<HistoryResponse> GetHistoryAsync(long? before = null, long? after = null, int? limit = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (before.HasValue)
        {
            query.Add("before=" + before.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (after.HasValue)
        {
            query.Add("after=" + after.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (limit.HasValue)
        {
            query.Add("limit=" + limit.Value.ToString(CultureInfo.InvariantCulture));
        }
        var path = "api/history" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return SendJsonAsync<object, HistoryResponse>(HttpMethod.Get, path, null, null, ProtocolJsonContext.Default.HistoryResponse, true, ct);
    }

    public Task<HistoryIndexResponse> GetHistoryIndexAsync(CancellationToken ct = default)
        => SendJsonAsync<object, HistoryIndexResponse>(HttpMethod.Get, "api/history/index", null, null, ProtocolJsonContext.Default.HistoryIndexResponse, true, ct);

    public Task<ItemHeader> SetPinnedAsync(string id, bool pinned, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, ItemPath(id) + "/pin", new PinRequest { Pinned = pinned }, ProtocolJsonContext.Default.PinRequest, ProtocolJsonContext.Default.ItemHeader, true, ct);

    public async Task DeleteItemAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await SendNoContentAsync(HttpMethod.Delete, ItemPath(id), null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
        }
    }

    public Task<StorageInfo> GetStorageAsync(CancellationToken ct = default)
        => SendJsonAsync<object, StorageInfo>(HttpMethod.Get, "api/storage", null, null, ProtocolJsonContext.Default.StorageInfo, true, ct);

    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
        => SendJsonAsync<object, HealthResponse>(HttpMethod.Get, "healthz", null, null, ProtocolJsonContext.Default.HealthResponse, false, ct);

    private static string ItemPath(string id)
    {
        if (!UuidV7.IsValid(id))
        {
            throw new ArgumentException("invalid item id", nameof(id));
        }
        return "api/items/" + id;
    }

    private static HttpContent JsonContent<T>(T value, JsonTypeInfo<T> info)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static HttpContent Binary(byte[] data, int offset, int length)
    {
        var content = new ByteArrayContent(data, offset, length);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, bool authenticated)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        if (authenticated)
        {
            if (string.IsNullOrEmpty(Token))
            {
                throw new ApiException(401, ErrorCodes.Unauthorized, "not signed in");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest? body,
        JsonTypeInfo<TRequest>? requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        bool authenticated,
        CancellationToken ct)
    {
        using var request = NewRequest(method, path, authenticated);
        if (body != null && requestInfo != null)
        {
            request.Content = JsonContent(body, requestInfo);
        }
        using var response = await SendAsync(request, authenticated, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize(bytes, responseInfo);
            return value ?? throw new ApiException((int)response.StatusCode, "invalid_response", "empty response body");
        }
        catch (JsonException ex)
        {
            throw new ApiException((int)response.StatusCode, "invalid_response", "invalid JSON from server", null, null, ex);
        }
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = NewRequest(method, path, true);
        request.Content = content;
        using var response = await SendAsync(request, true, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> SendBinaryAsync(string path, long maxBytes, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path, true);
        using var response = await SendAsync(request, true, ct).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > maxBytes)
        {
            throw new ApiException((int)response.StatusCode, "invalid_response", "response body too large");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(length.HasValue ? (int)length.Value : 65536);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes)
            {
                throw new ApiException((int)response.StatusCode, "invalid_response", "response body too large");
            }
        }
        return ms.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool authenticated, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new ApiException(0, "network", ex.Message, null, null, ex);
        }
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            string code = "http_" + status.ToString(CultureInfo.InvariantCulture);
            string message = response.ReasonPhrase ?? "request failed";
            JsonElement? details = null;
            try
            {
                var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (body.Length > 0)
                {
                    var error = JsonSerializer.Deserialize(body, ProtocolJsonContext.Default.ErrorResponse);
                    if (error != null && !string.IsNullOrEmpty(error.Code))
                    {
                        code = error.Code;
                        message = string.IsNullOrEmpty(error.Message) ? message : error.Message;
                        details = error.Details;
                    }
                }
            }
            catch (Exception)
            {
            }
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter is { } ra)
            {
                retryAfter = ra.Delta ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : null);
            }
            if (status == (int)HttpStatusCode.Unauthorized && authenticated)
            {
                try
                {
                    Unauthorized?.Invoke();
                }
                catch
                {
                }
            }
            throw new ApiException(status, code, message, details, retryAfter);
        }
    }
}

public static class RetryPolicy
{
    public static TimeSpan HttpDelay(int attempt, Random? random = null)
    {
        var cap = Math.Min(60.0, 1.0 * Math.Pow(2, Math.Min(attempt, 16)));
        return TimeSpan.FromSeconds((random ?? Random.Shared).NextDouble() * cap);
    }

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, int maxAttempts, CancellationToken ct, Action<int, Exception>? onRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.IsTransient && attempt + 1 < maxAttempts && !ct.IsCancellationRequested)
            {
                onRetry?.Invoke(attempt, ex);
                await Task.Delay(HttpDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task RunAsync(Func<CancellationToken, Task> action, int maxAttempts, CancellationToken ct, Action<int, Exception>? onRetry = null)
    {
        await RunAsync<bool>(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, maxAttempts, ct, onRetry).ConfigureAwait(false);
    }
}

public static class HttpClientFactory
{
    public static HttpClient Create(string appVersion)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YikzClipboard-Windows/" + appVersion);
        return client;
    }
}
