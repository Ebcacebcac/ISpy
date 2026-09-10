using ISpy.Core.Discovery;
using ISpy.Core.Model;
using ISpy.Core.Storage;

namespace ISpy.Core.Isapi;

/// <summary>Why an attempt to add a device did not work, in terms the UI can show verbatim.</summary>
public enum OnboardStatus
{
    Success,
    BadCredentials,
    Unreachable,
    NoChannels,
}

public sealed record OnboardResult(
    OnboardStatus Status,
    Device? Device = null,
    IReadOnlyList<Channel>? Channels = null,
    string? Message = null)
{
    public bool IsSuccess => Status == OnboardStatus.Success;
}

/// <summary>
/// Authenticates against a discovered device, enumerates its cameras and saves the result.
/// This is the whole "add my recorder" flow, kept out of the UI so it can be tested.
/// </summary>
public sealed class DeviceOnboarding(InventoryStore store, DeviceInspector? inspector = null)
{
    private readonly DeviceInspector _inspector = inspector ?? new DeviceInspector();

    public async Task<OnboardResult> AddAsync(
        DiscoveredDevice discovered,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var client = new IsapiClient(discovered.Host, discovered.HttpPort, username, password);

        DeviceInspection inspection;

        try
        {
            inspection = await _inspector.InspectAsync(client, discovered.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IsapiAuthenticationException ex)
        {
            // Surface the device's actual reason - a lockout reads very differently from a wrong
            // password, and the remedy is different too.
            return new OnboardResult(OnboardStatus.BadCredentials, Message: ex.Message);
        }
        catch (IsapiException ex)
        {
            return new OnboardResult(OnboardStatus.Unreachable, Message: ex.Message);
        }

        if (inspection.Channels.Count == 0)
        {
            return new OnboardResult(OnboardStatus.NoChannels,
                Message: "The device authenticated but reported no camera channels.");
        }

        // Prefer the identity the device reports over what discovery guessed: SADP model strings are
        // often a marketing name, while deviceInfo carries the real model and serial.
        var serialNumber = FirstNonEmpty(inspection.Info?.SerialNumber, discovered.SerialNumber);

        var device = discovered.ToDevice() with
        {
            // Keyed on the serial we ended up with, not the one discovery happened to know. A manual
            // add starts with no serial and learns it here; without recomputing, the next refresh
            // would key on the serial, miss the existing row and save the recorder twice.
            Id = Device.MakeId(serialNumber, discovered.Host),
            Username = username,
            DisplayName = FirstNonEmpty(
                inspection.Info?.DeviceName, inspection.Info?.Model, discovered.DisplayName)
                ?? discovered.Host,
            Model = FirstNonEmpty(inspection.Info?.Model, discovered.Model),
            SerialNumber = serialNumber,
            FirmwareVersion = FirstNonEmpty(inspection.Info?.FirmwareVersion, discovered.FirmwareVersion),
        };

        // Channels were parsed against the pre-inspection id; restamp them onto the final one.
        var channels = inspection.Channels
            .Select(channel => channel with { DeviceId = device.Id })
            .ToArray();

        store.UpsertDevice(device, password);
        store.ReplaceChannels(device.Id, channels);

        return new OnboardResult(OnboardStatus.Success, device, channels);
    }

    /// <summary>
    /// Re-reads an already-saved device's channel list, using the stored password. Used on refresh
    /// so a camera added to the recorder shows up without the user re-entering anything.
    /// </summary>
    public async Task<OnboardResult> RefreshAsync(
        Device device, CancellationToken cancellationToken = default)
    {
        var password = store.GetPassword(device.Id);
        if (password is null || device.Username is null)
        {
            return new OnboardResult(OnboardStatus.BadCredentials,
                Message: "No saved credentials for this device.");
        }

        return await AddAsync(
            new DiscoveredDevice
            {
                Host = device.Host,
                HttpPort = device.HttpPort,
                RtspPort = device.RtspPort,
                Model = device.Model,
                SerialNumber = device.SerialNumber,
                FirmwareVersion = device.FirmwareVersion,
                Source = device.Source,
            },
            device.Username,
            password,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
