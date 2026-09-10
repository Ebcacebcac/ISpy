using ISpy.Core.Model;

namespace ISpy.Core.Media;

/// <summary>
/// Chooses which encoded stream a tile should pull.
/// </summary>
/// <remarks>
/// This is the single biggest performance decision in the app. A recorder already encodes a
/// low-resolution sub-stream per camera; pulling sixteen main streams to draw them at 320x180
/// wastes bandwidth, decode capacity and, on the recorder side, its own outbound limit. The rule is
/// simple: tiles get the sub-stream, anything shown large gets the main stream.
/// </remarks>
public static class StreamSelection
{
    /// <summary>Below this displayed width the sub-stream is indistinguishable from the main one.</summary>
    public const int SubStreamWidthThreshold = 640;

    public static StreamProfile ForTile(int displayedWidthPixels, bool isMaximized)
    {
        if (isMaximized) return StreamProfile.Main;

        return displayedWidthPixels >= SubStreamWidthThreshold
            ? StreamProfile.Main
            : StreamProfile.Sub;
    }

    /// <summary>
    /// Falls back to the main stream when a camera has no sub-stream configured, which happens on
    /// third-party cameras proxied through an NVR.
    /// </summary>
    public static StreamProfile Resolve(Channel channel, StreamProfile wanted) =>
        wanted == StreamProfile.Sub && string.IsNullOrWhiteSpace(channel.SubCodec)
            ? StreamProfile.Main
            : wanted;
}
