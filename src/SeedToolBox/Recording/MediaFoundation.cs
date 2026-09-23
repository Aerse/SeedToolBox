using System;
using System.Runtime.InteropServices;

namespace SeedToolBox.Recording;

/// <summary>
/// Minimal Media Foundation interop. Unused vtable slots are declared as placeholders (_N) so the
/// slot order matches the native interfaces; they must never be called.
/// </summary>
static class MF
{
    public const int Version = 0x00020070;
    public const int SourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
    public const int SourceReaderFirstAudioStream = unchecked((int)0xFFFFFFFD);
    public const int SourceReaderAllStreams = unchecked((int)0xFFFFFFFE);
    public const int SourceReaderAnyStream = unchecked((int)0xFFFFFFFE);
    public const int SourceReaderFlagEndOfStream = 0x2;
    public const int SourceReaderFlagCurrentMediaTypeChanged = 0x20;

    public static Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static Guid MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static Guid MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static Guid MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");

    public static Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static Guid VideoFormatRGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static Guid AudioFormatAAC = new("00001610-0000-0010-8000-00AA00389B71");
    public static Guid AudioFormatPCM = new("00000001-0000-0010-8000-00AA00389B71");

    public static Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static Guid SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static Guid PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    // Not readonly so they can be passed by ref to the interop signatures; never assign them
    static bool _started;
    static bool? _available;

    /// <summary>False on Windows N/KN without the Media Feature Pack.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available == null)
            {
                try { Startup(); _available = true; }
                catch (Exception) { _available = false; }
            }
            return _available.Value;
        }
    }

    public static void Startup()
    {
        if (_started) return;
        Marshal.ThrowExceptionForHR(MFStartup(Version, 0));
        _started = true;
    }

    public static IMFMediaType CreateMediaType()
    {
        MFCreateMediaType(out var type);
        return type;
    }

    public static IMFAttributes CreateAttributes(int size = 2)
    {
        MFCreateAttributes(out var attributes, size);
        return attributes;
    }

    public static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    public static void Unpack(long value, out int high, out int low)
    {
        high = (int)(value >> 32);
        low = (int)(value & 0xFFFFFFFF);
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateAttributes(out IMFAttributes attributes, int size);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateMemoryBuffer(int length, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes, out IMFSourceReader reader);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    public static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr length);
}

/// <summary>PROPVARIANT holding a VT_I8, sized for both 32- and 64-bit.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type;
    ushort _r1, _r2, _r3;
    public long Value;
    IntPtr _pad;

    public static PropVariant FromLong(long value) => new() { Type = 20, Value = value };
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
    void _0(); void _1(); void _2(); void _3();
    void GetUINT32([In] ref Guid key, out int value);
    void GetUINT64([In] ref Guid key, out long value);
    void _6();
    void GetGUID([In] ref Guid key, out Guid value);
    void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
    void SetUINT32([In] ref Guid key, int value);
    void SetUINT64([In] ref Guid key, long value);
    void _20();
    void SetGUID([In] ref Guid key, [In] ref Guid value);
    void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaType
{
    void _0(); void _1(); void _2(); void _3();
    [PreserveSig] int GetUINT32([In] ref Guid key, out int value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out long value);
    void _6();
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
    void SetUINT32([In] ref Guid key, int value);
    void SetUINT64([In] ref Guid key, long value);
    void _20();
    void SetGUID([In] ref Guid key, [In] ref Guid value);
    void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    // IMFMediaType
    void _30(); void _31(); void _32(); void _33(); void _34();
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
{
    void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
    void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
    void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    // IMFSample
    void _30(); void _31();
    void GetSampleTime(out long time);
    void SetSampleTime(long time);
    void GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void _36(); void _37();
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void _40(); void _41(); void _42(); void _43();
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    void Unlock();
    void GetCurrentLength(out int length);
    void SetCurrentLength(int length);
    void GetMaxLength(out int length);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSinkWriter
{
    void AddStream(IMFMediaType type, out int streamIndex);
    void SetInputMediaType(int streamIndex, IMFMediaType type, IMFAttributes? encodingParameters);
    void BeginWriting();
    void WriteSample(int streamIndex, IMFSample sample);
    void SendStreamTick(int streamIndex, long timestamp);
    void _5(); void _6(); void _7();
    void DoFinalize();
    void _9(); void _10();
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSourceReader
{
    void _0();
    void SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(int streamIndex, int typeIndex, out IMFMediaType type);
    void GetCurrentMediaType(int streamIndex, out IMFMediaType type);
    void SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);
    void SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
    void ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);
    void _7(); void _8();
    void GetPresentationAttribute(int streamIndex, [In] ref Guid key, out PropVariant value);
}
