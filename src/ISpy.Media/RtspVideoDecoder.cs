using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ISpy.Media;

/// <summary>Raised when a stream cannot be opened or decoded.</summary>
public sealed class VideoStreamException(string message) : Exception(message);

/// <summary>
/// Opens one RTSP stream and pumps decoded frames at an <see cref="IVideoTarget"/> until cancelled.
/// One instance is one connection attempt; the retry loop lives in <see cref="CameraStream"/>.
/// </summary>
public sealed unsafe class RtspVideoDecoder(
    VideoStreamOptions options,
    IVideoTarget target,
    AVBufferRef* sharedHardwareDevice = null) : IDisposable
{
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVBufferRef* _hardwareDevice;
    private AVFrame* _frame;
    private AVFrame* _softwareFrame;
    private AVPacket* _packet;
    private int _videoStreamIndex = -1;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    // Both delegates are held in fields for the lifetime of the decoder. FFmpeg keeps the raw
    // function pointers; if these were locals the GC would collect them and the next callback
    // would jump into freed memory.
    private AVCodecContext_get_format? _getFormat;
    private AVIOInterruptCB_callback? _interrupt;

    private CancellationToken _cancellation;

    /// <summary>True when frames are being decoded on the GPU rather than the CPU.</summary>
    public bool IsHardwareAccelerated => _hardwarePixelFormat != AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>
    /// Connects and decodes until the token is cancelled or the stream ends. Blocking: callers run
    /// it on a dedicated thread.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;

        if (!FFmpegRuntime.EnsureInitialised())
            throw new VideoStreamException(FFmpegRuntime.Failure ?? "FFmpeg is unavailable.");

        Open();
        Pump();
    }

    private void Open()
    {
        _format = ffmpeg.avformat_alloc_context();
        if (_format is null) throw new VideoStreamException("Out of memory allocating a demuxer.");

        // Without this, a camera that stops answering mid-read blocks the thread indefinitely and
        // the tile can never be closed or reconnected.
        _interrupt = _ => _cancellation.IsCancellationRequested ? 1 : 0;
        _format->interrupt_callback.callback = new AVIOInterruptCB_callback_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_interrupt),
        };

        AVDictionary* dictionary = null;
        BuildOpenOptions(&dictionary);

        AVFormatContext* format = _format;
        var opened = ffmpeg.avformat_open_input(&format, options.Url, null, &dictionary);
        ffmpeg.av_dict_free(&dictionary);

        if (opened < 0)
            throw new VideoStreamException(
                $"Could not open {options.SafeUrl}: {FFmpegRuntime.DescribeError(opened)}");

        _format = format;

        var probed = ffmpeg.avformat_find_stream_info(_format, null);
        if (probed < 0)
            throw new VideoStreamException(
                $"No usable stream: {FFmpegRuntime.DescribeError(probed)}");

        AVCodec* decoder = null;
        _videoStreamIndex = ffmpeg.av_find_best_stream(
            _format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);

        if (_videoStreamIndex < 0 || decoder is null)
            throw new VideoStreamException("The stream contains no video track.");

        OpenDecoder(decoder);

        _frame = ffmpeg.av_frame_alloc();
        _softwareFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    private void BuildOpenOptions(AVDictionary** dictionary)
    {
        var micros = ((long)options.SocketTimeout.TotalMilliseconds * 1000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        ffmpeg.av_dict_set(dictionary, "rtsp_transport", options.UseTcp ? "tcp" : "udp", 0);

        // FFmpeg renamed this option; setting both means one of them lands whichever build ships.
        ffmpeg.av_dict_set(dictionary, "timeout", micros, 0);
        ffmpeg.av_dict_set(dictionary, "stimeout", micros, 0);

        // Latency settings. Cameras send a keyframe often enough that a short probe is safe, and
        // every millisecond saved here is visible as a faster first frame.
        ffmpeg.av_dict_set(dictionary, "fflags", "nobuffer", 0);
        ffmpeg.av_dict_set(dictionary, "flags", "low_delay", 0);
        ffmpeg.av_dict_set(dictionary, "probesize", "500000", 0);
        ffmpeg.av_dict_set(dictionary, "analyzeduration", "500000", 0);
        ffmpeg.av_dict_set(dictionary, "reorder_queue_size", "0", 0);
        ffmpeg.av_dict_set(dictionary, "max_delay", "500000", 0);
    }

    private void OpenDecoder(AVCodec* decoder)
    {
        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codec is null) throw new VideoStreamException("Out of memory allocating a decoder.");

        var stream = _format->streams[_videoStreamIndex];

        var copied = ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar);
        if (copied < 0)
            throw new VideoStreamException(
                $"Could not configure the decoder: {FFmpegRuntime.DescribeError(copied)}");

        _codec->pkt_timebase = stream->time_base;
        _codec->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        if (options.PreferHardwareDecode) TryEnableHardwareDecode(decoder);

        // Software decoding of several H.265 streams is the one case that needs real CPU; give it
        // threads. Ignored when hardware decoding is active.
        if (!IsHardwareAccelerated) _codec->thread_count = 0;

        var opened = ffmpeg.avcodec_open2(_codec, decoder, null);
        if (opened < 0)
            throw new VideoStreamException(
                $"Could not start the decoder: {FFmpegRuntime.DescribeError(opened)}");
    }

    /// <summary>
    /// Sets up D3D11VA. Any failure here is non-fatal: we fall back to software decoding, because a
    /// working picture at higher CPU beats a black tile.
    /// </summary>
    private void TryEnableHardwareDecode(AVCodec* decoder)
    {
        var pixelFormat = FindHardwarePixelFormat(decoder);
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE) return;

        // The shared device is the normal path; falling back to one of our own keeps a single
        // stream working in isolation (a test harness, or a machine where sharing failed).
        AVBufferRef* device = sharedHardwareDevice;

        if (device is null)
        {
            AVBufferRef* created = null;
            if (ffmpeg.av_hwdevice_ctx_create(
                    &created, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) < 0)
            {
                return;
            }

            device = _hardwareDevice = created;
        }

        if (device is null) return;

        _hardwarePixelFormat = pixelFormat;
        _codec->hw_device_ctx = ffmpeg.av_buffer_ref(device);

        // FFmpeg offers the formats it can produce and we pick the hardware one. Without this
        // callback the default picks a software format and the GPU path is silently never used.
        _getFormat = SelectPixelFormat;
        _codec->get_format = new AVCodecContext_get_format_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_getFormat),
        };
    }

    /// <summary>Finds the codec's D3D11VA configuration, if it has one.</summary>
    private static AVPixelFormat FindHardwarePixelFormat(AVCodec* decoder)
    {
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(decoder, i);
            if (config is null) return AVPixelFormat.AV_PIX_FMT_NONE;

            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
            {
                return config->pix_fmt;
            }
        }
    }

    private AVPixelFormat SelectPixelFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == _hardwarePixelFormat) return *format;
        }

        // The decoder cannot give us a GPU surface after all; take the first software format and
        // carry on rather than failing the stream.
        _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        return formats[0];
    }

    private void Pump()
    {
        var stream = _format->streams[_videoStreamIndex];
        var timeBase = ffmpeg.av_q2d(stream->time_base);
        var announced = false;

        while (!_cancellation.IsCancellationRequested)
        {
            var read = ffmpeg.av_read_frame(_format, _packet);

            if (read < 0)
            {
                ffmpeg.av_packet_unref(_packet);

                if (read == ffmpeg.AVERROR_EOF) return;
                if (_cancellation.IsCancellationRequested) return;

                throw new VideoStreamException(
                    $"The stream stopped: {FFmpegRuntime.DescribeError(read)}");
            }

            try
            {
                if (_packet->stream_index != _videoStreamIndex) continue;

                var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
                if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;

                while (true)
                {
                    var received = ffmpeg.avcodec_receive_frame(_codec, _frame);
                    if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) break;
                    if (received < 0) break;

                    if (!announced)
                    {
                        target.OnStateChanged(StreamState.Playing, null);
                        announced = true;
                    }

                    Deliver(timeBase);
                    ffmpeg.av_frame_unref(_frame);
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    private void Deliver(double timeBase)
    {
        var pts = _frame->best_effort_timestamp;
        var timestamp = pts == ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(pts * timeBase);

        var format = (AVPixelFormat)_frame->format;
        var isHardware = format == _hardwarePixelFormat &&
                         _hardwarePixelFormat != AVPixelFormat.AV_PIX_FMT_NONE;

        var info = new VideoFrameInfo(_frame->width, _frame->height, isHardware, format, timestamp);

        target.OnFrame(_frame, info);
    }

    public void Dispose()
    {
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_softwareFrame is not null)
        {
            var frame = _softwareFrame;
            ffmpeg.av_frame_free(&frame);
            _softwareFrame = null;
        }

        if (_codec is not null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_hardwareDevice is not null)
        {
            var device = _hardwareDevice;
            ffmpeg.av_buffer_unref(&device);
            _hardwareDevice = null;
        }

        if (_format is not null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        // Only now is it safe to let the callbacks go.
        _getFormat = null;
        _interrupt = null;
    }
}
