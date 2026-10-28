# Known limitations

Things Satsuma cannot do, and why. Each of these is a deliberate stopping point rather
than an oversight.

## Drags from elevated windows

Satsuma runs unelevated, on purpose. Windows' UIPI stops an unelevated process from
receiving input from a window running as administrator, so a drag that starts in an
elevated Explorer never reaches Satsuma's hooks and no wheel appears.

Running Satsuma elevated would fix it and is not worth it: a tray app that watches all
keyboard and mouse input should hold the fewest privileges it can.

## HEIC output

HEIC reads fine — Satsuma decodes through Windows Imaging Component, so it picks up the
HEIF codec Windows 11 ships with. Writing it is another matter. WIC's HEIF encoder is not
reachable through WPF's imaging types, and ffmpeg has no usable HEIF muxer. So HEIC is
input-only, and an image tool applied to a HEIC writes a JPG instead.

## PDF, Office and document formats

Catalogued in `FormatCatalog` but not implemented, so no handler claims them and they are
filtered off the wheel before it is drawn. Doing them properly means either bundling
LibreOffice or detecting an install, which is a size and licensing decision worth making
deliberately.

## RAR and 7z

Neither has a usable managed reader in the box. ZIP, TAR and GZIP are handled in-process
with the base class library; RAR and 7z would need a third-party dependency.

## The Windows share sheet

Tangerine's AirDrop slice has no clean Windows twin. The nearest thing is the share sheet,
which hosts Nearby Sharing, but it is a WinRT surface that wants a focused window to
anchor to — and the wheel is deliberately never focused, because taking focus would cancel
the drag it is sitting in. Reveal takes that slice for now.

## Drag sources that are not files

Satsuma reads `CF_HDROP`, the format Explorer and most apps use for real files on disk.
Some sources offer only a virtual-file descriptor instead — an unsaved mail attachment is
the usual one. Those are not handled, and the wheel will show nothing to do with them.

## Binding a wheel to Shift, Ctrl or Alt

Supported, but it costs you something. During a drag those three are how Explorer is told
to move, copy or make a shortcut. Satsuma swallows the key it is bound to so no app
underneath sees it, which means Explorer stops seeing it too. The defaults are 1, 2 and 3
to avoid this entirely.

## Multi-monitor and mixed DPI

Handled: the wheel reads the DPI of the monitor under the pointer before it exists as a
window, positions itself in physical pixels, and clamps to that monitor's work area. Worth
knowing if you change the wheel size — `WheelDiameter` is in device-independent pixels, so
it means the same apparent size on every display.

## The tray icon is drawn, not shipped

There is no `.ico` in the repository. The tray icon is drawn at runtime with GDI+, so it
stays crisp at any tray scale and there is no asset to keep in step. The consequence is
that `Satsuma.exe` itself has the default .NET icon in Explorer. Dropping a real
`Assets/satsuma.ico` into `src/Satsuma.App` is enough to fix that — the project file picks
it up if it exists.
