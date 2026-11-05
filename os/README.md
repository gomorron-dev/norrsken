# Norrsken OS

This folder turns a regular Windows 11 installer into one that installs Windows with Norrsken
already set up. From the first sign-in you get the dock, the launcher and the rest, the Windows
taskbar is hidden, and a welcome screen helps you install your apps.

Since it's still Windows underneath, everything runs: Steam, Epic, games with anti-cheat, whatever
you had before. Norrsken just replaces what you look at: Start, the taskbar, Alt+Tab, the
notification centre, File Explorer, Settings, Notepad, the Snipping Tool and (through Flickerbox)
Photos and Media Player.

> **Personal use only.** The stick or ISO contains Microsoft's Windows, licensed to you. Use it on
> your own machines and don't pass it around. A laptop that came with Windows 11 activates by itself
> as long as you install the same edition (usually *Windows 11 Home*).

## What ends up on the PC

| | |
|---|---|
| Desktop | Norrsken in `C:\Program Files\Norrsken`, starting for every user at sign-in |
| First sign-in | A welcome screen where you pick a look and the apps you want. Apps from your old PC are ticked already, and the basics always get installed: every Visual C++ runtime from 2005 to 2022, DirectX, .NET 6/8/10, GameInput, PowerShell 7, Terminal and 7-Zip |
| Look | Dark mode, a Norrsken wallpaper, and the Windows taskbar hidden (it comes back by itself if Norrsken ever stops) |
| About this PC | Settings › System › About says "Norrsken OS" and shows the logo |
| Default apps | Flickerbox for images, video, music, GIFs and playlists, and Notes for text, set up for every account before anyone signs in |
| Notifications | Norrsken's own signed package is trusted and registered, so the Today panel can show your notifications (you allow it once) |
| Removed | Clipchamp, News, Solitaire, the Office hub, Power Automate, Feedback Hub, To Do, Notepad and the Snipping Tool, plus Photos, Media Player and Films & TV once Flickerbox is in. All of them can be reinstalled from the Store. Add `-KeepStockApps` to keep the first group. |

## Making a USB stick (the easy way)

Start with a normal Windows 11 install stick, like the one Microsoft's Media Creation Tool makes.
Then, from PowerShell running as administrator in this folder:

```powershell
.\publish.ps1                       # builds Norrsken, Flickerbox and the sounds into payload\
.\make-usb.ps1 -Drive E: -NoBackup  # -NoBackup skips copying the stick's old contents first
```

It won't touch anything that isn't a removable USB stick with a Windows installer on it, and (unless
you pass `-NoBackup`) it copies the stick to `C:\NorrskenBackup\usb-<time>` before changing anything.
Norrsken gets built into the Home, Home Single Language and Pro editions, and the image is written
back in pieces small enough for FAT32, so the stick still boots on both UEFI and older BIOS
machines. It also adds *Install Norrsken*, renames the stick to NORRSKEN and checks every file it
wrote. The log ends up in `out\make-usb.log`.

### Updating the stick

Just run the same two commands again. The stick remembers what its Windows image was built from
(`lib\customize.ps1`, `assets\` and `-KeepStockApps`). If none of that changed, only the Norrsken
folder on the stick gets updated, which takes seconds instead of an hour. Fresh installs and
*Install Norrsken* both use that folder.

If the image itself needs to change, it gets rebuilt automatically. `-Full` forces a rebuild.

## Releases

Installed copies update themselves from the releases on GitHub. To make one, bump `<Version>` in
`src\Norrsken.Shell\Norrsken.Shell.csproj` and run `.\release.ps1 -NotesFile notes.md`. It needs
the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`).

The interface sounds aren't part of the public release. They're only on sticks and on PCs that
were installed from one.

## Making an ISO instead

From PowerShell running as administrator in this folder:

1. Download the Windows 11 ISO from <https://www.microsoft.com/software-download/windows11>.
2. Install the ADK Deployment Tools (that's where `oscdimg` comes from):
   ```powershell
   winget install Microsoft.WindowsADK
   ```
3. Build Norrsken and draw its wallpapers and logo (this part doesn't need admin):
   ```powershell
   .\publish.ps1
   .\payload\Norrsken\Norrsken.Shell.exe --render-branding .\assets
   ```
   `publish.ps1` needs the Windows SDK (for makeappx and signtool) and a built Flickerbox.
4. Build the ISO from the Windows ISO, a Windows USB stick, or a backup of one:
   ```powershell
   .\build-iso.ps1 -WindowsSource <path-to-Win11.iso-or-folder>
   ```
   You get `out\Norrsken-<version>-x64.iso` and a `.sha256` file next to it.

## Installing on a laptop

Write the ISO to a USB stick (16 GB or bigger) with **Rufus**, which handles the install image
being over 4 GB on UEFI. Or just use the stick from above. Then pick one of these:

### A. Keep everything (best if the laptop already runs Windows 10 or 11)

1. In the laptop's current Windows, open the stick and run **Install Norrsken**.
2. Click **Install**. Nothing is erased: your files, apps, settings and Windows activation all stay,
   and Norrsken becomes the desktop.
3. Optionally, "Also refresh Windows itself" runs Windows Setup from the stick and keeps your files
   and apps. It takes about an hour, asks you to accept Microsoft's licence terms, and only works if
   the stick's language and edition match what's installed.

### B. Clean install (keeps your files, not your apps)

1. Back up anything you can't lose to an external drive or the cloud first.
2. Boot from the stick (usually F12, F2 or Esc when the PC starts). In Windows Setup pick
   **Custom** and install onto the Windows partition **without formatting or deleting it**. Windows
   moves the old system into `C:\Windows.old`.
3. Sign in. The welcome screen finds the old Windows and offers to **bring your files back**:
   Desktop, Documents, Downloads, Pictures, Videos, Music and your other folders.
   - It copies them (it never moves or overwrites anything) and checks every file afterwards.
   - App data and game saves are only copied if you tick them.
   - Windows deletes `Windows.old` on its own after about 10 days, so do this on day one.
4. The app picker then ticks the apps it found on the old system, along with the usual ones, and
   installs them again.

## If something goes wrong

- **Norrsken stops or acts up:** the Windows taskbar comes back on its own, and Ctrl+Alt+N opens
  the launcher.
- **Removing Norrsken:** uninstall it from Settings › Apps (or run
  `"C:\Program Files\Norrsken\Norrsken.Shell.exe" --uninstall`). That brings back the Windows
  taskbar and Start and removes the startup task and redirects. Your files, apps and Flickerbox stay.
- **Starting over:** boot from the stick again, or use Windows' own "Reset this PC".
