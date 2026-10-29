# Satsuma

File conversion that lives inside the drag you were already doing.

Start dragging a file in File Explorer or on the Desktop, press **1**, and a wheel of
output formats appears under the pointer. Lean toward one, let go, and a converted copy
lands next to the original. No window, no upload, no app to open.

Satsuma is a Windows take on [Tangerine](https://tangerine.pet) for macOS by Thameem
(Leap Studio). This is an independent reimplementation, not a port — none of Tangerine's
code is here, and the two differ wherever Windows does.

![The wheel over a dark desktop](docs/wheel-dark.png)

## Status

Working and usable, but early. Images, video, audio, subtitles and archives convert today.
PDF works both ways: pages out as images or text, and images in. Office formats are
catalogued but not implemented, so they do not appear on the wheel — see
[Roadmap](#roadmap).

## How it works

There are two ways in.

**Dragging.** Hold the left mouse button and start dragging, then press a number key:

| Key | Wheel | What is on it |
| --- | --- | --- |
| **1** | Convert to | Output formats for whatever you are dragging |
| **2** | Tools | Compress, resize, rotate, trim, normalise, and so on |
| **3** | Actions | Reveal, Float, Copy, Move, Copy path, Terminal |

Then:

- **Pick a slice** by leaning toward it — the whole wedge is live, you do not have to land
  on it precisely — and release the mouse button to drop.
- **Presets** (Compress, Resize, Rotate) open a second ring. Hover one for a moment, or
  drop onto it, and the ring becomes its presets.
- **The centre** cancels. Inside a preset ring, resting there steps back out instead:
  going back has to work on hover, because the mouse button is still held and releasing
  it to click would end the drag rather than step back.
- **Esc** or **right-click** cancels outright.
- The wheel stays up after you release the key, so you can take your time.

**Clicking.** Select a file in Explorer and press the number key, or hold the key and
then click a file. The wheel opens where you
clicked and stays there, so you can let go of everything and take your time. The wheel opens where you are and stays there, so you can let go of everything and take
your time. Click a slice to pick it, press 1, 2 or 3 to switch between the wheels, click
the centre to go back out of a preset ring, and click away or press Esc to put it away.

This only arms over Explorer and the Desktop. Everywhere else a number key belongs to
whatever you are typing into.

Results are always new files written beside the original. Satsuma never overwrites or
moves what you dragged.

### Why number keys and not Shift, Ctrl and Alt

Tangerine uses modifiers, and on Windows that would be a mistake. During a drag, Explorer
reads **Shift** as move, **Ctrl** as copy and **Alt** as make-a-shortcut. Taking those over
would cost you the standard way of choosing between copy and move, which is worth more
than matching the original's key choice. Number keys do nothing during a drag — Explorer
suspends type-ahead while one is in flight — so they are free to take.

All three are rebindable; see [Settings](#settings).

## What converts

Everything below is covered by the self-test suite and verified to run.

| From | To |
| --- | --- |
| **Images** — JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, GIF, SVG\*, RAW\* | JPG, PNG, TIFF, BMP, GIF, WebP†, AVIF† |
| **Video** — MP4, MOV, MKV, WebM, AVI, WMV, and more | MP4, MOV, MKV, WebM, AVI, WMV, animated GIF, MP3 |
| **Audio** — MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA |
| **PDF** | JPG, PNG (300 dpi, one per page), TXT, Markdown |
| **Subtitles** — SRT, VTT, ASS | SRT†, VTT†, TXT |
| **Archives / folders** | ZIP, TAR, GZIP, and Extract |

\* read only  †  needs ffmpeg

**Tools.** Images: compress, resize, rotate, flip, grayscale, strip metadata. Video:
compress, resize, speed, rotate, remove audio, snapshot, extract audio, join. Audio:
compress, normalise, to mono, strip tags, join. PDF: merge, split, compress.

The wheel only ever shows conversions that actually work on your machine. Each one is
claimed by a handler, and a format no handler can do is never drawn — so if ffmpeg is
missing, the video wheel is honest about it instead of failing after the drop.

### HEIC

HEIC **input** works out of the box on Windows 11, because Satsuma decodes through Windows'
own imaging stack and picks up whatever codecs are installed. HEIC **output** is not
supported: nothing shippable can write it. Compressing a HEIC gives you a JPG.

## Installing

Satsuma needs **Windows 10 1809 or later**, x64.

```bash
git clone https://github.com/YOUR-NAME/satsuma.git
```

Build it with the .NET 8 SDK:

```bash
dotnet build Satsuma.sln -c Release
```

The app is `src/Satsuma.App/bin/x64/Release/net8.0-windows/Satsuma.exe`. It starts in the
tray; right-click the tray icon for the menu, and tick **Start with Windows** if you want
it there permanently.

### ffmpeg

Images that Windows can read and write need nothing. Video, audio, WebP and AVIF need
ffmpeg:

```bash
winget install BtbN.FFmpeg.GPL
```

Satsuma finds it on `PATH`, in a `tools\` folder next to the exe, or in the usual winget
install location. Restart Satsuma afterwards. Until it is there, those slices simply do
not appear.

## Settings

`%APPDATA%\Satsuma\settings.json`, reachable from the tray menu. Changes apply on restart.

```json
{
  "FormatsKey": "1",
  "ToolsKey": "2",
  "ActionsKey": "3",
  "SelectionStyle": "Depth",
  "GlassOpacity": 1.0,
  "GlassBlur": 24,
  "RevealResults": false,
  "WheelDiameter": 348,
  "RunAtLogin": false
}
```

Key names are what you would write: `"1"`, `"Space"`, `"F2"`, `"Q"`. Digits cover both the
top row and the numpad. `"Shift"`, `"Ctrl"` and `"Alt"` work too, if you want the Mac
behaviour and accept losing Explorer's copy/move for the duration of a drag.

**Appearance.** `GlassOpacity` biases how solid the glass is: 1 is the default, lower is
more see-through, higher is more opaque. It is a multiplier rather than a fixed opacity
because Satsuma already measures what the wheel is covering and tints to suit, so one flat
value would be too heavy over a dark desktop and too light over a white document.
`GlassBlur` is the backdrop blur radius. `SelectionStyle` is `Depth` (the frost under the
chosen slice goes softer) or `Density` (it thickens into a brighter pane).

`RevealResults` opens Explorer on the results of every job. It is off because results land
next to the original, which is usually the folder you were already looking at — click the
progress card to reveal them instead.

## When a drag does nothing

Start Satsuma with `--debug` and it writes a trace to
`%LOCALAPPDATA%\Satsuma\satsuma.log`. The interesting half of Satsuma runs inside global
input hooks during another app's modal drag loop, where there is no window to print to and
nothing to attach a debugger to, so the log is the way to see which link broke:

```
hooks installed: mouse=True, keyboard=True
bound keys: 0x31=Formats, 0x61=Formats, 0x32=Tools, ...
button down at 1440,810
drag detected
bound key 0x31 -> Formats, dragging=True
wheel requested: Formats at 1675,928
showing Formats wheel, seed=1 file(s)
```

If `drag detected` never appears, the pointer never passed the drag threshold. If a bound
key is seen with `dragging=False`, the key arrived before Satsuma judged a drag to have
started. If nothing appears at all, the drag began in an elevated window; see
[limitations](docs/limitations.md).

## Startup

Satsuma builds and renders the wheel once when it starts, off-screen and invisible. The
first time WPF realises that window it costs around a second -- creating it, loading
fonts, compiling the blur, setting up a layered surface -- and none of that work depends
on what is being dragged. Paid at startup it is invisible; paid on the first drag it was a
second of lag at exactly the wrong moment. Afterwards a wheel opens in about 40ms.

## Running the tests

```bash
dotnet run --project tests/Satsuma.SelfTest -c Release
```

The self-test covers the wheel's hit testing as pure geometry, then generates sample
files and drives the real engine over them, checking that a
resize really halves the pixels, that a rotate really swaps the axes, that compressing
really shrinks the file, and that the wheel hides what it cannot do. It skips the ffmpeg
half cleanly when ffmpeg is absent. The drag itself cannot be scripted, so everything
behind it is covered here instead.

## How it is put together

```
src/Satsuma.Core/     Engine: catalogues, handlers, ffmpeg and WIC plumbing. No UI.
src/Satsuma.App/      Tray app: input hooks, the wheel, progress, shelf, actions.
tests/Satsuma.SelfTest/   Drives the engine over generated files.
```

Three pieces do the interesting work:

**Noticing the drag.** Windows has no API for "is a drag in flight", and the drag source
sits in a modal loop inside whichever app started it. Low-level input hooks still fire
during that loop, so Satsuma infers a drag the way the shell itself does — left button
down, pointer past the system drag threshold — and treats a bound key pressed during one
as a request for a wheel.

**Catching the drop.** The wheel is a transparent, topmost window that is never activated,
which is what keeps the drag alive: taking focus would cancel it. It is painted only where
it means to catch something, so the OS routes the drag to Satsuma inside the circle and to
whatever is underneath outside it.

**Knowing what you are dragging.** The wheel has to appear the instant you press the key,
but the dragged paths only arrive when the pointer reaches the window. So Satsuma reads
the selection straight out of the Explorer view the drag started in, and treats it as a
guess — the real paths replace it on the way in, which is also what makes drags from
anywhere else work.

## Roadmap

- PDF to DOCX, PPTX and XLSX, which is layout reconstruction rather than conversion
- PDF to EPUB
- Office and document formats, which need LibreOffice detected or bundled
- Multi-image collages
- Crop, annotate and redact, which need an editor surface rather than a wheel
- The Windows share sheet, Tangerine's AirDrop slice — it is WinRT and wants a focused
  window to anchor to, which the wheel deliberately is not

## Credits

Inspired by [Tangerine](https://tangerine.pet) by Thameem / Leap Studio. If you are on a
Mac, buy theirs.

## Licence

MIT — see [LICENSE](LICENSE).
