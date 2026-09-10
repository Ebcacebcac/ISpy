using System.Net;
using ISpy.Core.Discovery;
using ISpy.Core.Isapi;
using ISpy.Core.Media;
using ISpy.Core.Model;
using ISpy.Core.Storage;
using Xunit;

namespace ISpy.Tests;

/// <summary>
/// Regression tests mirroring, response for response, the isapi.log captured from a real
/// NVR-216M-A on V3.4.95 (2017): Streaming/channels answers 403 notSupport in the PSIA v1.0
/// dialect, PTZ answers 503 Device Busy, and yet deviceInfo and the InputProxy camera list work.
/// Reading that 403 as "wrong password" sent a real user chasing password resets for an afternoon.
/// </summary>
public class LegacyNvrTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ispy-tests", Guid.NewGuid().ToString("N"));

    private InventoryStore NewStore() =>
        InventoryStore.Open(Path.Combine(_directory, "inventory.db"), new FakeSecretProtector());

    private const string NotSupportBody = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ResponseStatus version="1.0" xmlns="urn:psialliance-org">
          <requestURL>/ISAPI/Streaming/channels</requestURL>
          <statusCode>4</statusCode>
          <statusString>Invalid Operation</statusString>
          <subStatusCode>notSupport</subStatusCode>
        </ResponseStatus>
        """;

    private static FakeNvr LegacyNvr()
    {
        var nvr = new FakeNvr();

        nvr.Serve("/ISAPI/System/deviceInfo", """
            <DeviceInfo xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <deviceName>Network Video Recorder</deviceName>
              <model>NVR-216M-A</model>
              <serialNumber>1620170915AARR831427436WCVU</serialNumber>
              <firmwareVersion>V3.4.95</firmwareVersion>
            </DeviceInfo>
            """);

        nvr.Serve("/ISAPI/Streaming/channels", NotSupportBody, HttpStatusCode.Forbidden);

        nvr.Serve("/ISAPI/PTZCtrl/channels", """
            <ResponseStatus version="1.0" xmlns="urn:psialliance-org">
              <statusCode>2</statusCode>
              <statusString>Device Busy</statusString>
              <subStatusCode>serviceUnavailable</subStatusCode>
            </ResponseStatus>
            """, HttpStatusCode.ServiceUnavailable);

        nvr.Serve("/ISAPI/ContentMgmt/InputProxy/channels", """
            <InputProxyChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <InputProxyChannel><id>1</id><name>Gaming</name></InputProxyChannel>
              <InputProxyChannel><id>2</id><name>Reception</name></InputProxyChannel>
              <InputProxyChannel><id>3</id><name>Stairs</name></InputProxyChannel>
              <InputProxyChannel><id>4</id><name>Back Yard</name></InputProxyChannel>
              <InputProxyChannel><id>5</id><name>Side Door</name></InputProxyChannel>
              <InputProxyChannel><id>6</id><name>Corner Door</name></InputProxyChannel>
              <InputProxyChannel><id>7</id><name>Side Door 2</name></InputProxyChannel>
              <InputProxyChannel><id>8</id><name>Upstairs</name></InputProxyChannel>
            </InputProxyChannelList>
            """);

        return nvr;
    }

    private static DiscoveredDevice Target(FakeNvr nvr) => new()
    {
        Host = nvr.Host,
        HttpPort = nvr.Port,
        Source = DiscoverySource.Manual,
    };

    [Fact]
    public async Task The_2017_nvr_onboards_from_the_inputproxy_list()
    {
        using var nvr = LegacyNvr();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(8, result.Channels!.Count);
        Assert.Equal("Gaming", result.Channels[0].Name);
        Assert.Equal("Upstairs", result.Channels[7].Name);
        Assert.Equal("NVR-216M-A", result.Device!.Model);
        Assert.Equal("sn:1620170915AARR831427436WCVU", result.Device.Id);
    }

    [Fact]
    public async Task Fallback_channels_keep_tiles_on_the_substream()
    {
        using var nvr = LegacyNvr();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        // The fallback records an assumed sub stream, so the grid pulls the cheap stream rather
        // than treating "codec unknown" as "sub stream missing" and decoding eight main streams.
        Assert.All(result.Channels!, channel =>
            Assert.Equal(StreamProfile.Sub, StreamSelection.Resolve(channel, StreamProfile.Sub)));
    }

    [Fact]
    public async Task A_403_notSupport_is_never_reported_as_bad_credentials()
    {
        using var nvr = LegacyNvr();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.NotEqual(OnboardStatus.BadCredentials, result.Status);
    }

    [Fact]
    public async Task A_wrong_password_on_the_legacy_nvr_still_reads_as_bad_credentials()
    {
        using var nvr = LegacyNvr();
        nvr.RequireDigest = ("admin", "right-password");
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "wrong");

        Assert.Equal(OnboardStatus.BadCredentials, result.Status);
        Assert.Empty(store.GetDevices());
    }

    [Fact]
    public async Task The_legacy_nvr_authenticates_through_its_digest_realm()
    {
        // Same realm string the real recorder sent: realm="DVRNVRDVS", qop=auth.
        using var nvr = LegacyNvr();
        nvr.RequireDigest = ("admin", "hunter2");
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "hunter2");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(8, result.Channels!.Count);
    }

    [Fact]
    public void The_exact_403_body_maps_to_endpoint_not_supported()
    {
        Assert.False(IsapiClient.LooksAuthRelated(NotSupportBody));
    }

    [Fact]
    public void A_permission_403_still_maps_to_credentials()
    {
        Assert.True(IsapiClient.LooksAuthRelated(
            "<ResponseStatus><subStatusCode>noPermission</subStatusCode></ResponseStatus>"));
        Assert.True(IsapiClient.LooksAuthRelated(
            "<ResponseStatus><subStatusCode>userLock</subStatusCode></ResponseStatus>"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
