# Norrsken — Acceptance Status

## 0.3 (in progress): Norrsken OS, Windows edition

Direction set by the owner on 2026-11-04: a **bootable ISO for a clean laptop install**. It must
run every regular .exe, game and Steam title, and offer most apps from the current PC on first
boot. Only Windows itself runs *all* Windows software, so Norrsken OS is Windows 11 with Norrsken
built in as the desktop. It is built locally from Microsoft's official ISO and is for personal
use only (see [../os/README.md](../os/README.md)).

| Item | Status |
|---|---|
| Welcome flow (look → app picker → background install) | ✅ Built; isolated UI tests pass (simulated installs) |
| Catalogue | ✅ 19 always-installed essentials (all VC++ 2005–2022, DirectX, .NET 6/8/10, GameInput…). 71 optional apps; the 51 from the current PC are preselected. All are real winget ids from `winget list` / `winget show`. |
| Real winget installs | ⬜ Not run. They can only be verified on a fresh install (VM or laptop). |
| Route A: "Install Norrsken" from the stick, keeping everything | ✅ Built and tested on fake data: copies Norrsken, autostart for all accounts, identity, wallpaper, keeps existing settings. The optional in-place Windows refresh is only enabled after the licence terms are accepted. |
| Route B: clean install, bring files back from `Windows.old` | ✅ Built and tested with a **real copy** of fake data. Unicode names, a 436-character path, a SHA-256-identical 3 MB file, hidden attributes and custom folders all verified. Newer files are never overwritten; the registry hive, AppData (opt-in) and OneDrive are excluded; the originals stay untouched. |
| Old apps preselected after a clean install | ✅ Matched by whole words from the old Start menu and Program Files ("Go" doesn't match "Google") |
| Activation | Uses the laptop's own firmware licence when the edition matches. Never bypassed or hidden. |
| Boot menu entry "Norrsken OS" | ✅ Set by `SetupComplete.cmd`, again by the first-sign-in essentials (Windows skips SetupComplete on some OEM-key installs), and by route A. Test mode records the bcdedit call. ⬜ Not yet seen on real hardware. |
| Lock screen, sign-in and wallpaper follow the theme | ✅ Documented APIs (SystemParametersInfo, UserProfilePersonalizationSettings); on by default in the image. Tested as "recorded, not applied". ⬜ Real effect not yet checked in a VM. |
| Account pictures | ✅ Monochrome defaults in every size Windows uses |
| Boot logo / spinner, custom boot manager | ❌ Deliberately not done: it needs Secure Boot off, which Windows 11 and anti-cheat (Vanguard, FACEIT) require on |
| Self-contained Norrsken payload | ✅ 198 MB, 403 files; refuses unknown options (exit 64) |
| Branding: wallpapers, OEM logo | ✅ Rendered into `os/assets` |
| `os/build-iso.ps1` | ✅ Written and syntax-checked. ⬜ **Not run yet**: needs admin, the Windows 11 ISO and the ADK |
| ISO boot and install test in VirtualBox | ⬜ Pending the build |
| Multi-language UI | ⬜ Not started (requested) |
| Notifications / Win+N parity checks | ⬜ Not started. By design they're Windows' own and Norrsken registers no Win shortcuts. |

**Isolated UI suite** (`--uitest-isolated`, runs on a private hidden desktop): **50 pass, 0 fail**, 4 informational, on two consecutive runs. Auto-hide measured about 960 ms against a ~1 s design.

**Incidents on 2026-11-04 (both fixed):**
1. Synthetic test input went into the owner's game. Synthetic input on the real desktop is now
   forbidden; UI tests run on a hidden desktop instead.
2. A stale binary launched the full desktop UI for about 4 minutes after a failed build. Scripts
   now stop when the build fails, and Norrsken exits with code 64 on unknown options.

**Build:** 0.2.0 (Release, net10.0-windows10.0.22621.0) · **Tested:** 2026-11-04
**Test machine:** DESKTOP-PLCLOKJ: AMD Ryzen 7 5700G, 15.8 GB RAM, Windows 11 Home build 26300, 1920×1080, Wi-Fi
**Not tested on:** any other PC, laptop hardware (battery/lid/touchpad/brightness), multiple monitors, high-DPI scaling, VMs.

## Brief requirements

| Req | Status | Evidence / note |
|---|---|---|
| A. Files preserved | ✅ by architecture | Nothing is moved; files stay at their real `C:\` / `D:\` / `E:\` paths. |
| B. Installed apps keep working | ✅ by architecture | All 322 entries in Windows' app list are indexed and launchable. |
| C. Claude Desktop + Claude Code | ✅ native | Claude is found, pinned, and its running window is matched to the pin. |
| D. Windows filesystem + cmd | ✅ native | It's the real Windows. |
| Windows shortcuts | ✅ | A low-level keyboard hook on its own thread takes only the shortcuts that open Windows UI (Win, Win+E/I/N/A/R/X/V/Tab, Alt+Tab, PrtScn…) and routes them to Norrsken; window shortcuts (Win+D, snapping, virtual desktops) stay Windows'. |
| E. Bootable ISO | ❌ replacement ISO / ⬜ recovery ISO | See [ARCHITECTURE.md](ARCHITECTURE.md). |

## 0.2 polish pass: what's new

- **Sharp icons:** extracted at 96 px. The dock magnifies them to about 65 DIP, and 32 px sources were visibly soft.
- **Quick Settings** (click the dock's clock):
  - Live volume and mute through Core Audio, synced both ways with no polling.
  - Now playing (title, artist, art, prev/play/next) through Windows media sessions.
  - Network status through WinRT.
  - A brightness slider on displays that support it.
  - Light/dark toggle and settings shortcuts.
- **Live window previews:** rest on a running app for 0.55 s to see real-time DWM thumbnails. Click one to switch, or close it from its pebble. Once open, previews switch instantly as you move along the dock.
- **Launcher:** "Frequent" row; ranked search (prefix › word › initials › substring › in-order letters), boosted by your launch counts.
- **Status glyphs:** network and volume in the dock, drawn in the Norrsken icon style (48 glyphs in total now).
- **Menus** bloom in (fade, rise, scale). The status cell has a hover state.
- **Robustness:**
  - The dock re-registers itself when Explorer restarts (`TaskbarCreated`).
  - Launching Norrsken a second time opens the launcher.
  - New "Start when you sign in" toggle (per-user Run key, only changed from your own toggle).
- **Optional "Hide the Windows taskbar"** (off by default), with a crash guard process.

## Automated self-test: 28/28 pass

All 22 checks from 0.2-dev, plus:

| Check | Result |
|---|---|
| Launcher ranking (`vsc` → Visual Studio Code, `tmgr` → Task Manager, no noise matches) | ✅ |
| Audio readable (read-only) | ✅ |
| Network status | ✅ Wi-Fi "Wajfaj 5GHz", online |
| Brightness | ✅ Correctly reports none on this desktop monitor, so the slider hides |
| Media session | ✅ API reachable; nothing was playing |
| Quick Settings renders, Graphite + Paper | ✅ Reviewed visually |
| 48 glyphs parse and stay inside the 24-unit grid | ✅ |

One bug found and fixed in the test harness: blocking on WinRT async calls from the UI thread deadlocked the self-test (the live app awaits them properly).

## Live checks (measured)

| Measurement | Result |
|---|---|
| Launch to input-idle | 584 ms |
| Idle CPU | 16 ms over 15 s (≈0.1 % of one core) |
| Memory | 156 MB private, up from 89 MB in 0.2-dev (96 px icons plus the WinRT media/network runtime) |
| `--quit` | Exits and releases the strip (work area back to 1080) |
| **Taskbar crash guard** | ✅ Hide taskbar → force-kill Norrsken (no cleanup runs) → guard restored the taskbar, removed its marker and exited |

## Pointer-driven test: invalid, and stopped

An attempt to drive hover, previews, Quick Settings and the launcher with synthetic mouse input
failed because **Minecraft was in the foreground with the mouse captured**. The input went into
the game (camera turns plus in-game clicks), and nothing reached the dock. The test was stopped
and Norrsken was quit. These interactions are still **untested with a real pointer**:
- hover magnification live, window previews, Quick Settings and launcher open/close animations
- media controls with something playing, the volume slider moving the real volume

From now on, synthetic input is never sent without checking the foreground app first.

## Known limitations

- Pebble window controls appear only on Norrsken's own windows.
- With the Windows taskbar hidden, its tray icons are hidden too (no tray mirror yet).
- Bluetooth, Night light and Focus tiles open the matching Windows Settings page instead of toggling directly.
- Confirmation dialogs use the stock Windows message box.
