using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ISpy.Tests;

/// <summary>
/// A stand-in recorder that serves real ISAPI payloads over HTTP.
/// </summary>
/// <remarks>
/// This exists so the add-a-recorder flow is exercised end to end - HTTP request, status handling,
/// XML parsing, persistence - without needing the physical NVR on the bench. Endpoints can be made
/// to 404 or 401 individually to reproduce the OEM firmware differences that actually bite.
/// </remarks>
internal sealed class FakeNvr : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public FakeNvr()
    {
        Port = FreePort();

        // "localhost" rather than an explicit 127.0.0.1 prefix: on Windows, HttpListener needs a
        // netsh URL ACL reservation for a specific-IP prefix but allows localhost unelevated, and
        // the release workflow runs these tests on a Windows runner.
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }
    public string Host => "localhost";

    /// <summary>When set, every request must carry a valid Digest Authorization for these.</summary>
    public (string User, string Password)? RequireDigest { get; set; }

    private const string Realm = "ISpy-Test";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    /// <summary>Requests the fake has received, so tests can assert what was actually asked for.</summary>
    public List<string> Requests { get; } = [];

    public FakeNvr Serve(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[path] = (status, body);
        return this;
    }

    /// <summary>Wires up a recorder with two cameras, the way working firmware answers.</summary>
    public FakeNvr WithTypicalRecorder()
    {
        Serve("/ISAPI/System/deviceInfo", """
            <DeviceInfo xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <deviceName>Front NVR</deviceName>
              <model>DS-7608NI-K2/8P</model>
              <serialNumber>SN-FAKE-0001</serialNumber>
              <firmwareVersion>V4.30.085</firmwareVersion>
            </DeviceInfo>
            """);

        Serve("/ISAPI/Streaming/channels", """
            <StreamingChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <StreamingChannel><id>101</id><channelName>Driveway</channelName><enabled>true</enabled>
                <Video><videoCodecType>H.265</videoCodecType></Video></StreamingChannel>
              <StreamingChannel><id>102</id><channelName>Driveway</channelName><enabled>true</enabled>
                <Video><videoCodecType>H.264</videoCodecType></Video></StreamingChannel>
              <StreamingChannel><id>201</id><channelName></channelName><enabled>true</enabled>
                <Video><videoCodecType>H.264</videoCodecType></Video></StreamingChannel>
              <StreamingChannel><id>202</id><channelName></channelName><enabled>true</enabled>
                <Video><videoCodecType>H.264</videoCodecType></Video></StreamingChannel>
            </StreamingChannelList>
            """);

        Serve("/ISAPI/ContentMgmt/InputProxy/channels", """
            <InputProxyChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <InputProxyChannel><id>2</id><name>Back Door</name></InputProxyChannel>
            </InputProxyChannelList>
            """);

        Serve("/ISAPI/PTZCtrl/channels", """
            <PTZChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <PTZChannel><id>2</id></PTZChannel>
            </PTZChannelList>
            """);

        return this;
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Listener stopped.
            }

            var path = context.Request.Url?.AbsolutePath ?? "";
            lock (Requests) Requests.Add(path);

            if (RequireDigest is { } creds && !DigestOk(context.Request, creds))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.AddHeader("WWW-Authenticate",
                    $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\"");
                context.Response.Close();
                continue;
            }

            var (status, body) = _routes.TryGetValue(path, out var route)
                ? route
                : (HttpStatusCode.NotFound, "<ResponseStatus><statusCode>4</statusCode></ResponseStatus>");

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/xml";
            context.Response.ContentLength64 = bytes.Length;

            try
            {
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
                // Client gave up; nothing to do.
            }
        }
    }

    /// <summary>Verifies the request's Digest response the way real firmware would.</summary>
    private static bool DigestOk(HttpListenerRequest request, (string User, string Password) creds)
    {
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return false;

        var fields = header["Digest".Length..]
            .Split(',')
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);

        if (!fields.TryGetValue("response", out var response)) return false;

        var ha1 = Md5($"{creds.User}:{Realm}:{creds.Password}");
        var ha2 = Md5($"{request.HttpMethod}:{fields.GetValueOrDefault("uri")}");

        var expected = fields.TryGetValue("qop", out var qop) && qop.Length > 0
            ? Md5($"{ha1}:{Nonce}:{fields.GetValueOrDefault("nc")}:{fields.GetValueOrDefault("cnonce")}:{qop}:{ha2}")
            : Md5($"{ha1}:{Nonce}:{ha2}");

        return string.Equals(expected, response, StringComparison.OrdinalIgnoreCase);
    }

    private static string Md5(string input) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Close();
        _shutdown.Dispose();
    }
}
