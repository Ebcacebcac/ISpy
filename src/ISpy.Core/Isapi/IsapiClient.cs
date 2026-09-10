using System.Net;
using System.Text;
using ISpy.Core.Protocol;

namespace ISpy.Core.Isapi;

/// <summary>The device rejected the supplied username/password.</summary>
public sealed class IsapiAuthenticationException(string message) : Exception(message);

/// <summary>The device answered, but not with something we could use.</summary>
public sealed class IsapiException(string message, HttpStatusCode? status = null)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Talks ISAPI, the HTTP/XML control API on Hikvision-family devices. Authentication is HTTP
/// Digest, which .NET negotiates for us once the handler has credentials.
/// </summary>
public sealed class IsapiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public IsapiClient(string host, int httpPort, string username, string password, TimeSpan? timeout = null)
    {
        _baseUrl = HikvisionUrls.IsapiBase(host, httpPort);

        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(username, password),

            // Send the digest header on subsequent requests instead of paying a 401 round trip for
            // every single call. Channel enumeration alone is several requests.
            PreAuthenticate = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _http = new HttpClient(handler)
        {
            // A recorder that is powered off must fail fast: this timeout is on the startup path
            // for refresh, and the user should never watch a spinner for 100 seconds.
            Timeout = timeout ?? TimeSpan.FromSeconds(8),
        };
    }

    public string BaseUrl => _baseUrl;

    public Task<string> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(path)), cancellationToken);

    public Task<string> PostXmlAsync(string path, string xml, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(path))
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };

        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues a request, translating the failures we actually care about into typed exceptions.
    /// Returns null-free content; callers parse it with <see cref="IsapiParsers"/>.
    /// </summary>
    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IsapiException($"The device did not respond within the timeout ({request.RequestUri}).");
            }
            catch (HttpRequestException ex)
            {
                throw new IsapiException($"Could not reach the device: {ex.Message}");
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new IsapiAuthenticationException("The device rejected these credentials.");

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw new IsapiException(
                        $"{request.RequestUri?.AbsolutePath} returned {(int)response.StatusCode}.",
                        response.StatusCode);

                return body;
            }
        }
    }

    /// <summary>
    /// Requests an endpoint that may legitimately be absent. Returns null on 4xx rather than
    /// throwing, because whether a device exposes InputProxy or PTZ is exactly what we are probing
    /// for and a 404 is a valid answer.
    /// </summary>
    public async Task<string?> TryGetAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IsapiException)
        {
            return null;
        }
    }

    private string Url(string path) => $"{_baseUrl}/{path.TrimStart('/')}";

    public void Dispose() => _http.Dispose();
}
