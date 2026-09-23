using System;
using System.Runtime.InteropServices;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Recording;

/// <summary>
/// Encodes top-down BGRA frames to H.264 and optional 16-bit stereo PCM to AAC in an MP4 file.
/// Not thread-safe: call everything from one (MTA) thread. Times are in 100 ns units.
/// </summary>
sealed class Mp4Writer : IDisposable
{
    public const int AudioRate = 48000;
    public const int AudioChannels = 2;

    readonly IMFSinkWriter _writer;
    readonly int _video;
    readonly int _audio = -1;
    readonly int _width, _height;
    bool _finished;

    public bool HasAudio => _audio >= 0;

    Mp4Writer(string path, int width, int height, int fps, int bitrate, bool audio, bool hardware)
    {
        _width = width;
        _height = height;
        MF.Startup();

        var attributes = MF.CreateAttributes();
        if (hardware) attributes.SetUINT32(ref MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer);
        MF.Release(attributes);

        try
        {
            var output = VideoType(MF.VideoFormatH264, width, height, fps);
            output.SetUINT32(ref MF.MT_AVG_BITRATE, bitrate);
            output.SetUINT32(ref MF.MT_MPEG2_PROFILE, 77); // eAVEncH264VProfile_Main
            _writer.AddStream(output, out _video);
            MF.Release(output);

            var input = VideoType(MF.VideoFormatRGB32, width, height, fps);
            input.SetUINT32(ref MF.MT_DEFAULT_STRIDE, width * 4);
            _writer.SetInputMediaType(_video, input, null);
            MF.Release(input);

            if (audio)
            {
                var aac = AudioType(MF.AudioFormatAAC);
                aac.SetUINT32(ref MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 128000 / 8);
                _writer.AddStream(aac, out _audio);
                MF.Release(aac);

                var pcm = AudioType(MF.AudioFormatPCM);
                pcm.SetUINT32(ref MF.MT_AUDIO_BLOCK_ALIGNMENT, AudioChannels * 2);
                pcm.SetUINT32(ref MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * AudioChannels * 2);
                _writer.SetInputMediaType(_audio, pcm, null);
                MF.Release(pcm);
            }

            _writer.BeginWriting();
        }
        catch
        {
            MF.Release(_writer);
            throw;
        }
    }

    /// <summary>Opens the writer, preferring a hardware encoder and falling back to the software one.</summary>
    public static Mp4Writer Create(string path, int width, int height, int fps, int bitrate, bool audio)
    {
        try
        {
            return new Mp4Writer(path, width, height, fps, bitrate, audio, true);
        }
        catch (Exception ex)
        {
            Log.Error("Hardware encoder unavailable, using software", ex);
            try { System.IO.File.Delete(path); } catch (Exception) { }
            return new Mp4Writer(path, width, height, fps, bitrate, audio, false);
        }
    }

    /// <summary>Bits per second for a quality preset (0 = low, 1 = medium, 2 = high).</summary>
    public static int Bitrate(int width, int height, int fps, int quality)
    {
        double bpp = quality <= 0 ? 0.05 : quality == 1 ? 0.1 : 0.2;
        return (int)Math.Max(500_000, Math.Min(50_000_000, width * (double)height * fps * bpp));
    }

    static IMFMediaType VideoType(Guid subtype, int width, int height, int fps)
    {
        var type = MF.CreateMediaType();
        type.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaTypeVideo);
        type.SetGUID(ref MF.MT_SUBTYPE, ref subtype);
        type.SetUINT32(ref MF.MT_INTERLACE_MODE, 2); // progressive
        type.SetUINT64(ref MF.MT_FRAME_SIZE, MF.Pack(width, height));
        type.SetUINT64(ref MF.MT_FRAME_RATE, MF.Pack(fps, 1));
        type.SetUINT64(ref MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
        return type;
    }

    static IMFMediaType AudioType(Guid subtype)
    {
        var type = MF.CreateMediaType();
        type.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaTypeAudio);
        type.SetGUID(ref MF.MT_SUBTYPE, ref subtype);
        type.SetUINT32(ref MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
        type.SetUINT32(ref MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
        type.SetUINT32(ref MF.MT_AUDIO_NUM_CHANNELS, AudioChannels);
        return type;
    }

    /// <summary>Writes one frame of top-down 32-bit pixels; <paramref name="stride"/> may be larger than width × 4.</summary>
    public void WriteFrame(IntPtr pixels, int stride, long time, long duration)
    {
        int row = _width * 4, length = row * _height;
        MF.MFCreateMemoryBuffer(length, out var buffer);
        try
        {
            buffer.Lock(out var dest, out _, out _);
            try
            {
                if (stride == row)
                {
                    MF.CopyMemory(dest, pixels, (IntPtr)length);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                        MF.CopyMemory(dest + y * row, pixels + y * stride, (IntPtr)row);
                }
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(length);
            Write(_video, buffer, time, duration);
        }
        finally
        {
            MF.Release(buffer);
        }
    }

    /// <summary>Writes interleaved 16-bit stereo samples; <paramref name="frames"/> counts sample pairs.</summary>
    public void WriteAudio(short[] samples, int frames, long time)
    {
        if (_audio < 0 || frames <= 0) return;
        int length = frames * AudioChannels * 2;
        MF.MFCreateMemoryBuffer(length, out var buffer);
        try
        {
            buffer.Lock(out var dest, out _, out _);
            Marshal.Copy(samples, 0, dest, frames * AudioChannels);
            buffer.Unlock();
            buffer.SetCurrentLength(length);
            Write(_audio, buffer, time, frames * 10_000_000L / AudioRate);
        }
        finally
        {
            MF.Release(buffer);
        }
    }

    void Write(int stream, IMFMediaBuffer buffer, long time, long duration)
    {
        MF.MFCreateSample(out var sample);
        try
        {
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            _writer.WriteSample(stream, sample);
        }
        finally
        {
            MF.Release(sample);
        }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.DoFinalize();
    }

    public void Dispose()
    {
        try { Finish(); }
        catch (Exception ex) { Log.Error("Failed to finalize MP4", ex); }
        finally { MF.Release(_writer); }
    }
}
