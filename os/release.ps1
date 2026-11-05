<#
.SYNOPSIS
    Publishes a Norrsken release on GitHub (gomorron-dev/norrsken). Every installed Norrsken
    finds it within six hours (or at once with Settings › Updates › Check now) and updates
    itself and Flickerbox.

.DESCRIPTION
    1. Builds the payload (publish.ps1), including the latest Flickerbox build.
    2. Zips it as out\Norrsken-<version>.zip (<version> comes from Norrsken.Shell.csproj).
    3. Creates the GitHub release v<version> with that zip, using the GitHub CLI (gh).

    Bump <Version> in src\Norrsken.Shell\Norrsken.Shell.csproj first: installed copies only take a
    release that is newer than themselves.

.EXAMPLE
    .\release.ps1 -Notes "Smoother dock, Chrome title bars fixed"
#>
[CmdletBinding()]
param([string] $Notes = '', [switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = 'gomorron-dev/norrsken'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI is missing: winget install GitHub.cli, then gh auth login.' }
$csproj = Join-Path $PSScriptRoot '..\src\Norrsken.Shell\Norrsken.Shell.csproj'
$version = ([regex]::Match([IO.File]::ReadAllText($csproj), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $version) { throw "No <Version> in $csproj" }
$tag = "v$version"
gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists. Bump <Version> in Norrsken.Shell.csproj first." }

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$payload = Join-Path $PSScriptRoot 'payload\Norrsken'
if (-not (Test-Path (Join-Path $payload 'Norrsken.Shell.exe'))) { throw "No payload in $payload" }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$zip = Join-Path $out "Norrsken-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Write-Host "== Zipping $payload"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "   $([math]::Round((Get-Item $zip).Length / 1MB)) MB"

Write-Host "== Publishing $tag to github.com/$repo"
if (-not $Notes) { $Notes = "Norrsken $version" }
gh release create $tag $zip --repo $repo --title "Norrsken $version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "== Done: installed copies pick up $tag on their next check."