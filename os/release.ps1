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
param([string] $Notes = '', [string] $NotesFile = '', [switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = 'gomorron-dev/norrsken'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI is missing: winget install GitHub.cli, then gh auth login.' }
$csproj = Join-Path $PSScriptRoot '..\src\Norrsken.Shell\Norrsken.Shell.csproj'
$version = ([regex]::Match([IO.File]::ReadAllText($csproj), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $version) { throw "No <Version> in $csproj" }
$tag = "v$version"
# Windows PowerShell turns gh's "release not found" on stderr into an error under Stop: ask with errors off.
$ErrorActionPreference = 'Continue'; gh release view $tag --repo $repo 2>&1 | Out-Null; $exists = $LASTEXITCODE -eq 0; $ErrorActionPreference = 'Stop'
if ($exists) { throw "Release $tag already exists. Bump <Version> in Norrsken.Shell.csproj first." }

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$payload = Join-Path $PSScriptRoot 'payload\Norrsken'
if (-not (Test-Path (Join-Path $payload 'Norrsken.Shell.exe'))) { throw "No payload in $payload" }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$zip = Join-Path $out "Norrsken-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Write-Host "== Zipping $payload"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Everything but sfx\ (interface sounds from a personal library: they ship on sticks and installs, not publicly).
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $payload -Recurse -File | Where-Object { $_.FullName.Substring($payload.Length + 1) -notlike 'sfx\*' } | ForEach-Object {
        $rel = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Host "   $([math]::Round((Get-Item $zip).Length / 1MB)) MB"

Write-Host "== Publishing $tag to github.com/$repo"
if ($NotesFile) { $Notes = [IO.File]::ReadAllText((Resolve-Path $NotesFile)) } # quotes and line breaks survive a file, not a command line
if (-not $Notes) { $Notes = "Norrsken $version" }
$notesPath = Join-Path $out "notes-$version.md"; [IO.File]::WriteAllText($notesPath, $Notes, (New-Object Text.UTF8Encoding($false)))
gh release create $tag $zip --repo $repo --title "Norrsken $version" --notes-file $notesPath
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "== Done: installed copies pick up $tag on their next check."