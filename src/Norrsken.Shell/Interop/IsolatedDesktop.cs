using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Norrsken.Shell.Interop;

/// <summary>
/// Runs a child process on a private, never-shown Windows desktop. Nothing on it can appear on
/// the user's screen, take focus or receive their input; the desktop is destroyed afterwards.
/// We never call SwitchDesktop.
/// </summary>
internal static class IsolatedDesktop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder? info, int length, out int needed);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

    const int UOI_NAME = 2;
    const uint DESKTOP_ALL = 0x01FF | 0x000F0000; // all desktop rights + standard rights

    /// <summary>Name of the desktop this thread runs on ("Default" is the user's visible one).</summary>
    public static string CurrentName()
    {
        var sb = new StringBuilder(256);
        GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME, sb, sb.Capacity * 2, out _);
        return sb.ToString();
    }

    public static bool OnUserDesktop => CurrentName().Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts this executable with <paramref name="args"/> on a new hidden desktop and waits for it.</summary>
    public static int Run(string args, int timeoutMs)
    {
        string name = "NorrskenTest-" + Guid.NewGuid().ToString("N")[..8];
        IntPtr desk = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL, IntPtr.Zero);
        if (desk == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\" + name };
            var cmd = new StringBuilder($"\"{Environment.ProcessPath}\" {args}");
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Environment.CurrentDirectory, ref si, out var pi))
                throw new Win32Exception();
            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0)
                {
                    TerminateProcess(pi.hProcess, 99);
                    return 99;
                }
                GetExitCodeProcess(pi.hProcess, out uint code);
                return (int)code;
            }
            finally
            {
                Native.CloseHandle(pi.hProcess);
                Native.CloseHandle(pi.hThread);
            }
        }
        finally { CloseDesktop(desk); }
    }
}
