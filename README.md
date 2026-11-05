<div align="center">

<img src="docs/images/logo.png" width="96" alt="Norrsken logo">

# Norrsken

**A calm, monochrome, dock-first desktop for Windows 11.**<br>
*Norrsken* is Swedish for the northern lights.

[![Build](https://github.com/gomorron-dev/norrsken/actions/workflows/build.yml/badge.svg)](https://github.com/gomorron-dev/norrsken/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/gomorron-dev/norrsken?label=release&color=2b2b2e)](https://github.com/gomorron-dev/norrsken/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/gomorron-dev/norrsken/total?color=2b2b2e)](https://github.com/gomorron-dev/norrsken/releases)
![Windows 11](https://img.shields.io/badge/Windows-11-2b2b2e?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-2b2b2e?logo=dotnet&logoColor=white)

[Install](#install) · [What it replaces](#what-it-replaces) · [Updates](#updates) · [Build](#build-and-test) · [Releases](https://github.com/gomorron-dev/norrsken/releases)

<br>

<picture>
  <source media="(prefers-color-scheme: light)" srcset="docs/images/hero-light.png">
  <img src="docs/images/hero.png" alt="Norrsken: the launcher, Quick Settings and the dock" width="100%">
</picture>

</div>

<br>

Norrsken replaces the Windows UI you look at (the taskbar, Start, Alt+Tab, the notification
centre, File Explorer, Settings, Task Manager, Terminal, Notepad and the Snipping Tool) with
one quiet, consistent design. Windows itself stays underneath, so every `.exe`, game, launcher
and anti-cheat keeps working exactly as before.

- **Monochrome by design.** Norrsken is greys only; colour comes from your apps' icons.
- **Motion that feels physical.** Magnification, springs and squash-and-stretch, at your display's full refresh rate.
- **Yours, not Microsoft's.** No ads, no suggestions, no web results in search.
- **Keeps itself current.** Installed copies update from this repository's releases.

## The dock

<img src="docs/images/dock.png" alt="The dock with magnification" width="100%">

Icons magnify under the pointer and their neighbours glide apart. Launching bounces, running
apps get a dot that stretches into a bar for the app in front, and what's playing shows up with
art and controls. It hides itself for full-screen games, and the Win key always brings it back.

## Launcher and Quick Settings

<table>
<tr>
<td width="62%"><img src="docs/images/launcher.png" alt="The launcher"></td>
<td width="38%"><img src="docs/images/quick.png" alt="Quick Settings"></td>
</tr>
<tr>
<td>Frequent apps on top, folders or A–Z below, and one search for apps, files and settings. It understands initials (<code>vsc</code>) and learns what you open.</td>
<td>Volume, brightness, Wi-Fi, Bluetooth, night light, focus timer and now playing, one click from the clock.</td>
</tr>
</table>

## Settings that don't send you back to Windows

<img src="docs/images/settings.png" alt="Norrsken Settings" width="100%">

Display, sound, network, Bluetooth, apps, storage, accounts, privacy and updates, all native.
Nothing opens Windows' Settings app.

## Eight themes

<img src="docs/images/themes.png" alt="The dock in Graphite, Paper, Slate and Sand" width="100%">

Graphite, Ash, Slate, Ink, Paper, Fog, Sand and High Contrast. The wallpaper, lock screen
and Windows' own light or dark mode follow the theme.

## What it replaces

| Windows | Norrsken | Keys |
|---|---|---|
| Taskbar | The dock | Win+T, Win+1…9 |
| Start | Launcher | Win, Win+S |
| Alt+Tab / Task View | Switcher and window overview | Alt+Tab, Win+Tab |
| Action centre | Quick Settings | Win+A |
| Notification centre | Today: notifications, calendar, week | Win+N |
| File Explorer | Files: tabs, Quick Look, search | Win+E |
| Settings | Norrsken Settings | Win+I |
| Task Manager | Processes, live graphs, startup apps | Ctrl+Shift+Esc |
| Windows Terminal | Norrsken Terminal (ConPTY, tabs) | |
| Run · Win+X · Project · Clipboard | Norrsken versions of each | Win+R, Win+X, Win+P, Win+V |
| Snipping Tool | Screenshot: area, window, screen or free-form on any monitor | PrtScn, Win+Shift+S |
| Photos, Media Player | [Flickerbox](#flickerbox) | |
| Title bars | Centred title and pebble buttons (apps that draw their own are left alone) | |
| Desktop right-click | Norrsken's menu | |

Shortcuts that act on windows (Win+D, snapping, virtual desktops) stay Windows' own.

## Install

<details open>
<summary><b>On a new PC: Norrsken OS</b></summary>

<br>

Turn a Windows 11 installation stick into a Norrsken OS stick, then boot from it. From an
elevated PowerShell:

```powershell
.\os\publish.ps1
.\os\make-usb.ps1 -Drive E: -NoBackup
```

The first run builds Norrsken into the Windows image (about an hour). After that the same
command only syncs what changed, which takes seconds. Details in [os/README.md](os/README.md).

</details>

<details>
<summary><b>On the Windows you already have</b></summary>

<br>

Open the stick and run `Install Norrsken.cmd`. Nothing is erased; choose *Reinstall* to replace
an older Norrsken.

</details>

<details>
<summary><b>Just the screenshot tool, on any Windows PC</b></summary>

<br>

```powershell
.\os\payload\Norrsken\Norrsken.Shell.exe --install-capture
```

PrtScn and Win+Shift+S open Norrsken's screenshot tool instead of Windows'. Remove it from
Windows' app list, or with `--uninstall-capture`.

</details>

## Updates

Norrsken checks the [latest release](https://github.com/gomorron-dev/norrsken/releases/latest)
a couple of minutes after it starts and every six hours after that. With *Settings › Updates ›
Install updates automatically* on (the default) it downloads the release, checks its SHA-256,
replaces itself, updates Flickerbox and restarts the dock.

<details>
<summary>Publishing a release</summary>

<br>

Bump `<Version>` in `src/Norrsken.Shell/Norrsken.Shell.csproj`, then:

```powershell
.\os\release.ps1 -Notes "What changed"
```

It builds the payload with the latest Flickerbox, zips it as `Norrsken-<version>.zip` and
creates the GitHub release `v<version>`.

</details>

## Flickerbox

Norrsken's photo, video and music app lives in its own project. `publish.ps1` bundles its latest
build; Norrsken installs it to `Program Files\Flickerbox`, makes it the default for media files,
puts it in the launcher and updates it along with Norrsken.

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

The pictures in this README are drawn by Norrsken itself, offscreen, with demo data:

```powershell
src/Norrsken.Shell/bin/Release/net10.0-windows10.0.22621.0/Norrsken.Shell.exe --render-showcase docs/images
```

<details>
<summary>Repository layout</summary>

```
src/Norrsken.Shell/
  Interop/      Win32 and shell P/Invoke, icons, window identity, hidden-desktop runner
  Core/         settings, themes, motion, hooks, system controls, updater, integration
  Ui/           dock, launcher, quick settings, Today, Files, Settings, Terminal, Task Manager, title bars
  Showcase.cs   the README and release pictures
  UiTest*.cs    interaction tests (hidden desktop)
os/
  publish.ps1   builds payload\Norrsken (self-contained) with the identity package and Flickerbox
  make-usb.ps1  turns a Windows 11 install stick into a Norrsken OS stick
  build-iso.ps1 the same as an ISO
  release.ps1   publishes a GitHub release that installed copies update from
  lib/          what gets built into the Windows image
docs/           architecture, status and the pictures
```

</details>

## Personal use

Norrsken OS media contains Microsoft's Windows, licensed to you: use it on your own devices and
don't share the stick or ISO. This repository holds only Norrsken's own code and art.
