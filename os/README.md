# Norrsken OS: Windows edition

A bootable ISO that installs Windows 11 with Norrsken as the desktop from the first sign-in:
- the monochrome dock, launcher, Quick Settings, previews and icons
- the Windows taskbar hidden
- the Welcome screen, which installs your apps

Because Windows is underneath, **every .exe, game and launcher runs**: Steam, Epic, anti-cheat
games, Claude Desktop and Claude Code. Norrsken replaces the Windows UI you see: Start (launcher,
Win key), the taskbar (dock), Alt+Tab, Win+N (Today panel with notifications and calendar),
File Explorer (Files, Win+E), Settings (Win+I), Notepad (Notes), Snipping Tool (Screenshot,
PrtScn / Win+Shift+S) and, through Flickerbox, Photos and Media Player. Every other shortcut is
Windows' own.

> **Personal use only.** The ISO contains Microsoft's Windows files, licensed to you. Use it on
> your own devices and don't share it. A laptop that shipped with Windows 11 activates
> automatically when you install the matching edition (usually *Windows 11 Home*).

## What's in the image

| | |
|---|---|
| Desktop | Norrsken in `C:\Program Files\Norrsken`, started for every user at sign-in |
| First sign-in | Welcome: choose a look, then pick apps. 51 apps from your current PC are preselected, and 19 essentials always install: every Visual C++ runtime 2005–2022, DirectX, .NET 6/8/10, GameInput, PowerShell 7, Terminal, 7-Zip |
| Look | Dark mode, Norrsken wallpaper, Windows taskbar hidden (comes back automatically if Norrsken stops) |
| Identity | Settings › System › About shows "Norrsken OS" and its logo |
| Default apps | Flickerbox (bundled) for every image, video, audio, GIF and playlist format; Notes for text. Set before the first sign-in through Windows' default-associations policy, so they hold for every account |
| Notifications | Norrsken's signed identity package (`identity\`) is trusted and registered so the Today panel can read your notifications (you allow it once) |
| Removed | Clipchamp, News, Solitaire, Office hub, Power Automate, Feedback Hub, To Do, Notepad, Snipping Tool; Photos, Media Player and Film & TV once Flickerbox is in. All reinstallable from the Store. Use `-KeepStockApps` to keep the first group. |

## Make a Norrsken USB stick (easiest)

Start from a Windows 11 installation stick, for example one made with Microsoft's Media
Creation Tool. Then, from an **elevated** PowerShell in this folder:

```powershell
.\publish.ps1                       # Norrsken + identity package + Flickerbox into payload\
.\make-usb.ps1 -Drive E: -NoBackup  # -NoBackup skips the safety copy of the stick
```

It refuses anything that isn't a removable USB stick holding a Windows installer, and backs the
stick up to `C:\NorrskenBackup\usb-<time>` before changing anything. Home, Home Single Language
and Pro each get Norrsken built in. The result is written back in FAT32-sized parts, so the stick
still boots on UEFI and BIOS. It also adds *Install Norrsken*, relabels the stick NORRSKEN and
checks every written file by SHA-256. A log goes to `out\make-usb.log`.

### Updating the stick

Run the same two commands again. The stick remembers a fingerprint of everything that shapes the
Windows image (`lib\customize.ps1`, `assets\`, `-KeepStockApps`) in `sources\norrsken-recipe.txt`.
While that matches, `make-usb.ps1` only syncs `<stick>\Norrsken`, copying just the files that
changed: seconds, not an hour. Windows Setup installs Norrsken from that folder (SetupComplete
copies it over the one inside the image), and so does *Install Norrsken*.

A change to the image recipe rebuilds the image automatically; `-Full` forces a rebuild.

## Releases (updating installed copies)

Installed Norrsken updates itself from the GitHub releases of `gomorron-dev/norrsken`. Bump
`<Version>` in `src\Norrsken.Shell\Norrsken.Shell.csproj`, then run `.\release.ps1 -Notes "…"`.
It needs the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`).

## Build an ISO

Run from an **elevated** PowerShell in this folder:

1. Download the Windows 11 ISO from <https://www.microsoft.com/software-download/windows11>.
2. Install the ADK Deployment Tools (provides `oscdimg`):
   ```powershell
   winget install Microsoft.WindowsADK
   ```
3. Publish Norrsken (with its identity package and Flickerbox) and render its art (no admin needed):
   ```powershell
   .\publish.ps1
   .\payload\Norrsken\Norrsken.Shell.exe --render-branding .\assets
   ```
   `publish.ps1` needs the Windows SDK (makeappx, signtool) and Flickerbox's `build\stage` folder.
4. Build from an ISO, or from a Windows USB stick or its backup folder:
   ```powershell
   .\build-iso.ps1 -WindowsSource <path-to-Win11.iso-or-folder>
   ```
   The output is `out\Norrsken-0.3.0-x64.iso` plus a `.sha256` file.

## Install on the laptop

Write the ISO to a USB stick (16 GB or more) with **Rufus**. It handles the >4 GB install image
on UEFI. Then choose a route.

### A. Keep everything (recommended if the laptop already runs Windows 10/11)

1. In the laptop's current Windows, open the USB stick and run **Install Norrsken**.
2. Click **Install**. Nothing is erased: every file, app, setting and the Windows activation
   stay, and Norrsken becomes the desktop.
3. *Optional:* "Also refresh Windows itself" runs Windows Setup in place from the stick, keeping
   files and apps. It takes about an hour, needs you to accept Microsoft's licence terms, and only
   works when the stick's language and edition match the installed Windows.

### B. Clean install (keeps your files, not your installed apps)

1. Back up anything irreplaceable to an external drive or the cloud first.
2. Boot from the USB stick (often F12, F2 or Esc at power-on). In Windows Setup choose
   **Custom** and install onto the Windows partition **without formatting or deleting it**.
   Windows then parks the old system in `C:\Windows.old`.
3. Sign in. Norrsken's Welcome screen finds the old Windows and offers **Bring your files back**:
   Desktop, Documents, Downloads, Pictures, Videos, Music and your other folders.
   - It copies them (never moves or overwrites anything) and checks every file afterwards.
   - App data and game saves are opt-in.
   - Windows deletes `Windows.old` by itself after about 10 days, so do this on the first day.
4. The app picker then preselects the apps it recognised on the old system, alongside your usual
   ones, and reinstalls them.

## Recovery

- **Norrsken stops or misbehaves:** the Windows taskbar comes back by itself. Ctrl+Alt+N opens the
  launcher. Settings › System turns off "Start Norrsken when you sign in".
- **Remove Norrsken entirely:** delete `C:\Program Files\Norrsken` and the `Norrsken` value under
  `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`. What's left is plain Windows 11.
- **Reinstall:** boot the USB stick again. Windows' own "Reset this PC" also works.
