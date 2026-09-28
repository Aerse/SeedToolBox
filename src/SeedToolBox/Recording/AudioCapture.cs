using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SeedToolBox.Core.Services;

namespace SeedToolBox.Recording;

/// <summary>
/// Captures the default speakers (loopback) or microphone through WASAPI shared mode and delivers
/// float stereo at <see cref="Mp4Writer.AudioRate"/>, whatever the device mix format is.
/// </summary>
sealed class AudioCapture : IDisposable
{
    readonly IAudioClient _client;
    readonly IAudioCaptureClient _capture;
    readonly int _rate, _channels, _bits, _blockAlign;
    readonly bool _float;
    // How much each device channel contributes to the left and right output
    readonly float[] _gainL, _gainR;
    readonly Thread _thread;
    volatile bool _stop;

    // Linear resampler state: position between the previous and current input frame
    double _phase;
    float _lastL, _lastR;
    byte[] _bytes = new byte[0];

    /// <summary>
    /// Raised on the capture thread with interleaved stereo floats (length = frames × 2) and the
    /// performance-counter time of the first frame, in 100 ns units.
    /// </summary>
    public event Action<float[], int, long>? Data;

    public AudioCapture(bool loopback)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice device;
        try
        {
            enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        var iid = typeof(IAudioClient).GUID;
        object client;
        try { device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out client); }
        finally { Marshal.ReleaseComObject(device); }
        _client = (IAudioClient)client;

        // A half-built capture is never disposed, so let go of the client here if the device refuses
        try
        {
            _client.GetMixFormat(out var format);
            try
            {
                int tag = Marshal.ReadInt16(format);
                _channels = Marshal.ReadInt16(format, 2);
                _rate = Marshal.ReadInt32(format, 4);
                _blockAlign = Marshal.ReadInt16(format, 12);
                _bits = Marshal.ReadInt16(format, 14);
                _float = tag == 3;
                int mask = 0;
                if (tag == unchecked((short)0xFFFE))
                {
                    mask = Marshal.ReadInt32(format, 20);
                    var sub = (Guid)Marshal.PtrToStructure(format + 24, typeof(Guid));
                    _float = sub == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                (_gainL, _gainR) = DownmixGains(_channels, mask);
                // 200 ms buffer, polled every 10 ms
                _client.Initialize(0, loopback ? 0x00020000 : 0, 2_000_000, 0, format, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }

            var captureIid = typeof(IAudioCaptureClient).GUID;
            _client.GetService(ref captureIid, out var capture);
            _capture = (IAudioCaptureClient)capture;
        }
        catch
        {
            Marshal.ReleaseComObject(_client);
            throw;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = loopback ? "Loopback capture" : "Microphone capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    void Run()
    {
        var output = new List<float>(4096);
        long firstTime = 0;
        try
        {
            while (!_stop)
            {
                Thread.Sleep(10);
                while (!_stop)
                {
                    _capture.GetNextPacketSize(out int packet);
                    if (packet == 0) break;
                    _capture.GetBuffer(out var data, out int frames, out int flags, out _, out long qpc);
                    // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR: no usable time, assume the packet is fresh
                    if (output.Count == 0) firstTime = (flags & 0x4) != 0 || qpc <= 0 ? Now() : qpc;
                    // AUDCLNT_BUFFERFLAGS_SILENT: the data is to be treated as zeros
                    Convert(data, frames, (flags & 0x2) != 0, output);
                    _capture.ReleaseBuffer(frames);
                }
                if (output.Count > 0)
                {
                    Data?.Invoke(output.ToArray(), output.Count / 2, firstTime);
                    output.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            // The device can disappear mid-recording (unplugged headset); the mixer pads with silence
            Log.Error("Audio capture stopped", ex);
        }
    }

    void Convert(IntPtr data, int frames, bool silent, List<float> output)
    {
        double step = (double)_rate / Mp4Writer.AudioRate;
        int length = frames * _blockAlign;
        if (!silent)
        {
            if (_bytes.Length < length) _bytes = new byte[length];
            Marshal.Copy(data, _bytes, 0, length);
        }
        for (int i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            if (!silent)
            {
                for (int c = 0; c < _channels; c++)
                {
                    if (_gainL[c] == 0 && _gainR[c] == 0) continue;
                    float v = Read(i, c);
                    l += v * _gainL[c];
                    r += v * _gainR[c];
                }
                l = Math.Max(-1f, Math.Min(1f, l));
                r = Math.Max(-1f, Math.Min(1f, r));
            }
            // Emit every output sample that falls between the previous input frame and this one
            while (_phase < 1)
            {
                output.Add(_lastL + (float)((l - _lastL) * _phase));
                output.Add(_lastR + (float)((r - _lastR) * _phase));
                _phase += step;
            }
            _phase -= 1;
            _lastL = l;
            _lastR = r;
        }
    }

    /// <summary>Performance-counter time in 100 ns units, the clock WASAPI timestamps use.</summary>
    public static long Now() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Standard stereo downmix: centre and surrounds at -3 dB, LFE dropped.</summary>
    static (float[] L, float[] R) DownmixGains(int channels, int mask)
    {
        var l = new float[channels];
        var r = new float[channels];
        if (channels == 1)
        {
            l[0] = r[0] = 1;
            return (l, r);
        }
        // No mask: assume the usual layouts, anything else is just its first two channels
        if (mask == 0) mask = channels switch { 2 => 0x3, 6 => 0x3F, 8 => 0x63F, _ => 0 };
        if (mask == 0 || channels == 2)
        {
            l[0] = 1;
            r[1] = 1;
            return (l, r);
        }

        const float Half = 0.7071f;
        int c = 0;
        // Channels appear in the order of their mask bits
        for (int bit = 0; bit < 32 && c < channels; bit++)
        {
            int speaker = 1 << bit;
            if ((mask & speaker) == 0) continue;
            switch (speaker)
            {
                case 0x1: case 0x40: l[c] = 1; break;                   // front left, front left of centre
                case 0x2: case 0x80: r[c] = 1; break;                   // front right, front right of centre
                case 0x4: l[c] = r[c] = Half; break;                    // front centre
                case 0x10: case 0x200: l[c] = Half; break;              // back left, side left
                case 0x20: case 0x400: r[c] = Half; break;              // back right, side right
                case 0x100: l[c] = r[c] = 0.5f; break;                  // back centre
            }
            c++;
        }
        return (l, r);
    }

    float Read(int frame, int channel)
    {
        var b = _bytes;
        int offset = frame * _blockAlign + channel * (_bits / 8);
        if (_float) return BitConverter.ToSingle(b, offset);
        return _bits switch
        {
            16 => BitConverter.ToInt16(b, offset) / 32768f,
            24 => ((b[offset] << 8) | (b[offset + 1] << 16) | (b[offset + 2] << 24)) / 2147483648f,
            32 => BitConverter.ToInt32(b, offset) / 2147483648f,
            _ => 0,
        };
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(500);
        try { _client.Stop(); } catch (Exception) { }
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void _0();
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int frames);
        void _4();
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int frames);
        void GetNextPacketSize(out int frames);
    }
}
