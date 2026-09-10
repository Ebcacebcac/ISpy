using System.Xml.Linq;
using ISpy.Core.Model;
using ISpy.Core.Protocol;

namespace ISpy.Core.Discovery;

/// <summary>
/// SADP - the UDP discovery protocol Hikvision-family devices answer on multicast
/// 239.255.255.250:37020. This is what the vendor's own SADP tool speaks, and it finds devices
/// regardless of subnet configuration, including ones sitting on a wrong/unknown IP.
/// </summary>
public static class SadpMessages
{
    public const string MulticastAddress = "239.255.255.250";
    public const int Port = 37020;

    /// <summary>Builds an inquiry datagram. The uuid lets us ignore echoes of our own probe.</summary>
    public static string BuildProbe(Guid uuid) =>
        new XElement("Probe",
            new XElement("Uuid", uuid.ToString("D").ToUpperInvariant()),
            new XElement("Types", "inquiry"))
        .ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Parses a ProbeMatch reply. Returns null for anything that is not a usable device reply -
    /// including our own probe echoed back by the multicast group.
    /// </summary>
    public static DiscoveredDevice? ParseProbeMatch(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return null;

        // Our own probe carries Types=inquiry and no address; devices answer with one.
        var address = root.Value("IPv4Address") ?? root.Value("IPv6Address");
        if (string.IsNullOrWhiteSpace(address)) return null;

        return new DiscoveredDevice
        {
            Host = address,
            HttpPort = root.Int("HttpPort") ?? 80,
            RtspPort = root.Int("RtspPort") ?? 554,
            Model = root.Value("DeviceDescription") ?? root.Value("DeviceType"),
            SerialNumber = root.Value("DeviceSN"),
            FirmwareVersion = root.Value("SoftwareVersion"),
            MacAddress = NormalizeMac(root.Value("MAC")),
            IsActivated = root.Bool("Activated"),
            Source = DiscoverySource.Sadp,
        };
    }

    /// <summary>Firmware reports MACs as either 44-19-b6-.. or 4419b6..; settle on colon-separated upper.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return mac.Trim().ToUpperInvariant();

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
