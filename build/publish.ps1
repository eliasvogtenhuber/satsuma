<#
.SYNOPSIS
    Builds the shipping folder: Satsuma, self-contained, with ffmpeg beside it.

.DESCRIPTION
    The result is a folder a user can unzip anywhere and run. Nothing to install: not the
    .NET runtime, which Windows does not carry, and not ffmpeg, which everything in video
    and audio depends on.

    ffmpeg goes into tools\ next to the exe because that is the first place FfmpegLocator
    looks. A copy already on PATH still wins nothing -- the bundled one is found first,
    which is deliberate: the version shipped is the version tested.

    LibreOffice is not bundled and cannot sensibly be: it is around 700MB and would be
    three quarters of the download for the one thing it does that nothing else can, which
    is laying out a document as pages. It stays an optional install.

.PARAMETER Output
    Where the folder is built. Defaults to artifacts\ at the repository root.

.PARAMETER Version
    Version stamped into the binaries and the zip name. Defaults to SatsumaVersion from
    Directory.Build.props.

.PARAMETER SkipFfmpeg
    Builds without ffmpeg, for a quick local check. The result still runs; video and audio
    simply stay off the wheel, exactly as on a machine without it.

.PARAMETER NoZip
    Leaves the folder without packing it.

.EXAMPLE
    pwsh build\publish.ps1
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $Version,
    [switch] $SkipFfmpeg,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<SatsumaVersion>([^<]+)</SatsumaVersion>') { $Version = $Matches[1] }
    else { throw 'No SatsumaVersion in Directory.Build.props, and none given.' }
}

if (-not $Output) { $Output = Join-Path $root 'artifacts' }

$stage = Join-Path $Output "Satsuma-$Version-win-x64"

Write-Host "Satsuma $Version -> $stage" -ForegroundColor Cyan

# ---- The app ---------------------------------------------------------------

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

& dotnet publish (Join-Path $root 'src\Satsuma.App\Satsuma.App.csproj') `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE." }

# The publish folder carries the import libraries and debug symbols of every dependency.
# None of it is read at runtime and it is a tenth of the download.
Get-ChildItem $stage -Include *.pdb, *.xml -File -Recurse | Remove-Item -Force

# ---- Licences --------------------------------------------------------------

# A self-contained publish drops the .NET runtime's own licence in as plain "LICENSE",
# which is the only file by that name in the folder a user unzips. Left alone, Satsuma
# ships somebody else's licence as its licence, and its own is nowhere in the download.
$runtimeLicence = Join-Path $stage 'LICENSE'
if (Test-Path $runtimeLicence) {
    Move-Item $runtimeLicence (Join-Path $stage 'dotnet-LICENSE.txt') -Force
}

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt') -Force

# The folder is the whole install, so it is also the only documentation anyone is
# guaranteed to see. Satsuma has no window and does nothing until a key is pressed
# mid-drag; a zip that does not say so is a zip that looks broken.
@"
Satsuma $Version
Tangerine for Windows: file conversion inside the drag you were already doing.


GETTING STARTED

  1. Put this folder somewhere permanent -- it is the whole install, and Satsuma
     remembers where it is. Not your Downloads folder.
  2. Run Satsuma.exe. Windows will warn you about an unknown publisher, because
     the exe is not code-signed: More info -> Run anyway.
  3. Nothing appears. That is correct. Satsuma lives in the system tray, and sets
     itself to start with Windows so it is always there.


USING IT

  Start dragging a file in File Explorer, and while still dragging, press:

      1    convert to another format
      2    tools: compress, resize, rotate, split, merge
      3    actions: reveal, copy, move, shelf

  A wheel appears under the pointer. Lean toward a slice and let go of the mouse
  button. The converted file lands next to the original.

  Or skip the drag: select a file and press the same key. The wheel stays put and
  you click a slice.


  While the wheel is open:

      4         back out of a question and show the wheel again
      Esc       cancel, as does right-clicking

  Held through to the end of the gesture:

      Space     put the results somewhere else. Let go after dropping and Satsuma
                makes a folder for them; keep holding and select one instead.

      Alt       replace the original. It goes to the Recycle Bin, and the result
                takes its name.


WHAT IS IN THIS FOLDER

  Satsuma.exe               the app
  tools\                    ffmpeg, for video, audio, WebP and AVIF
  LICENSE.txt               Satsuma's licence (MIT)
  dotnet-LICENSE.txt        the bundled .NET runtime's licence
  THIRD-PARTY-NOTICES.md    everything else, and under what terms

  Documents, spreadsheets and slides need LibreOffice, which is not bundled: it is
  larger than everything here put together. The tray menu says whether it found one
  and how to install it. Everything else works out of the box.


  Right-click the tray icon for "How it works", settings, and the rest.

  https://github.com/eliasvogtenhuber/satsuma
"@ | Set-Content (Join-Path $stage 'How to use Satsuma.txt') -Encoding utf8

$notices = Join-Path $root 'THIRD-PARTY-NOTICES.md'
if (Test-Path $notices) { Copy-Item $notices $stage -Force }

# ---- ffmpeg ----------------------------------------------------------------

# An LGPL build, used as a separate process and shipped unmodified. That keeps it clear of
# Satsuma's own MIT licence: nothing here links against it. The GPL builds carry encoders
# Satsuma never calls, and a licence that would reach the rest of the folder.
$ffmpegAsset = 'ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip'
$ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$ffmpegAsset"

# Only what the two executables actually load. The full archive is 181MB; most of it is
# ffplay, the HTML manual and the import libraries for people compiling against it.
$ffmpegWanted = @(
    'ffmpeg.exe', 'ffprobe.exe',
    'avcodec-*.dll', 'avdevice-*.dll', 'avfilter-*.dll', 'avformat-*.dll',
    'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll'
)

if (-not $SkipFfmpeg) {
    $cache = Join-Path $Output '.cache'
    New-Item -ItemType Directory -Force $cache | Out-Null

    $zip = Join-Path $cache $ffmpegAsset
    if (-not (Test-Path $zip)) {
        Write-Host "Fetching $ffmpegAsset" -ForegroundColor Cyan
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $zip -UseBasicParsing
    }
    else {
        Write-Host "Using cached $ffmpegAsset" -ForegroundColor DarkGray
    }

    $unpacked = Join-Path $cache 'ffmpeg'
    if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
    Expand-Archive -Path $zip -DestinationPath $unpacked -Force

    $bin = Get-ChildItem $unpacked -Directory -Recurse |
        Where-Object { $_.Name -eq 'bin' } |
        Select-Object -First 1
    if (-not $bin) { throw "No bin\ folder inside $ffmpegAsset." }

    $tools = Join-Path $stage 'tools'
    New-Item -ItemType Directory -Force $tools | Out-Null

    foreach ($pattern in $ffmpegWanted) {
        $found = Get-ChildItem (Join-Path $bin.FullName $pattern) -ErrorAction SilentlyContinue
        if (-not $found) { throw "ffmpeg build has no $pattern; the asset layout changed." }
        Copy-Item $found.FullName $tools
    }

    # LGPL asks that the licence travel with the binaries and that their source be
    # findable. Both live here rather than in a file nobody opens.
    Copy-Item (Join-Path $bin.Parent.FullName 'LICENSE.txt') (Join-Path $tools 'ffmpeg-LICENSE.txt') -ErrorAction SilentlyContinue

    @"
FFmpeg, shipped unmodified under the LGPL v2.1 or later.

Build:   $ffmpegAsset
Source:  https://github.com/BtbN/FFmpeg-Builds
Project: https://ffmpeg.org

Satsuma runs these as separate processes and does not link against them. Satsuma's own
licence is MIT and covers only Satsuma's own code; see LICENSE.txt and
THIRD-PARTY-NOTICES.md in the folder above this one.
"@ | Set-Content (Join-Path $tools 'README.txt') -Encoding utf8

    $size = (Get-ChildItem $tools | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("ffmpeg bundled: {0:N0} MB in tools\" -f $size) -ForegroundColor Green
}
else {
    Write-Host 'ffmpeg skipped; video and audio will not appear on the wheel.' -ForegroundColor Yellow
}

# ---- Pack ------------------------------------------------------------------

$total = (Get-ChildItem $stage -File -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Folder: {0:N0} MB" -f $total) -ForegroundColor Green

if (-not $NoZip) {
    $archive = "$stage.zip"
    if (Test-Path $archive) { Remove-Item -Force $archive }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
    Write-Host ("Zip: {0} ({1:N0} MB)" -f $archive, ((Get-Item $archive).Length / 1MB)) -ForegroundColor Green
}
