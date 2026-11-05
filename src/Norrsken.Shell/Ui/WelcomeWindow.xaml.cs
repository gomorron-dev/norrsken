using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>First boot: say hello, pick a look, pick apps, install them in the background.</summary>
public partial class WelcomeWindow : Window
{
    readonly Dictionary<ToggleButton, CatalogApp> _chips = new();
    readonly Dictionary<ToggleButton, string> _profiles = new(), _folders = new();
    readonly bool _hasOldWindows;
    FrameworkElement _current;

    /// <summary>Catalogue apps found on the previous Windows (preselected in the picker).</summary>
    internal HashSet<string> Detected { get; } = new();
    internal List<InstallItem> RestoreItems { get; } = new();

    public WelcomeWindow(bool appsOnly = false)
    {
        InitializeComponent();
        _current = HelloStep;
        _hasOldWindows = !appsOnly && WindowsOld.Exists && WindowsOld.Profiles().Count > 0;
        if (_hasOldWindows)
        {
            Detected.UnionWith(WindowsOld.DetectApps(AppCatalog.Optional));
            BuildRestore();
        }
        BuildThemes();
        BuildCatalogue();
        UpdateSelected();
        if (appsOnly) { HelloStep.Visibility = Visibility.Collapsed; AppsStep.Visibility = Visibility.Visible; _current = AppsStep; }
        Loaded += (_, _) => Enter(_current);
        if (App.Installer is { Running: true }) Loaded += (_, _) => ShowInstall(App.Installer);
    }

    // ───────────── steps ─────────────

    void SwitchTo(FrameworkElement next)
    {
        var prev = _current;
        _current = next;
        var shift = Shift(prev);
        Motion.To(shift, TranslateTransform.XProperty, -36, 180, Motion.InOut);
        Motion.To(prev, OpacityProperty, 0, 180, done: () =>
        {
            prev.Visibility = Visibility.Collapsed;
            next.Visibility = Visibility.Visible;
            Enter(next);
        });
    }

    static void Enter(FrameworkElement step)
    {
        Motion.From(step, OpacityProperty, 0, 1, 260);
        Motion.From(Shift(step), TranslateTransform.XProperty, 44, 0, 380, Motion.SoftSpring);
    }

    static TranslateTransform Shift(FrameworkElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }

    // ───────────── hello ─────────────

    void BuildThemes()
    {
        foreach (var t in Theme.All)
        {
            var rb = new RadioButton
            {
                GroupName = "WelcomeTheme", Tag = t.Name, Uid = t.Id, Style = (Style)FindResource("ThemeCard"),
                IsChecked = Theme.Current == t.Id, Content = SettingsWindow.PreviewTile(t.Id),
            };
            System.Windows.Automation.AutomationProperties.SetName(rb, t.Name + " theme");
            rb.Checked += (_, _) => App.SetTheme(t.Id);
            ThemeList.Children.Add(rb);
        }
    }

    void Hello_Continue(object sender, RoutedEventArgs e)
    {
        _ = SystemLook.ApplyAsync(Theme.Current); // first sign-in: lock screen and wallpaper match the chosen look
        SwitchTo(_hasOldWindows ? RestoreStep : AppsStep);
    }

    // ───────────── bring your files back ─────────────

    void BuildRestore()
    {
        var profiles = WindowsOld.Profiles();
        string preferred = profiles.FirstOrDefault(p => p.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
        foreach (var p in profiles)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("AppChip"), IsChecked = p == preferred, Content = ChipLabel(p) };
            chip.Checked += (_, _) => { foreach (var other in _profiles.Keys.Where(k => k != chip)) other.IsChecked = false; };
            chip.Unchecked += (_, _) => { if (_profiles.Keys.All(k => k.IsChecked != true)) chip.IsChecked = true; }; // exactly one
            _profiles[chip] = p;
            ProfileList.Children.Add(chip);
        }
        ProfileHeader.Visibility = ProfileList.Visibility = profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        void Folder(string key, string label, bool on)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("AppChip"), IsChecked = on, Content = ChipLabel(label) };
            _folders[chip] = key;
            FolderList.Children.Add(chip);
        }
        foreach (var k in WindowsOld.Known) Folder(k.Name, k.Label, true);
        Folder("other", "Other folders and files", true);
        Folder("appdata", "App data and game saves (advanced)", false);
    }

    static FrameworkElement ChipLabel(string text) =>
        new TextBlock { Text = text, FontSize = 14, Margin = new Thickness(8, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        string profile = _profiles.First(p => p.Key.IsChecked == true).Value;
        var chosen = _folders.Where(f => f.Key.IsChecked == true).Select(f => f.Value).ToHashSet();
        var plan = WindowsOld.Plan(profile, chosen, chosen.Contains("other"), chosen.Contains("appdata"));
        if (plan.Count == 0) { SwitchTo(AppsStep); return; }

        // Rows reuse the install item model: the "app" is the folder being brought back.
        RestoreItems.Clear();
        RestoreItems.AddRange(plan.Select(t => new InstallItem(new CatalogApp(t.Id, t.Label, "Files", InstallScope.Machine))));
        RestoreList.ItemsSource = RestoreItems;
        RestoreChoices.Visibility = Visibility.Collapsed;
        RestoreList.Visibility = Visibility.Visible;
        RestoreButton.Visibility = RestoreSkipButton.Visibility = Visibility.Collapsed;
        RestoreTitle.Text = "Bringing your files back";
        Motion.From(RestoreList, OpacityProperty, 0, 1, 260);

        string log = Elevated.NewLogPath("restore");
        string planFile = System.IO.Path.ChangeExtension(log, ".json");
        File.WriteAllText(planFile, System.Text.Json.JsonSerializer.Serialize(plan));
        await Elevated.RunAsync($"--restore-files \"{planFile}\" \"{log}\"", log, RestoreItems);

        int failed = RestoreItems.Count(i => i.State == InstallState.Failed);
        RestoreTitle.Text = failed == 0 ? "Your files are back" : "Most of your files are back";
        RestoreSubtitle.Text = failed == 0
            ? "Everything was copied and checked. Your old Windows still has the originals until Windows cleans it up."
            : $"{failed} folder{(failed == 1 ? "" : "s")} had files that couldn't be copied — the originals are still in {WindowsOld.Root}.";
        RestoreNextButton.Visibility = Visibility.Visible;
    }

    void RestoreSkip_Click(object sender, RoutedEventArgs e) => SwitchTo(AppsStep);

    // ───────────── apps ─────────────

    void BuildCatalogue()
    {
        foreach (var group in AppCatalog.ByCategory)
        {
            CategoryList.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("PageTitle"), Margin = new Thickness(2, 22, 0, 10) });
            var wrap = new WrapPanel();
            foreach (var app in group)
            {
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("AppChip"), IsChecked = app.Default || Detected.Contains(app.Id), Content = ChipContent(app),
                };
                System.Windows.Automation.AutomationProperties.SetName(chip, app.Name);
                chip.Checked += (_, _) => UpdateSelected();
                chip.Unchecked += (_, _) => UpdateSelected();
                _chips[chip] = app;
                wrap.Children.Add(chip);
            }
            CategoryList.Children.Add(wrap);
        }
        if (Detected.Count > 0)
            AppsSubtitle.Text += $" {Detected.Count} apps from your previous Windows are already ticked.";
    }

    /// <summary>Monogram tile + name; the tile's outline follows the chip's (inverting) foreground.</summary>
    static FrameworkElement ChipContent(CatalogApp app)
    {
        var tile = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = app.Monogram, FontWeight = FontWeights.SemiBold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        tile.SetBinding(Border.BorderBrushProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1),
        });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { tile, new TextBlock { Text = app.Name, FontSize = 14, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    internal ToggleButton? ChipFor(string id) => _chips.FirstOrDefault(c => c.Value.Id == id).Key;

    internal IReadOnlyList<CatalogApp> Selected => _chips.Where(c => c.Key.IsChecked == true).Select(c => c.Value).ToList();

    void UpdateSelected()
    {
        int n = Selected.Count;
        SelectedText.Text = n == 0 ? "Just the essentials" : n == 1 ? "1 app selected, plus essentials" : $"{n} apps selected, plus essentials";
    }

    void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var installer = new AppInstaller { Simulate = App.TestMode };
        App.Installer = installer;
        ShowInstall(installer);
        await installer.RunAsync(Selected);
    }

    // ───────────── installing ─────────────

    void ShowInstall(AppInstaller installer)
    {
        InstallList.ItemsSource = installer.Items;
        installer.Items.CollectionChanged += (_, _) => UpdateProgress(installer);
        installer.Finished += () => Dispatcher.Invoke(() => UpdateProgress(installer));
        foreach (var i in installer.Items) i.PropertyChanged += (_, _) => Dispatcher.InvokeAsync(() => UpdateProgress(installer));
        installer.Items.CollectionChanged += (_, args) =>
        {
            foreach (InstallItem i in args.NewItems ?? Array.Empty<InstallItem>())
                i.PropertyChanged += (_, _) => Dispatcher.InvokeAsync(() => UpdateProgress(installer));
        };
        if (_current != InstallStep) SwitchTo(InstallStep);
    }

    void UpdateProgress(AppInstaller installer)
    {
        int total = installer.Items.Count;
        int finished = installer.Items.Count(i => i.State is InstallState.Done or InstallState.Failed);
        double width = ((FrameworkElement)ProgressFill.Parent).ActualWidth * (total == 0 ? 0 : (double)finished / total);
        Motion.To(ProgressFill, WidthProperty, width, 380, Motion.Out);
        bool done = !installer.Running && total > 0;
        int failed = installer.FailedCount;
        RetryButton.Visibility = done && failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (done)
        {
            InstallTitle.Text = failed == 0 ? "You're all set" : "Almost there";
            InstallSubtitle.Text = failed == 0
                ? $"{installer.DoneCount} apps are ready."
                : $"{installer.DoneCount} installed, {failed} didn't — you can retry now or later from Settings.";
        }
    }

    async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (App.Installer is { Running: false } installer) await installer.RetryFailedAsync();
    }

    void Finish_Click(object sender, RoutedEventArgs e) => Finish();

    void Finish()
    {
        App.Settings.WelcomeDone = true;
        App.Settings.Save();
        Motion.To(Root, OpacityProperty, 0, 220, done: Close);
    }
}
