using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Norrsken.Shell.Core;

/// <summary>Live machine-wide readings for Task Manager: CPU, memory, disk, network and GPU.</summary>
public sealed class SystemMeters : IDisposable
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
    [StructLayout(LayoutKind.Explicit)] struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

    long _idle, _kernel, _user;
    long _netBytes;
    DateTime _netAt;
    readonly IntPtr _query, _disk, _gpu;

    public double Cpu { get; private set; }
    public double MemoryUsedGb { get; private set; }
    public double MemoryTotalGb { get; private set; }
    public double MemoryPercent => MemoryTotalGb > 0 ? MemoryUsedGb / MemoryTotalGb * 100 : 0;
    public double Disk { get; private set; }
    public double Gpu { get; private set; }
    public double NetworkKbps { get; private set; }

    public SystemMeters()
    {
        GetSystemTimes(out _idle, out _kernel, out _user);
        _netBytes = NetBytes();
        _netAt = DateTime.UtcNow;
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0)
        {
            PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _disk);
            PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpu);
            PdhCollectQueryData(_query);
        }
    }

    static long NetBytes()
    {
        long sum = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    sum += n.GetIPStatistics().BytesReceived + n.GetIPStatistics().BytesSent;
        }
        catch (Exception) { }
        return sum;
    }

    /// <summary>Takes a new reading (call about once a second).</summary>
    public void Sample()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        long total = (kernel - _kernel) + (user - _user);
        Cpu = total > 0 ? Math.Clamp(100.0 * (total - (idle - _idle)) / total, 0, 100) : 0;
        (_idle, _kernel, _user) = (idle, kernel, user);

        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) { MemoryTotalGb = m.ullTotalPhys / 1073741824.0; MemoryUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0; }

        long net = NetBytes();
        var now = DateTime.UtcNow;
        double secs = Math.Max(0.2, (now - _netAt).TotalSeconds);
        NetworkKbps = Math.Max(0, (net - _netBytes) * 8 / 1000.0 / secs);
        (_netBytes, _netAt) = (net, now);

        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            if (_disk != IntPtr.Zero && PdhGetFormattedCounterValue(_disk, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var d) == 0) Disk = Math.Clamp(d.Double, 0, 100);
            if (_gpu != IntPtr.Zero) Gpu = Math.Clamp(SumArray(_gpu), 0, 100);
        }
    }

    static double SumArray(IntPtr counter)
    {
        uint size = 0;
        PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (size == 0) return 0;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out uint count, buf) != 0) return 0;
            double sum = 0;
            int item = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer + value (8-byte aligned)
            for (int i = 0; i < count; i++) sum += Marshal.PtrToStructure<double>(buf + i * item + IntPtr.Size + 8);
            return sum;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); }
}

/// <summary>Programs that start with Windows (Run keys and Startup folders), and turning them on or off the way Windows does.</summary>
public static class StartupApps
{
    public sealed record Entry(string Name, string Command, string Source, string ApprovedKey, string ValueName, bool Machine, bool Enabled);

    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        void FromRun(Microsoft.Win32.RegistryKey hive, string path, string approved, bool machine)
        {
            using var k = hive.OpenSubKey(path);
            if (k == null) return;
            foreach (var n in k.GetValueNames())
                if (n.Length > 0) list.Add(new(n, k.GetValue(n)?.ToString() ?? "", machine ? "All users" : "You", approved, n, machine, IsOn(hive, approved, n)));
        }
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        var hklm = Microsoft.Win32.Registry.LocalMachine;
        FromRun(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", false);
        FromRun(hklm, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", true);
        FromRun(hklm, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", true);
        foreach (var (folder, machine) in new[] { (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false), (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true) })
            if (Directory.Exists(folder))
                foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(Path.GetFileNameWithoutExtension(f), f, machine ? "Startup folder (all users)" : "Startup folder", "StartupFolder", Path.GetFileName(f), machine, IsOn(machine ? hklm : hkcu, "StartupFolder", Path.GetFileName(f))));
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool IsOn(Microsoft.Win32.RegistryKey hive, string approved, string name)
    {
        using var k = hive.OpenSubKey(Approved + approved);
        return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0; // 02 = on, 03 = off
    }

    public static void Set(Entry e, bool on)
    {
        var hive = e.Machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
        using var k = hive.CreateSubKey(Approved + e.ApprovedKey);
        var value = new byte[12];
        value[0] = (byte)(on ? 2 : 3);
        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        k.SetValue(e.ValueName, value, Microsoft.Win32.RegistryValueKind.Binary);
    }
}
