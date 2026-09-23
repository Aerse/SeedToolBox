using System;
using System.Runtime.InteropServices;

namespace SeedToolBox.Recording;

/// <summary>Decodes an MP4 to top-down 32-bit frames and 16-bit stereo PCM. Times are in 100 ns units.</summary>
sealed class VideoReader : IDisposable
{
    readonly IMFSourceReader _reader;
    readonly int _width, _height;
    int _decodedHeight, _stride;
    short[] _audio = new short[0];
    int _audioStream = -1;
    bool _videoEnded, _audioEnded;

    public bool HasAudio { get; }
    public long Duration { get; }

    /// <summary>Called with a frame's time and its top-down pixels (<paramref name="stride"/> bytes per row).</summary>
    public delegate void FrameHandler(long time, IntPtr pixels, int stride);

    /// <summary>Called with a chunk of interleaved samples; <paramref name="frames"/> counts sample pairs.</summary>
    public delegate void AudioHandler(long time, short[] samples, int frames);

    /// <param name="width">The recorded size; decoders may pad frames to a multiple of 16.</param>
    public VideoReader(string path, int width, int height, bool audio)
    {
        _width = width;
        _height = height;
        MF.Startup();

        var attributes = MF.CreateAttributes();
        attributes.SetUINT32(ref MF.SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
        MF.MFCreateSourceReaderFromURL(path, attributes, out _reader);
        MF.Release(attributes);

        _reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
        _reader.SetStreamSelection(MF.SourceReaderFirstVideoStream, true);
        var video = MF.CreateMediaType();
        video.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaTypeVideo);
        video.SetGUID(ref MF.MT_SUBTYPE, ref MF.VideoFormatRGB32);
        _reader.SetCurrentMediaType(MF.SourceReaderFirstVideoStream, IntPtr.Zero, video);
        MF.Release(video);
        UpdateVideoFormat();

        // ReadSample reports real stream indexes, so find which one is the audio
        for (int i = 0; audio && _reader.GetNativeMediaType(i, 0, out var native) >= 0; i++)
        {
            native.GetGUID(ref MF.MT_MAJOR_TYPE, out var major);
            MF.Release(native);
            if (major == MF.MediaTypeAudio)
            {
                _audioStream = i;
                break;
            }
        }
        if (_audioStream >= 0)
        {
            _reader.SetStreamSelection(MF.SourceReaderFirstAudioStream, true);
            var pcm = MF.CreateMediaType();
            pcm.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaTypeAudio);
            pcm.SetGUID(ref MF.MT_SUBTYPE, ref MF.AudioFormatPCM);
            pcm.SetUINT32(ref MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
            pcm.SetUINT32(ref MF.MT_AUDIO_SAMPLES_PER_SECOND, Mp4Writer.AudioRate);
            pcm.SetUINT32(ref MF.MT_AUDIO_NUM_CHANNELS, Mp4Writer.AudioChannels);
            _reader.SetCurrentMediaType(MF.SourceReaderFirstAudioStream, IntPtr.Zero, pcm);
            MF.Release(pcm);
            HasAudio = true;
        }

        _reader.GetPresentationAttribute(-1 /* MF_SOURCE_READER_MEDIASOURCE */, ref MF.PD_DURATION, out var duration);
        Duration = duration.Value;
    }

    void UpdateVideoFormat()
    {
        _reader.GetCurrentMediaType(MF.SourceReaderFirstVideoStream, out var type);
        try
        {
            int width = _width;
            if (type.GetUINT64(ref MF.MT_FRAME_SIZE, out long size) >= 0) MF.Unpack(size, out width, out _decodedHeight);
            else _decodedHeight = _height;
            // Negative stride means bottom-up rows
            _stride = type.GetUINT32(ref MF.MT_DEFAULT_STRIDE, out int stride) >= 0 ? stride : width * 4;
        }
        finally
        {
            MF.Release(type);
        }
    }

    public void Seek(long time)
    {
        var position = PropVariant.FromLong(time);
        var format = Guid.Empty;
        _reader.SetCurrentPosition(ref format, ref position);
    }

    /// <summary>Decodes the next sample of either stream. Returns false at the end of the file.</summary>
    public bool Read(FrameHandler onFrame, AudioHandler? onAudio)
    {
        while (true)
        {
            _reader.ReadSample(MF.SourceReaderAnyStream, 0, out int stream, out int flags, out long time, out var sample);
            try
            {
                bool isAudio = stream == _audioStream;
                if ((flags & MF.SourceReaderFlagEndOfStream) != 0)
                {
                    if (isAudio) _audioEnded = true;
                    else _videoEnded = true;
                }
                if ((flags & MF.SourceReaderFlagCurrentMediaTypeChanged) != 0 && !isAudio) UpdateVideoFormat();
                if (sample == null)
                {
                    // One stream ended (or a gap); carry on until both have
                    if (_videoEnded && (_audioEnded || !HasAudio)) return false;
                    continue;
                }

                sample.ConvertToContiguousBuffer(out var buffer);
                try
                {
                    buffer.Lock(out var data, out _, out int length);
                    try
                    {
                        if (isAudio) DeliverAudio(time, data, length, onAudio);
                        else DeliverFrame(time, data, onFrame);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                finally
                {
                    MF.Release(buffer);
                }
                return true;
            }
            finally
            {
                MF.Release(sample);
            }
        }
    }

    void DeliverFrame(long time, IntPtr data, FrameHandler onFrame)
    {
        if (_stride < 0)
        {
            // Bottom-up: start at the last row and walk backwards
            onFrame(time, data + (_decodedHeight - 1) * -_stride, _stride);
        }
        else
        {
            onFrame(time, data, _stride);
        }
    }

    void DeliverAudio(long time, IntPtr data, int length, AudioHandler? onAudio)
    {
        if (onAudio == null) return;
        int count = length / 2;
        if (_audio.Length < count) _audio = new short[count];
        Marshal.Copy(data, _audio, 0, count);
        onAudio(time, _audio, count / Mp4Writer.AudioChannels);
    }

    public void Dispose() => MF.Release(_reader);
}
