using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's package identity (see os\identity): a signed sparse package registered with
/// Norrsken's own folder as its external location. Windows then treats Norrsken.Shell.exe as that
/// package, which unlocks the notification listener. The certificate is trusted machine-wide by
/// the installer (admin); each account registers the package itself on first start, then Norrsken
/// restarts once so the identity applies.
/// </summary>
public static class PackageIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);
    const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    static string Folder => Path.Combine(AppContext.BaseDirectory, "identity");
    static string Msix => Path.Combine(Folder, "Norrsken.msix");
    static string Cer => Path.Combine(Folder, "norrsken.cer");

    public static bool Has
    {
        get { uint len = 0; return GetCurrentPackageFullName(ref len, null) != APPMODEL_ERROR_NO_PACKAGE; }
    }

    public static bool Bundled => File.Exists(Msix) && File.Exists(Cer);

    public static bool CertificateTrusted()
    {
        if (!File.Exists(Cer)) return false;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(Cer);
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Admin only (installer, first-sign-in system setup).</summary>
    public static string TrustCertificate()
    {
        if (!File.Exists(Cer)) return "no identity package in this build";
        using var cert = X509CertificateLoader.LoadCertificateFromFile(Cer);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        return "notification access enabled";
    }

    /// <summary>
    /// At dock start: register the package for this account if it isn't yet. Returns true when Norrsken
    /// should restart to pick the identity up (at most twice, so it can never loop).
    /// </summary>
    public static async Task<bool> EnsureAsync()
    {
        if (App.TestMode || Has || !Bundled || App.Settings.IdentityAttempts >= 2) return false;
        if (!CertificateTrusted()) { Log.Write("Identity: certificate not trusted yet (installer step pending)"); return false; }
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var options = new Windows.Management.Deployment.AddPackageOptions { ExternalLocationUri = new Uri(AppContext.BaseDirectory) };
            var result = await pm.AddPackageByUriAsync(new Uri(Msix), options);
            App.Settings.IdentityAttempts++;
            App.Settings.Save();
            if (result.ExtendedErrorCode != null) { Log.Write("Identity registration failed: " + result.ErrorText); return false; }
            Log.Write("Identity registered; restarting Norrsken to apply it");
            return true;
        }
        catch (Exception ex) { Log.Write("Identity registration failed: " + ex.Message); return false; }
    }

    /// <summary>Starts a fresh dock that waits for this one to exit, then quits this one.</summary>
    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}") { UseShellExecute = false });
        App.Quit();
    }
}
