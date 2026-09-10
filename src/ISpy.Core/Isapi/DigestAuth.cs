using System.Security.Cryptography;
using System.Text;

namespace ISpy.Core.Isapi;

/// <summary>
/// HTTP Digest authentication (RFC 2617), computed by hand.
/// </summary>
/// <remarks>
/// Hikvision-family firmware - especially the 2015-2018 vintage these OEM recorders ship - speaks
/// Digest in a way .NET's built-in handler frequently fails against: it 401-loops even with the
/// correct password, which is indistinguishable from a wrong one. Doing the handshake ourselves is
/// the reliable way to talk to the whole family, and it lets us log the exact challenge when
/// something is still off.
/// </remarks>
public sealed record DigestChallenge
{
    public required string Realm { get; init; }
    public required string Nonce { get; init; }
    public string? Qop { get; init; }
    public string? Opaque { get; init; }
    public string Algorithm { get; init; } = "MD5";

    /// <summary>
    /// Parses a <c>WWW-Authenticate: Digest …</c> header value. Returns null for anything that is
    /// not a Digest challenge (a Basic-only device, say), so the caller can react rather than crash.
    /// </summary>
    public static DigestChallenge? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        var value = headerValue.Trim();
        if (value.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            value = value["Digest".Length..].Trim();

        var fields = ParseFields(value);

        if (!fields.TryGetValue("realm", out var realm) || !fields.TryGetValue("nonce", out var nonce))
            return null;

        // qop may be offered as "auth,auth-int"; we implement auth, which is what these devices use.
        var qop = fields.GetValueOrDefault("qop");
        if (qop is not null)
        {
            qop = qop.Split(',').Select(q => q.Trim())
                .FirstOrDefault(q => q.Equals("auth", StringComparison.OrdinalIgnoreCase));
        }

        return new DigestChallenge
        {
            Realm = realm,
            Nonce = nonce,
            Qop = qop,
            Opaque = fields.GetValueOrDefault("opaque"),
            Algorithm = fields.GetValueOrDefault("algorithm") ?? "MD5",
        };
    }

    /// <summary>
    /// Builds the <c>Authorization: Digest …</c> header value for one request.
    /// </summary>
    /// <param name="nonceCount">Monotonic counter for this nonce, so a reused nonce stays valid.</param>
    /// <param name="clientNonce">Client nonce; a fresh random value per request in practice.</param>
    public string CreateAuthorization(
        string username, string password, string method, string uri, int nonceCount, string clientNonce)
    {
        var ha1 = Md5($"{username}:{Realm}:{password}");
        var ha2 = Md5($"{method}:{uri}");

        string response;
        var builder = new StringBuilder()
            .Append("Digest username=\"").Append(username).Append('"')
            .Append(", realm=\"").Append(Realm).Append('"')
            .Append(", nonce=\"").Append(Nonce).Append('"')
            .Append(", uri=\"").Append(uri).Append('"');

        if (Qop is not null)
        {
            var nc = nonceCount.ToString("x8");
            response = Md5($"{ha1}:{Nonce}:{nc}:{clientNonce}:{Qop}:{ha2}");

            builder.Append(", qop=").Append(Qop)
                   .Append(", nc=").Append(nc)
                   .Append(", cnonce=\"").Append(clientNonce).Append('"');
        }
        else
        {
            response = Md5($"{ha1}:{Nonce}:{ha2}");
        }

        builder.Append(", response=\"").Append(response).Append('"');

        if (Opaque is not null) builder.Append(", opaque=\"").Append(Opaque).Append('"');
        if (!string.IsNullOrEmpty(Algorithm)) builder.Append(", algorithm=").Append(Algorithm);

        return builder.ToString();
    }

    /// <summary>Splits a challenge's <c>key=value, key="value"</c> list, honouring quotes and commas.</summary>
    private static Dictionary<string, string> ParseFields(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;

        while (i < value.Length)
        {
            var eq = value.IndexOf('=', i);
            if (eq < 0) break;

            var key = value[i..eq].Trim().TrimStart(',').Trim();
            var j = eq + 1;
            string fieldValue;

            if (j < value.Length && value[j] == '"')
            {
                var end = value.IndexOf('"', j + 1);
                if (end < 0) break;

                fieldValue = value[(j + 1)..end];
                i = end + 1;
            }
            else
            {
                var comma = value.IndexOf(',', j);
                if (comma < 0) comma = value.Length;

                fieldValue = value[j..comma].Trim();
                i = comma;
            }

            if (key.Length > 0) fields[key] = fieldValue;
            while (i < value.Length && (value[i] == ',' || value[i] == ' ')) i++;
        }

        return fields;
    }

    private static string Md5(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>A fresh client nonce.</summary>
    public static string NewClientNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
