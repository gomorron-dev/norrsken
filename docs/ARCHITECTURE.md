# How Norrsken works

Norrsken isn't a separate operating system. It's a desktop that runs on top of a normal, licensed
Windows 11. Windows still does all the real work (drivers, files, apps, games), and Norrsken takes
over what you see and touch.

I went this way because I wanted to keep everything I already had. My files stay where they are,
every installed app keeps working with its settings, `C:\Users\…` paths are real, and games with
anti-cheat run like they always did. A Linux base would have meant migrating terabytes of files and
running Windows apps through a compatibility layer, and shipping a modified Windows image isn't
allowed. Building on top of Windows avoids all of that.

## The layers

```
┌──────────────────────────────────────────────────────────────┐
│ Norrsken (C# / .NET 10 / WPF)                                 │
│  dock · launcher · Settings · Files · Bin · title bars · …    │
├──────────────────────────────────────────────────────────────┤
│ Windows shell services: shell:AppsFolder, WinEvent hooks,     │
│ AppBar, DWM, ShellExecute, Core Audio, the Recycle Bin        │
├──────────────────────────────────────────────────────────────┤
│ Windows 11: kernel, drivers, NTFS, apps, games                │
└──────────────────────────────────────────────────────────────┘
```

## Why WPF

- It's GPU-composited and starts fast, and it's a lot lighter than an Electron app would be.
- Calling into Windows (the shell, Core Audio, DWM, the Recycle Bin) is straightforward from C#.
- The .NET SDK was already on my machine, so there was nothing extra to set up.

## How it fits into Windows

- **Explorer keeps running.** Norrsken hides the taskbar and Start rather than replacing Explorer,
  so if Norrsken ever crashes, the Windows taskbar comes straight back.
- **Startup:** a scheduled task starts Norrsken when you sign in, with admin rights so it can manage
  things like the system volume and devices.
- **Windows tools open Norrsken's versions.** Task Manager, Notepad, the Snipping Tool, Windows
  Terminal and Settings are redirected to Norrsken's apps. Uninstalling removes the redirects again.
- **Keyboard shortcuts:** a keyboard hook on its own thread catches only the shortcuts that would
  open Windows UI (Win, Win+E, Win+I, Alt+Tab, PrtScn and so on) and sends them to Norrsken.
  Everything else passes straight through.
- **Title bars on other apps** are small overlay windows owned by the app's window, so they move and
  minimise with it. Nothing is injected into other processes, so anti-cheat has nothing to complain
  about. Apps that draw their own title bar (browsers, Electron apps, Explorer) are left alone.
- **The Bin** reads Windows' Recycle Bin directly, so it and Explorer always agree.
- **Updates** come from the GitHub releases of this repo, and the new version installs itself over
  the old one.

## Things that are still missing

- System tray icons still belong to Explorer's hidden taskbar and aren't mirrored in the dock yet.
- Running Norrsken as the only shell, with no Explorer at all, is possible but I haven't enabled it.
  That needs the tray and a recovery shortcut first.
