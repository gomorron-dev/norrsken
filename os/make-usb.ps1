<#
.SYNOPSIS
    Turns a Windows 11 installation USB stick (e.g. one made with Microsoft's Media Creation
    Tool) into a bootable Norrsken OS stick, using the Windows image already on it.

.DESCRIPTION
    1. Refuses anything that isn't a removable USB drive holding a Windows installer.
    2. Backs the stick's current contents up to C:\NorrskenBackup\usb-<time> (never deleted by this script).
    3. Takes the Home, Home Single Language and Pro editions from the stick and builds Norrsken into each.
    4. Writes the result back (split .swm parts, so it fits FAT32 and keeps booting on UEFI and BIOS),
       adds "Install Norrsken" for upgrading an existing Windows, relabels the stick NORRSKEN,
       and verifies every written file by SHA-256.

    Personal use only: the stick contains Microsoft's Windows, licensed to you.

.EXAMPLE
    .\make-usb.ps1 -Drive E:
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z]:$')] [string] $Drive,
    [string] $WorkDir = (Join-Path $env:SystemDrive 'NorrskenBuild'),
    [switch] $KeepStockApps,
    # Skip the safety copy of the stick's current contents (it's only a Windows installer you can re-download).
    [switch] $NoBackup,
    # Rebuild the Windows image even when only Norrsken itself changed (normally a quick sync is enough).
    [switch] $Full
)

$ErrorActionPreference = 'Stop'

# A click inside a console window normally pauses it ("Select" mode) until Esc is pressed;
# switch that off for this window so a stray click can't stall a half-written stick.
Add-Type -Namespace Norrsken -Name Console -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetStdHandle(int h);
[DllImport("kernel32.dll")] public static extern bool GetConsoleMode(System.IntPtr h, out uint m);
[DllImport("kernel32.dll")] public static extern bool SetConsoleMode(System.IntPtr h, uint m);
'@
$stdin = [Norrsken.Console]::GetStdHandle(-10)
$mode = 0
if ([Norrsken.Console]::GetConsoleMode($stdin, [ref]$mode)) { [void][Norrsken.Console]::SetConsoleMode($stdin, ($mode -band (-bnot 0x40)) -bor 0x80) }

. (Join-Path $PSScriptRoot 'lib\customize.ps1')
$Payload = Join-Path $PSScriptRoot 'payload\Norrsken'
$Assets = Join-Path $PSScriptRoot 'assets'
$MarkerName = '.norrsken-build'
$LogDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
Start-Transcript -Path (Join-Path $LogDir 'make-usb.log') -Force | Out-Null

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor White }
function Hash([string] $path) { (Get-FileHash $path -Algorithm SHA256).Hash }

try {
    # ───────────────────────── safety checks ─────────────────────────
    Step "Checking $Drive"
    $letter = $Drive.Substring(0, 1)
    $volume = Get-Volume -DriveLetter $letter
    $disk = Get-Partition -DriveLetter $letter | Get-Disk
    if ($volume.DriveType -ne 'Removable' -or $disk.BusType -ne 'USB' -or $disk.IsBoot -or $disk.IsSystem) {
        throw "$Drive is not a removable USB stick (type $($volume.DriveType), bus $($disk.BusType)). Refusing."
    }
    $sources = "$Drive\sources"
    $parts = @(Get-ChildItem $sources -Filter 'install*.swm' -ErrorAction SilentlyContinue)
    $single = @(Get-ChildItem $sources -Include 'install.wim', 'install.esd' -File -Recurse -Depth 0 -ErrorAction SilentlyContinue)
    if (-not (Test-Path "$Drive\setup.exe") -or ($parts.Count -eq 0 -and $single.Count -eq 0)) {
        throw "$Drive doesn't look like a Windows installation stick (no setup.exe / install image). Refusing."
    }
    if (-not (Test-Path (Join-Path $Payload 'Norrsken.Shell.exe'))) { throw "Norrsken payload missing in $Payload (see README.md)." }
    if (-not (Test-Path (Join-Path $Assets 'account-pictures\user.png'))) { throw "Norrsken art missing in $Assets (see README.md)." }
    Write-Host "   $($disk.FriendlyName), $([math]::Round($disk.Size / 1GB, 1)) GB, label '$($volume.FileSystemLabel)', $($volume.FileSystem)"

    # ───────────────────────── quick update ─────────────────────────
    # The image only has to be rebuilt when what shapes it changed. Otherwise Norrsken itself lives in
    # <stick>\Norrsken, which Windows Setup (SetupComplete) and "Install Norrsken" both install from:
    # syncing that folder copies just the files that changed. Seconds instead of most of an hour.
    $recipe = Get-NorrskenImageRecipe -Assets $Assets -KeepStockApps:$KeepStockApps
    $recipeFile = "$sources\norrsken-recipe.txt"
    $stickRecipe = if (Test-Path $recipeFile) { ([IO.File]::ReadAllText($recipeFile)).Trim() } else { '' }
    if (-not $Full -and $volume.FileSystemLabel -eq 'NORRSKEN' -and $parts.Count -gt 0 -and $stickRecipe -eq $recipe) {
        Step "Quick update: the Windows image on $Drive is current, syncing Norrsken only"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        Add-NorrskenSetupToMedia -MediaRoot $Drive -Payload $Payload -Assets $Assets
        Step 'Verifying'
        $mine = Get-ChildItem $Payload -Recurse -File
        $theirs = Get-ChildItem "$Drive\Norrsken" -Recurse -File | Where-Object { $_.FullName -notlike "$Drive\Norrsken\assets\norrsken-*" }
        $mineBytes = ($mine | Measure-Object Length -Sum).Sum
        $theirBytes = ($theirs | Where-Object { Test-Path (Join-Path $Payload $_.FullName.Substring("$Drive\Norrsken\".Length)) } | Measure-Object Length -Sum).Sum
        if ($mineBytes -ne $theirBytes) { throw "Norrsken on the stick doesn't match the payload ($mineBytes vs $theirBytes bytes)." }
        foreach ($name in 'Norrsken.Shell.exe', 'Norrsken.Shell.dll') {
            if ((Hash (Join-Path $Payload $name)) -ne (Hash "$Drive\Norrsken\$name")) { throw "$name on the stick doesn't match the payload." }
        }
        Write-Host "   $($mine.Count) files, $([math]::Round($mineBytes / 1MB)) MB, sizes and program hashes match"
        Step "Done in $([math]::Round($clock.Elapsed.TotalSeconds)) s"
        Write-Host "   $Drive installs this Norrsken (fresh Windows installs and 'Install Norrsken' alike)."
        return
    }
    if ($Full) { Step 'Full rebuild requested (-Full)' }
    elseif ($volume.FileSystemLabel -eq 'NORRSKEN') { Step 'The Windows image recipe changed (or the stick predates quick updates): full rebuild' }

    if (Test-Path $WorkDir) {
        if (-not (Test-Path (Join-Path $WorkDir $MarkerName))) { throw "$WorkDir exists and wasn't created by Norrsken. Choose another -WorkDir." }
        Remove-Item $WorkDir -Recurse -Force
    }
    $Mount = Join-Path $WorkDir 'mount'
    $Wim = Join-Path $WorkDir 'install.wim'
    $Split = Join-Path $WorkDir 'swm'
    New-Item -ItemType Directory -Path $Mount, $Split -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $WorkDir $MarkerName) | Out-Null

    # ───────────────────────── 1. backup ─────────────────────────
    $src = Get-ChildItem "$Drive\" -Recurse -File -Force | Where-Object FullName -NotLike "*System Volume Information*"
    $srcBytes = ($src | Measure-Object Length -Sum).Sum
    function Matches([string] $dir) {
        $files = Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue
        $files.Count -eq $src.Count -and ($files | Measure-Object Length -Sum).Sum -eq $srcBytes
    }
    # An earlier run may already have made a verified backup of exactly this content: reuse it.
    $backup = Get-ChildItem (Join-Path $env:SystemDrive 'NorrskenBackup') -Directory -Filter 'usb-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Where-Object { Matches $_.FullName } | Select-Object -First 1 -ExpandProperty FullName
    # A stick that's already Norrsken (label NORRSKEN) was backed up when it was first converted.
    $earlier = Get-ChildItem (Join-Path $env:SystemDrive 'NorrskenBackup') -Directory -Filter 'usb-*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if ($NoBackup) { $backup = '(none — skipped with -NoBackup)'; Step 'Skipping the backup (-NoBackup)' }
    elseif (-not $backup -and $volume.FileSystemLabel -eq 'NORRSKEN' -and $earlier) { $backup = $earlier; Step "Updating an existing Norrsken stick (original backed up in $backup)" }
    elseif ($backup) { Step "Reusing the verified backup in $backup" }
    else {
        $backup = Join-Path $env:SystemDrive ("NorrskenBackup\usb-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Step "Backing up the stick to $backup"
        robocopy "$Drive\" $backup /E /XD "$Drive\System Volume Information" /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "backup failed with code $LASTEXITCODE" }
        if (-not (Matches $backup)) { throw "backup doesn't match the stick. Nothing on the stick was changed." }
    }
    Write-Host "   $($src.Count) files, $([math]::Round($srcBytes / 1GB, 2)) GB, counts and sizes match"

    # ───────────────────────── 2. editions ─────────────────────────
    Step 'Choosing editions'
    # DISM reads an image in small scattered chunks, which crawls on a USB stick (~0.1 MB/s on some).
    # Copy it to the local work folder first in one sequential pass and export from there.
    $local = Join-Path $WorkDir 'source'
    New-Item -ItemType Directory -Path $local -Force | Out-Null
    $files = if ($parts.Count -gt 0) { Get-ChildItem "$sources\install*.swm" } else { $single[0] }
    foreach ($file in $files) {
        Write-Host "   copying $($file.Name) ($([math]::Round($file.Length / 1GB, 2)) GB) to the local disk"
        robocopy $file.DirectoryName $local $file.Name /J /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "copying $($file.Name) failed with code $LASTEXITCODE" }
    }
    if ($parts.Count -gt 0) { $image = "$local\install.swm"; $pattern = "$local\install*.swm" } else { $image = Join-Path $local $single[0].Name; $pattern = $null }
    $wanted = @{ Core = 'Windows 11 Home'; CoreSingleLanguage = 'Windows 11 Home Single Language'; Professional = 'Windows 11 Pro' }
    $chosen = foreach ($img in Get-WindowsImage -ImagePath $image) {
        $info = Get-WindowsImage -ImagePath $image -Index $img.ImageIndex
        # A split image (install.swm) often reports no EditionId; fall back to its name.
        if ($wanted.ContainsKey([string]$info.EditionId) -or ($wanted.Values -contains $img.ImageName)) { $info }
    }
    if (-not $chosen) { throw "No Home/Pro edition on this stick." }
    foreach ($c in $chosen) {
        Write-Host "   $($c.ImageName) ($($c.EditionId), $($c.Languages -join ', '))"
        $export = @{ SourceImagePath = $image; SourceIndex = $c.ImageIndex; DestinationImagePath = $Wim; CompressionType = 'Max' }
        if ($pattern) { $export.SplitImageFilePattern = $pattern }
        Export-WindowsImage @export | Out-Null
    }

    # ───────────────────────── 3. build Norrsken into each edition ─────────────────────────
    $count = (Get-WindowsImage -ImagePath $Wim).Count
    for ($i = 1; $i -le $count; $i++) {
        $name = (Get-WindowsImage -ImagePath $Wim -Index $i).ImageName
        Step "Building Norrsken into $name ($i of $count)"
        Mount-WindowsImage -ImagePath $Wim -Index $i -Path $Mount | Out-Null
        $saved = $false
        try {
            Add-NorrskenToImage -Mount $Mount -Payload $Payload -Assets $Assets -KeepStockApps:$KeepStockApps
            $saved = $true
        }
        finally {
            if ($saved) { Dismount-WindowsImage -Path $Mount -Save | Out-Null } else { Dismount-WindowsImage -Path $Mount -Discard | Out-Null }
        }
    }

    # ───────────────────────── 4. split for FAT32 ─────────────────────────
    Step 'Splitting the image into FAT32-sized parts'
    Split-WindowsImage -ImagePath $Wim -SplitImagePath (Join-Path $Split 'install.swm') -FileSize 3800 -CheckIntegrity | Out-Null
    $newParts = Get-ChildItem $Split -Filter 'install*.swm'
    $needed = ($newParts | Measure-Object Length -Sum).Sum + (Get-ChildItem $Payload -Recurse -File | Measure-Object Length -Sum).Sum
    $old = (($parts + $single) | Measure-Object Length -Sum).Sum
    $available = (Get-Volume -DriveLetter $letter).SizeRemaining + $old
    if ($needed -gt $available - 100MB) {
        throw "Not enough room on the stick: need $([math]::Round($needed / 1GB, 2)) GB, have $([math]::Round($available / 1GB, 2)) GB. Nothing on the stick was changed."
    }

    # ───────────────────────── 5. write the stick ─────────────────────────
    Step "Writing Norrsken OS to $Drive"
    ($parts + $single) | Remove-Item -Force
    foreach ($p in $newParts) {
        Write-Host "   $($p.Name) ($([math]::Round($p.Length / 1MB)) MB)"
        Copy-Item $p.FullName "$sources\$($p.Name)"
    }
    # No trailing backslash: "E:\" + "\Norrsken" made robocopy fail with "file name too long".
    Add-NorrskenSetupToMedia -MediaRoot $Drive -Payload $Payload -Assets $Assets
    Set-Volume -DriveLetter $letter -NewFileSystemLabel 'NORRSKEN'

    # ───────────────────────── 6. verify ─────────────────────────
    Step 'Verifying'
    foreach ($p in $newParts) {
        if ((Hash $p.FullName) -ne (Hash "$sources\$($p.Name)")) { throw "$($p.Name) on the stick doesn't match what was built." }
    }
    $payloadExe = Join-Path $Payload 'Norrsken.Shell.exe'
    if ((Hash $payloadExe) -ne (Hash "$Drive\Norrsken\Norrsken.Shell.exe")) { throw 'Norrsken on the stick does not match the payload.' }
    $onStick = Get-WindowsImage -ImagePath "$sources\install.swm"
    Write-Host "   image parts: SHA-256 match ($($newParts.Count) files)"
    Write-Host "   editions on the stick: $($onStick.ImageName -join ', ')"
    Write-Host "   boot files untouched: $(Test-Path "$Drive\bootmgr") (BIOS), $(Test-Path "$Drive\efi\boot\bootx64.efi") (UEFI)"

    [IO.File]::WriteAllText($recipeFile, $recipe) # later updates can take the quick path
    Step 'Done'
    Write-Host "   $Drive is now a Norrsken OS stick (label NORRSKEN)."
    Write-Host "   The original stick contents are backed up in $backup"
    Remove-Item $WorkDir -Recurse -Force
}
finally {
    if ($Mount -and (Test-Path $Mount)) {
        if (Get-WindowsImage -Mounted | Where-Object Path -EQ $Mount) { Dismount-WindowsImage -Path $Mount -Discard | Out-Null }
    }
    Stop-Transcript | Out-Null
}
