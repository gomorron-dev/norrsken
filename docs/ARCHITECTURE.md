# Norrsken — Architecture Decision Record

**Status:** accepted · **Date:** 2026-10-30 · **Release line:** 0.x

## What Norrsken is (stated plainly)

Norrsken is an **original desktop environment that runs on top of the user's existing, licensed
Windows 11 installation**. It does not replace the Windows kernel and does not ship Windows. It is
not an independent operating system, and none of the documentation should describe it as one.

## The conflict in the brief

The brief asks for two things that can't both be true:

1. A bootable ISO that *replaces* Windows 11.
2. Every file, every installed application (with its settings), the official Claude Desktop app,
   real `cmd`/batch/PowerShell, Windows paths (`C:\Users\...`), and gaming with anti-cheat must
   keep working.

The brief puts (2) above (1) in its own priority order: files first, then Claude and app
compatibility, then the Windows filesystem and shell, and only then "bootable, installable".
Following that order decides the architecture.

## Options evaluated

| Requirement | A. Custom Linux distro + Wine/Proton | B. Custom Windows ISO | **C. Original shell on the user's Windows 11** |
|---|---|---|---|
| Files preserved in place | Needs migration and repartitioning, which is risky | Needs a reinstall | **Untouched: zero migration** |
| Existing installed apps + settings | Partial at best. Installers, services, drivers and COM all break | Lost on reinstall | **All keep working** |
| Claude Desktop (official) | No official Linux build has been verified. Running it under Wine would be an unsupported wrapper, which the brief rules out | Works | **Works natively** |
| Claude Code | Works natively, but not on `C:\` paths | Works | **Works natively** (already installed here) |
| `C:\`, `Program Files`, `AppData` | Emulated inside a Wine prefix (a "decorative path") | Real | **Real** |
| `cmd`, `.bat`, PowerShell | Wine `cmd` is partial | Real | **Real** |
| Anti-cheat games (EAC/BattlEye/Vanguard) | Many are blocked or banned | Works | **Works** |
| Drivers and laptop hardware | Good but different | Works | **Unchanged** |
| Legal to distribute | Yes | **No.** Redistributing modified Windows images breaks the licence | Yes (only our own code ships) |
| Can be built and tested on this machine | Yes, once QEMU is installed (the user has authorised installs) | Yes, but it can't be shipped | **Yes** |

**Decision: Option C.**

A Linux distro would produce a real ISO, but it would fail priorities 1–3 (the top three). It
would need a repartitioning migration on a machine with about 2 TB of user data. Claude Desktop
would only run through an unsupported wrapper. And every Windows path would be emulated. The
user has authorised installing any tooling, but that doesn't change this: the blockers are
compatibility and licensing, not missing tools.

## Consequences

- **Requirement A (files):** satisfied by construction. Nothing is moved, so nothing can be lost.
  `C:\Users\<you>\Downloads` stays exactly where it is.
- **Requirement B (apps):** satisfied by construction. Norrsken launches the user's real apps
  through the Windows shell namespace (`shell:AppsFolder`), the same list Windows Start uses.
- **Requirement C (Claude):** the official apps run unmodified. Norrsken only has to launch them.
- **Requirement D (Windows-like):** it *is* Windows underneath, and Norrsken is the experience
  layer on top.
- **Requirement E (ISO):** a bootable ISO that *replaces* Windows can't coexist with A–D, and
  redistributing Windows is not legal. Two deliverables are planned instead:
  1. **Norrsken desktop package**, installed per user. Removing it means quitting the bar and
     deleting a folder, so it is fully reversible and never touches partitions.
  2. **Norrsken Recovery ISO (planned phase):** a real bootable image built locally from
     Microsoft's free Windows ADK + WinPE add-on, whose licence permits recovery and deployment
     use. It will contain Norrsken-branded tools to back files up to an external drive, verify
     them with SHA-256 manifests, and repair boot. It will be boot-tested in QEMU. The ISO is
     *built on the user's machine* from Microsoft's components and never redistributed.

## Layering

```
┌──────────────────────────────────────────────────────────────┐
│ Norrsken experience layer (this repo, C# / .NET 10 / WPF)    │
│  Norrsken Bar · Launcher · Settings · window "pebbles" · themes│
├──────────────────────────────────────────────────────────────┤
│ Windows shell services: shell:AppsFolder, WinEvent hooks,      │
│ AppBar API, DWM, ShellExecute                                  │
├──────────────────────────────────────────────────────────────┤
│ Windows 11 (user's licence): kernel, drivers, NTFS, apps, games│
└──────────────────────────────────────────────────────────────┘
```

### Why C# / .NET 10 / WPF

- The SDK is already installed, so nothing has to be added to the system to build.
- It is GPU-composited and starts in well under a second. It's far lighter than Electron, which
  matters for the 8 GB RAM baseline in the brief.
- It has first-class P/Invoke access to the Win32 shell APIs a desktop shell needs.

### Safety rules the code follows

- **Explorer is never replaced or killed.** Norrsken runs alongside it as a normal per-user
  process. If Norrsken crashes, Windows is unaffected.
- No writes outside `%AppData%\Norrsken` and `%LocalAppData%\Norrsken`. No registry shell
  replacement, and no DLL injection into other processes.
- The AppBar registration is removed on exit, and the bar steps out of the way of full-screen
  games (`ABN_FULLSCREENAPP`).

## Known gaps in this architecture (not hidden)

- **Right-aligned pebble window controls on *third-party* windows** would mean injecting code into
  other processes or patching `uxtheme`. Both are risky, and injection can trip anti-cheat. So
  pebbles currently appear only on Norrsken's own windows. A safe approach is an open design item.
- **System-tray icons** are owned by Explorer's `Shell_TrayWnd` while Explorer is running. They
  aren't mirrored yet.
- Running Norrsken as the *sole* shell (in place of Explorer) is possible on Windows through
  per-user `Winlogon\Shell`, but it is deliberately not offered until the tray, notifications and
  a recovery hotkey exist.
