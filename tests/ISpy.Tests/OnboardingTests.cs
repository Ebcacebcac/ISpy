using System.Net;
using ISpy.Core.Discovery;
using ISpy.Core.Isapi;
using ISpy.Core.Model;
using ISpy.Core.Storage;
using Xunit;

namespace ISpy.Tests;

public class OnboardingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ispy-tests", Guid.NewGuid().ToString("N"));

    private InventoryStore NewStore() =>
        InventoryStore.Open(Path.Combine(_directory, "inventory.db"), new FakeSecretProtector());

    private static DiscoveredDevice Target(FakeNvr nvr) => new()
    {
        Host = nvr.Host,
        HttpPort = nvr.Port,
        Source = DiscoverySource.Manual,
    };

    [Fact]
    public async Task Adding_a_recorder_saves_it_with_its_cameras()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "hunter2");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Channels!.Count);

        var device = Assert.Single(store.GetDevices());
        Assert.Equal("Front NVR", device.DisplayName);
        Assert.Equal("SN-FAKE-0001", device.SerialNumber);
        Assert.Equal("V4.30.085", device.FirmwareVersion);
        Assert.Equal("admin", device.Username);
        Assert.Equal("hunter2", store.GetPassword(device.Id));
    }

    [Fact]
    public async Task Cameras_are_persisted_with_names_codecs_and_ptz()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");
        var channels = store.GetChannels(result.Device!.Id);

        Assert.Equal("Driveway", channels[0].Name);
        Assert.Equal("H.265", channels[0].MainCodec);
        Assert.Equal("H.264", channels[0].SubCodec);
        Assert.False(channels[0].SupportsPtz);

        // Name came from InputProxy, PTZ capability from the PTZ endpoint.
        Assert.Equal("Back Door", channels[1].Name);
        Assert.True(channels[1].SupportsPtz);
    }

    [Fact]
    public async Task Rejected_credentials_are_reported_as_such_and_nothing_is_saved()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        nvr.Serve("/ISAPI/Streaming/channels", "", HttpStatusCode.Unauthorized);
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "wrong");

        Assert.Equal(OnboardStatus.BadCredentials, result.Status);
        Assert.Empty(store.GetDevices());
    }

    [Fact]
    public async Task A_device_with_no_channels_is_not_saved()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        nvr.Serve("/ISAPI/Streaming/channels", "<StreamingChannelList/>");
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.Equal(OnboardStatus.NoChannels, result.Status);
        Assert.Empty(store.GetDevices());
    }

    [Fact]
    public async Task An_unreachable_device_is_reported_rather_than_throwing()
    {
        using var store = NewStore();

        // Port 1 on loopback: nothing listening, connection refused immediately.
        var result = await new DeviceOnboarding(store).AddAsync(
            new DiscoveredDevice { Host = "127.0.0.1", HttpPort = 1 }, "admin", "pw");

        Assert.Equal(OnboardStatus.Unreachable, result.Status);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Optional_endpoints_may_be_missing()
    {
        // A standalone camera has neither InputProxy nor PTZ; both 404. Enumeration must still work.
        using var nvr = new FakeNvr();
        nvr.Serve("/ISAPI/Streaming/channels", """
            <StreamingChannelList>
              <StreamingChannel><id>101</id><channelName>Doorbell</channelName></StreamingChannel>
            </StreamingChannelList>
            """);
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.True(result.IsSuccess);
        Assert.Equal("Doorbell", Assert.Single(result.Channels!).Name);

        // With no deviceInfo the display name falls back to what discovery knew.
        Assert.Equal(nvr.Host, result.Device!.DisplayName);
    }

    [Fact]
    public async Task Refresh_reuses_the_stored_password_and_picks_up_a_new_camera()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();
        var onboarding = new DeviceOnboarding(store);

        var added = await onboarding.AddAsync(Target(nvr), "admin", "pw");
        Assert.Equal(2, store.GetChannels(added.Device!.Id).Count);

        // A third camera is plugged into the recorder.
        nvr.Serve("/ISAPI/Streaming/channels", """
            <StreamingChannelList>
              <StreamingChannel><id>101</id><channelName>Driveway</channelName></StreamingChannel>
              <StreamingChannel><id>201</id><channelName>Back Door</channelName></StreamingChannel>
              <StreamingChannel><id>301</id><channelName>Shed</channelName></StreamingChannel>
            </StreamingChannelList>
            """);

        var refreshed = await onboarding.RefreshAsync(store.GetDevices()[0]);

        Assert.True(refreshed.IsSuccess);
        Assert.Equal(3, store.GetChannels(added.Device.Id).Count);
        Assert.Single(store.GetDevices());
    }

    [Fact]
    public async Task A_manually_added_device_is_rekeyed_on_the_serial_it_reports()
    {
        // Added by address, so discovery knew no serial - but the device reports one during
        // inspection. The saved record must key on that, or refresh saves a duplicate recorder.
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();

        var result = await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.Equal("sn:SN-FAKE-0001", result.Device!.Id);
        Assert.All(store.GetChannels(result.Device.Id), c => Assert.Equal(result.Device.Id, c.DeviceId));
    }

    [Fact]
    public async Task Repeated_adds_of_the_same_recorder_do_not_duplicate_it()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();
        var onboarding = new DeviceOnboarding(store);

        await onboarding.AddAsync(Target(nvr), "admin", "pw");
        await onboarding.AddAsync(Target(nvr), "admin", "pw");

        Assert.Single(store.GetDevices());
        Assert.Equal(2, store.GetChannels(store.GetDevices()[0].Id).Count);
    }

    [Fact]
    public async Task Refresh_without_saved_credentials_fails_cleanly()
    {
        using var store = NewStore();
        store.UpsertDevice(new Device { Id = "sn:X", Host = "192.168.1.5", DisplayName = "Orphan" });

        var result = await new DeviceOnboarding(store).RefreshAsync(store.GetDevices()[0]);

        Assert.Equal(OnboardStatus.BadCredentials, result.Status);
    }

    [Fact]
    public async Task Inspection_asks_for_all_four_endpoints_in_one_pass()
    {
        using var nvr = new FakeNvr().WithTypicalRecorder();
        using var store = NewStore();

        await new DeviceOnboarding(store).AddAsync(Target(nvr), "admin", "pw");

        Assert.Contains("/ISAPI/System/deviceInfo", nvr.Requests);
        Assert.Contains("/ISAPI/Streaming/channels", nvr.Requests);
        Assert.Contains("/ISAPI/ContentMgmt/InputProxy/channels", nvr.Requests);
        Assert.Contains("/ISAPI/PTZCtrl/channels", nvr.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
