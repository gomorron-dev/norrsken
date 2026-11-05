using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's motion runs at the display's full refresh rate. WPF animates at 60 fps unless told otherwise,
/// which looks choppy on a 120/144/165/240 Hz screen; Windows may also throttle a background-ish process
/// (EcoQoS) or put it on the power-saving GPU. All three are undone here, once, before any window exists.
/// </summary>
public static class FrameRate
{
    /// <summary>The fastest refresh rate among the connected displays (60 if it can't be read).</summary>
    public static int Hz { get; private set; } = 60;

    public static void Apply()
    {
        Hz = Math.Clamp(FastestDisplay(), 60, 500);
        // Every animation, storyboard and the CompositionTarget.Rendering loop then ticks at the display rate.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = Hz });
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default; // GPU, never software

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch (Exception) { }
        OptOutOfThrottling();
        Log.Write($"Frame rate: {Hz} Hz");
    }

    /// <summary>Re-reads the displays (a monitor was plugged in or its mode changed).</summary>
    public static int FastestDisplay()
    {
        int best = 0;
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dev, 0); i++, dev.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dev.StateFlags & 1 /* ATTACHED_TO_DESKTOP */) == 0) continue;
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(dev.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) && mode.dmDisplayFrequency > 1)
                best = Math.Max(best, mode.dmDisplayFrequency);
        }
        return best > 1 ? best : 60;
    }

    static void OptOutOfThrottling()
    {
        try
        {
            // PROCESS_POWER_THROTTLING_EXECUTION_SPEED in the mask, not in the state: never run in efficiency mode.
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch (Exception) { }
    }

    /// <summary>Graphics settings → "High performance" for Norrsken (only matters on PCs with two GPUs).</summary>
    public static void PreferFastGpu()
    {
        if (App.TestMode || Environment.ProcessPath is not { } exe) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (k.GetValue(exe) == null) k.SetValue(exe, "GpuPreference=2;");
        }
        catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE info);
}
