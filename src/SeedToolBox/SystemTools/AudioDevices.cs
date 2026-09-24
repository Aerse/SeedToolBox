using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace SeedToolBox.SystemTools;

sealed record AudioDevice(string Id, string Name, bool IsDefault);

sealed record AudioSession(uint ProcessId, string Name, float Volume, bool Muted, ISimpleAudioVolumeCom Control);

/// <summary>Windows Core Audio: endpoints, their volume and mute, the default device, and per-app volume.</summary>
static class AudioDevices
{
    const int Render = 0, Capture = 1, Active = 1;
    static Guid _context = Guid.Empty;

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public static List<AudioDevice> Outputs() => List(Render);
    public static List<AudioDevice> Inputs() => List(Capture);

    static List<AudioDevice> List(int flow)
    {
        var enumerator = Enumerator();
        string? defaultId = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, 1, out var device);
            device.GetId(out defaultId);
        }
        catch (COMException) { } // no device of this kind
        enumerator.EnumAudioEndpoints(flow, Active, out var collection);
        collection.GetCount(out var count);
        var list = new List<AudioDevice>();
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            device.GetId(out var id);
            list.Add(new AudioDevice(id, FriendlyName(device), id == defaultId));
        }
        return list;
    }

    static string FriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(0, out var store);
        var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        store.GetValue(ref key, out var value);
        try { return value.vt == 31 ? Marshal.PtrToStringUni(value.p) ?? "" : "未知设备"; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Makes the device the default for every role, like the sound settings do.</summary>
    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigCom();
        for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
    }

    static IAudioEndpointVolume? Volume(string? id, int flow)
    {
        try
        {
            var enumerator = Enumerator();
            IMMDevice device;
            if (id == null) enumerator.GetDefaultAudioEndpoint(flow, 1, out device);
            else enumerator.GetDevice(id, out device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 1, IntPtr.Zero, out var o);
            return (IAudioEndpointVolume)o;
        }
        catch (COMException) { return null; }
    }

    public static float GetVolume(string? id, bool input = false)
    {
        if (Volume(id, input ? Capture : Render) is not { } v) return 0;
        v.GetMasterVolumeLevelScalar(out var level);
        return level;
    }

    public static void SetVolume(string? id, float level, bool input = false) =>
        Volume(id, input ? Capture : Render)?.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, level)), ref _context);

    public static bool GetMute(string? id, bool input = false)
    {
        if (Volume(id, input ? Capture : Render) is not { } v) return false;
        v.GetMute(out var muted);
        return muted;
    }

    public static void SetMute(string? id, bool muted, bool input = false) =>
        Volume(id, input ? Capture : Render)?.SetMute(muted, ref _context);

    /// <summary>Apps playing on the default output device.</summary>
    public static List<AudioSession> Sessions()
    {
        var list = new List<AudioSession>();
        var enumerator = Enumerator();
        try { enumerator.GetDefaultAudioEndpoint(Render, 1, out var device); Collect(device, list); }
        catch (COMException) { }
        return list;
    }

    static void Collect(IMMDevice device, List<AudioSession> list)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, 1, IntPtr.Zero, out var o);
        ((IAudioSessionManager2)o).GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);
        for (int i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var session);
            session.GetState(out var state);
            if (state == 2) continue; // expired
            session.GetProcessId(out var pid);
            bool system = session.IsSystemSoundsSession() == 0;
            session.GetDisplayName(out var display);
            var control = (ISimpleAudioVolumeCom)session;
            control.GetMasterVolume(out var volume);
            control.GetMute(out var muted);
            var name = system ? "系统声音" : ProcessName(pid, display);
            if (name == null) continue;
            list.Add(new AudioSession(pid, name, volume, muted, control));
        }
    }

    static string? ProcessName(uint pid, string? display)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // other bitness or elevated
            return !string.IsNullOrWhiteSpace(display) && !display!.StartsWith("@") ? display : process.ProcessName;
        }
        catch (ArgumentException) { return null; } // the process has exited
    }

    public static void SetSessionVolume(AudioSession session, float level) => session.Control.SetMasterVolume(Math.Max(0, Math.Min(1, level)), ref _context);
    public static void SetSessionMute(AudioSession session, bool muted) => session.Control.SetMute(muted, ref _context);

    /// <summary>Switches to the next active output device and returns its name.</summary>
    public static string? NextOutput()
    {
        var outputs = Outputs();
        if (outputs.Count < 2) return outputs.Count == 1 ? null : "";
        int current = outputs.FindIndex(d => d.IsDefault);
        var next = outputs[(current + 1) % outputs.Count];
        SetDefault(next.Id);
        return next.Name;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float db, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float db);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName(IntPtr name, ref Guid context);
        void GetIconPath(out IntPtr path);
        void SetIconPath(IntPtr path, ref Guid context);
        void GetGroupingParam(out Guid param);
        void SetGroupingParam(ref Guid param, ref Guid context);
        void RegisterAudioSessionNotification(IntPtr notify);
        void UnregisterAudioSessionNotification(IntPtr notify);
        void GetSessionIdentifier(out IntPtr id);
        void GetSessionInstanceIdentifier(out IntPtr id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolumeCom
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}
