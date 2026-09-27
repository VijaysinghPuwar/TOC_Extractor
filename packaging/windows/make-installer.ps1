# Build the Windows app and an installer for it.
#
#   pwsh packaging/windows/make-installer.ps1
#
# Writes dist/TOC-Extractor-<version>-windows-x64-setup.exe, which installs
# the app with a Start menu entry and an uninstaller. Needs Inno Setup 6
# (preinstalled on GitHub's Windows runners). Self-contained: the person
# downloading it needs no .NET.
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

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
$compiler = if ($iscc) { $iscc.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $compiler)) { throw "Inno Setup 6 not found; install it with: choco install innosetup" }

& $compiler "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$out" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
Write-Host "built $(Join-Path $out "TOC-Extractor-$version-windows-x64-setup.exe")"
