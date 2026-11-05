<#
.SYNOPSIS
    Builds Norrsken OS (Windows edition) as a bootable ISO from Microsoft's official Windows 11
    media: an ISO file, a Windows installation USB stick, or a copy of one in a folder.

.DESCRIPTION
    The output contains Microsoft's Windows files, licensed to you: use it on your own devices and
    don't share it. Home, Home Single Language and Pro all get Norrsken, so a laptop's firmware
    licence picks the matching edition and activates by itself.

    Requirements: Administrator; Windows ADK Deployment Tools for oscdimg
    (winget install Microsoft.WindowsADK); the payload and art (see README.md); ~25 GB free.
    (For a USB stick no ADK is needed — use make-usb.ps1.)

.EXAMPLE
    .\build-iso.ps1 -WindowsSource D:\Downloads\Win11_25H2_Swedish_x64.iso
.EXAMPLE
    .\build-iso.ps1 -WindowsSource C:\NorrskenBackup\usb-20261104-210000
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WindowsSource,
    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),
    [string] $WorkDir = (Join-Path $env:SystemDrive 'NorrskenBuild'),
    [switch] $KeepStockApps,
    [switch] $KeepWork
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\customize.ps1')
$Payload = Join-Path $PSScriptRoot 'payload\Norrsken'
$Assets = Join-Path $PSScriptRoot 'assets'
$MarkerName = '.norrsken-build'

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor White }

# ───────────────────────── preflight ─────────────────────────
Step 'Checking prerequisites'
if (-not (Test-Path $WindowsSource)) { throw "Windows source not found: $WindowsSource" }
if (-not (Test-Path (Join-Path $Payload 'Norrsken.Shell.exe'))) { throw "Norrsken payload missing in $Payload (see README.md: dotnet publish)." }
foreach ($f in 'norrsken-graphite.png', 'oemlogo.bmp', 'account-pictures\user.png') {
    if (-not (Test-Path (Join-Path $Assets $f))) { throw "Missing art $f in $Assets (run Norrsken.Shell.exe --render-branding .\assets)." }
}
$oscdimg = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $oscdimg) { throw 'oscdimg.exe not found. Install the Windows ADK Deployment Tools: winget install Microsoft.WindowsADK' }
if ($WorkDir -match '\s') { throw "WorkDir must not contain spaces (oscdimg boot parameters can't quote them): $WorkDir" }
$free = (Get-PSDrive $WorkDir.Substring(0, 1)).Free
if ($free -lt 25GB) { throw "Need about 25 GB free on $($WorkDir.Substring(0, 2)); only $([math]::Round($free / 1GB)) GB available." }

# Never delete a folder this script didn't create.
if (Test-Path $WorkDir) {
    if (-not (Test-Path (Join-Path $WorkDir $MarkerName))) { throw "$WorkDir exists and wasn't created by this script. Choose another -WorkDir." }
    Remove-Item $WorkDir -Recurse -Force
}
$IsoDir = Join-Path $WorkDir 'iso'
$Mount = Join-Path $WorkDir 'mount'
$Wim = Join-Path $WorkDir 'install.wim'
New-Item -ItemType Directory -Path $IsoDir, $Mount -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $WorkDir $MarkerName) | Out-Null

# ───────────────────────── 1. copy Windows setup ─────────────────────────
Step 'Copying Windows setup files'
if ((Get-Item $WindowsSource).PSIsContainer) {
    robocopy (Resolve-Path $WindowsSource).Path.TrimEnd('\') $IsoDir /E /XD 'System Volume Information' /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
}
else {
    $isoPath = (Resolve-Path $WindowsSource).Path
    $disk = Mount-DiskImage -ImagePath $isoPath -PassThru
    try {
        $drive = ($disk | Get-Volume).DriveLetter
        robocopy "$($drive):\" $IsoDir /E /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
    }
    finally { Dismount-DiskImage -ImagePath $isoPath | Out-Null }
}
Get-ChildItem $IsoDir -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
# A previous Norrsken stick may already carry the setup payload; it's rebuilt below.
Remove-Item "$IsoDir\Norrsken", "$IsoDir\Install Norrsken.cmd" -Recurse -Force -ErrorAction SilentlyContinue

# ───────────────────────── 2. editions ─────────────────────────
Step 'Choosing editions'
$sources = "$IsoDir\sources"
$parts = @(Get-ChildItem $sources -Filter 'install*.swm' -ErrorAction SilentlyContinue)
$single = @(Get-ChildItem $sources -File | Where-Object Name -In 'install.wim', 'install.esd')
if ($parts.Count -gt 0) { $image = "$sources\install.swm"; $pattern = "$sources\install*.swm" }
elseif ($single.Count -gt 0) { $image = $single[0].FullName; $pattern = $null }
else { throw 'No install image in the Windows source.' }
$wanted = 'Core', 'CoreSingleLanguage', 'Professional'
$chosen = foreach ($img in Get-WindowsImage -ImagePath $image) {
    $info = Get-WindowsImage -ImagePath $image -Index $img.ImageIndex
    if ($info.EditionId -in $wanted) { $info }
}
if (-not $chosen) { throw 'No Home/Pro edition in this Windows source.' }
foreach ($c in $chosen) {
    Write-Host "   $($c.ImageName) ($($c.EditionId))"
    $export = @{ SourceImagePath = $image; SourceIndex = $c.ImageIndex; DestinationImagePath = $Wim; CompressionType = 'Max' }
    if ($pattern) { $export.SplitImageFilePattern = $pattern }
    Export-WindowsImage @export | Out-Null
}

# ───────────────────────── 3. build Norrsken into each edition ─────────────────────────
$count = (Get-WindowsImage -ImagePath $Wim).Count
for ($i = 1; $i -le $count; $i++) {
    Step "Building Norrsken into $((Get-WindowsImage -ImagePath $Wim -Index $i).ImageName) ($i of $count)"
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

# ───────────────────────── 4. assemble and write the ISO ─────────────────────────
Step 'Writing the bootable ISO (UEFI + legacy BIOS)'
($parts + $single) | Remove-Item -Force
Move-Item $Wim "$sources\install.wim"
Add-NorrskenSetupToMedia -MediaRoot $IsoDir -Payload $Payload -Assets $Assets
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$iso = Join-Path $OutDir "Norrsken-$NorrskenVersion-x64.iso"
$bootdata = "2#p0,e,b$IsoDir\boot\etfsboot.com#pEF,e,b$IsoDir\efi\microsoft\boot\efisys.bin"
& $oscdimg.FullName -m -o -u2 -udfver102 -lNORRSKEN_OS "-bootdata:$bootdata" $IsoDir $iso
if ($LASTEXITCODE -ne 0) { throw "oscdimg failed with code $LASTEXITCODE" }

$hash = (Get-FileHash $iso -Algorithm SHA256).Hash
"$hash  $(Split-Path $iso -Leaf)" | Set-Content "$iso.sha256" -Encoding ascii
if (-not $KeepWork) { Remove-Item $WorkDir -Recurse -Force }

Step 'Done'
Write-Host "   ISO      $iso"
Write-Host "   Size     $([math]::Round((Get-Item $iso).Length / 1GB, 2)) GB"
Write-Host "   SHA-256  $hash"
Write-Host "   Editions $($chosen.ImageName -join ', ')  ·  Norrsken $NorrskenVersion"
