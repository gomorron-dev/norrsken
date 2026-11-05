using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's default apps. Flickerbox (bundled in `apps\Flickerbox`) is installed to Program Files
/// and registered for every image, video, audio, GIF and playlist format; Norrsken Notes for text.
/// Windows protects per-user defaults from being written by apps, so Norrsken uses the supported
/// route instead: a default-associations file plus the machine policy that applies it at every
/// sign-in (also for brand-new accounts). On Norrsken OS this runs from SetupComplete, before the
/// first sign-in, so the defaults are there from the start. Needs admin (`--register-defaults`).
/// </summary>
public static class MediaDefaults
{
    static readonly string[] Video =
    [
        "mp4", "m4v", "mp4v", "mov", "qt", "mkv", "mk3d", "webm", "avi", "divx", "xvid", "wmv", "asf", "flv", "f4v",
        "3gp", "3g2", "3gpp", "mts", "m2ts", "m2t", "ts", "tp", "trp", "tod", "mod", "mpg", "mpeg", "mpe", "mpv",
        "m1v", "m2v", "m2p", "vob", "evo", "ogv", "ogm", "rm", "rmvb", "dv", "dif", "mxf", "nut", "y4m", "amv",
        "mjpg", "mjpeg", "h264", "264", "avc", "h265", "265", "hevc", "vvc", "266", "ivf", "obu", "wtv", "dvr-ms",
        "bik", "bk2", "smk", "nsv", "gxf", "vro", "flc", "fli", "roq", "insv", "lrv", "dav", "swf",
    ];
    static readonly string[] Audio =
    [
        "mp3", "mp2", "mp1", "mpa", "m4a", "m4b", "m4r", "aac", "adts", "wav", "wave", "bwf", "w64", "rf64", "flac",
        "ogg", "oga", "opus", "spx", "wma", "aiff", "aif", "aifc", "ape", "wv", "tta", "tak", "mka", "ac3", "eac3",
        "ec3", "dts", "dtshd", "thd", "truehd", "mlp", "amr", "awb", "3ga", "au", "snd", "caf", "gsm", "voc", "dsf",
        "dff", "mpc", "ra", "weba", "oma", "qcp", "8svx", "sln", "mid", "midi", "rmi", "kar", "xm", "it", "s3m",
        "mptm", "669", "stm", "ult", "mtm", "umx",
    ];
    static readonly string[] Image =
    [
        "jpg", "jpeg", "jpe", "jfif", "jif", "pjpeg", "pjp", "png", "apng", "webp", "avif", "avifs", "bmp", "dib",
        "rle", "ico", "cur", "svg", "svgz", "tif", "tiff", "heic", "heif", "heics", "heifs", "hif", "jxl", "jp2",
        "j2k", "j2c", "jpf", "jpx", "jpm", "jls", "psd", "tga", "icb", "vda", "vst", "dds", "exr", "hdr", "rgbe",
        "pcx", "ppm", "pgm", "pbm", "pnm", "pam", "pfm", "phm", "xbm", "xpm", "xwd", "sgi", "rgb", "rgba", "bw",
        "ras", "sun", "im1", "im8", "im24", "im32", "qoi", "wdp", "jxr", "hdp", "emf", "wmf", "fits", "fit", "fts",
        "dpx", "cin", "wbmp", "pcd",
        // camera RAW
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "ori", "rw2", "raf", "pef", "ptx",
        "srw", "x3f", "3fr", "fff", "mef", "mos", "erf", "kdc", "dcr", "dcs", "rwl", "iiq", "mrw", "raw", "rwz",
        "gpr", "bay", "cap",
    ];
    static readonly string[] Gif = ["gif"];
    static readonly string[] Playlist = ["m3u", "m3u8", "pls", "xspf", "wpl"];
    static HashSet<string>? _media;

    /// <summary>Photo, video, music, GIF and playlist extensions (without the dot) that Flickerbox opens.</summary>
    public static bool IsMedia(string ext) => (_media ??= MediaMap().Select(m => m.Ext).ToHashSet(StringComparer.OrdinalIgnoreCase)).Contains(ext.TrimStart('.'));

    public static readonly string[] Text = ["txt", "log", "md", "ini", "cfg", "csv", "json", "xml", "yml", "yaml", "toml", "nfo", "conf"];

    const string App = "Flickerbox";
    const string CapKey = @"SOFTWARE\Gomorronmannen\Flickerbox";
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public const string NotesProgId = "Norrsken.Notes.Text";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Flickerbox");
    public static string Exe => Path.Combine(Dir, "Flickerbox.exe");
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "apps", "Flickerbox");
    public static string AssociationsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Norrsken", "DefaultAssociations.xml");

    static IEnumerable<(string Ext, string ProgId)> MediaMap() =>
        Video.Select(e => (e, "Flickerbox.Video"))
            .Concat(Audio.Select(e => (e, "Flickerbox.Audio")))
            .Concat(Image.Select(e => (e, "Flickerbox.Image")))
            .Concat(Gif.Select(e => (e, "Flickerbox.Gif")))
            .Concat(Playlist.Select(e => (e, "Flickerbox.Playlist")))
            .DistinctBy(p => p.e);

    /// <summary>`--register-defaults`: install Flickerbox (if bundled), register it and Notes, set the defaults policy.</summary>
    public static (InstallState, string) Apply()
    {
        var notes = new List<string>();
        try
        {
            bool flickerbox = InstallFlickerbox(notes);
            RegisterNotes();
            if (flickerbox) RegisterFlickerbox();
            WriteDefaults(flickerbox);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return (InstallState.Done, flickerbox
                ? "Photos, video and music open in Flickerbox, text in Notes (from your next sign-in)"
                : "Text opens in Notes; Flickerbox isn't bundled in this build" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""));
        }
        catch (Exception ex) { return (InstallState.Failed, ex.Message); }
    }

    /// <summary>
    /// Copies the bundled Flickerbox into Program Files. A running Flickerbox is closed first (its files would be
    /// locked and the copy would fail half-way); an identical install is left alone; the result is checked.
    /// </summary>
    static bool InstallFlickerbox(List<string> notes)
    {
        string bundledExe = Path.Combine(Bundled, "Flickerbox.exe");
        if (!File.Exists(bundledExe)) { notes.Add("no apps\\Flickerbox folder"); return File.Exists(Exe); }
        if (!WebView2Installed()) notes.Add("the WebView2 runtime Flickerbox draws with is missing: install it from microsoft.com/edge/webview2");
        if (File.Exists(Exe) && SameFile(Path.Combine(Bundled, "Flickerbox.dll"), Path.Combine(Dir, "Flickerbox.dll")) && SameFile(bundledExe, Exe))
            return true; // already this version
        foreach (var p in Process.GetProcessesByName("Flickerbox"))
            try { if (p.MainModule?.FileName?.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) != false) { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); } } catch (Exception) { }
        Directory.CreateDirectory(Dir);
        using var rc = Process.Start(new ProcessStartInfo("robocopy", $"\"{Bundled}\" \"{Dir}\" /E /R:3 /W:1 /NP /NFL /NDL /NJH /NJS")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        rc.StandardOutput.ReadToEnd();
        rc.WaitForExit();
        if (rc.ExitCode >= 8) throw new IOException($"Copying Flickerbox failed (robocopy {rc.ExitCode}).");
        if (!SameFile(Path.Combine(Bundled, "Flickerbox.dll"), Path.Combine(Dir, "Flickerbox.dll")))
            throw new IOException("Flickerbox was copied but doesn't match the bundled version.");
        return File.Exists(Exe);
    }

    static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(fa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(fb));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Flickerbox's interface runs in Microsoft's WebView2 (part of Windows 11; missing on some stripped installs).</summary>
    public static bool WebView2Installed()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" })
        {
            if (Registry.LocalMachine.OpenSubKey(key)?.GetValue("pv") is string v && v.Length > 0 && v != "0.0.0.0") return true;
            if (Registry.CurrentUser.OpenSubKey(key)?.GetValue("pv") is string u && u.Length > 0 && u != "0.0.0.0") return true;
        }
        return false;
    }
    static RegistryKey Cls(string sub) => Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\" + sub);

    static void RegisterFlickerbox()
    {
        var names = new Dictionary<string, string> { ["Flickerbox.Video"] = "Video", ["Flickerbox.Audio"] = "Audio", ["Flickerbox.Image"] = "Image", ["Flickerbox.Gif"] = "Animated GIF", ["Flickerbox.Playlist"] = "Playlist" };
        foreach (var (id, label) in names)
        {
            using var k = Cls(id);
            k.SetValue("", $"{label} · {App}");
            k.SetValue("FriendlyTypeName", $"{label} · {App}");
            string icon = Path.Combine(Dir, "icons", id[(id.IndexOf('.') + 1)..].ToLowerInvariant() + ".ico");
            using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", File.Exists(icon) ? icon : Exe + ",0");
            using (var a = k.CreateSubKey("Application"))
            {
                a.SetValue("ApplicationName", App);
                a.SetValue("ApplicationCompany", "Gomorronmannen");
                a.SetValue("ApplicationIcon", Exe + ",0");
            }
            using var sh = k.CreateSubKey("shell");
            sh.SetValue("", "open");
            using (var o = sh.CreateSubKey("open")) { o.SetValue("FriendlyAppName", App); o.SetValue("MultiSelectModel", "Player"); }
            using (var c = sh.CreateSubKey(@"open\command")) c.SetValue("", $"\"{Exe}\" \"%1\"");
            if (id == "Flickerbox.Playlist") continue;
            using (var ed = sh.CreateSubKey("flickerbox.edit")) { ed.SetValue("", "Edit with Flickerbox"); ed.SetValue("Icon", Exe + ",0"); }
            using (var c = sh.CreateSubKey(@"flickerbox.edit\command")) c.SetValue("", $"\"{Exe}\" --edit \"%1\"");
            using (var cv = sh.CreateSubKey("flickerbox.convert")) { cv.SetValue("", "Convert with Flickerbox"); cv.SetValue("Icon", Exe + ",0"); }
            using (var c = sh.CreateSubKey(@"flickerbox.convert\command")) c.SetValue("", $"\"{Exe}\" --convert \"%1\"");
        }

        using var cap = Registry.LocalMachine.CreateSubKey(CapKey + @"\Capabilities");
        cap.SetValue("ApplicationName", App);
        cap.SetValue("ApplicationDescription", "Play, edit and convert every video, sound, image and GIF.");
        cap.SetValue("ApplicationIcon", Exe + ",0");
        using var fa = cap.CreateSubKey("FileAssociations");
        using var app = Cls(@"Applications\Flickerbox.exe");
        app.SetValue("FriendlyAppName", App);
        using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue("", $"\"{Exe}\" \"%1\"");
        using var sup = app.CreateSubKey("SupportedTypes");
        foreach (var (ext, id) in MediaMap())
        {
            string e = "." + ext;
            fa.SetValue(e, id);
            sup.SetValue(e, "");
            try
            {
                using var ek = Cls(e);
                using (var ow = ek.CreateSubKey("OpenWithProgids")) ow.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
                ek.SetValue("", id); // machine default too (per-user choices still win until the policy applies)
                string? perceived = id switch { "Flickerbox.Video" => "video", "Flickerbox.Audio" => "audio", "Flickerbox.Playlist" => null, _ => "image" };
                if (perceived != null && ek.GetValue("PerceivedType") == null) ek.SetValue("PerceivedType", perceived);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        using (var ra = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\RegisteredApplications")) ra.SetValue(App, CapKey + @"\Capabilities");
        using (var ap = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Flickerbox.exe")) { ap.SetValue("", Exe); ap.SetValue("Path", Dir); }
        foreach (var kind in new[] { "video", "audio", "image" })
        {
            string root = $@"SystemFileAssociations\{kind}\shell";
            using (var ed = Cls(root + @"\flickerbox.edit")) { ed.SetValue("MUIVerb", "Edit with Flickerbox"); ed.SetValue("Icon", Exe + ",0"); }
            using (var c = Cls(root + @"\flickerbox.edit\command")) c.SetValue("", $"\"{Exe}\" --edit \"%1\"");
            using (var cv = Cls(root + @"\flickerbox.convert")) { cv.SetValue("MUIVerb", "Convert with Flickerbox"); cv.SetValue("Icon", Exe + ",0"); }
            using (var c = Cls(root + @"\flickerbox.convert\command")) c.SetValue("", $"\"{Exe}\" --convert \"%1\"");
        }
        EnsureShortcut(Environment.SpecialFolder.CommonPrograms);
        using (var un = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Flickerbox"))
        {
            un.SetValue("DisplayName", App);
            un.SetValue("Publisher", "Gomorronmannen");
            un.SetValue("DisplayIcon", Exe + ",0");
            un.SetValue("InstallLocation", Dir);
            un.SetValue("NoModify", 1, RegistryValueKind.DWord);
            un.SetValue("NoRemove", 1, RegistryValueKind.DWord); // part of Norrsken OS
        }
    }

    /// <summary>
    /// A Start-menu shortcut, so Flickerbox shows in the launcher and search. Without it Flickerbox was
    /// installed but invisible. Machine-wide from the elevated helper; per-user from the dock as a fallback.
    /// </summary>
    public static void EnsureShortcut(Environment.SpecialFolder where = Environment.SpecialFolder.Programs)
    {
        try
        {
            if (!File.Exists(Exe)) return;
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Flickerbox.lnk");
            string lnk = Path.Combine(Environment.GetFolderPath(where), "Flickerbox.lnk");
            if (File.Exists(lnk) || (where != Environment.SpecialFolder.CommonPrograms && File.Exists(common))) return;
            ShellLink.Save(lnk, Exe, "", Exe + ",0", "Photos, video and music", null);
        }
        catch (Exception ex) { Log.Write("Flickerbox shortcut not created: " + ex.Message); }
    }

    /// <summary>Notes for text files, machine-wide (so the policy can point at it for every account).</summary>
    static void RegisterNotes()
    {
        string exe = Environment.ProcessPath!;
        using (var k = Cls(NotesProgId))
        {
            k.SetValue("", "Text document");
            using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", Path.Combine(AppContext.BaseDirectory, "assets", "notes.ico") is var ico && File.Exists(ico) ? ico : exe + ",0");
            using (var a = k.CreateSubKey("Application")) { a.SetValue("ApplicationName", "Notes"); a.SetValue("ApplicationCompany", "Norrsken"); }
            using var c = k.CreateSubKey(@"shell\open\command");
            c.SetValue("", $"\"{exe}\" --app notes \"%1\"");
        }
        using (var cap = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Norrsken\Notes\Capabilities"))
        {
            cap.SetValue("ApplicationName", "Notes");
            cap.SetValue("ApplicationDescription", "Norrsken's text editor");
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var e in Text) fa.SetValue("." + e, NotesProgId);
        }
        using (var ra = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\RegisteredApplications")) ra.SetValue("Norrsken Notes", @"SOFTWARE\Norrsken\Notes\Capabilities");
        foreach (var e in Text)
            using (var ow = Cls($@".{e}\OpenWithProgids")) ow.SetValue(NotesProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    /// <summary>The associations file and the policy that applies it at each sign-in.</summary>
    static void WriteDefaults(bool flickerbox)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<DefaultAssociations>\n");
        if (flickerbox)
            foreach (var (ext, id) in MediaMap())
                sb.Append($"  <Association Identifier=\".{ext}\" ProgId=\"{id}\" ApplicationName=\"{App}\" />\n");
        foreach (var e in Text) sb.Append($"  <Association Identifier=\".{e}\" ProgId=\"{NotesProgId}\" ApplicationName=\"Notes\" />\n");
        sb.Append("</DefaultAssociations>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(AssociationsFile)!);
        File.WriteAllText(AssociationsFile, sb.ToString(), new UTF8Encoding(false));
        using var pk = Registry.LocalMachine.CreateSubKey(PolicyKey);
        pk.SetValue("DefaultAssociationsConfiguration", AssociationsFile);
    }

    /// <summary>For tests and the About page: how many formats Norrsken assigns.</summary>
    public static int FormatCount => MediaMap().Count() + Text.Length;

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void SHChangeNotify(int evt, uint flags, IntPtr a, IntPtr b);
}
