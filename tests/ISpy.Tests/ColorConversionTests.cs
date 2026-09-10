using ISpy.Core.Media;
using Xunit;

namespace ISpy.Tests;

public class ColorConversionTests
{
    private const int Width = 4;
    private const int Height = 4;

    private static byte[] SolidBgra(byte b, byte g, byte r)
    {
        var pixels = new byte[Width * Height * 4];

        for (var i = 0; i < Width * Height; i++)
        {
            pixels[i * 4 + 0] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = 255;
        }

        return pixels;
    }

    private static byte[] RoundTrip(byte[] bgra)
    {
        var nv12 = new byte[ColorConversion.Nv12Size(Width, Height)];
        ColorConversion.BgraToNv12(bgra, nv12, Width, Height, Width);
        return ColorConversion.Nv12ToBgra(nv12, Width, Height, Width);
    }

    [Fact]
    public void Nv12_size_is_one_and_a_half_bytes_per_pixel() =>
        Assert.Equal(1920 * 1080 * 3 / 2, ColorConversion.Nv12Size(1920, 1080));

    [Theory]
    [InlineData(0, 0, 0)]        // black
    [InlineData(255, 255, 255)]  // white
    [InlineData(0, 0, 255)]      // red
    [InlineData(0, 255, 0)]      // green
    [InlineData(255, 0, 0)]      // blue
    [InlineData(128, 128, 128)]  // mid grey
    public void Colours_survive_a_round_trip(byte b, byte g, byte r)
    {
        var result = RoundTrip(SolidBgra(b, g, r));

        // Limited-range 8-bit chroma subsampling is lossy; a few levels of drift is expected, a
        // wrong hue is not.
        Assert.InRange(result[0], Math.Max(0, b - 6), Math.Min(255, b + 6));
        Assert.InRange(result[1], Math.Max(0, g - 6), Math.Min(255, g + 6));
        Assert.InRange(result[2], Math.Max(0, r - 6), Math.Min(255, r + 6));
    }

    [Fact]
    public void Alpha_is_opaque_because_video_has_none()
    {
        var result = RoundTrip(SolidBgra(10, 20, 30));

        for (var i = 3; i < result.Length; i += 4) Assert.Equal(255, result[i]);
    }

    [Fact]
    public void Output_is_four_bytes_per_pixel() =>
        Assert.Equal(Width * Height * 4, RoundTrip(SolidBgra(1, 2, 3)).Length);

    [Fact]
    public void A_readback_stride_wider_than_the_image_is_honoured()
    {
        // GPU staging textures are padded, so the row pitch is larger than the width.
        const int stride = 16;
        var nv12 = new byte[stride * Height * 3 / 2];

        ColorConversion.BgraToNv12(SolidBgra(0, 0, 255), nv12, Width, Height, stride);
        var result = ColorConversion.Nv12ToBgra(nv12, Width, Height, stride);

        Assert.InRange(result[2], 249, 255);  // red channel
        Assert.InRange(result[0], 0, 6);      // blue channel
    }

    [Fact]
    public void Luma_uses_limited_range()
    {
        // Black sits at 16 and white at 235 in limited range, not 0 and 255.
        var nv12 = new byte[ColorConversion.Nv12Size(Width, Height)];

        ColorConversion.BgraToNv12(SolidBgra(0, 0, 0), nv12, Width, Height, Width);
        Assert.Equal(16, nv12[0]);

        ColorConversion.BgraToNv12(SolidBgra(255, 255, 255), nv12, Width, Height, Width);
        Assert.InRange(nv12[0], 233, 236);
    }

    [Fact]
    public void Odd_dimensions_are_rejected_because_nv12_cannot_represent_them()
    {
        var buffer = new byte[1024];

        Assert.Throws<ArgumentException>(() =>
            ColorConversion.BgraToNv12(new byte[5 * 4 * 4], buffer, 5, 4, 5));
        Assert.Throws<ArgumentException>(() =>
            ColorConversion.Nv12ToBgra(buffer, 4, 5, 4));
    }

    [Fact]
    public void Degenerate_dimensions_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ColorConversion.Nv12ToBgra(new byte[16], 0, 4, 4));
}
