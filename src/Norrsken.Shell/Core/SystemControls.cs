using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Norrsken.Shell.Core;

/// <summary>Display modes, HDR and Norrsken's own night light.</summary>
public static class DisplayControl
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string? device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    const int ENUM_CURRENT_SETTINGS = -1, CDS_UPDATEREGISTRY = 1, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

    public sealed record Mode(int Width, int Height, int Hz) { public override string ToString() => $"{Width} × {Height}"; }

    public static Mode? Current()
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm) ? new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    public static List<Mode> Modes()
    {
        var set = new HashSet<Mode>();
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettingsW(null, i, ref dm); i++)
            if (dm.dmBitsPerPel >= 24 && dm.dmPelsWidth >= 800) set.Add(new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return set.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Hz).ToList();
    }

    /// <summary>Applies a resolution/refresh rate to the main display. Returns false if the display refused.</summary>
    public static bool Apply(Mode m)
    {
        if (App.TestMode) { App.TestLaunches.Add($"display {m.Width}x{m.Height}@{m.Hz}"); return true; }
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        dm.dmPelsWidth = m.Width; dm.dmPelsHeight = m.Height; dm.dmDisplayFrequency = m.Hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }

    // ── HDR (advanced colour) via the documented DisplayConfig API ──

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct HEADER { public int type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] struct GET_COLOR { public HEADER header; public uint value; public int colorEncoding, bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)] struct SET_COLOR { public HEADER header; public uint enable; }
    [StructLayout(LayoutKind.Sequential, Size = 72)] struct PATH_INFO { public LUID sourceAdapter; public uint sourceId; public uint sourceModeIdx; public uint sourceStatus; public LUID targetAdapter; public uint targetId; }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref GET_COLOR info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_COLOR info);
    const uint QDC_ONLY_ACTIVE_PATHS = 2;

    static List<(LUID Adapter, uint Target)> Targets()
    {
        var list = new List<(LUID, uint)>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return list;
        var paths = new PATH_INFO[np];
        IntPtr modes = Marshal.AllocHGlobal((int)(nm * 64));
        try { if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) == 0) list.AddRange(paths.Take((int)np).Select(p => (p.targetAdapter, p.targetId))); }
        finally { Marshal.FreeHGlobal(modes); }
        return list;
    }

    /// <summary>(supported, on) for the first display that can do HDR.</summary>
    public static (bool Supported, bool On) Hdr()
    {
        foreach (var (adapter, target) in Targets())
        {
            var info = new GET_COLOR { header = new HEADER { type = 9, size = Marshal.SizeOf<GET_COLOR>(), adapterId = adapter, id = target } };
            if (DisplayConfigGetDeviceInfo(ref info) == 0 && (info.value & 1) != 0) return (true, (info.value & 2) != 0);
        }
        return (false, false);
    }

    public static void SetHdr(bool on)
    {
        if (App.TestMode) { App.TestLaunches.Add("hdr " + on); return; }
        foreach (var (adapter, target) in Targets())
        {
            var set = new SET_COLOR { header = new HEADER { type = 10, size = Marshal.SizeOf<SET_COLOR>(), adapterId = adapter, id = target }, enable = on ? 1u : 0u };
            DisplayConfigSetDeviceInfo(ref set);
        }
    }

    // ── Night light: Norrsken warms the screen itself through the display's gamma ramp ──

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    static System.Windows.Threading.DispatcherTimer? _nightTimer;
    static double _applied = -1;

    /// <summary>Warmth now (0 = off … 1 = warmest), following the on/off switch and the evening schedule.</summary>
    public static double NightWarmth()
    {
        var s = App.Settings;
        if (!s.NightLight) return 0;
        if (!s.NightLightScheduled) return s.NightLightWarmth;
        var now = DateTime.Now.TimeOfDay;
        bool night = now >= TimeSpan.FromHours(21) || now < TimeSpan.FromHours(7);
        return night ? s.NightLightWarmth : 0;
    }

    public static void StartNightLight()
    {
        if (App.TestMode || _nightTimer != null) return;
        _nightTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _nightTimer.Tick += (_, _) => ApplyNightLight(force: true); // Windows resets ramps after sleep or display changes
        _nightTimer.Start();
        ApplyNightLight();
    }

    /// <summary>Neutral colours again (Norrsken quitting, or night light switched off).</summary>
    public static void ResetNightLight()
    {
        if (App.TestMode || _applied <= 0) return;
        _applied = 0;
        var ramp = new ushort[768];
        for (int i = 0; i < 256; i++) ramp[i] = ramp[256 + i] = ramp[512 + i] = (ushort)(i * 257);
        IntPtr dc = GetDC(IntPtr.Zero);
        try { SetDeviceGammaRamp(dc, ramp); } finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    public static void ApplyNightLight(bool force = false)
    {
        double w = NightWarmth();
        if (!force && Math.Abs(w - _applied) < 0.001) return;
        if (w == 0 && _applied <= 0 && !force) return;
        _applied = w;
        if (App.TestMode) return;
        var ramp = new ushort[256 * 3];
        double g = 1 - 0.28 * w, b = 1 - 0.62 * w;
        for (int i = 0; i < 256; i++)
        {
            int v = i * 257;
            ramp[i] = (ushort)v;
            ramp[256 + i] = (ushort)(v * g);
            ramp[512 + i] = (ushort)(v * b);
        }
        IntPtr dc = GetDC(IntPtr.Zero);
        try { SetDeviceGammaRamp(dc, ramp); } finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    // ── Per-app GPU preference (what Windows' Graphics page writes) ──

    const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public static List<(string Exe, int Pref)> GpuPreferences()
    {
        using var k = Registry.CurrentUser.OpenSubKey(GpuKey);
        return k?.GetValueNames().Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(n => (n, int.TryParse((k.GetValue(n) as string ?? "").Split(';').FirstOrDefault(p => p.StartsWith("GpuPreference="))?[14..], out int v) ? v : 0)).ToList() ?? [];
    }
    public static void SetGpuPreference(string exe, int pref)
    {
        if (App.TestMode) return;
        using var k = Registry.CurrentUser.CreateSubKey(GpuKey);
        if (pref < 0) k.DeleteValue(exe, false); else k.SetValue(exe, $"GpuPreference={pref};");
    }
}

/// <summary>Sound devices and per-app volume (Windows Core Audio).</summary>
public static class AudioControl
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore { int GetCount(out int c); int GetAt(int i, out PROPERTYKEY k); int GetValue(ref PROPERTYKEY k, out PROPVARIANT v); }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr p; }

    // Undocumented but stable since Vista: how Windows itself switches the default device.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat(); int GetProcessingPeriod(); int SetProcessingPeriod();
        int GetShareMode(); int SetShareMode(); int GetPropertyValue(); int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        int SetEndpointVisibility();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 { int GetAudioSessionControl(); int GetSimpleAudioVolume(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, out IAudioSessionControl2 s); }
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state); int GetDisplayName(out IntPtr n); int SetDisplayName(); int GetIconPath(out IntPtr p); int SetIconPath();
        int GetGroupingParam(); int SetGroupingParam(); int RegisterAudioSessionNotification(); int UnregisterAudioSessionNotification();
        int GetSessionIdentifier(out IntPtr id); int GetSessionInstanceIdentifier(out IntPtr id); int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume { int SetMasterVolume(float level, ref Guid ctx); int GetMasterVolume(out float level); int SetMute(bool mute, ref Guid ctx); int GetMute(out bool mute); }

    public sealed record Device(string Id, string Name, bool IsDefault);
    public sealed record Session(int Pid, string Name, float Volume, object Control);

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    static string NameOf(IMMDevice d)
    {
        if (d.OpenPropertyStore(0, out var store) != 0) return "Audio device";
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        return store.GetValue(ref key, out var v) == 0 && v.vt == 31 ? Marshal.PtrToStringUni(v.p) ?? "Audio device" : "Audio device";
    }

    /// <summary>Output (render) or input (capture) devices that are active.</summary>
    public static List<Device> Devices(bool output)
    {
        var list = new List<Device>();
        try
        {
            var e = Enumerator();
            int flow = output ? 0 : 1;
            string? def = e.GetDefaultAudioEndpoint(flow, 1, out var d) == 0 && d.GetId(out var id) == 0 ? id : null;
            if (e.EnumAudioEndpoints(flow, 1 /* ACTIVE */, out var col) != 0) return list;
            col.GetCount(out int n);
            for (int i = 0; i < n; i++)
                if (col.Item(i, out var dev) == 0 && dev.GetId(out var devId) == 0) list.Add(new(devId, NameOf(dev), devId == def));
        }
        catch (Exception ex) { Log.Write("Audio devices: " + ex.Message); }
        return list;
    }

    public static void SetDefault(string id)
    {
        if (App.TestMode) { App.TestLaunches.Add("audio default " + id); return; }
        var pc = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
        SystemStatus.ReopenAudio();
    }

    /// <summary>Apps currently making (or able to make) sound on the default output, with their volume.</summary>
    public static List<Session> Sessions()
    {
        var list = new List<Session>();
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return list;
            var iid = typeof(IAudioSessionManager2).GUID;
            dev.Activate(ref iid, 1, IntPtr.Zero, out var o);
            ((IAudioSessionManager2)o).GetSessionEnumerator(out var en);
            en.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                en.GetSession(i, out var s);
                s.GetProcessId(out uint pid);
                if (pid == 0) continue;
                string name;
                try { var p = Process.GetProcessById((int)pid); name = p.MainWindowTitle.Length > 0 && p.ProcessName != "chrome" ? p.ProcessName : p.ProcessName; name = FileVersionInfo.GetVersionInfo(Interop.Native.GetProcessPath(pid) ?? "").FileDescription is { Length: > 0 } d ? d : name; }
                catch (Exception) { continue; }
                ((ISimpleAudioVolume)s).GetMasterVolume(out float v);
                if (list.All(x => x.Name != name)) list.Add(new((int)pid, name, v, s));
            }
        }
        catch (Exception ex) { Log.Write("Audio sessions: " + ex.Message); }
        return list;
    }

    public static void SetVolume(Session s, float v)
    {
        if (App.TestMode) return;
        var ctx = Guid.Empty;
        ((ISimpleAudioVolume)s.Control).SetMasterVolume(Math.Clamp(v, 0, 1), ref ctx);
    }
}

/// <summary>Mouse, power timeouts, privacy permissions, time sync.</summary>
public static class DeviceControl
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, ref int value, uint flags);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, int[] value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool SetSpi(uint action, uint param, IntPtr value, uint flags);
    [DllImport("user32.dll")] static extern bool SwapMouseButton(bool swap);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    const uint SPIF = 3;

    public static int MouseSpeed { get { int v = 10; SystemParametersInfoW(0x70, 0, ref v, 0); return v; } set { if (!App.TestMode) SetSpi(0x71, 0, value, SPIF); } }
    public static int ScrollLines { get { int v = 3; SystemParametersInfoW(0x68, 0, ref v, 0); return v; } set { if (!App.TestMode) SetSpi(0x69, (uint)value, IntPtr.Zero, SPIF); } }
    public static bool ButtonsSwapped { get => GetSystemMetrics(23) != 0; set { if (App.TestMode) return; SwapMouseButton(value); using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse"); k.SetValue("SwapMouseButtons", value ? "1" : "0"); } }
    public static bool Acceleration
    {
        get { var a = new int[3]; SystemParametersInfoW(3, 0, a, 0); return a[2] != 0; }
        set { if (App.TestMode) return; var a = value ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 }; SystemParametersInfoW(4, 0, a, SPIF); }
    }

    /// <summary>Screen-off and sleep timeouts in minutes for battery/plugged in (0 = never).</summary>
    public static void SetTimeout(string what, bool battery, int minutes)
    {
        if (App.TestMode) { App.TestLaunches.Add($"powercfg {what} {battery} {minutes}"); return; }
        string arg = $"/change {what}-timeout-{(battery ? "dc" : "ac")} {minutes}";
        using var p = Process.Start(new ProcessStartInfo("powercfg.exe", arg) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(5000);
    }

    public static int GetTimeout(string what, bool battery)
    {
        // powercfg /q prints the current values as hex seconds; fall back to 0 when unknown.
        try
        {
            string sub = what == "monitor" ? "7516b95f-f776-4464-8c53-06167f40cc99 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e" : "238c9fa8-0aad-41ed-83f4-97be242c8f20 29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/q scheme_current " + sub) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n').Where(l => l.Contains("0x")).ToList();
            var hex = lines.Where(l => l.Contains(':')).Select(l => l[(l.LastIndexOf("0x", StringComparison.Ordinal) + 2)..].Trim()).ToList();
            if (hex.Count >= 2) return Convert.ToInt32(hex[battery ? ^1 : ^2], 16) / 60;
        }
        catch (Exception) { }
        return 0;
    }

    // ── App permissions (the switches Windows' Privacy pages flip) ──
    public static readonly (string Id, string Name)[] Permissions =
    [
        ("location", "Location"), ("webcam", "Camera"), ("microphone", "Microphone"), ("userNotificationListener", "Notifications"),
        ("contacts", "Contacts"), ("appointments", "Calendar"), ("phoneCall", "Phone calls"), ("radios", "Radios"),
        ("broadFileSystemAccess", "File system"), ("documentsLibrary", "Documents"), ("picturesLibrary", "Pictures"), ("videosLibrary", "Videos"),
        ("graphicsCaptureProgrammatic", "Screenshots by apps"), ("bluetoothSync", "Other devices"),
    ];
    const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    public static bool Allowed(string id) { using var k = Registry.CurrentUser.OpenSubKey(Consent + id); return (k?.GetValue("Value") as string) != "Deny"; }
    public static void Allow(string id, bool on)
    {
        if (App.TestMode) { App.TestLaunches.Add($"privacy {id} {on}"); return; }
        using var k = Registry.CurrentUser.CreateSubKey(Consent + id);
        k.SetValue("Value", on ? "Allow" : "Deny");
    }

    // ── Clock ──
    public static bool AutoTime
    {
        get { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"); return (k?.GetValue("Type") as string) != "NoSync"; }
        set
        {
            if (App.TestMode) return;
            using var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
            k.SetValue("Type", value ? "NTP" : "NoSync");
            if (value) SyncTime();
        }
    }
    public static void SyncTime()
    {
        if (App.TestMode) return;
        Process.Start(new ProcessStartInfo("w32tm.exe", "/resync /force") { UseShellExecute = false, CreateNoWindow = true });
    }
}

/// <summary>Windows Update, Defender, activation, PC name and clean-up, without opening Windows Settings.</summary>
public static class Maintenance
{
    public sealed record Update(string Title, string Kb, double SizeMb, object Native);

    /// <summary>Asks Windows Update for updates that aren't installed (takes a while; run off the UI thread).</summary>
    public static List<Update> FindUpdates()
    {
        var list = new List<Update>();
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        foreach (dynamic u in result.Updates)
        {
            string kb = "";
            try { foreach (dynamic id in u.KBArticleIDs) { kb = "KB" + id; break; } } catch (Exception) { }
            double size = 0;
            try { size = (double)(decimal)u.MaxDownloadSize / 1048576; } catch (Exception) { }
            list.Add(new(u.Title, kb, size, u));
        }
        return list;
    }

    /// <summary>Downloads and installs the given updates (needs administrator). Returns whether a restart is needed.</summary>
    public static bool Install(IEnumerable<Update> updates)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        foreach (var u in updates) { dynamic n = u.Native; try { if (!n.EulaAccepted) n.AcceptEula(); } catch (Exception) { } coll.Add(n); }
        dynamic dl = session.CreateUpdateDownloader();
        dl.Updates = coll;
        dl.Download();
        dynamic inst = session.CreateUpdateInstaller();
        inst.Updates = coll;
        dynamic res = inst.Install();
        return (bool)res.RebootRequired;
    }

    public sealed record Security(bool Antivirus, bool RealTime, DateTime? Signatures, DateTime? LastScan);

    public static Security? Defender()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
                return new((bool)o["AntivirusEnabled"], (bool)o["RealTimeProtectionEnabled"], ToDate(o["AntivirusSignatureLastUpdated"]), ToDate(o["QuickScanEndTime"]));
        }
        catch (Exception) { }
        return null;
    }

    static DateTime? ToDate(object? o) => o is string s && s.Length >= 14 ? ManagementDateTimeConverter.ToDateTime(s) : null;

    public static Process? QuickScan()
    {
        if (App.TestMode) { App.TestLaunches.Add("defender quick scan"); return null; }
        string mp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        return File.Exists(mp) ? Process.Start(new ProcessStartInfo(mp, "-Scan -ScanType 1") { UseShellExecute = false, CreateNoWindow = true }) : null;
    }

    /// <summary>"Activated" / "Not activated" for Windows itself.</summary>
    public static string Activation()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["LicenseStatus"]) switch { 1 => "Activated", 0 => "Not activated", 5 => "Not activated (notification)", _ => "Activation pending" };
        }
        catch (Exception) { }
        return "Unknown";
    }

    /// <summary>Renames the PC (takes effect after a restart; needs administrator).</summary>
    public static string? RenamePc(string name)
    {
        if (App.TestMode) { App.TestLaunches.Add("rename " + name); return null; }
        try
        {
            using var cs = new ManagementObject($"Win32_ComputerSystem.Name='{Environment.MachineName}'");
            var r = cs.InvokeMethod("Rename", [name, null, null]);
            return Convert.ToInt32(r) == 0 ? null : $"Windows refused the name (code {r}).";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Temporary files Windows and apps leave behind: (bytes, files).</summary>
    public static (long Bytes, int Files) TempSize()
    {
        long b = 0; int n = 0;
        foreach (var dir in TempDirs())
            try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })) { b += f.Length; n++; } }
            catch (Exception) { }
        return (b, n);
    }

    static IEnumerable<string> TempDirs() => new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\INetCache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps") }.Where(Directory.Exists);

    /// <summary>Deletes what it can (files in use are skipped). Returns bytes freed.</summary>
    public static long CleanTemp()
    {
        if (App.TestMode) return 0;
        long freed = 0;
        foreach (var dir in TempDirs())
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { long len = f.Length; f.Delete(); freed += len; } catch (Exception) { }
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                try { d.Delete(true); } catch (Exception) { }
        }
        return freed;
    }

    public static void RestartToRecovery()
    {
        if (App.TestMode) { App.TestLaunches.Add("restart /o"); return; }
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /o /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }
}
