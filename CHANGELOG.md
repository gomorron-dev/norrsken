# Changelog

Installed copies update themselves to the newest version (see [Updates](README.md#updates)).

## 0.4.1

- Updates work like they do in proper software now. Norrsken downloads a new version in the background, checks it, and then asks if you want to restart to install it. Say later and it installs the next time you sign in.
- While it installs you get an update screen showing each step, and afterwards Norrsken tells you what's new and offers to restart your PC.
- Settings › Updates shows exactly where things are: checking, downloading (with progress), ready to install, or what went wrong.
- Installing an update copies only the files that changed, retries files that are briefly in use, and keeps your sounds.

## 0.4.0

New:
- A **Customise** page in Settings. Pick an accent colour (one of eight, or any hex code), choose how round the corners are, resize the dock, change how much the icons grow and how see-through the dock is, and choose how things move (natural, springy, smooth or snappy) and how fast. Changes show up right away in every open window and fade in instead of snapping.
- **Interface sounds**, off until you turn them on: opening the launcher, clicking in the dock, switches, notifications, screenshots, deleting and restoring, errors and start-up. Hover and volume ticks stay off unless you want them. You can mute or preview each one, or use your own folder of sounds.

Fixed:
- Sound devices and per-app volume stopped working after the first time you used them.
- Norrsken's apps (Files, Notes, Bin, Settings, Terminal, Task Manager) and the screenshot tool closed on unexpected errors. They log the error and keep going now.
- If measuring a folder's size or the temp files failed, the label got stuck. It says so now.
- The Bin in the dock missed deletions on drives that hadn't had anything deleted before Norrsken started.
- Open Norrsken apps followed theme changes but ignored the other look settings.

## 0.3.2

- **The Bin.** A new app and a spot at the end of the dock. Drag files onto it to delete them, or open it to see what you've deleted, sorted by day. Ctrl+Z brings back the last delete, putting something back recreates its folder if needed, pinned items survive the automatic clean-up, and you can shred instead of delete. It also shows how much space you've freed this month.
- Icons are sharper: they load at a higher resolution and scale more smoothly, so they don't go blurry when shown big.
- Removing Windows' frames from small icons no longer eats icons that are actually light squares (like Android Studio's).
- Pinned apps and folders in the launcher sit in even rows, file results show readable paths, and app cache folders no longer show up in search.
- Now playing shows the app's proper name ("Google Chrome" rather than an internal ID).
- Flickerbox installs more reliably: it's closed before being updated, left alone if it's already up to date, and checked after copying.

## 0.3.1

- Norrsken has its own icon now.
- The frames Windows draws around small icons are gone, and apps with a missing icon get a tile with their first letter instead of a blank page.
- Settings switches no longer slide into place every time a page opens.
- Task Manager's graphs look a lot better: proper size, a grid, a soft fill and a dot on the current value.
- The selected filter in the launcher is always readable.
- The launcher shows your full name if Windows has one.

## 0.3.0

- Animations run at your monitor's full refresh rate, and Norrsken is never slowed down by Windows' power saving.
- Screenshots no longer flash white, and the toolbar shows up on the monitor you're using.
- Norrsken's title bars skip apps that draw their own, like Chrome and Discord.
- Quick Settings can't cover the dock anymore, and the Windows key brings up the dock with the launcher.
- Text and password boxes match the theme.
- Flickerbox shows up in the launcher.
- Norrsken can update itself from GitHub (Settings › Updates).
- The screenshot tool can be installed on its own on any Windows PC.
- Updating a USB stick takes seconds once it's been built the first time.
