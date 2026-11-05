using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Ui;

/// <summary>"Install Norrsken" from the USB stick, inside the current Windows: keeps everything.</summary>
public partial class SetupWindow : Window
{
    internal List<InstallItem> Items { get; } = new();
    internal InPlaceSetup.Plan? LastPlan { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();
        if (InPlaceSetup.AlreadyInstalled) { ReinstallRow.Visibility = Visibility.Visible; Heading.Text = "Reinstall Norrsken"; }
        Options_Changed(this, new RoutedEventArgs());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.RoundCorners(new WindowInteropHelper(this).Handle);
    }

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        bool refresh = RefreshToggle.IsChecked == true;
        LicenceRow.Visibility = refresh ? Visibility.Visible : Visibility.Collapsed;
        // Refreshing Windows needs the user's own acceptance of Microsoft's licence terms.
        InstallButton.IsEnabled = !refresh || LicenceToggle.IsChecked == true;
    }

    void Licence_Click(object sender, RoutedEventArgs e)
    {
        const string terms = "https://www.microsoft.com/useterms";
        if (App.TestMode) { App.TestLaunches.Add(terms); return; }
        Process.Start(new ProcessStartInfo(terms) { UseShellExecute = true });
    }

    internal async void Install_Click(object sender, RoutedEventArgs e)
    {
        var plan = InPlaceSetup.MakePlan(RefreshToggle.IsChecked == true, ReinstallRow.Visibility == Visibility.Visible && ReinstallToggle.IsChecked == true);
        LastPlan = plan;
        Items.Clear();
        Items.AddRange(InPlaceSetup.Steps.Where(s => (s.Id != "refresh" || plan.RefreshWindows) && (s.Id != "stop" || plan.Reinstall))
            .Select(s => new InstallItem(new CatalogApp(s.Id, s.Label, "Setup", InstallScope.Machine))));
        StepList.ItemsSource = Items;
        Options.Visibility = Visibility.Collapsed;
        StepList.Visibility = Visibility.Visible;
        InstallButton.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        Heading.Text = "Installing Norrsken";

        string log = Elevated.NewLogPath("setup");
        string planFile = System.IO.Path.ChangeExtension(log, ".json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        await Elevated.RunAsync($"--setup-apply \"{planFile}\" \"{log}\"", log, Items);

        bool ok = Items.All(i => i.State == InstallState.Done);
        Heading.Text = ok ? "Norrsken is installed" : "Norrsken couldn't finish installing";
        Subtitle.Text = !ok ? "Nothing on your PC was removed. Check the steps above, then try again."
            : plan.RefreshWindows ? "Windows Setup is refreshing Windows now. When it's done, Norrsken will be your desktop."
            : "Start it now, or it will start by itself the next time you sign in.";
        StartButton.Visibility = ok && !plan.RefreshWindows ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = "Close";
        CancelButton.Visibility = Visibility.Visible;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        string exe = Path.Combine(LastPlan!.Target, "Norrsken.Shell.exe");
        if (App.TestMode) App.TestLaunches.Add(exe);
        else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); // normal rights, not elevated
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
