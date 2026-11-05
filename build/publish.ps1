<#
.SYNOPSIS
    Builds the portable release zip: ScreenshotApp (self-contained) + ScreenshotApp.Updater.exe (NativeAOT) + file manifest.

.DESCRIPTION
    Output in artifacts\:
      ScreenshotApp-<version>-<runtime>\         the portable folder
      ScreenshotApp-<version>-<runtime>.zip      the release asset (files at the zip root, no top folder)
      release-notes-<version>.md                 this version's CHANGELOG.md section, for the GitHub release body

    The in-app updater needs: the asset name ending in -<runtime>.zip, ScreenshotApp.Updater.exe next to
    ScreenshotApp.exe, and ScreenshotApp.files (every file of the package, one relative path per line) so the next
    update knows which files belong to the app. Tag releases as v<version>.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -Runtime win-arm64
    ./build/publish.ps1 -Version 0.0.1 -Out $env:TEMP\ssold     # an "old" build for testing updates
#>
param(
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [string] $Version,
    [string] $Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $root 'artifacts' }
$csproj = Join-Path $root 'src\ScreenshotApp\ScreenshotApp.csproj'
$projectVersion = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") } else { $Version = $projectVersion }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }

$name = "ScreenshotApp-$Version-$Runtime"
$app = Join-Path $Out $name
$zip = "$app.zip"
New-Item -ItemType Directory -Force $Out | Out-Null
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
if (Test-Path $zip) { Remove-Item -Force $zip }

Write-Host "== ScreenshotApp $Version ($Runtime)"
dotnet publish $csproj -c Release -r $Runtime -p:Platform=$platform --self-contained true -o $app @versionArgs
if ($LASTEXITCODE) { throw 'dotnet publish (app) failed' }

# NativeAOT links with MSVC; its targets look for vswhere.exe on PATH.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vswhere) { $env:PATH = "$env:PATH;$vswhere" }
$updaterOut = Join-Path $Out "updater-$Runtime"
Write-Host "== ScreenshotApp.Updater ($Runtime, NativeAOT)"
dotnet publish (Join-Path $root 'src\ScreenshotApp.Updater\ScreenshotApp.Updater.csproj') -c Release -r $Runtime -o $updaterOut
if ($LASTEXITCODE) { throw 'dotnet publish (updater) failed' }
Copy-Item (Join-Path $updaterOut 'ScreenshotApp.Updater.exe') $app

# Leave out debug symbols; they are not needed to run and add size.
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item -Force

# The manifest lists every file of the package, itself included.
$manifest = Join-Path $app 'ScreenshotApp.files'
$files = Get-ChildItem $app -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($app, $_.FullName) }
@($files) + 'ScreenshotApp.files' | Sort-Object -Unique | Set-Content -Encoding utf8NoBOM $manifest

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# This version's CHANGELOG.md section ("## 0.2.6" up to the next "## "), in the release-body style used so far.
$notes = Join-Path $Out "release-notes-$Version.md"
$section = @()
$inSection = $false
foreach ($line in Get-Content (Join-Path $root 'CHANGELOG.md')) {
    if ($line -match '^## ') {
        if ($inSection) { break }
        $inSection = $line.Trim() -eq "## $Version"
        continue
    }
    if ($inSection) { $section += $line }
}
if ($section.Count -eq 0) { Write-Warning "CHANGELOG.md has no '## $Version' section" }
(@("### $Version", '') + $section) -join "`n" | Set-Content -Encoding utf8NoBOM $notes

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
Write-Host ''
Write-Host "Zip:    $zip ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "SHA256: $hash"
Write-Host "Notes:  $notes"
Write-Host ''
Write-Host 'Release it with:'
Write-Host "  gh release create v$Version `"$zip`" --title `"$Version`" --notes-file `"$notes`""
