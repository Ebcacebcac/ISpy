using ISpy.Core.Media;
using ISpy.Core.Model;

namespace ISpy.Core.Isapi;

/// <summary>
/// Pages a full search out of a recorder and hands back a timeline ready to draw.
/// </summary>
public sealed class RecordingBrowser(IsapiClient client)
{
    /// <summary>Stop after this many pages, so a firmware bug cannot spin us forever.</summary>
    public const int MaxPages = 200;

    /// <summary>
    /// Fetches every recorded segment for a camera between two moments.
    /// </summary>
    public async Task<RecordingTimeline> BrowseAsync(
        int channel,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<RecordingSegment>();

        // One search id for the whole paged search: the device keys its result set on it, and
        // changing it mid-search restarts from the beginning and loops forever.
        var searchId = Guid.NewGuid();
        var index = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var request = RecordingSearch.BuildRequest(searchId, channel, from, to, index);
            var response = await client
                .PostXmlAsync("/ContentMgmt/search", request, cancellationToken)
                .ConfigureAwait(false);

            var parsed = RecordingSearch.ParseResponse(response);
            segments.AddRange(parsed.Segments);

            // A page that returns nothing ends the search whatever the status says; without this a
            // device that reports MORE forever would page until MaxPages.
            if (parsed.IsComplete || parsed.Segments.Count == 0) break;

            index += parsed.Segments.Count;
        }

        return new RecordingTimeline(from, to, segments);
    }
}
