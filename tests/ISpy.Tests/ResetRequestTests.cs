using ISpy.Core.Discovery;
using ISpy.Core.Model;
using Xunit;

namespace ISpy.Tests;

public class ResetRequestTests
{
    private static DiscoveredDevice Device() => new()
    {
        Host = "192.168.1.135",
        Model = "DS-216M-A",
        SerialNumber = "DS-216M-A0120170101CCRRJ12345",
        MacAddress = "44:19:B6:1A:2B:3C",
        FirmwareVersion = "V3.4.95build 170706",
        Source = DiscoverySource.Sadp,
    };

    [Fact]
    public void Reset_request_is_built_from_a_discovered_device()
    {
        var request = ResetRequest.From(Device());

        Assert.Equal("DS-216M-A", request.Model);
        Assert.Equal("DS-216M-A0120170101CCRRJ12345", request.SerialNumber);
        Assert.Equal("44:19:B6:1A:2B:3C", request.MacAddress);
        Assert.True(request.IsComplete);
    }

    [Fact]
    public void A_device_without_a_serial_is_incomplete()
    {
        var request = ResetRequest.From(Device() with { SerialNumber = null });
        Assert.False(request.IsComplete);
    }

    [Fact]
    public void Shareable_text_carries_every_detail_a_reseller_asks_for()
    {
        var text = ResetRequest.From(Device()).ToShareableText();

        Assert.Contains("DS-216M-A", text);
        Assert.Contains("DS-216M-A0120170101CCRRJ12345", text);
        Assert.Contains("44:19:B6:1A:2B:3C", text);
        Assert.Contains("V3.4.95build 170706", text);
        // The date hint matters: the reseller code is derived from the device's own current date.
        Assert.Contains("date", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_fields_read_as_not_reported_rather_than_blank()
    {
        var text = ResetRequest.From(Device() with { MacAddress = null, FirmwareVersion = null })
            .ToShareableText();

        Assert.Contains("(not reported)", text);
    }

    [Fact]
    public void Suggested_file_name_is_filesystem_safe()
    {
        var name = ResetRequest.From(Device()).SuggestedFileName;

        Assert.StartsWith("ISpy-reset-", name);
        Assert.EndsWith(".txt", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.All(System.IO.Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, name));
    }

    [Fact]
    public void Suggested_file_name_falls_back_to_the_address_without_a_serial()
    {
        var name = ResetRequest.From(Device() with { SerialNumber = "" }).SuggestedFileName;

        Assert.Contains("19216811", name);  // host digits, punctuation stripped
    }
}
