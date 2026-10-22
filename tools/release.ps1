<#
.SYNOPSIS
Builds the release packages and update.json into publish/release/v<version>.
Upload every file from that folder to a GitHub release tagged v<version> of UpdateRepository.

.EXAMPLE
pwsh tools/release.ps1 -Notes "Nowy edytor configów"
#>
param(
    [string]$Notes = "",
    [string]$Version,                         # defaults to <Version> in Directory.Build.props
    [string]$BaseUrl,                         # where the zips will be downloadable; defaults to the GitHub release
    [string[]]$Rids = @("win-x64", "linux-x64")
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot)
$props = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup
if (-not $Version) { $Version = $props.Version }
if (-not $BaseUrl) { $BaseUrl = "https://github.com/$($props.UpdateRepository)/releases/download/v$Version" }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source ?? "$env:ProgramFiles\dotnet\dotnet.exe"
$out = Join-Path (Get-Location) "publish/release/v$Version"
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$assets = [ordered]@{}
foreach ($rid in $Rids) {
    $build = Join-Path $out $rid
    & $dotnet publish src/ModpackManager.App -c Release -r $rid --self-contained -p:Version=$Version -o $build
    if ($LASTEXITCODE) { throw "dotnet publish $rid failed" }

    $zip = Join-Path $out "ModpackManager-$rid.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    $archive = [IO.Compression.ZipFile]::Open($zip, "Create")
    try {
        # debug symbols (the SkiaSharp one alone is huge) are not needed to run the app
        Get-ChildItem $build -Recurse -File | Where-Object Extension -ne ".pdb" | ForEach-Object {
            $name = [IO.Path]::GetRelativePath($build, $_.FullName).Replace('\', '/')
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, "Optimal")
            # rwxr-xr-x on the Linux executable, so a plain unzip keeps it runnable
            if ($rid -like "linux-*" -and $name -eq "ModpackManager.App") { $entry.ExternalAttributes = 0x81ED0000 }
        }
    } finally {
        $archive.Dispose()
    }
    $assets[$rid] = [ordered]@{
        url    = "$BaseUrl/ModpackManager-$rid.zip"
        sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

[ordered]@{ version = $Version; notes = $Notes; assets = $assets } | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $out "update.json") -Encoding utf8NoBOM
Get-ChildItem $out -File | Select-Object Name, Length
