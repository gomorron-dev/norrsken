using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Norrsken.Shell.Core;

/// <summary>
/// A console program (PowerShell, cmd, wsl, anything) running on Windows' pseudo-console (ConPTY):
/// its screen arrives as VT text through <see cref="Output"/>, keystrokes go in through <see cref="Write"/>.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_UNICODE_ENVIRONMENT = 0x400;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new(0x00020016);

    readonly IntPtr _hpc;
    readonly FileStream _input;
    readonly FileStream _output;
    PROCESS_INFORMATION _pi;
    bool _disposed;

    /// <summary>Raw output bytes (UTF-8 VT), on a background thread.</summary>
    public event Action<byte[], int>? Output;
    /// <summary>The program ended (exit code), on a background thread.</summary>
    public event Action<int>? Exited;
    public int ProcessId => _pi.dwProcessId;

    public PseudoConsole(string commandLine, string? workingDir, int cols, int rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inRead, outWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr);
        inRead.Dispose();
        outWrite.Dispose();
        _input = new FileStream(inWrite, FileAccess.Write);
        _output = new FileStream(outRead, FileAccess.Read);

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, workingDir, ref si, out _pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }

        new Thread(ReadLoop) { IsBackground = true, Name = "Terminal output" }.Start();
        new Thread(() =>
        {
            WaitForSingleObject(_pi.hProcess, 0xFFFFFFFF);
            int code = 0;
            try { code = System.Diagnostics.Process.GetProcessById(_pi.dwProcessId).ExitCode; } catch (Exception) { }
            Exited?.Invoke(code);
        }) { IsBackground = true, Name = "Terminal watch" }.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = _output.Read(buf, 0, buf.Length)) > 0) Output?.Invoke(buf, n);
        }
        catch (Exception) { }
    }

    public void Write(string text)
    {
        if (_disposed) return;
        try { var b = Encoding.UTF8.GetBytes(text); _input.Write(b, 0, b.Length); _input.Flush(); }
        catch (IOException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (!_disposed && cols > 0 && rows > 0) ResizePseudoConsole(_hpc, new COORD { X = (short)cols, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ClosePseudoConsole(_hpc); } catch (Exception) { }
        try { System.Diagnostics.Process.GetProcessById(_pi.dwProcessId).Kill(entireProcessTree: true); } catch (Exception) { }
        _input.Dispose();
        _output.Dispose();
        CloseHandle(_pi.hProcess);
        CloseHandle(_pi.hThread);
    }
}
