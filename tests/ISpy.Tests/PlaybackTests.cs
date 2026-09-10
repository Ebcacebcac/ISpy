using ISpy.Core.Isapi;
using ISpy.Core.Media;
using ISpy.Core.Model;
using Xunit;

namespace ISpy.Tests;

public class RecordingSearchTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Request_targets_the_channel_main_track_over_the_window()
    {
        var request = RecordingSearch.BuildRequest(Guid.NewGuid(), 3, Noon, Noon.AddHours(1));

        Assert.Contains("<trackID>301</trackID>", request);
        Assert.Contains("<startTime>2026-09-10T12:00:00Z</startTime>", request);
        Assert.Contains("<endTime>2026-09-10T13:00:00Z</endTime>", request);
    }

    [Fact]
    public void Request_carries_the_paging_index()
    {
        var request = RecordingSearch.BuildRequest(Guid.NewGuid(), 1, Noon, Noon.AddHours(1), startIndex: 40);

        // The field really is spelled "Positon" in the ISAPI schema.
        Assert.Contains("<searchResultPostion>40</searchResultPostion>", request);
    }

    [Fact]
    public void Request_rejects_an_inverted_window() =>
        Assert.Throws<ArgumentException>(() =>
            RecordingSearch.BuildRequest(Guid.NewGuid(), 1, Noon, Noon));

    [Fact]
    public void Search_time_format_differs_from_the_rtsp_one()
    {
        // Extended ISO for search, basic format for the playback URL. Swapping them silently
        // returns no results.
        Assert.Equal("2026-09-10T12:00:00Z", RecordingSearch.FormatIso(Noon));
        Assert.Equal("20260910T120000Z", ISpy.Core.Protocol.HikvisionUrls.FormatTime(Noon));
    }

    private const string SearchResult = """
        <CMSearchResult xmlns="http://www.hikvision.com/ver20/XMLSchema">
          <searchID>{6B1B2E62-1111-2222-3333-444455556666}</searchID>
          <responseStatus>true</responseStatus>
          <responseStatusStrg>MORE</responseStatusStrg>
          <numOfMatches>2</numOfMatches>
          <matchList>
            <searchMatchItem>
              <timeSpan>
                <startTime>2026-09-10T12:00:00Z</startTime>
                <endTime>2026-09-10T12:30:00Z</endTime>
              </timeSpan>
              <mediaSegmentDescriptor>
                <recordType>timing</recordType>
                <playbackURI>rtsp://192.168.1.64/Streaming/tracks/101?starttime=20260910T120000Z</playbackURI>
              </mediaSegmentDescriptor>
              <metadataMatches><metadataDescriptor>recordType.meta</metadataDescriptor></metadataMatches>
              <size>734003200</size>
            </searchMatchItem>
            <searchMatchItem>
              <timeSpan>
                <startTime>2026-09-10T13:00:00Z</startTime>
                <endTime>2026-09-10T13:05:00Z</endTime>
              </timeSpan>
              <mediaSegmentDescriptor><recordType>motionDetection</recordType></mediaSegmentDescriptor>
            </searchMatchItem>
          </matchList>
        </CMSearchResult>
        """;

    [Fact]
    public void Matches_are_parsed_with_their_trigger()
    {
        var page = RecordingSearch.ParseResponse(SearchResult);

        Assert.Equal(2, page.Segments.Count);
        Assert.Equal(RecordingTrigger.Continuous, page.Segments[0].Trigger);
        Assert.Equal(TimeSpan.FromMinutes(30), page.Segments[0].Duration);
        Assert.Equal(734003200, page.Segments[0].SizeBytes);
        Assert.Equal(RecordingTrigger.Motion, page.Segments[1].Trigger);
    }

    [Fact]
    public void More_results_are_reported_so_paging_continues()
    {
        Assert.False(RecordingSearch.ParseResponse(SearchResult).IsComplete);

        var last = SearchResult.Replace("<responseStatusStrg>MORE</responseStatusStrg>",
                                        "<responseStatusStrg>OK</responseStatusStrg>");
        Assert.True(RecordingSearch.ParseResponse(last).IsComplete);
    }

    [Fact]
    public void A_segment_still_being_written_is_skipped()
    {
        const string xml = """
            <CMSearchResult><matchList>
              <searchMatchItem><timeSpan><startTime>2026-09-10T12:00:00Z</startTime></timeSpan></searchMatchItem>
            </matchList></CMSearchResult>
            """;

        Assert.Empty(RecordingSearch.ParseResponse(xml).Segments);
    }

    [Fact]
    public void Malformed_responses_yield_nothing_rather_than_throwing()
    {
        Assert.Empty(RecordingSearch.ParseResponse("<broken").Segments);
        Assert.Empty(RecordingSearch.ParseResponse("").Segments);
    }

    [Theory]
    [InlineData("timing", RecordingTrigger.Continuous)]
    [InlineData("SCHEDULE", RecordingTrigger.Continuous)]
    [InlineData("motionDetection", RecordingTrigger.Motion)]
    [InlineData("vmd", RecordingTrigger.Motion)]
    [InlineData("alarmInput", RecordingTrigger.Alarm)]
    [InlineData("manualRecord", RecordingTrigger.Manual)]
    [InlineData("somethingNew", RecordingTrigger.Unknown)]
    [InlineData(null, RecordingTrigger.Unknown)]
    public void Record_types_are_matched_loosely(string? value, RecordingTrigger expected) =>
        Assert.Equal(expected, RecordingSearch.ParseTrigger(value));
}

public class RecordingTimelineTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayEnd = DayStart.AddDays(1);

    private static RecordingSegment Segment(int startHour, int endHour,
        RecordingTrigger trigger = RecordingTrigger.Continuous) => new()
    {
        Start = DayStart.AddHours(startHour),
        End = DayStart.AddHours(endHour),
        Trigger = trigger,
    };

    private static RecordingTimeline Timeline(params RecordingSegment[] segments) =>
        new(DayStart, DayEnd, segments);

    [Fact]
    public void Touching_segments_of_the_same_type_merge_into_one_bar()
    {
        // Recorders return one segment per file; drawn raw these look like gaps in the footage.
        var timeline = Timeline(Segment(1, 2), Segment(2, 3), Segment(3, 4));

        var segment = Assert.Single(timeline.Segments);
        Assert.Equal(DayStart.AddHours(1), segment.Start);
        Assert.Equal(DayStart.AddHours(4), segment.End);
    }

    [Fact]
    public void Overlapping_segments_merge()
    {
        var timeline = Timeline(Segment(1, 3), Segment(2, 5));

        Assert.Single(timeline.Segments);
        Assert.Equal(TimeSpan.FromHours(4), timeline.RecordedDuration);
    }

    [Fact]
    public void Different_triggers_stay_separate_so_the_colours_survive()
    {
        var timeline = Timeline(
            Segment(1, 2, RecordingTrigger.Continuous),
            Segment(2, 3, RecordingTrigger.Motion));

        Assert.Equal(2, timeline.Segments.Count);
    }

    [Fact]
    public void A_merged_span_drops_its_single_file_handle()
    {
        var timeline = Timeline(
            Segment(1, 2) with { PlaybackUri = "rtsp://a", SizeBytes = 100 },
            Segment(2, 3) with { PlaybackUri = "rtsp://b", SizeBytes = 200 });

        Assert.Null(Assert.Single(timeline.Segments).PlaybackUri);
    }

    [Fact]
    public void Segments_are_clipped_to_the_window()
    {
        var timeline = new RecordingTimeline(DayStart, DayEnd,
        [
            new RecordingSegment { Start = DayStart.AddHours(-3), End = DayStart.AddHours(1) },
            new RecordingSegment { Start = DayEnd.AddHours(-1), End = DayEnd.AddHours(5) },
        ]);

        Assert.Equal(DayStart, timeline.Segments[0].Start);
        Assert.Equal(DayEnd, timeline.Segments[^1].End);
    }

    [Fact]
    public void Segments_entirely_outside_the_window_are_dropped()
    {
        var timeline = new RecordingTimeline(DayStart, DayEnd,
        [
            new RecordingSegment { Start = DayStart.AddDays(-2), End = DayStart.AddDays(-1) },
        ]);

        Assert.True(timeline.IsEmpty);
    }

    [Fact]
    public void Position_maps_time_onto_the_bar()
    {
        var timeline = Timeline(Segment(0, 24));

        Assert.Equal(0.5, timeline.PositionOf(DayStart.AddHours(12)), 6);
        Assert.Equal(0, timeline.PositionOf(DayStart.AddDays(-1)));
        Assert.Equal(1, timeline.PositionOf(DayEnd.AddDays(1)));
    }

    [Fact]
    public void Clicking_the_bar_maps_back_to_a_moment()
    {
        var timeline = Timeline(Segment(0, 24));

        Assert.Equal(DayStart.AddHours(6), timeline.MomentAt(0.25));
    }

    [Fact]
    public void Footage_lookup_respects_segment_bounds()
    {
        var timeline = Timeline(Segment(1, 2));

        Assert.True(timeline.HasFootageAt(DayStart.AddHours(1)));
        Assert.True(timeline.HasFootageAt(DayStart.AddMinutes(90)));
        Assert.False(timeline.HasFootageAt(DayStart.AddHours(2)));
        Assert.False(timeline.HasFootageAt(DayStart.AddHours(5)));
    }

    [Fact]
    public void Clicking_a_gap_snaps_to_the_closest_recording()
    {
        var timeline = Timeline(Segment(1, 2), Segment(10, 11, RecordingTrigger.Motion));

        // 03:00 is nearer the end of the first segment than the start of the second.
        var snapped = timeline.SnapToFootage(DayStart.AddHours(3));
        Assert.Equal(DayStart.AddHours(2).AddSeconds(-1), snapped);

        // 09:00 is nearer the start of the second.
        Assert.Equal(DayStart.AddHours(10), timeline.SnapToFootage(DayStart.AddHours(9)));
    }

    [Fact]
    public void Snapping_inside_footage_stays_put()
    {
        var timeline = Timeline(Segment(1, 5));
        var moment = DayStart.AddHours(3);

        Assert.Equal(moment, timeline.SnapToFootage(moment));
    }

    [Fact]
    public void Snapping_with_no_footage_at_all_returns_nothing() =>
        Assert.Null(Timeline().SnapToFootage(DayStart.AddHours(3)));

    [Fact]
    public void Gaps_are_reported_including_the_ends_of_the_day()
    {
        var gaps = Timeline(Segment(1, 2), Segment(10, 11)).Gaps();

        Assert.Equal(3, gaps.Count);
        Assert.Equal((DayStart, DayStart.AddHours(1)), gaps[0]);
        Assert.Equal((DayStart.AddHours(2), DayStart.AddHours(10)), gaps[1]);
        Assert.Equal((DayStart.AddHours(11), DayEnd), gaps[2]);
    }

    [Fact]
    public void A_fully_recorded_day_has_no_gaps() =>
        Assert.Empty(Timeline(Segment(0, 24)).Gaps());

    [Fact]
    public void An_empty_day_is_one_long_gap() =>
        Assert.Equal((DayStart, DayEnd), Assert.Single(Timeline().Gaps()));

    [Fact]
    public void Recorded_duration_excludes_gaps() =>
        Assert.Equal(TimeSpan.FromHours(2), Timeline(Segment(1, 2), Segment(10, 11)).RecordedDuration);

    [Fact]
    public void An_inverted_window_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new RecordingTimeline(DayEnd, DayStart, []));

    [Fact]
    public void Segment_lookup_finds_the_covering_span()
    {
        var timeline = Timeline(Segment(1, 2, RecordingTrigger.Motion));

        Assert.Equal(RecordingTrigger.Motion, timeline.SegmentAt(DayStart.AddMinutes(70))!.Trigger);
        Assert.Null(timeline.SegmentAt(DayStart.AddHours(8)));
    }
}


public class RecordingBrowserTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private static string Page(string status, params (int Start, int End)[] hours)
    {
        var items = string.Concat(hours.Select(h => $"""
            <searchMatchItem>
              <timeSpan>
                <startTime>2026-09-10T{h.Start:00}:00:00Z</startTime>
                <endTime>2026-09-10T{h.End:00}:00:00Z</endTime>
              </timeSpan>
              <mediaSegmentDescriptor><recordType>timing</recordType></mediaSegmentDescriptor>
            </searchMatchItem>
            """));

        return $"<CMSearchResult><responseStatusStrg>{status}</responseStatusStrg>" +
               $"<matchList>{items}</matchList></CMSearchResult>";
    }

    [Fact]
    public async Task A_single_page_search_returns_its_segments()
    {
        using var nvr = new FakeNvr();
        nvr.Serve("/ISAPI/ContentMgmt/search", Page("OK", (1, 2), (3, 4)));

        using var client = new IsapiClient(nvr.Host, nvr.Port, "admin", "pw");
        var timeline = await new RecordingBrowser(client)
            .BrowseAsync(1, DayStart, DayStart.AddDays(1));

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Equal(TimeSpan.FromHours(2), timeline.RecordedDuration);
    }

    [Fact]
    public async Task An_empty_page_ends_the_search_even_when_the_device_says_MORE()
    {
        // Firmware that always reports MORE would otherwise page until the safety limit.
        using var nvr = new FakeNvr();
        nvr.Serve("/ISAPI/ContentMgmt/search", Page("MORE"));

        using var client = new IsapiClient(nvr.Host, nvr.Port, "admin", "pw");
        var timeline = await new RecordingBrowser(client)
            .BrowseAsync(1, DayStart, DayStart.AddDays(1));

        Assert.True(timeline.IsEmpty);
        Assert.Single(nvr.Requests);
    }

    [Fact]
    public async Task The_window_is_carried_onto_the_timeline()
    {
        using var nvr = new FakeNvr();
        nvr.Serve("/ISAPI/ContentMgmt/search", Page("OK", (1, 2)));

        using var client = new IsapiClient(nvr.Host, nvr.Port, "admin", "pw");
        var timeline = await new RecordingBrowser(client)
            .BrowseAsync(1, DayStart, DayStart.AddDays(1));

        Assert.Equal(DayStart, timeline.WindowStart);
        Assert.Equal(DayStart.AddDays(1), timeline.WindowEnd);
    }
}


public class TimelineTickTests
{
    [Fact]
    public void A_full_day_ticks_in_hours() =>
        Assert.Equal(TimeSpan.FromHours(2), TimelineTicks.ChooseStep(TimeSpan.FromHours(24), 12));

    [Fact]
    public void A_narrow_window_ticks_in_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(1), TimelineTicks.ChooseStep(TimeSpan.FromMinutes(10), 12));

    [Fact]
    public void Fewer_requested_ticks_gives_a_coarser_step()
    {
        var dense = TimelineTicks.ChooseStep(TimeSpan.FromHours(24), 24);
        var sparse = TimelineTicks.ChooseStep(TimeSpan.FromHours(24), 4);

        Assert.True(sparse > dense);
    }

    [Fact]
    public void A_window_longer_than_the_candidates_falls_back_to_a_day() =>
        Assert.Equal(TimeSpan.FromHours(24), TimelineTicks.ChooseStep(TimeSpan.FromDays(30), 4));

    [Fact]
    public void A_degenerate_window_does_not_divide_by_zero() =>
        Assert.Equal(TimeSpan.FromHours(1), TimelineTicks.ChooseStep(TimeSpan.Zero, 10));
}
