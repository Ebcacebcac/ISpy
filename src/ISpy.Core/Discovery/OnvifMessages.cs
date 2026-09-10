using System.Xml.Linq;
using ISpy.Core.Model;
using ISpy.Core.Protocol;

namespace ISpy.Core.Discovery;

/// <summary>
/// ONVIF WS-Discovery on multicast 239.255.255.250:3702. Runs alongside SADP as a second net:
/// it catches third-party cameras plugged into the recorder, and OEM firmware that has SADP off.
/// </summary>
public static class OnvifMessages
{
    public const string MulticastAddress = "239.255.255.250";
    public const int Port = 3702;

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Network = "http://www.onvif.org/ver10/network/wsdl";

    public static string BuildProbe(Guid messageId)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "e", Soap),
            new XAttribute(XNamespace.Xmlns + "w", Addressing),
            new XAttribute(XNamespace.Xmlns + "d", Discovery),
            new XAttribute(XNamespace.Xmlns + "dn", Network),
            new XElement(Soap + "Header",
                new XElement(Addressing + "MessageID", $"uuid:{messageId:D}"),
                new XElement(Addressing + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
                new XElement(Addressing + "Action",
                    "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
            new XElement(Soap + "Body",
                new XElement(Discovery + "Probe",
                    new XElement(Discovery + "Types", "dn:NetworkVideoTransmitter"))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Parses a ProbeMatches envelope. A device may advertise several transport addresses.</summary>
    public static IReadOnlyList<DiscoveredDevice> ParseProbeMatches(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return [];

        var results = new List<DiscoveredDevice>();

        foreach (var match in root.DescendantsLocal("ProbeMatch"))
        {
            var addresses = (match.Value("XAddrs") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var scopes = (match.Value("Scopes") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var address in addresses)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) continue;

                results.Add(new DiscoveredDevice
                {
                    Host = uri.Host,
                    HttpPort = uri.IsDefaultPort ? 80 : uri.Port,
                    Model = ScopeValue(scopes, "hardware") ?? ScopeValue(scopes, "name"),
                    SerialNumber = ScopeValue(scopes, "serial"),
                    Source = DiscoverySource.Onvif,
                });

                // One transport address is enough; the rest are alternate routes to the same device.
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Pulls a value out of an ONVIF scope URI such as
    /// <c>onvif://www.onvif.org/hardware/DS-2CD2042WD</c>. Values are percent-encoded.
    /// </summary>
    public static string? ScopeValue(IEnumerable<string> scopes, string category)
    {
        var marker = $"/{category}/";

        foreach (var scope in scopes)
        {
            var index = scope.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var value = scope[(index + marker.Length)..].Trim();
            if (value.Length > 0) return Uri.UnescapeDataString(value);
        }

        return null;
    }
}
