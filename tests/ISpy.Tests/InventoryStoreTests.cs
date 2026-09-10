using ISpy.Core.Model;
using ISpy.Core.Storage;
using Xunit;

namespace ISpy.Tests;

public class InventoryStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ispy-tests", Guid.NewGuid().ToString("N"));

    private InventoryStore NewStore() =>
        InventoryStore.Open(Path.Combine(_directory, "inventory.db"), new FakeSecretProtector());

    private static Device SampleDevice(string host = "192.168.1.64") => new()
    {
        Id = Device.MakeId("DS-7608-ABC123", host),
        Host = host,
        DisplayName = "Front NVR",
        HttpPort = 80,
        RtspPort = 554,
        Model = "DS-7608NI",
        SerialNumber = "DS-7608-ABC123",
        Username = "admin",
        Source = DiscoverySource.Sadp,
    };

    [Fact]
    public void Device_round_trips()
    {
        using var store = NewStore();
        store.UpsertDevice(SampleDevice(), "hunter2");

        var device = Assert.Single(store.GetDevices());
        Assert.Equal("Front NVR", device.DisplayName);
        Assert.Equal("192.168.1.64", device.Host);
        Assert.Equal(DiscoverySource.Sadp, device.Source);
        Assert.Equal("hunter2", store.GetPassword(device.Id));
    }

    [Fact]
    public void Password_is_not_stored_in_plaintext()
    {
        var path = Path.Combine(_directory, "inventory.db");
        using (var store = InventoryStore.Open(path, new FakeSecretProtector()))
        {
            store.UpsertDevice(SampleDevice(), "sup3rs3cret");
        }

        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("sup3rs3cret", System.Text.Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void Rediscovery_without_a_password_keeps_the_stored_one()
    {
        using var store = NewStore();
        var device = SampleDevice();
        store.UpsertDevice(device, "hunter2");

        // SADP rediscovery refreshes the address but carries no credentials.
        store.UpsertDevice(device with { Host = "192.168.1.99", FirmwareVersion = "V5.7.3" });

        var stored = Assert.Single(store.GetDevices());
        Assert.Equal("192.168.1.99", stored.Host);
        Assert.Equal("V5.7.3", stored.FirmwareVersion);
        Assert.Equal("hunter2", store.GetPassword(stored.Id));
    }

    [Fact]
    public void Upsert_is_keyed_on_serial_so_a_dhcp_move_is_not_a_new_device()
    {
        using var store = NewStore();
        store.UpsertDevice(SampleDevice("192.168.1.64"), "pw");
        store.UpsertDevice(SampleDevice("192.168.1.77"));

        Assert.Single(store.GetDevices());
    }

    [Fact]
    public void GetPassword_returns_null_when_none_saved()
    {
        using var store = NewStore();
        store.UpsertDevice(SampleDevice());
        Assert.Null(store.GetPassword(SampleDevice().Id));
    }

    [Fact]
    public void Channels_replace_rather_than_accumulate()
    {
        using var store = NewStore();
        var device = SampleDevice();
        store.UpsertDevice(device, "pw");

        store.ReplaceChannels(device.Id,
        [
            new Channel { DeviceId = device.Id, Number = 1, Name = "Drive", SupportsPtz = true },
            new Channel { DeviceId = device.Id, Number = 2, Name = "Back Door" },
        ]);

        // Camera 2 unplugged from the NVR; camera 1 renamed.
        store.ReplaceChannels(device.Id,
        [
            new Channel { DeviceId = device.Id, Number = 1, Name = "Driveway", SupportsPtz = true },
        ]);

        var channel = Assert.Single(store.GetChannels(device.Id));
        Assert.Equal("Driveway", channel.Name);
        Assert.True(channel.SupportsPtz);
    }

    [Fact]
    public void Channels_come_back_in_channel_order()
    {
        using var store = NewStore();
        var device = SampleDevice();
        store.UpsertDevice(device, "pw");
        store.ReplaceChannels(device.Id, new[] { 10, 2, 1 }
            .Select(n => new Channel { DeviceId = device.Id, Number = n }));

        Assert.Equal([1, 2, 10], store.GetChannels(device.Id).Select(c => c.Number));
    }

    [Fact]
    public void Unnamed_channel_gets_a_sensible_display_name() =>
        Assert.Equal("Camera 4", new Channel { DeviceId = "d", Number = 4 }.DisplayName);

    [Fact]
    public void Deleting_a_device_takes_its_channels_with_it()
    {
        using var store = NewStore();
        var device = SampleDevice();
        store.UpsertDevice(device, "pw");
        store.ReplaceChannels(device.Id, [new Channel { DeviceId = device.Id, Number = 1 }]);

        store.DeleteDevice(device.Id);

        Assert.Empty(store.GetDevices());
        Assert.Empty(store.GetChannels(device.Id));
    }

    [Fact]
    public void Settings_round_trip_and_overwrite()
    {
        using var store = NewStore();
        Assert.Null(store.GetSetting("layout"));

        store.SetSetting("layout", "grid-9");
        store.SetSetting("layout", "grid-16");

        Assert.Equal("grid-16", store.GetSetting("layout"));
    }

    [Fact]
    public void Reopening_an_existing_database_keeps_data_and_does_not_remigrate()
    {
        var path = Path.Combine(_directory, "inventory.db");
        using (var store = InventoryStore.Open(path, new FakeSecretProtector()))
        {
            store.UpsertDevice(SampleDevice(), "pw");
        }

        using var reopened = InventoryStore.Open(path, new FakeSecretProtector());
        Assert.Single(reopened.GetDevices());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
