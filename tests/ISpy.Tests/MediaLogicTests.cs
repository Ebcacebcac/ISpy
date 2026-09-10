using ISpy.Core.Media;
using ISpy.Core.Model;
using Xunit;

namespace ISpy.Tests;

public class FrameQueueTests
{
    [Fact]
    public void Frames_come_back_in_order_while_there_is_room()
    {
        var queue = new FrameQueue<int>(capacity: 3);

        Assert.True(queue.Enqueue(1));
        Assert.True(queue.Enqueue(2));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(1, first);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void A_full_queue_drops_the_oldest_frame_rather_than_growing()
    {
        var released = new List<int>();
        var queue = new FrameQueue<int>(capacity: 2, released.Add);

        queue.Enqueue(1);
        queue.Enqueue(2);
        Assert.False(queue.Enqueue(3));

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedCount);
        Assert.Equal([1], released);

        queue.TryDequeue(out var next);
        Assert.Equal(2, next);
    }

    [Fact]
    public void Dropped_frames_are_released_so_gpu_memory_is_reclaimed()
    {
        var released = new List<string>();
        var queue = new FrameQueue<string>(capacity: 1, released.Add);

        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.Enqueue("c");

        Assert.Equal(["a", "b"], released);
        Assert.Equal(2, queue.DroppedCount);
    }

    [Fact]
    public void Renderer_takes_the_newest_frame_and_discards_the_backlog()
    {
        var released = new List<int>();
        var queue = new FrameQueue<int>(capacity: 4, released.Add);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Assert.True(queue.TryDequeueLatest(out var latest));
        Assert.Equal(3, latest);
        Assert.Equal(0, queue.Count);
        Assert.Equal([1, 2], released);
    }

    [Fact]
    public void Dequeue_on_an_empty_queue_reports_nothing()
    {
        var queue = new FrameQueue<int>();

        Assert.False(queue.TryDequeue(out _));
        Assert.False(queue.TryDequeueLatest(out _));
    }

    [Fact]
    public void Clearing_releases_everything_still_held()
    {
        var released = new List<int>();
        var queue = new FrameQueue<int>(capacity: 3, released.Add);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Equal([1, 2], released);
    }

    [Fact]
    public void Capacity_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameQueue<int>(0));

    [Fact]
    public async Task Concurrent_producers_and_consumers_do_not_corrupt_the_queue()
    {
        var queue = new FrameQueue<int>(capacity: 2);
        var consumed = 0;

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++) queue.Enqueue(i);
        });

        var consumer = Task.Run(() =>
        {
            while (!producer.IsCompleted || queue.Count > 0)
            {
                if (queue.TryDequeueLatest(out _)) consumed++;
            }
        });

        await Task.WhenAll(producer, consumer);

        // Exact counts depend on scheduling; the invariant is that nothing is lost or double-counted.
        Assert.Equal(5000, consumed + queue.DroppedCount + queue.Count);
    }
}

public class ReconnectPolicyTests
{
    [Fact]
    public void Delay_grows_exponentially()
    {
        var policy = new ReconnectPolicy(
            initialDelay: TimeSpan.FromMilliseconds(500),
            maxDelay: TimeSpan.FromSeconds(15),
            jitterFraction: 0);

        Assert.Equal(500, policy.NextDelay().TotalMilliseconds);
        Assert.Equal(1000, policy.NextDelay().TotalMilliseconds);
        Assert.Equal(2000, policy.NextDelay().TotalMilliseconds);
        Assert.Equal(4000, policy.NextDelay().TotalMilliseconds);
    }

    [Fact]
    public void Delay_is_capped_so_a_camera_never_stops_retrying()
    {
        var policy = new ReconnectPolicy(
            initialDelay: TimeSpan.FromMilliseconds(500),
            maxDelay: TimeSpan.FromSeconds(15),
            jitterFraction: 0);

        for (var i = 0; i < 20; i++) policy.NextDelay();

        Assert.Equal(15_000, policy.NextDelay().TotalMilliseconds);
    }

    [Fact]
    public void Jitter_spreads_reconnects_but_stays_within_bounds()
    {
        // Sixteen tiles failing at once must not retry in lockstep.
        var low = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), jitterFraction: 0.2);
        var high = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), jitterFraction: 0.2);

        low.NextDelay(() => 0);
        high.NextDelay(() => 0);

        var lowSecond = low.NextDelay(() => 0).TotalMilliseconds;    // 1000 - 20%
        var highSecond = high.NextDelay(() => 1).TotalMilliseconds;  // 1000 + 20%

        Assert.Equal(800, lowSecond);
        Assert.Equal(1200, highSecond);
    }

    [Fact]
    public void Jitter_never_drops_below_the_initial_delay()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), jitterFraction: 0.9);
        Assert.True(policy.NextDelay(() => 0).TotalMilliseconds >= 500);
    }

    [Fact]
    public void A_healthy_stream_resets_the_backoff()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), jitterFraction: 0);

        policy.NextDelay();
        policy.NextDelay();
        Assert.Equal(2, policy.Attempt);

        policy.Reset();

        Assert.Equal(0, policy.Attempt);
        Assert.Equal(500, policy.NextDelay().TotalMilliseconds);
    }
}

public class StreamSelectionTests
{
    [Fact]
    public void Small_tiles_use_the_substream() =>
        Assert.Equal(StreamProfile.Sub, StreamSelection.ForTile(320, isMaximized: false));

    [Fact]
    public void A_maximized_tile_always_uses_the_main_stream() =>
        Assert.Equal(StreamProfile.Main, StreamSelection.ForTile(320, isMaximized: true));

    [Fact]
    public void A_large_tile_uses_the_main_stream_even_in_the_grid() =>
        Assert.Equal(StreamProfile.Main, StreamSelection.ForTile(960, isMaximized: false));

    [Fact]
    public void A_camera_with_no_substream_falls_back_to_main()
    {
        var channel = new Channel { DeviceId = "d", Number = 1, MainCodec = "H.264", SubCodec = null };

        Assert.Equal(StreamProfile.Main, StreamSelection.Resolve(channel, StreamProfile.Sub));
    }

    [Fact]
    public void A_camera_with_a_substream_keeps_the_requested_profile()
    {
        var channel = new Channel { DeviceId = "d", Number = 1, MainCodec = "H.265", SubCodec = "H.264" };

        Assert.Equal(StreamProfile.Sub, StreamSelection.Resolve(channel, StreamProfile.Sub));
    }
}

public class TileLayoutTests
{
    [Fact]
    public void A_four_up_grid_has_four_tiles_in_two_rows()
    {
        var tiles = TileLayout.Compute(GridLayout.TwoByTwo, 800, 600);

        Assert.Equal(4, tiles.Count);
        Assert.Equal(0, tiles[0].X);
        Assert.Equal(0, tiles[0].Y);
        Assert.Equal(tiles[0].Y, tiles[1].Y);
        Assert.True(tiles[2].Y > tiles[0].Y);
    }

    [Fact]
    public void Tiles_fill_the_canvas_exactly_despite_rounding()
    {
        // 1000 does not divide by 3; the remainder must not leave a visible seam on the right edge.
        var tiles = TileLayout.Compute(GridLayout.ThreeByThree, 1000, 1000);

        Assert.Equal(1000, tiles[2].Right);
        Assert.Equal(1000, tiles[^1].Right);
        Assert.Equal(1000, tiles[^1].Bottom);
    }

    [Fact]
    public void Gutters_sit_between_tiles_only()
    {
        var tiles = TileLayout.Compute(GridLayout.TwoByTwo, 802, 602);

        Assert.Equal(TileLayout.Gutter, tiles[1].X - tiles[0].Right);
        Assert.Equal(0, tiles[0].X);
    }

    [Fact]
    public void A_zero_sized_canvas_produces_no_tiles()
    {
        Assert.Empty(TileLayout.Compute(GridLayout.TwoByTwo, 0, 600));
        Assert.Empty(TileLayout.Compute(GridLayout.TwoByTwo, 800, 0));
    }

    [Theory]
    [InlineData(1, GridLayout.Single)]
    [InlineData(3, GridLayout.TwoByTwo)]
    [InlineData(4, GridLayout.TwoByTwo)]
    [InlineData(5, GridLayout.ThreeByThree)]
    [InlineData(9, GridLayout.ThreeByThree)]
    [InlineData(12, GridLayout.FourByFour)]
    public void Layout_is_chosen_to_fit_the_camera_count(int cameras, GridLayout expected) =>
        Assert.Equal(expected, TileLayout.FitFor(cameras));

    [Fact]
    public void Widescreen_video_is_letterboxed_and_centred_in_a_square_tile()
    {
        var tile = new TileRect(0, 0, 400, 400);
        var video = TileLayout.Letterbox(tile, 1920, 1080);

        Assert.Equal(400, video.Width);
        Assert.Equal(225, video.Height);
        Assert.Equal(0, video.X);
        Assert.Equal((400 - 225) / 2, video.Y);
    }

    [Fact]
    public void Aspect_ratio_is_preserved_when_the_tile_is_wider_than_the_video()
    {
        var video = TileLayout.Letterbox(new TileRect(0, 0, 800, 200), 1920, 1080);

        Assert.Equal(200, video.Height);
        Assert.Equal(356, video.Width);
    }

    [Fact]
    public void A_frame_with_no_dimensions_yet_draws_nothing()
    {
        var video = TileLayout.Letterbox(new TileRect(0, 0, 400, 400), 0, 0);

        Assert.Equal(0, video.Width);
        Assert.Equal(0, video.Height);
    }

    [Fact]
    public void Clicking_a_tile_finds_it()
    {
        var tiles = TileLayout.Compute(GridLayout.TwoByTwo, 800, 600);

        Assert.Equal(0, TileLayout.HitTest(tiles, 10, 10));
        Assert.Equal(3, TileLayout.HitTest(tiles, 790, 590));
    }

    [Fact]
    public void Clicking_a_gutter_selects_nothing()
    {
        var tiles = TileLayout.Compute(GridLayout.TwoByTwo, 802, 602);
        var gutterX = tiles[0].Right;

        Assert.Null(TileLayout.HitTest(tiles, gutterX, 10));
    }
}
