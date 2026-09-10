using System.Text;
using ISpy.Core.Model;

namespace ISpy.Core.Discovery;

/// <summary>
/// The recovery methods a Hikvision-family device supports, in the order ISpy prefers them.
/// </summary>
public enum ResetMethod
{
    /// <summary>
    /// Reset through the bound Guarding Vision / Hik-Connect account. The account proves ownership
    /// server-side, so no code has to be relayed by hand - the cleanest path when the owner still
    /// has the account, which is the common case.
    /// </summary>
    GuardingVisionAccount,

    /// <summary>
    /// The reseller / manufacturer computes an unlock code from the device serial and date. This is
    /// the route for older firmware whose account-based reset predates the instant flow.
    /// </summary>
    ResellerCode,
}

/// <summary>
/// The identity and instructions needed to recover a device's admin password, gathered from the
/// device's own SADP reply.
/// </summary>
/// <remarks>
/// ISpy deliberately stops at gathering and presenting this. Turning it into a new password is a
/// deliberate ownership check - performed by the Guarding Vision account or the reseller, both of
/// which hold a key ISpy does not and should not. Faking that step would only fail on the device.
/// </remarks>
public sealed record ResetRequest
{
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public string? MacAddress { get; init; }
    public string? FirmwareVersion { get; init; }
    public string Host { get; init; } = "";

    /// <summary>When ISpy captured these details - the reseller code method needs a reference date.</summary>
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;

    public static ResetRequest From(DiscoveredDevice device) => new()
    {
        Model = device.Model ?? "Unknown model",
        SerialNumber = device.SerialNumber ?? "",
        MacAddress = device.MacAddress,
        FirmwareVersion = device.FirmwareVersion,
        Host = device.Host,
    };

    /// <summary>True when the device gave us enough to identify it to an account or reseller.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(SerialNumber);

    /// <summary>A safe file name for the exported request, unique to the device and moment.</summary>
    public string SuggestedFileName
    {
        get
        {
            var serial = new string((SerialNumber.Length > 0 ? SerialNumber : Host)
                .Where(c => char.IsLetterOrDigit(c)).ToArray());

            var stamp = CapturedUtc.ToLocalTime().ToString("yyyyMMdd-HHmm");
            return $"ISpy-reset-{serial}-{stamp}.txt";
        }
    }

    /// <summary>
    /// The details to hand to a reseller or read into an account reset. Written as plain text so it
    /// can be pasted into an email or a support form without anything being lost in formatting.
    /// </summary>
    public string ToShareableText() => new StringBuilder()
        .AppendLine("ISpy - security camera password reset request")
        .AppendLine("================================================")
        .AppendLine()
        .AppendLine("Please help me reset the admin password on a device I own.")
        .AppendLine("The device is a Hikvision-family recorder/camera; these are the details")
        .AppendLine("its own network reply reported:")
        .AppendLine()
        .Append("  Model:            ").AppendLine(Model)
        .Append("  Serial number:    ").AppendLine(SerialNumber)
        .Append("  MAC address:      ").AppendLine(MacAddress ?? "(not reported)")
        .Append("  Firmware:         ").AppendLine(FirmwareVersion ?? "(not reported)")
        .Append("  Address on LAN:   ").AppendLine(Host)
        .Append("  Captured (local): ").AppendLine(CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        .AppendLine()
        .AppendLine("Note: the reset/security code is usually tied to the device's own current")
        .AppendLine("date. If asked, read the date shown on the recorder's screen or web page.")
        .ToString();
}
