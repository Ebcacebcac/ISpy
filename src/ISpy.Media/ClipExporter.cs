using FFmpeg.AutoGen;
using ISpy.Core.Protocol;

namespace ISpy.Media;

/// <summary>Progress while a clip is being written.</summary>
public readonly record struct ExportProgress(TimeSpan Written, TimeSpan Total)
{
    public double Fraction => Total > TimeSpan.Zero
        ? Math.Clamp(Written / Total, 0, 1)
        : 0;
}

/// <summary>
/// Saves a span of recorded footage to an MP4 on the PC.
/// </summary>
/// <remarks>
/// The packets are remuxed, never re-encoded: the recorder's own H.264/H.265 bitstream is copied
/// into an MP4 container untouched. That makes an export as fast as the recorder can send and keeps
/// the footage bit-for-bit identical to what is on the drive - which is what matters if a clip is
/// ever going to be handed to somebody else.
/// </remarks>
public sealed unsafe class ClipExporter
{
    public Task ExportAsync(
        string playbackUrl,
        string destinationPath,
        TimeSpan expectedDuration,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(playbackUrl, destinationPath, expectedDuration, progress, cancellationToken),
            cancellationToken);

    private void Export(
        string playbackUrl,
        string destinationPath,
        TimeSpan expectedDuration,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!FFmpegRuntime.EnsureInitialised())
            throw new VideoStreamException(FFmpegRuntime.Failure ?? "FFmpeg is unavailable.");

        AVFormatContext* input = null;
        AVFormatContext* output = null;
        AVPacket* packet = null;

        try
        {
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&options, "timeout", "12000000", 0);
            ffmpeg.av_dict_set(&options, "stimeout", "12000000", 0);

            var opened = ffmpeg.avformat_open_input(&input, playbackUrl, null, &options);
            ffmpeg.av_dict_free(&options);

            if (opened < 0)
                throw new VideoStreamException(
                    $"Could not open the recording ({HikvisionUrls.Redact(playbackUrl)}): " +
                    FFmpegRuntime.DescribeError(opened));

            if (ffmpeg.avformat_find_stream_info(input, null) < 0)
                throw new VideoStreamException("The recording had no readable streams.");

            var created = ffmpeg.avformat_alloc_output_context2(&output, null, null, destinationPath);
            if (created < 0 || output is null)
                throw new VideoStreamException(
                    $"Could not create {destinationPath}: {FFmpegRuntime.DescribeError(created)}");

            // Map input streams onto output streams, keeping only what MP4 can carry.
            var streamMap = new int[input->nb_streams];
            var nextIndex = 0;

            for (var i = 0; i < input->nb_streams; i++)
            {
                var source = input->streams[i];
                var type = source->codecpar->codec_type;

                if (type != AVMediaType.AVMEDIA_TYPE_VIDEO && type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    streamMap[i] = -1;
                    continue;
                }

                var destination = ffmpeg.avformat_new_stream(output, null);
                if (destination is null) throw new VideoStreamException("Could not add an output stream.");

                if (ffmpeg.avcodec_parameters_copy(destination->codecpar, source->codecpar) < 0)
                    throw new VideoStreamException("Could not copy the stream settings.");

                // Let the muxer choose the tag for the container; the input's tag may be invalid here.
                destination->codecpar->codec_tag = 0;
                streamMap[i] = nextIndex++;
            }

            if (nextIndex == 0) throw new VideoStreamException("The recording had no video to save.");

            if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                var ioOpened = ffmpeg.avio_open(&output->pb, destinationPath, ffmpeg.AVIO_FLAG_WRITE);
                if (ioOpened < 0)
                    throw new VideoStreamException(
                        $"Could not write to {destinationPath}: {FFmpegRuntime.DescribeError(ioOpened)}");
            }

            if (ffmpeg.avformat_write_header(output, null) < 0)
                throw new VideoStreamException("Could not start the MP4 file.");

            packet = ffmpeg.av_packet_alloc();
            var firstPts = new long[input->nb_streams];
            Array.Fill(firstPts, ffmpeg.AV_NOPTS_VALUE);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (ffmpeg.av_read_frame(input, packet) < 0) break;

                var sourceIndex = packet->stream_index;
                var targetIndex = sourceIndex < streamMap.Length ? streamMap[sourceIndex] : -1;

                if (targetIndex < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var source = input->streams[sourceIndex];
                var destination = output->streams[targetIndex];

                // Playback streams start at whatever timestamp the recording had; rebase to zero so
                // the exported file starts at 00:00 rather than several hours in.
                if (firstPts[sourceIndex] == ffmpeg.AV_NOPTS_VALUE &&
                    packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    firstPts[sourceIndex] = packet->pts;
                }

                if (firstPts[sourceIndex] != ffmpeg.AV_NOPTS_VALUE)
                {
                    if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts -= firstPts[sourceIndex];
                    if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts -= firstPts[sourceIndex];
                }

                packet->stream_index = targetIndex;
                ffmpeg.av_packet_rescale_ts(packet, source->time_base, destination->time_base);
                packet->pos = -1;

                var written = ffmpeg.av_interleaved_write_frame(output, packet);
                ffmpeg.av_packet_unref(packet);

                if (written < 0)
                    throw new VideoStreamException(
                        $"Writing failed: {FFmpegRuntime.DescribeError(written)}");

                if (progress is not null && packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var seconds = packet->pts * ffmpeg.av_q2d(destination->time_base);
                    progress.Report(new ExportProgress(TimeSpan.FromSeconds(seconds), expectedDuration));
                }
            }

            ffmpeg.av_write_trailer(output);
        }
        finally
        {
            if (packet is not null) ffmpeg.av_packet_free(&packet);

            if (output is not null)
            {
                if (output->pb is not null && (output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&output->pb);

                ffmpeg.avformat_free_context(output);
            }

            if (input is not null) ffmpeg.avformat_close_input(&input);
        }

        // A cancelled export leaves a partial file; remove it rather than leaving a broken clip.
        if (cancellationToken.IsCancellationRequested && File.Exists(destinationPath))
        {
            try { File.Delete(destinationPath); } catch (IOException) { }
        }
    }
}
