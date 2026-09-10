using System.Globalization;
using System.Xml.Linq;
using ISpy.Core.Model;
using ISpy.Core.Protocol;

namespace ISpy.Core.Isapi;

/// <summary>Outcome of one search request, including whether the device has more to give.</summary>
public sealed record RecordingSearchPage(
    IReadOnlyList<RecordingSegment> Segments,
    bool IsComplete,
    int MatchesFound);

/// <summary>
/// Finds recorded footage through <c>/ISAPI/ContentMgmt/search</c>.
/// </summary>
/// <remarks>
/// The device answers a limited number of matches per request, so a full day is assembled by paging
/// with an increasing start index. The search id must stay the same across pages of one search.
/// </remarks>
public static class RecordingSearch
{
    /// <summary>Matches per request. Firmware commonly caps this around 40-100.</summary>
    public const int PageSize = 40;

    public static string BuildRequest(
        Guid searchId, int channel, DateTimeOffset from, DateTimeOffset to, int startIndex = 0)
    {
        if (to <= from) throw new ArgumentException("Search end must be after start.", nameof(to));

        var trackId = HikvisionUrls.StreamId(channel, StreamProfile.Main);

        var request = new XElement("CMSearchDescription",
            new XElement("searchID", searchId.ToString("D").ToUpperInvariant()),
            new XElement("trackIDList",
                new XElement("trackID", trackId.ToString(CultureInfo.InvariantCulture))),
            new XElement("timeSpanList",
                new XElement("timeSpan",
                    new XElement("startTime", FormatIso(from)),
                    new XElement("endTime", FormatIso(to)))),
            new XElement("maxResults", PageSize.ToString(CultureInfo.InvariantCulture)),
            new XElement("searchResultPostion", startIndex.ToString(CultureInfo.InvariantCulture)),
            new XElement("metadataList",
                new XElement("metadataDescriptor", "//recordType.meta.std-cgi.com")));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), request)
            .ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// ISAPI search uses extended ISO 8601 with a Z suffix, unlike the basic format the RTSP
    /// playback URL wants. Mixing the two up is a silent "no recordings found".
    /// </summary>
    public static string FormatIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static RecordingSearchPage ParseResponse(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return new RecordingSearchPage([], IsComplete: true, MatchesFound: 0);

        var segments = new List<RecordingSegment>();

        foreach (var match in root.DescendantsLocal("searchMatchItem"))
        {
            var start = TryParseIso(match.Value("startTime"));
            var end = TryParseIso(match.Value("endTime"));

            // A segment still being written has no end time yet; skip rather than inventing one.
            if (start is null || end is null || end <= start) continue;

            segments.Add(new RecordingSegment
            {
                Start = start.Value,
                End = end.Value,
                Trigger = ParseTrigger(match.Value("recordType")),
                PlaybackUri = match.Value("playbackURI"),
                SizeBytes = long.TryParse(match.Value("size"), out var size) ? size : null,
            });
        }

        // "MORE" means the device has further matches for this search id.
        var status = root.Value("responseStatusStrg") ?? root.Value("responseStatus");
        var isComplete = !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase);

        return new RecordingSearchPage(
            segments,
            isComplete,
            root.Int("numOfMatches") ?? segments.Count);
    }

    /// <summary>
    /// Recorder firmware spells the record type inconsistently, so this matches on substrings
    /// rather than an exact set - an unrecognised value degrades to Unknown, never to a crash.
    /// </summary>
    public static RecordingTrigger ParseTrigger(string? recordType)
    {
        if (string.IsNullOrWhiteSpace(recordType)) return RecordingTrigger.Unknown;

        var value = recordType.ToLowerInvariant();

        if (value.Contains("timing") || value.Contains("schedule") || value.Contains("continuous"))
            return RecordingTrigger.Continuous;
        if (value.Contains("motion") || value.Contains("vmd"))
            return RecordingTrigger.Motion;
        if (value.Contains("alarm") || value.Contains("event") || value.Contains("io"))
            return RecordingTrigger.Alarm;
        if (value.Contains("manual"))
            return RecordingTrigger.Manual;

        return RecordingTrigger.Unknown;
    }

    private static DateTimeOffset? TryParseIso(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
