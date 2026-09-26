# Build the Windows app and a zip of it.
#
#   pwsh packaging/windows/make-zip.ps1
#
# Writes dist/TOC-Extractor-<version>-windows-x64.zip holding a folder with
# TocExtractor.exe. Self-contained: the person downloading it needs no .NET.
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$props = Get-Content (Join-Path $root 'dotnet\Directory.Build.props') -Raw
$version = [regex]::Match($props, '<Version>(.*?)</Version>').Groups[1].Value
$out = Join-Path $root 'dist'
$app = Join-Path $out 'stage-win-x64\TOC Extractor'

if (Test-Path (Join-Path $out 'stage-win-x64')) { Remove-Item -Recurse -Force (Join-Path $out 'stage-win-x64') }
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish (Join-Path $root 'dotnet\src\TocExtractor.Desktop') `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishReadyToRun=true `
  --output $app
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# A windowed app has no console, so the self-test reports by exit code.
$test = Start-Process -FilePath (Join-Path $app 'TocExtractor.exe') -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
if ($test.ExitCode -ne 0) { throw "self-test failed with exit code $($test.ExitCode)" }
Write-Host 'self-test passed'

$zip = Join-Path $out "TOC-Extractor-$version-windows-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $app -DestinationPath $zip
Write-Host "built $zip"
