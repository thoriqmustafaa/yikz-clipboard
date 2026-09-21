using System.Net;
using System.Net.Http.Headers;
using YikzClipboard.Core.Net;

namespace YikzClipboard.Core.Updates;

public sealed record UpdateEndpoint(Uri BaseUri, string? Token);

public sealed class UpdateClient
{
    private readonly HttpClient _http;
    private readonly Func<UpdateEndpoint?> _endpoint;

    public UpdateClient(HttpClient http, Func<UpdateEndpoint?> endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public static HttpClient CreateHttpClient(string appVersion)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YikzClipboard-Windows/" + appVersion);
        return client;
    }

    private UpdateEndpoint RequireEndpoint()
    {
        var endpoint = _endpoint();
        if (endpoint == null || string.IsNullOrEmpty(endpoint.Token))
        {
            throw new UpdateException("sign in to check for updates");
        }
        return endpoint;
    }

    public static Uri LatestUri(Uri baseUri, string platformKey)
    {
        return new Uri(ApiClient.NormalizeBase(baseUri), "api/releases/latest?platform=" + Uri.EscapeDataString(platformKey));
    }

    public static Uri ResolveAssetUri(Uri baseUri, ReleaseAsset asset, string version)
    {
        var normalized = ApiClient.NormalizeBase(baseUri);
        var url = asset.Url;
        if (!string.IsNullOrEmpty(url))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            {
                if (!string.Equals(absolute.Authority, normalized.Authority, StringComparison.OrdinalIgnoreCase) || absolute.Scheme != normalized.Scheme)
                {
                    throw new UpdateException("the asset URL points to a different server");
                }
                return absolute;
            }
            return new Uri(normalized, url.TrimStart('/'));
        }
        return new Uri(normalized, "api/releases/" + Uri.EscapeDataString(version) + "/assets/" + Uri.EscapeDataString(asset.File));
    }

    public async Task<LatestRelease?> GetLatestAsync(string platformKey, CancellationToken ct)
    {
        var endpoint = RequireEndpoint();
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUri(endpoint.BaseUri, platformKey));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new UpdateException("the update server is unreachable: " + ex.Message, ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return LatestReleaseParser.Parse(response.StatusCode, body);
        }
    }

    public async Task DownloadAsync(ReleaseAsset asset, string version, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var endpoint = RequireEndpoint();
        var uri = ResolveAssetUri(endpoint.BaseUri, asset, version);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
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
            throw new UpdateException("download failed: " + ex.Message, ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException("download failed with HTTP " + (int)response.StatusCode);
            }
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            if (asset.Size > 0 && total > asset.Size)
            {
                throw new UpdateException("the download is larger than announced");
            }
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var partial = destination + ".part";
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    var lastReport = -1.0;
                    int read;
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(90));
                        try
                        {
                            read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new UpdateException("the download stalled");
                        }
                        if (read == 0)
                        {
                            break;
                        }
                        written += read;
                        if (asset.Size > 0 && written > asset.Size)
                        {
                            throw new UpdateException("the download is larger than announced");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        if (total > 0)
                        {
                            var fraction = Math.Min(1.0, (double)written / total);
                            if (fraction - lastReport >= 0.01 || fraction >= 1.0)
                            {
                                lastReport = fraction;
                                progress?.Report(fraction);
                            }
                        }
                    }
                }
                File.Move(partial, destination, true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
