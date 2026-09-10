using ISpy.Core.Discovery;
using ISpy.Core.Model;
using Xunit;

namespace ISpy.Tests;

public class SadpTests
{
    // Shape of a real ProbeMatch from an NVR in this family.
    private const string ProbeMatch = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ProbeMatch>
          <Uuid>{2F3A1C7E-1111-2222-3333-AABBCCDDEEFF}</Uuid>
          <Types>inquiry</Types>
          <DeviceType>81blptvj</DeviceType>
          <DeviceDescription>DS-7608NI-K2/8P</DeviceDescription>
          <DeviceSN>DS-7608NI-K2/8P0120190101CCRRJ12345678WCVU</DeviceSN>
          <CommandPort>8000</CommandPort>
          <HttpPort>8080</HttpPort>
          <MAC>44-19-b6-1a-2b-3c</MAC>
          <IPv4Address>192.168.1.64</IPv4Address>
          <IPv4SubnetMask>255.255.255.0</IPv4SubnetMask>
          <IPv4Gateway>192.168.1.1</IPv4Gateway>
          <DHCP>true</DHCP>
          <SoftwareVersion>V4.30.085build 190709</SoftwareVersion>
          <Activated>true</Activated>
        </ProbeMatch>
        """;

    [Fact]
    public void ProbeMatch_is_parsed()
    {
        var device = SadpMessages.ParseProbeMatch(ProbeMatch);

        Assert.NotNull(device);
        Assert.Equal("192.168.1.64", device.Host);
        Assert.Equal(8080, device.HttpPort);
        Assert.Equal("DS-7608NI-K2/8P", device.Model);
        Assert.Equal("V4.30.085build 190709", device.FirmwareVersion);
        Assert.Equal("44:19:B6:1A:2B:3C", device.MacAddress);
        Assert.True(device.IsActivated);
        Assert.Equal(DiscoverySource.Sadp, device.Source);
    }

    [Fact]
    public void ProbeMatch_defaults_rtsp_port_when_absent() =>
        Assert.Equal(554, SadpMessages.ParseProbeMatch(ProbeMatch)!.RtspPort);

    [Fact]
    public void Devices_are_identified_by_serial_so_the_address_can_change()
    {
        var device = SadpMessages.ParseProbeMatch(ProbeMatch)!;
        var moved = device with { Host = "192.168.1.99" };

        Assert.Equal(device.Id, moved.Id);
        Assert.StartsWith("sn:", device.Id);
    }

    [Fact]
    public void Our_own_probe_echo_is_ignored() =>
        Assert.Null(SadpMessages.ParseProbeMatch(SadpMessages.BuildProbe(Guid.NewGuid())));

    [Fact]
    public void Garbage_does_not_throw()
    {
        Assert.Null(SadpMessages.ParseProbeMatch("not xml at all"));
        Assert.Null(SadpMessages.ParseProbeMatch(""));
        Assert.Null(SadpMessages.ParseProbeMatch("<ProbeMatch><Truncated>"));
    }

    [Fact]
    public void Probe_declares_an_inquiry_with_our_uuid()
    {
        var uuid = Guid.NewGuid();
        var probe = SadpMessages.BuildProbe(uuid);

        Assert.Contains("<Types>inquiry</Types>", probe);
        Assert.Contains(uuid.ToString("D").ToUpperInvariant(), probe);
    }

    [Theory]
    [InlineData("44-19-b6-1a-2b-3c", "44:19:B6:1A:2B:3C")]
    [InlineData("4419b61a2b3c", "44:19:B6:1A:2B:3C")]
    [InlineData("44:19:B6:1A:2B:3C", "44:19:B6:1A:2B:3C")]
    public void Mac_formats_are_normalised(string input, string expected) =>
        Assert.Equal(expected, SadpMessages.NormalizeMac(input));

    [Fact]
    public void Unactivated_devices_are_reported_as_such()
    {
        var xml = ProbeMatch.Replace("<Activated>true</Activated>", "<Activated>false</Activated>");
        Assert.False(SadpMessages.ParseProbeMatch(xml)!.IsActivated);
    }
}

public class OnvifDiscoveryTests
{
    private const string ProbeMatches = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"
                           xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <SOAP-ENV:Body>
            <d:ProbeMatches>
              <d:ProbeMatch>
                <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/Front%20Door onvif://www.onvif.org/hardware/DS-2CD2042WD</d:Scopes>
                <d:XAddrs>http://192.168.1.70/onvif/device_service http://10.8.0.4/onvif/device_service</d:XAddrs>
              </d:ProbeMatch>
            </d:ProbeMatches>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    [Fact]
    public void ProbeMatches_yields_one_device_per_match()
    {
        var device = Assert.Single(OnvifMessages.ParseProbeMatches(ProbeMatches));

        Assert.Equal("192.168.1.70", device.Host);
        Assert.Equal(80, device.HttpPort);
        Assert.Equal("DS-2CD2042WD", device.Model);
        Assert.Equal(DiscoverySource.Onvif, device.Source);
    }

    [Fact]
    public void Non_default_port_is_carried_through()
    {
        var xml = ProbeMatches.Replace("http://192.168.1.70/", "http://192.168.1.70:8000/");
        Assert.Equal(8000, OnvifMessages.ParseProbeMatches(xml)[0].HttpPort);
    }

    [Fact]
    public void Scope_values_are_percent_decoded() =>
        Assert.Equal("Front Door", OnvifMessages.ScopeValue(
            ["onvif://www.onvif.org/name/Front%20Door"], "name"));

    [Fact]
    public void Probe_asks_for_video_transmitters()
    {
        var probe = OnvifMessages.BuildProbe(Guid.NewGuid());

        Assert.Contains("NetworkVideoTransmitter", probe);
        Assert.Contains("http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe", probe);
    }

    [Fact]
    public void Garbage_does_not_throw() =>
        Assert.Empty(OnvifMessages.ParseProbeMatches("<broken"));
}

public class DiscoveryMergeTests
{
    [Fact]
    public void Sadp_identity_wins_over_generic_onvif_scopes()
    {
        var sadp = new DiscoveredDevice
        {
            Host = "192.168.1.64",
            Model = "DS-7608NI-K2/8P",
            SerialNumber = "REAL-SERIAL",
            FirmwareVersion = "V4.30.085",
            Source = DiscoverySource.Sadp,
        };

        var onvif = new DiscoveredDevice
        {
            Host = "192.168.1.64",
            Model = "Network Video Recorder",
            Source = DiscoverySource.Onvif,
        };

        var merged = LanDiscovery.Merge(sadp, onvif);

        Assert.Equal("DS-7608NI-K2/8P", merged.Model);
        Assert.Equal("REAL-SERIAL", merged.SerialNumber);
        Assert.Equal(DiscoverySource.Sadp, merged.Source);
    }

    [Fact]
    public void Merge_fills_gaps_from_the_second_sighting()
    {
        var onvif = new DiscoveredDevice { Host = "192.168.1.64", Source = DiscoverySource.Onvif };
        var sadp = new DiscoveredDevice
        {
            Host = "192.168.1.64",
            SerialNumber = "SN-1",
            FirmwareVersion = "V4.30",
            Source = DiscoverySource.Sadp,
        };

        var merged = LanDiscovery.Merge(onvif, sadp);

        Assert.Equal("SN-1", merged.SerialNumber);
        Assert.Equal("V4.30", merged.FirmwareVersion);
        Assert.Equal(DiscoverySource.Sadp, merged.Source);
    }

    [Fact]
    public void Discovered_device_converts_to_a_storable_device()
    {
        var device = new DiscoveredDevice
        {
            Host = "192.168.1.64",
            Model = "DS-7608",
            SerialNumber = "SN-1",
            HttpPort = 8080,
            Source = DiscoverySource.Sadp,
        }.ToDevice();

        Assert.Equal("sn:SN-1", device.Id);
        Assert.Equal("DS-7608", device.DisplayName);
        Assert.Equal(8080, device.HttpPort);
        Assert.NotNull(device.LastSeenUtc);
    }

    [Fact]
    public void Unnamed_device_falls_back_to_its_address() =>
        Assert.Equal("192.168.1.64", new DiscoveredDevice { Host = "192.168.1.64" }.DisplayName);
}
