# Builds dist\Clipper.exe: one self-contained exe with the .NET runtime and FFmpeg inside.
# Requires the .NET 8 SDK. Run from anywhere: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$build = Join-Path $root 'build'
$zip = Join-Path $build 'ffmpeg.zip'

if (-not (Test-Path $zip)) {
    New-Item -ItemType Directory -Force $build | Out-Null
    # BtbN's "shared" build: small ffmpeg/ffprobe exes + DLLs they share, much smaller than two static exes.
    # Asset names on the rolling "latest" release change with FFmpeg versions; update this if it 404s.
    $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip'
    $download = Join-Path $build 'ffmpeg-download.zip'
    $extract = Join-Path $build 'ffmpeg-extract'
    Write-Host "Downloading FFmpeg from $url"
    Invoke-WebRequest $url -OutFile $download
    Expand-Archive $download $extract -Force
    $pkg = Get-ChildItem $extract -Directory | Select-Object -First 1
    $files = @(Get-ChildItem (Join-Path $pkg.FullName 'bin') | Where-Object Name -ne 'ffplay.exe') + @(Get-Item (Join-Path $pkg.FullName 'LICENSE.txt'))
    $files | Compress-Archive -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $download, $extract -Recurse -Force
}

dotnet publish (Join-Path $root 'src') -c Release -o (Join-Path $root 'dist')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $(Join-Path $root 'dist\Clipper.exe')"
