using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Norrsken.Shell.Core;

public sealed class NotificationItem : INotifyPropertyChanged
{
    public uint Id { get; init; }
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset Time { get; init; }
    ImageSource? _icon;
    public ImageSource? AppIcon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(AppIcon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Your Windows notifications, read through the notification listener (needs Norrsken's package
/// identity and your one-time permission). Polled every 2 s while Norrsken runs.
/// </summary>
public static class Notifications
{
    public enum State { Starting, NeedsIdentity, Denied, Ready }

    public static ObservableCollection<NotificationItem> Items { get; } = new();
    public static State Status { get; private set; } = State.Starting;
    public static event Action? Changed;

    static UserNotificationListener? _listener;
    static readonly DispatcherTimer Poll = new() { Interval = TimeSpan.FromSeconds(2) };
    static bool _refreshing;

    public static async Task StartAsync()
    {
        if (App.TestMode) { Status = State.Ready; return; }
        if (!PackageIdentity.Has) { Status = State.NeedsIdentity; Changed?.Invoke(); return; }
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed) { Status = State.Denied; Changed?.Invoke(); return; }
            Status = State.Ready;
            Poll.Tick += async (_, _) => await RefreshAsync();
            Poll.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { Log.Write("Notifications unavailable: " + ex.Message); Status = State.Denied; Changed?.Invoke(); }
    }

    static async Task RefreshAsync()
    {
        if (_listener == null || _refreshing) return;
        _refreshing = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = list.Select(n => n.Id).ToHashSet();
            bool changed = false;
            foreach (var gone in Items.Where(i => !ids.Contains(i.Id)).ToList()) { Items.Remove(gone); changed = true; }
            foreach (var n in list.Where(n => Items.All(i => i.Id != n.Id)).OrderBy(n => n.CreationTime))
            {
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                string app = "", appId = "";
                try { app = n.AppInfo.DisplayInfo.DisplayName; appId = n.AppInfo.AppUserModelId; } catch (Exception) { }
                var item = new NotificationItem
                {
                    Id = n.Id, AppId = appId, AppName = app.Length > 0 ? app : "App", Time = n.CreationTime,
                    Title = texts.FirstOrDefault() ?? "", Body = string.Join("\n", texts.Skip(1)),
                };
                Items.Insert(0, item);
                changed = true;
                _ = LoadIconAsync(n, item);
            }
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("Notifications refresh: " + ex.Message); }
        finally { _refreshing = false; }
    }

    static async Task LoadIconAsync(UserNotification n, NotificationItem item)
    {
        try
        {
            var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)).OpenReadAsync();
            using var s = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit(); img.StreamSource = ms; img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
            item.AppIcon = img;
        }
        catch (Exception) { }
    }

    public static void Dismiss(NotificationItem item)
    {
        try { _listener?.RemoveNotification(item.Id); } catch (Exception) { }
        Items.Remove(item);
        Changed?.Invoke();
    }

    public static void ClearAll()
    {
        try { _listener?.ClearNotifications(); } catch (Exception) { }
        Items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Clicking a notification opens the app that sent it.</summary>
    public static void Open(NotificationItem item)
    {
        if (App.TestMode) { App.TestLaunches.Add("notification:" + item.AppName); return; }
        if (item.AppId.Length > 0)
        {
            try { Unelevated.Start(@"shell:AppsFolder\" + item.AppId); }
            catch (Exception ex) { Log.Write("Open from notification failed: " + ex.Message); }
        }
        Dismiss(item);
    }

    /// <summary>UI tests: fill the centre with sample notifications.</summary>
    internal static void TestAdd(string app, string title, string body, int minutesAgo)
    {
        Items.Insert(0, new NotificationItem { Id = (uint)(Items.Count + 1), AppName = app, Title = title, Body = body, Time = DateTimeOffset.Now.AddMinutes(-minutesAgo) });
        Changed?.Invoke();
    }
}
