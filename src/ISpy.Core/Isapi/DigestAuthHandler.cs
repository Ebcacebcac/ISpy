using System.Net;
using System.Net.Http.Headers;

namespace ISpy.Core.Isapi;

/// <summary>
/// A message handler that performs HTTP Digest authentication for every request, computing the
/// response ourselves rather than relying on the framework's built-in Digest, which is unreliable
/// against Hikvision-family firmware.
/// </summary>
/// <remarks>
/// The first request to a host is sent unauthenticated to draw out the challenge, then retried with
/// the Authorization header. The challenge is cached per host and pre-applied to later requests
/// (with an incrementing nonce count), exactly as PreAuthenticate would - and if the device retires
/// the nonce, the fresh 401 is caught and the request recomputed once, so a stale nonce is a
/// transparent retry rather than a failure.
/// </remarks>
public sealed class DigestAuthHandler : DelegatingHandler
{
    private readonly string _username;
    private readonly string _password;
    private readonly Lock _gate = new();

    private DigestChallenge? _challenge;
    private int _nonceCount;

    public DigestAuthHandler(string username, string password, HttpMessageHandler inner)
    {
        _username = username;
        _password = password;
        InnerHandler = inner;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body up front: a challenge retry re-sends the request, and a one-shot content
        // stream cannot be read twice.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);

        ApplyCachedAuthorization(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // Either we had no challenge yet, or the cached one went stale. Take the new one and retry.
        var challenge = ReadChallenge(response);
        if (challenge is null)
        {
            Core.Logs.Append("isapi.log",
                "401 with no Digest challenge - device may be set to Basic-only auth.");
            return response;
        }

        Core.Logs.Append("isapi.log",
            $"digest challenge: realm=\"{challenge.Realm}\" qop={challenge.Qop ?? "(none)"} " +
            $"algorithm={challenge.Algorithm} nonceLen={challenge.Nonce.Length}");

        lock (_gate)
        {
            _challenge = challenge;
            _nonceCount = 0;
        }

        response.Dispose();

        var retry = await CloneAsync(request).ConfigureAwait(false);
        ApplyCachedAuthorization(retry);

        var authed = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);

        if (authed.StatusCode == HttpStatusCode.Unauthorized)
        {
            // We answered the challenge and the device still said no. That narrows it to a genuinely
            // wrong password or a firmware quirk in the response format - never a missing handshake.
            Core.Logs.Append("isapi.log",
                $"digest response rejected for user '{_username}' - the password is wrong, or this " +
                "firmware wants a different digest form.");
        }

        return authed;
    }

    private void ApplyCachedAuthorization(HttpRequestMessage request)
    {
        DigestChallenge? challenge;
        int nonceCount;

        lock (_gate)
        {
            challenge = _challenge;
            if (challenge is null) return;
            nonceCount = ++_nonceCount;
        }

        var uri = request.RequestUri?.PathAndQuery ?? "/";
        var header = challenge.CreateAuthorization(
            _username, _password, request.Method.Method, uri, nonceCount,
            DigestChallenge.NewClientNonce());

        // Set the raw value directly: AuthenticationHeaderValue re-quotes parameters and some
        // firmware rejects the reformatting.
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", header);
    }

    private static DigestChallenge? ReadChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            var parsed = DigestChallenge.Parse(Combine(header));
            if (parsed is not null) return parsed;
        }

        return null;
    }

    private static string Combine(AuthenticationHeaderValue header) =>
        header.Parameter is null ? header.Scheme : $"{header.Scheme} {header.Parameter}";

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);

            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
