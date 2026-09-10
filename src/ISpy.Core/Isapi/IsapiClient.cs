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
/// Digest, performed by <see cref="DigestAuthHandler"/> because the framework's built-in Digest is
/// unreliable against this family's firmware.
/// </summary>
public sealed class IsapiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public IsapiClient(string host, int httpPort, string username, string password, TimeSpan? timeout = null)
    {
        _baseUrl = HikvisionUrls.IsapiBase(host, httpPort);

        var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // We drive Digest ourselves rather than through the handler's Credentials: the built-in
        // implementation fails against this family's firmware, whereas DigestAuthHandler is tested
        // against the RFC vector and caches the challenge so repeat calls stay cheap.
        var handler = new DigestAuthHandler(username, password, transport);

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
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // The device's own reply is the fastest route to the real cause, so record the
                    // status and a slice of the body verbatim before turning it into a message.
                    Core.Logs.Append("isapi.log",
                        $"{(int)response.StatusCode} {request.Method} {request.RequestUri?.AbsolutePath} :: " +
                        Snippet(body));
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new IsapiAuthenticationException(DescribeAuthFailure(body));

                // Old firmware answers 403 for two very different things: a credential problem
                // (lockout, no permission) and an endpoint it simply does not implement, which it
                // marks notSupport/invalidOperation. Only the former is an authentication failure -
                // reading the latter as "wrong password" sent a real user chasing password resets
                // while the device was answering other endpoints perfectly.
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (LooksAuthRelated(body))
                        throw new IsapiAuthenticationException(DescribeAuthFailure(body));

                    var sub = Protocol.Xml.TryParse(body)?.Value("subStatusCode");
                    throw new IsapiException(
                        $"{request.RequestUri?.AbsolutePath} is not supported by this device" +
                        (sub is null ? "." : $" ({sub})."),
                        response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                    throw new IsapiException(
                        $"{request.RequestUri?.AbsolutePath} returned {(int)response.StatusCode}.",
                        response.StatusCode);

                return body;
            }
        }
    }

    /// <summary>
    /// Requests an endpoint that may legitimately be absent. An error status (404, 403 notSupport,
    /// 503) becomes null, because whether a device implements an endpoint is exactly what we are
    /// probing for. A transport failure - unreachable, timed out - still throws: it says nothing
    /// about the endpoint and everything about the device, and swallowing it would make a powered-
    /// off recorder read as "reports no cameras".
    /// </summary>
    public async Task<string?> TryGetAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IsapiException ex) when (ex.Status is not null)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a 401/403 body into a message that names the real cause. The one that matters most is
    /// the login lockout: after a handful of failed attempts, Hikvision firmware locks the client's
    /// IP for a while and rejects even the correct password - so "wrong password" would be a lie,
    /// and the fix (wait, or reboot the recorder) is completely different.
    /// </summary>
    public static string DescribeAuthFailure(string body)
    {
        var lowered = body.ToLowerInvariant();

        if (lowered.Contains("lock"))
        {
            return "The recorder has temporarily locked out this PC after repeated sign-in " +
                   "attempts. Reboot the recorder to clear it immediately (or wait ~30 minutes), " +
                   "then try once with the correct password.";
        }

        if (lowered.Contains("notactivate"))
            return "The device has not been activated yet. Activate it in Guarding Vision first.";

        return "The device rejected the username or password.";
    }

    /// <summary>True when a 403's body points at the user rather than the endpoint.</summary>
    public static bool LooksAuthRelated(string body)
    {
        var lowered = body.ToLowerInvariant();

        return lowered.Contains("lock") || lowered.Contains("password") ||
               lowered.Contains("permission") || lowered.Contains("privilege") ||
               lowered.Contains("usercheck") || lowered.Contains("notactivate");
    }

    /// <summary>A single-line, length-capped slice of a response body, for the log.</summary>
    private static string Snippet(string body)
    {
        var oneLine = string.Join(' ', body.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));
        return oneLine.Length > 300 ? oneLine[..300] : oneLine;
    }

    private string Url(string path) => $"{_baseUrl}/{path.TrimStart('/')}";

    public void Dispose() => _http.Dispose();
}
