using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Norrsken.Shell.Core;

/// <summary>
/// Runs Norrsken in a helper process for work that needs administrator rights (installing apps,
/// copying old files), behind a single admin prompt. The helper appends one JSON line per change
/// to a log file, which the UI tails to update its rows. In UI-test mode the helper runs as a plain
/// child process on the hidden test desktop (no prompt).
/// </summary>
public static class Elevated
{
    const int ErrorCancelled = 1223; // the user declined the admin prompt

    public sealed record ProgressEntry(string Id, InstallState State, string? Detail);

    public static string NewLogPath(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Norrsken");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, $"{prefix}-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, "");
        return log;
    }

    public static void Report(string log, string id, InstallState state, string? detail = null) =>
        File.AppendAllText(log, JsonSerializer.Serialize(new ProgressEntry(id, state, detail)) + Environment.NewLine);

    /// <summary>Start the helper with <paramref name="args"/> and mirror its log into <paramref name="items"/> until it exits.</summary>
    public static async Task RunAsync(string args, string log, IReadOnlyList<InstallItem> items)
    {
        Process? helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = !App.TestMode, Verb = App.TestMode ? "" : "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            foreach (var i in items) { i.State = InstallState.Failed; i.Detail = "Administrator permission was declined."; }
            return;
        }
        if (helper == null) return;

        long read = 0;
        while (true)
        {
            bool exited = helper.HasExited;
            read = Apply(log, read, items);
            if (exited) break;
            await Task.Delay(300);
        }
        foreach (var i in items.Where(i => i.State is InstallState.Queued or InstallState.Installing))
        {
            i.State = InstallState.Failed;
            i.Detail = "The helper stopped unexpectedly.";
        }
    }

    static long Apply(string log, long offset, IReadOnlyList<InstallItem> items)
    {
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            long consumed = offset;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var entry = JsonSerializer.Deserialize<ProgressEntry>(line);
                consumed = fs.Position;
                var item = items.FirstOrDefault(i => i.App.Id == entry?.Id);
                if (item == null || entry == null) continue;
                item.State = entry.State;
                item.Detail = entry.Detail ?? "";
            }
            return consumed;
        }
        catch (IOException) { return offset; }
        catch (JsonException) { return offset; }
    }
}
