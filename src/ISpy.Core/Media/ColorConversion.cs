namespace ISpy.Core.Media;

/// <summary>
/// NV12 to BGRA and back, in BT.709 limited range.
/// </summary>
/// <remarks>
/// Used only off the hot path - saving a tile's last frame as a thumbnail on shutdown, and turning
/// that thumbnail back into a frame the renderer can draw at startup. Live video never comes
/// through here; it is converted on the GPU by the pixel shader, which uses the same coefficients.
/// </remarks>
public static class ColorConversion
{
    /// <summary>Bytes an NV12 image of these dimensions occupies: full luma plus half-size chroma.</summary>
    public static int Nv12Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// Converts NV12 to 32-bit BGRA.
    /// </summary>
    /// <param name="lumaStride">Row pitch of the source, which for a GPU readback exceeds the width.</param>
    public static byte[] Nv12ToBgra(ReadOnlySpan<byte> source, int width, int height, int lumaStride)
    {
        ValidateDimensions(width, height);

        var chromaOffset = lumaStride * height;
        var destination = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var lumaRow = y * lumaStride;

            // Chroma is subsampled 2x2, so two output rows share one chroma row.
            var chromaRow = chromaOffset + (y / 2) * lumaStride;

            for (var x = 0; x < width; x++)
            {
                var luma = (source[lumaRow + x] - 16) * 1.164383f;
                var chromaIndex = chromaRow + (x / 2) * 2;

                var u = source[chromaIndex] - 128;
                var v = source[chromaIndex + 1] - 128;

                var offset = (y * width + x) * 4;
                destination[offset + 0] = Clamp(luma + 2.112402f * u);
                destination[offset + 1] = Clamp(luma - 0.213249f * u - 0.532909f * v);
                destination[offset + 2] = Clamp(luma + 1.792741f * v);
                destination[offset + 3] = 255;
            }
        }

        return destination;
    }

    /// <summary>Converts 32-bit BGRA to NV12, writing into <paramref name="destination"/>.</summary>
    public static void BgraToNv12(
        ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int lumaStride)
    {
        ValidateDimensions(width, height);

        var chromaOffset = lumaStride * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var b = source[offset + 0];
                var g = source[offset + 1];
                var r = source[offset + 2];

                destination[y * lumaStride + x] =
                    Clamp(16f + 0.182586f * r + 0.614231f * g + 0.062007f * b);

                // Sample chroma from the top-left pixel of each 2x2 block. Averaging would be more
                // correct, but this only ever runs on thumbnails.
                if ((y & 1) != 0 || (x & 1) != 0) continue;

                var chromaIndex = chromaOffset + (y / 2) * lumaStride + (x / 2) * 2;
                destination[chromaIndex] =
                    Clamp(128f - 0.100644f * r - 0.338572f * g + 0.439216f * b);
                destination[chromaIndex + 1] =
                    Clamp(128f + 0.439216f * r - 0.398942f * g - 0.040274f * b);
            }
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        // NV12 subsamples chroma 2x2, so odd dimensions have no valid representation.
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("NV12 requires even width and height.");
    }

    private static byte Clamp(float value) => (byte)Math.Clamp(value, 0f, 255f);
}
