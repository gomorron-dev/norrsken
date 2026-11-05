# Changelog

Installed copies update themselves to the newest release; see [Updates](README.md#updates).

## 0.3.2

- **The Bin**: a new Norrsken app and dock item. Drop files on it to delete them; open it to see what you deleted, grouped by day, with a preview. Undo the last delete in one go, put things back (rebuilding a missing folder) or somewhere else, keep items safe from auto-emptying, empty automatically after 7/30/90 days, shred instead of delete, and see how much space you've freed this month. Right-click it in the dock to undo or empty.
- **Sharper icons**: icons load at 128 px (256 px in previews) and every image scales with the high-quality filter, so nothing goes soft when shown large or on high-DPI screens.
- **Icons keep their real look**: frame removal only strips Windows' translucent frame line, so apps whose icon is a light rounded square (Android Studio and others) stay intact.
- **Launcher**: pinned apps and folders sit in balanced rows (eight pins are two rows of four, not seven and one); file results show friendly paths ("Documents › … › Launcher"); app caches (blob_storage, Session Storage, GPUCache…) no longer clutter search.
- **Now playing** shows the app's real name ("Google Chrome", "Spotify") instead of an internal ID.
- **Flickerbox installs reliably**: a running Flickerbox is closed before updating, an identical install is left alone, the copy is verified, and a missing WebView2 runtime is reported.
## 0.3.1

- Norrsken has its own app icon: the aurora mark, in Task Manager, the Apps list and Alt+Tab.
- App icons: Windows' frames around small icons are removed in the launcher and the dock, and apps whose icon is missing (Steam and Epic shortcuts, broken links) get a Norrsken tile with their initial instead of a blank page.
- Settings switches no longer slide into place every time a page opens.
- Task Manager graphs draw at their real size with a grid, a fading fill, smooth curves and a dot on the current value.
- Launcher chips: the selected chip is always readable.
- The account name in the launcher is your display name when Windows has one.

## 0.3.0

- Animations run at the display's full refresh rate (120/144/180 Hz…); Norrsken is never power-throttled and prefers the fast GPU.
- Screenshots: a quiet outline instead of the white flash; the toolbar sits on the monitor you're using.
- Norrsken title bars skip apps that draw their own (Chrome, Edge, Electron apps, Explorer, Store apps).
- Quick Settings can no longer cover the dock; the Win key brings the dock up with the launcher, even in full screen.
- Text and password fields match the theme.
- Flickerbox gets a launcher shortcut.
- Self-updates from GitHub releases (Settings › Updates).
- Norrsken Capture: the screenshot tool on its own, for any Windows PC.
- USB sticks update in seconds once built.