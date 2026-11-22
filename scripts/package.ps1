# Publishes the self-contained exe and zips it with the license notices for a GitHub release.
# Set DOTNET_ROOT or PATH first if the .NET 10 SDK isn't the default dotnet.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Clearcut.csproj'
$release = Join-Path $root 'release'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

& (Join-Path $PSScriptRoot 'get-models.ps1')
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    --source https://api.nuget.org/v3/index.json -o $release
if ($LASTEXITCODE) { throw 'Publish failed.' }
Remove-Item (Join-Path $release '*.lib') -ErrorAction SilentlyContinue

$zip = Join-Path $release "Clearcut-v$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $release 'Clearcut.exe'), (Join-Path $release 'notices') -DestinationPath $zip
Write-Host "Packaged $zip"
