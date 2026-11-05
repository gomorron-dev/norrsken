<#
.SYNOPSIS
    Builds everything that goes into the Norrsken payload (os\payload\Norrsken), which build-iso.ps1
    and make-usb.ps1 then put into the Windows image and onto the stick:
      * Norrsken itself (self-contained .NET, ReadyToRun)
      * identity\  Norrsken's signed sparse package (lets it read notifications)
      * apps\      bundled apps installed on first sign-in (Flickerbox)
.EXAMPLE
    .\publish.ps1
#>
param([string] $Flickerbox = (Join-Path $PSScriptRoot '..\..\! Flickerbox\build\stage'), [string] $SoundLibrary = (Join-Path $PSScriptRoot '..\..\! Babblebox\sfx\library'))
$ErrorActionPreference = 'Stop'

$payload = Join-Path $PSScriptRoot 'payload\Norrsken'
Write-Host '== Norrsken'
dotnet publish (Join-Path $PSScriptRoot '..\src\Norrsken.Shell') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $payload -nologo -v q
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host '== Identity package'
& (Join-Path $PSScriptRoot 'identity\build-identity.ps1') -Out (Join-Path $payload 'identity')

Write-Host '== Bundled apps'
New-Item -ItemType Directory -Path (Join-Path $payload 'apps') -Force | Out-Null
if (Test-Path (Join-Path $Flickerbox 'Flickerbox.exe')) { robocopy $Flickerbox (Join-Path $payload 'apps\Flickerbox') /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null; if ($LASTEXITCODE -ge 8) { throw 'copying Flickerbox failed' }; Write-Host '   Flickerbox' }
else { Write-Warning "Flickerbox build not found at $Flickerbox (build it with its tools\build.ps1); Norrsken will skip it." }

$exe = Join-Path $payload 'Norrsken.Shell.exe'
$p = Start-Process $exe -ArgumentList '--definitely-not-an-option' -PassThru -Wait
if ($p.ExitCode -ne 64) { throw "The published Norrsken didn't refuse an unknown option (exit $($p.ExitCode))." }
# Interface sounds (off until turned on in Settings › Sound), picked from the Babblebox library and named after
# the events they play for. They stay on the stick and installs only: never in git or the public release zip.
$sounds = [ordered]@{
    'startup' = 'glass ding 11.wav'; 'launcher' = 'swoosh 1.wav'; 'click' = 'airpods case closed 1.wav'; 'toggle' = 'light switch on.wav'
    'notify' = 'clear-bell.wav'; 'capture' = 'clicky button 13.wav'; 'trash' = 'wood and metal cabinet closed 1.wav'; 'empty' = 'paper ripping 4.wav'
    'restore' = 'bubble.wav'; 'error' = 'SFX_UI_ErrorBuzz_03.wav'; 'hover' = 'SFX_UI_HoverTick_03.wav'; 'volume' = 'SFX_UI_HoverTick_05.wav'
}
if (Test-Path $SoundLibrary) {
    $sfx = Join-Path $payload 'sfx'
    New-Item -ItemType Directory -Path $sfx -Force | Out-Null
    $n = 0
    foreach ($e in $sounds.GetEnumerator()) {
        $src = Get-ChildItem $SoundLibrary -Recurse -File -Filter $e.Value -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($src) { Copy-Item $src.FullName (Join-Path $sfx ($e.Key + $src.Extension)) -Force; $n++ } else { Write-Warning "sound '$($e.Value)' not found for $($e.Key)" }
    }
    Write-Host "   $n interface sounds"
}
else { Write-Warning "Sound library not found at $SoundLibrary; Norrsken ships without interface sounds." }
Write-Host "== Done: $payload ($([math]::Round((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB)"
