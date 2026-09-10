using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ISpy.Core;

namespace ISpy.App;

/// <summary>A saved still, ready to upload as a tile's placeholder.</summary>
public sealed record Poster(byte[] Bgra, int Width, int Height);

/// <summary>
/// Keeps the last frame of each camera on disk between runs.
/// </summary>
/// <remarks>
/// This is what makes the app feel instant. Connecting to a recorder and waiting for a keyframe
/// takes a second or so no matter how well the pipeline is written; painting last night's frame
/// immediately means the grid is never a wall of black rectangles while that happens.
/// </remarks>
public static class PosterStore
{
    /// <summary>Thumbnails are stored small - they exist to fill a tile for a moment, not to be kept.</summary>
    private const int MaxWidth = 640;

    private static string PathFor(string deviceId, int channel)
    {
        // Device ids contain characters that are not valid in a file name (":" and "/").
        var safe = string.Concat(deviceId.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        return Path.Combine(AppPaths.ThumbnailDirectory, $"{safe}-{channel}.jpg");
    }

    /// <summary>Saves a frame. Best effort - a failure here must never affect shutdown.</summary>
    public static void Save(string deviceId, int channel, byte[] bgra, int width, int height)
    {
        try
        {
            AppPaths.EnsureCreated();

            var source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);

            BitmapSource scaled = width > MaxWidth
                ? new TransformedBitmap(source, new ScaleTransform((double)MaxWidth / width, (double)MaxWidth / width))
                : source;

            var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
            encoder.Frames.Add(BitmapFrame.Create(scaled));

            using var file = File.Create(PathFor(deviceId, channel));
            encoder.Save(file);
        }
        catch (Exception)
        {
            // A missing thumbnail costs nothing; a crash on exit costs the user their session.
        }
    }

    /// <summary>Loads a saved frame, or null when there is none.</summary>
    public static Poster? Load(string deviceId, int channel)
    {
        var path = PathFor(deviceId, channel);
        if (!File.Exists(path)) return null;

        try
        {
            var decoded = new FormatConvertedBitmap(
                new BitmapImage(new Uri(path, UriKind.Absolute)), PixelFormats.Bgra32, null, 0);

            // NV12 has no representation for odd dimensions, so trim to an even size.
            var width = decoded.PixelWidth & ~1;
            var height = decoded.PixelHeight & ~1;
            if (width < 2 || height < 2) return null;

            var pixels = new byte[width * height * 4];
            decoded.CopyPixels(
                new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);

            return new Poster(pixels, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
