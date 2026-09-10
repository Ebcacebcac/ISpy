using System.Windows;
using System.Windows.Controls;
using ISpy.Core.Discovery;
using ISpy.Core.Isapi;
using ISpy.Core.Model;
using ISpy.Core.Storage;

namespace ISpy.App;

/// <summary>Row shown in the discovery list.</summary>
public sealed record DiscoveredRow(DiscoveredDevice Device)
{
    public string Title => Device.DisplayName;

    public string Detail
    {
        get
        {
            var parts = new List<string> { $"{Device.Host}:{Device.HttpPort}" };
            if (Device.FirmwareVersion is { } firmware) parts.Add(firmware);
            if (Device.IsActivated == false) parts.Add("not activated");
            parts.Add(Device.Source.ToString().ToUpperInvariant());
            return string.Join("  ·  ", parts);
        }
    }
}

public partial class AddDeviceWindow : Window
{
    private readonly InventoryStore _store;
    private CancellationTokenSource? _scan;

    public AddDeviceWindow(InventoryStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += (_, _) => StartScan();
    }

    /// <summary>Set once a device has been added successfully, so the caller can refresh.</summary>
    public Device? AddedDevice { get; private set; }

    private void OnScan(object sender, RoutedEventArgs e) => StartScan();

    private async void StartScan()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();

        DeviceList.Items.Clear();
        ScanButton.IsEnabled = false;
        ScanStatus.Text = "Searching the network…";

        try
        {
            var found = await new LanDiscovery().DiscoverAsync(
                onFound: device => Dispatcher.Invoke(() => DeviceList.Items.Add(new DiscoveredRow(device))),
                cancellationToken: _scan.Token);

            ScanStatus.Text = found.Count switch
            {
                0 => "Nothing found. Some routers block discovery — enter the address below instead.",
                1 => "Found 1 device.",
                _ => $"Found {found.Count} devices.",
            };
        }
        catch (OperationCanceledException)
        {
            // Superseded by another scan.
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    /// <summary>Selecting a discovered device fills the manual fields, so both paths share one flow.</summary>
    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DiscoveredRow row) return;

        HostBox.Text = row.Device.Host;
        PortBox.Text = row.Device.HttpPort.ToString();
        PasswordBox.Focus();
    }

    /// <summary>
    /// Opens the recovery assistant for whichever device is in focus - the selected scan result, or
    /// the address typed in manually. A successful recovery connects and closes this dialog too.
    /// </summary>
    private void OnForgotPassword(object sender, System.Windows.RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();

        var target = (DeviceList.SelectedItem as DiscoveredRow)?.Device
            ?? (host.Length > 0
                ? new DiscoveredDevice { Host = host, Source = DiscoverySource.Manual }
                : null);

        if (target is null)
        {
            ResultText.Text = "Pick a device from the list, or type its address first.";
            return;
        }

        var reset = new PasswordResetWindow(_store, target) { Owner = this };

        if (reset.ShowDialog() == true)
        {
            AddedDevice = reset.ConnectedDevice;
            DialogResult = true;
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            ResultText.Text = "Enter an address, or pick a device from the list.";
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ResultText.Text = "That HTTP port is not valid.";
            return;
        }

        // Carry the discovery metadata through when the address matches a scanned device, so the
        // saved record keeps its serial and firmware rather than being identified only by address.
        var selected = (DeviceList.SelectedItem as DiscoveredRow)?.Device;
        var target = selected is not null && selected.Host == host
            ? selected with { HttpPort = port }
            : new DiscoveredDevice { Host = host, HttpPort = port, Source = DiscoverySource.Manual };

        ConnectButton.IsEnabled = false;
        ResultText.Text = "Connecting…";

        try
        {
            var result = await new DeviceOnboarding(_store)
                .AddAsync(target, UserBox.Text.Trim(), PasswordBox.Password);

            if (result.IsSuccess)
            {
                AddedDevice = result.Device;
                DialogResult = true;
                return;
            }

            ResultText.Text = result.Message ?? "Could not add that device.";
        }
        catch (Exception ex)
        {
            ResultText.Text = ex.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _scan?.Cancel();
        _scan?.Dispose();
        base.OnClosed(e);
    }
}
