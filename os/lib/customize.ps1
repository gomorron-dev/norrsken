# Shared by build-iso.ps1 and make-usb.ps1: everything that turns a mounted Windows image into
# Norrsken OS. Files only — no offline registry editing (loading an image's hives fails on some
# Windows builds). Registry-level settings are applied on the running system instead:
#   * per user, by Norrsken on first sign-in (dark mode, wallpaper, file extensions);
#   * machine-wide, by SetupComplete.cmd and again by Norrsken's first-sign-in admin step
#     ("Norrsken system setup": OEM identity, boot menu name, registry autostart).

$NorrskenVersion = '0.3.0'
$NorrskenExe = 'C:\Program Files\Norrsken\Norrsken.Shell.exe'
$NorrskenCustomizeScript = $PSCommandPath # captured while dot-sourced: this file

function Add-NorrskenToImage {
    param(
        [Parameter(Mandatory)] [string] $Mount,
        [Parameter(Mandatory)] [string] $Payload,
        [Parameter(Mandatory)] [string] $Assets,
        [switch] $KeepStockApps
    )

    Write-Host '     Norrsken'
    robocopy $Payload "$Mount\Program Files\Norrsken" /E /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying Norrsken failed with code $LASTEXITCODE" }

    # Autostart for every account from first sign-in. Norrsken's first admin step later swaps this
    # shortcut for a registry Run entry (starts sooner) and removes it, so it never starts twice.
    Write-Host '     autostart'
    $startup = "$Mount\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp"
    New-Item -ItemType Directory -Path $startup -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut("$startup\Norrsken.lnk")
    $lnk.TargetPath = $NorrskenExe
    $lnk.WorkingDirectory = 'C:\Program Files\Norrsken'
    $lnk.Description = 'Norrsken desktop'
    $lnk.Save()

    Write-Host '     wallpapers and logo'
    $wall = "$Mount\Windows\Web\Wallpaper\Norrsken"
    New-Item -ItemType Directory -Path $wall -Force | Out-Null
    Copy-Item (Join-Path $Assets 'norrsken-*.png') $wall
    Copy-Item (Join-Path $Assets 'oemlogo.bmp') "$Mount\Windows\System32\oemlogo.bmp"

    # Default account pictures are a nicety: warn and carry on if the folder can't be unlocked.
    $pictures = "$Mount\ProgramData\Microsoft\User Account Pictures"
    if (Test-Path $pictures) {
        try {
            Write-Host '     account pictures'
            takeown /F $pictures /R /A /D Y 2>&1 | Out-Null
            icacls $pictures /grant '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1 | Out-Null
            Copy-Item (Join-Path $Assets 'account-pictures\*') $pictures -Force
        }
        catch { Write-Warning "Default account pictures not replaced: $($_.Exception.Message)" }
    }

    # Runs once as SYSTEM right after Windows Setup (skipped by Windows on some OEM-key installs,
    # which is why Norrsken's first-sign-in admin step repeats it).
    Write-Host '     after-setup script'
    $scripts = "$Mount\Windows\Setup\Scripts"
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    Set-Content -Path "$scripts\SetupComplete.cmd" -Encoding ascii -Value @"
@echo off
bcdedit /set {current} description "Norrsken OS" >nul 2>&1
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\OEMInformation" /v Manufacturer /t REG_SZ /d "Norrsken" /f >nul 2>&1
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\OEMInformation" /v Model /t REG_SZ /d "Norrsken OS $NorrskenVersion" /f >nul 2>&1
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\OEMInformation" /v Logo /t REG_SZ /d "C:\Windows\System32\oemlogo.bmp" /f >nul 2>&1
rem A newer Norrsken on the install stick (make-usb's quick update) replaces the one inside the image.
for %%d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do if exist "%%d:\Norrsken\Norrsken.Shell.exe" if exist "%%d:\sources\install.swm" robocopy "%%d:\Norrsken" "C:\Program Files\Norrsken" /MIR /R:1 /W:1 /NP /NFL /NDL /NJH /NJS >nul
if exist "C:\Program Files\Norrsken\identity\norrsken.cer" certutil -addstore TrustedPeople "C:\Program Files\Norrsken\identity\norrsken.cer" >nul 2>&1
"C:\Program Files\Norrsken\Norrsken.Shell.exe" --register-defaults
"C:\Program Files\Norrsken\Norrsken.Shell.exe" --integrate
"@

    # Norrsken defaults for every new account: dock-first (Windows taskbar hidden, auto-restored if
    # Norrsken ever stops), wallpaper and lock screen follow the theme, dark mode applied on first
    # sign-in, Welcome shown once.
    Write-Host '     default settings for new accounts'
    $defaults = "$Mount\Users\Default\AppData\Roaming\Norrsken"
    New-Item -ItemType Directory -Path $defaults -Force | Out-Null
    [IO.File]::WriteAllText("$defaults\settings.json",
        '{ "Theme": "graphite", "HideWindowsTaskbar": true, "MatchWallpaper": true, "ApplyUserLook": true, "RetireReplacedApps": true, "WelcomeDone": false }')

    # Consumer extras that don't fit a calm desktop (re-installable from the Store).
    # Optional: app servicing of an offline image fails on some host/image build combinations,
    # so this warns and carries on rather than aborting the build.
    if (-not $KeepStockApps) {
        $clutter = 'Clipchamp.Clipchamp', 'Microsoft.BingNews', 'Microsoft.MicrosoftSolitaireCollection',
                   'Microsoft.MicrosoftOfficeHub', 'Microsoft.PowerAutomateDesktop', 'Microsoft.WindowsFeedbackHub', 'Microsoft.Todos',
                   'Microsoft.WindowsNotepad', 'Microsoft.ScreenSketch' # replaced by Norrsken Notes and Screenshot
        try {
            Get-AppxProvisionedPackage -Path $Mount | Where-Object DisplayName -In $clutter | ForEach-Object {
                Write-Host "     removing $($_.DisplayName)"
                Remove-AppxProvisionedPackage -Path $Mount -PackageName $_.PackageName | Out-Null
            }
        }
        catch { Write-Warning "Stock apps kept (couldn't service this image's apps here): $($_.Exception.Message)" }
    }
}

# The "keep everything" route: run inside an existing Windows to install Norrsken without erasing anything.
function Add-NorrskenSetupToMedia {
    param([Parameter(Mandatory)] [string] $MediaRoot, [Parameter(Mandatory)] [string] $Payload, [Parameter(Mandatory)] [string] $Assets)
    robocopy $Payload "$MediaRoot\Norrsken" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null # only what changed; stale files go
    if ($LASTEXITCODE -ge 8) { throw "copying the setup payload failed with code $LASTEXITCODE" }
    robocopy $Assets "$MediaRoot\Norrsken\assets" norrsken-*.png /NFL /NDL /NJH /NJS | Out-Null
    Set-Content -Path "$MediaRoot\Install Norrsken.cmd" -Encoding ascii -Value @'
@echo off
rem Installs Norrsken into the Windows that's running now. Nothing is erased.
start "" "%~dp0Norrsken\Norrsken.Shell.exe" --setup
'@
}

# A fingerprint of everything that shapes the Windows image apart from the Norrsken payload (this script,
# the art, the options). make-usb stores it on the stick; while it matches, an update only syncs the
# payload folder on the stick (seconds) instead of rebuilding and rewriting the whole image.
function Get-NorrskenImageRecipe {
    param([Parameter(Mandatory)] [string] $Assets, [switch] $KeepStockApps)
    $parts = @((Get-FileHash $NorrskenCustomizeScript -Algorithm SHA256).Hash, "KeepStockApps=$([bool]$KeepStockApps)")
    $parts += Get-ChildItem $Assets -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($Assets.Length) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))
    [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')
}
