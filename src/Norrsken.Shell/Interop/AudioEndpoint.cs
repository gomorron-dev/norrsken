using System.Runtime.InteropServices;

namespace Norrsken.Shell.Interop;

/// <summary>
/// The default playback device's master volume via Windows Core Audio. Changes made anywhere
/// (keyboard volume keys, the Windows mixer, apps) arrive through <see cref="Changed"/>; no polling.
/// </summary>
internal sealed class AudioEndpoint : IDisposable
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    sealed class Callback(AudioEndpoint owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData) { owner.Changed?.Invoke(); return 0; }
    }

    const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

    readonly IAudioEndpointVolume _volume;
    readonly Callback _callback;
    readonly IntPtr _callbackPtr;
    Guid _context = Guid.NewGuid(); // lets us tell our own changes apart, if ever needed

    /// <summary>Raised on a Core Audio worker thread whenever volume or mute changes.</summary>
    public event Action? Changed;

    AudioEndpoint(IAudioEndpointVolume volume)
    {
        _volume = volume;
        _callback = new Callback(this);
        _callbackPtr = Marshal.GetComInterfaceForObject(_callback, typeof(IAudioEndpointVolumeCallback));
        _volume.RegisterControlChangeNotify(_callbackPtr);
    }

    public static AudioEndpoint? OpenDefault()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) == 0
                ? new AudioEndpoint((IAudioEndpointVolume)o) : null;
        }
        catch (COMException) { return null; }
    }

    /// <summary>0..1, perceptual (the same scale as the Windows volume slider).</summary>
    public float Volume
    {
        get => _volume.GetMasterVolumeLevelScalar(out float v) == 0 ? v : 0;
        set => _volume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _context);
    }

    public bool Muted
    {
        get => _volume.GetMute(out bool m) == 0 && m;
        set => _volume.SetMute(value, ref _context);
    }

    public void Dispose()
    {
        _volume.UnregisterControlChangeNotify(_callbackPtr);
        Marshal.Release(_callbackPtr);
        Marshal.ReleaseComObject(_volume);
    }
}
