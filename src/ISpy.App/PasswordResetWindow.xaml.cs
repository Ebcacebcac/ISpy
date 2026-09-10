using System.IO;
using System.Windows;
using ISpy.Core.Discovery;
using ISpy.Core.Isapi;
using ISpy.Core.Model;
using ISpy.Core.Storage;
using Microsoft.Win32;

namespace ISpy.App;

/// <summary>
/// Guides the owner through recovering a device's admin password and, once they've reset it,
/// connects with the new one - without reopening the Add recorder dialog.
/// </summary>
/// <remarks>
/// ISpy stops short of performing the reset. Turning the device's challenge into a new password is
/// an ownership check the Guarding Vision account and the reseller each hold a key for; ISpy does
/// not, and faking it would only fail on the device. So this window does the honest half - it
/// gathers and presents exactly what those routes need, and resumes the moment the password exists.
/// </remarks>
public partial class PasswordResetWindow : Window
{
    private readonly InventoryStore _store;
    private readonly DiscoveredDevice _device;
    private readonly ResetRequest _request;

    public PasswordResetWindow(InventoryStore store, DiscoveredDevice device)
    {
        _store = store;
        _device = device;
        _request = ResetRequest.From(device);

        InitializeComponent();

        DeviceName.Text = device.DisplayName;
        DeviceDetail.Text = string.Join("   ·   ", new[]
        {
            $"{device.Host}:{device.HttpPort}",
            string.IsNullOrWhiteSpace(device.SerialNumber) ? null : $"S/N {device.SerialNumber}",
            device.FirmwareVersion,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        // Without a serial there is nothing for a reseller to key on; the account route still works,
        // so we disable only the export half rather than the whole window.
        if (!_request.IsComplete)
        {
            CopyButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            ExportStatus.Text = "This device didn't report a serial number.";
        }
    }

    /// <summary>The password the user recovered, set once a connection succeeds.</summary>
    public Device? ConnectedDevice { get; private set; }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_request.ToShareableText());
            ExportStatus.Text = "Copied — paste it into your email or support form.";
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"Could not copy: {ex.Message}";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text file|*.txt",
            FileName = _request.SuggestedFileName,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _request.ToShareableText());
            ExportStatus.Text = $"Saved {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"Could not save: {ex.Message}";
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (password.Length == 0)
        {
            ResultText.Text = "Enter the new password once you've reset it.";
            return;
        }

        ConnectButton.IsEnabled = false;
        ResultText.Text = "Connecting…";

        try
        {
            var result = await new DeviceOnboarding(_store)
                .AddAsync(_device, UserBox.Text.Trim(), password);

            switch (result.Status)
            {
                case OnboardStatus.Success:
                    ConnectedDevice = result.Device;
                    DialogResult = true;
                    return;

                case OnboardStatus.BadCredentials:
                    // The most useful message here: the reset either hasn't taken yet or set a
                    // different password than what was typed.
                    ResultText.Text =
                        "That password was rejected. If you just reset it, give the device a few " +
                        "seconds and check you entered the new password exactly.";
                    break;

                default:
                    ResultText.Text = result.Message ?? "Could not connect.";
                    break;
            }
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
}
