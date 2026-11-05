# Norrsken

A calm, monochrome, dock-first desktop for Windows 11. *Norrsken* is Swedish for the northern lights.

![The Norrsken dock](docs/images/dock.png)

Norrsken replaces the Windows UI you look at: the taskbar, Start, Alt+Tab, the notification
centre, File Explorer, Settings, Task Manager, Terminal, Notepad and the Snipping Tool. Windows
itself stays underneath, so every `.exe`, game, launcher and anti-cheat keeps working. It ships
two ways:

- **Norrsken OS**: a Windows 11 install stick with Norrsken built in, from the first sign-in.
- **Install Norrsken**: added to the Windows you already have; nothing is erased.

Installed copies update themselves from this repository's [releases](https://github.com/gomorron-dev/norrsken/releases).

## What it replaces

| Windows | Norrsken | Keys |
|---|---|---|
| Taskbar | The dock: magnifying icons, bounce on launch, running dots, now playing, clock | Win+T, Win+1…9 |
| Start | Launcher: frequent apps, folders, A–Z, search with filters | Win, Win+S |
| Alt+Tab / Task View | Switcher and window overview | Alt+Tab, Win+Tab |
| Action centre | Quick Settings: volume, brightness, Wi-Fi, Bluetooth, night light, focus, media | Win+A |
| Notification centre | Today: notifications, calendar, week view | Win+N |
| File Explorer | Files: tabs, Quick Look, search | Win+E |
| Settings | Norrsken Settings, fully standalone (display, sound, network, Bluetooth, apps, accounts, updates…) | Win+I |
| Task Manager | Processes, performance graphs, startup apps | Ctrl+Shift+Esc |
| Windows Terminal | Norrsken Terminal (ConPTY, tabs) | |
| Run, Win+X, Project, clipboard | Norrsken versions of each | Win+R, Win+X, Win+P, Win+V |
| Snipping Tool | Screenshot: area, window, screen or free-form across every monitor; Edit opens Flickerbox | PrtScn, Win+Shift+S |
| Photos, Media Player | [Flickerbox](#flickerbox) | |
| Title bars | Centred title, pebble buttons on the right (apps that draw their own are left alone) | |
| Desktop right-click | Norrsken's menu | |

Everything animates at your display's full refresh rate, and *Reduce motion* turns every
animation into an instant change.

## Install

**On a new PC (Norrsken OS):** make a stick from a Windows 11 installation stick, then boot
from it. See [os/README.md](os/README.md).

```powershell
.\os\publish.ps1
.\os\make-usb.ps1 -Drive E: -NoBackup   # elevated
```

The first run rebuilds the Windows image on the stick (around an hour). After that, running it
again only syncs the Norrsken files that changed, which takes seconds.

**On the Windows you already have:** open the stick and run `Install Norrsken.cmd`. Choose
*Reinstall* to replace an older Norrsken.

**Just the screenshot tool, on any Windows PC:**

```powershell
.\os\payload\Norrsken\Norrsken.Shell.exe --install-capture     # --uninstall-capture to remove
```

## Updates

Norrsken checks this repository's latest release a couple of minutes after it starts and then
every six hours. With *Settings › Updates › Install updates automatically* on (the default) it
downloads the release, checks its SHA-256, replaces itself in `Program Files\Norrsken`, updates
Flickerbox and restarts the dock. *Check now* does the same on demand.

To publish a release, bump `<Version>` in `src/Norrsken.Shell/Norrsken.Shell.csproj`, then:

```powershell
.\os\release.ps1 -Notes "What changed"
```

It builds the payload (with the latest Flickerbox build), zips it as `Norrsken-<version>.zip`
and creates the GitHub release `v<version>` with the GitHub CLI.

## Flickerbox

Norrsken's photo, video and music app lives in its own project. `publish.ps1` bundles its latest
build (`..\..\! Flickerbox\build\stage`) into `payload\Norrsken\apps\Flickerbox`; Norrsken
installs it to `Program Files\Flickerbox`, makes it the default for media files and updates it
along with Norrsken.

## Build and test

Needs the .NET 10 SDK on Windows.

```powershell
dotnet build src/Norrsken.Shell -c Release
```

The interaction tests drive every window on a private hidden desktop, so they never touch your
screen, and write screenshots plus `uitest.json`:

```powershell
src/Norrsken.Shell/bin/Release/net10.0-windows10.0.22621.0/Norrsken.Shell.exe --uitest-isolated uitest-out
```

## Layout

```
src/Norrsken.Shell/
  Interop/   Win32 and shell P/Invoke, icons, window identity, hidden-desktop runner
  Core/      settings, themes, motion, keyboard and mouse hooks, system controls, updater, integration
  Ui/        dock, launcher, quick settings, Today, Files, Settings, Terminal, Task Manager, title bars
  UiTest*.cs interaction tests (hidden desktop)
os/
  publish.ps1    builds payload\Norrsken (self-contained) with the identity package and Flickerbox
  make-usb.ps1   turns a Windows 11 install stick into a Norrsken OS stick (quick sync after the first time)
  build-iso.ps1  the same as an ISO
  release.ps1    publishes a GitHub release that installed copies update from
  lib/           what gets built into the Windows image
docs/            architecture and status notes
```

## Personal use

Norrsken OS media contains Microsoft's Windows, licensed to you: use it on your own devices
and don't share the stick or ISO. This repository holds only Norrsken's own code and art.
