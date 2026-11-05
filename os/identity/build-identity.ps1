<#
.SYNOPSIS
    Builds Norrsken's sparse identity package (Norrsken.msix + norrsken.cer) into -Out.
.DESCRIPTION
    The package contains no program files: it only gives the normally installed Norrsken.Shell.exe a
    package identity, which Windows requires before an app may read your notifications. It's signed
    with a local self-signed certificate (created once, kept next to this script); the installer adds
    that certificate to the PC's Trusted People store so Windows accepts the package.
#>
param([Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'

$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { Test-Path (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK tools (makeappx, signtool) not found. Install the Windows SDK.' }

$here = $PSScriptRoot
$pfx = Join-Path $here 'norrsken-signing.pfx'
$cer = Join-Path $here 'norrsken.cer'
$password = 'norrsken'   # local, personal-use signing key
if (-not (Test-Path $pfx)) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Norrsken' -KeyUsage DigitalSignature -FriendlyName 'Norrsken package signing' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(20) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

$stage = Join-Path $env:TEMP 'norrsken-identity'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
Copy-Item (Join-Path $here 'AppxManifest.xml') $stage

# Package logos, scaled from the Norrsken OEM logo.
Add-Type -AssemblyName System.Drawing
$logo = [System.Drawing.Image]::FromFile((Resolve-Path (Join-Path $here '..\assets\oemlogo.bmp')))
foreach ($l in @(@('StoreLogo', 50), @('Square150', 150), @('Square44', 44))) {
    $bmp = New-Object System.Drawing.Bitmap $l[1], $l[1]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($logo, 0, 0, $l[1], $l[1])
    $g.Dispose()
    $bmp.Save((Join-Path $stage "Assets\$($l[0]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$logo.Dispose()

New-Item -ItemType Directory -Path $Out -Force | Out-Null
$msix = Join-Path $Out 'Norrsken.msix'
& (Join-Path $sdk 'makeappx.exe') pack /d $stage /p $msix /nv /o | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed ($LASTEXITCODE)" }
& (Join-Path $sdk 'signtool.exe') sign /fd SHA256 /f $pfx /p $password $msix | Out-Null
if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE)" }
Copy-Item $cer (Join-Path $Out 'norrsken.cer') -Force
Write-Host "Identity package: $msix"
