# Third-party notices

Satsuma itself is MIT — see [LICENSE](LICENSE). It is built on other people's work, and
some of that work travels inside the release zip. This is what is in there and under what
terms.

## Bundled in the release

These are in the folder you unzip. Nothing here needs installing, and nothing here is
Satsuma's own code.

### .NET 8 runtime — MIT

Satsuma is published self-contained, so the runtime is in the folder rather than on your
machine. Copyright (c) .NET Foundation and Contributors.

<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

### ffmpeg — LGPL-2.1-or-later

`tools\ffmpeg.exe` and `tools\ffprobe.exe`, with the DLLs beside them, do everything
Satsuma knows about video and audio, plus WebP and AVIF images. They are
[BtbN's LGPL shared builds](https://github.com/BtbN/FFmpeg-Builds), used unmodified and
run as separate programs; Satsuma links against nothing in them.

The LGPL build is deliberate. The GPL builds carry encoders Satsuma never calls and a
licence that would reach the rest of the folder.

The licence text travels with the binaries as `tools\ffmpeg-LICENSE.txt`, and
`tools\ffmpeg-README.txt` records which build it is and where its source is. ffmpeg's
source is at <https://ffmpeg.org> and for these exact binaries at the BtbN releases page
above.

## Referenced by the code

NuGet packages Satsuma compiles against. Their licences are reproduced in full in the
packages themselves.

### PdfPig — Apache-2.0

Reads and writes PDF structure: merging, splitting, keeping a page range, and the
lossless rewrite half of Compress.

<https://github.com/UglyToad/PdfPig>

### Docnet.Core — MIT

Rasterises PDF pages, which is how a PDF becomes images and how the scanned half of
Compress works. It carries a build of **PDFium** (BSD-3-Clause, © The Chromium Authors).

<https://github.com/GowenGit/docnet>

## Not bundled, not required

### LibreOffice — MPL-2.0

Satsuma uses an installed LibreOffice to convert documents, spreadsheets and slides. It
is **not** shipped: it is several hundred megabytes, more than everything else here put
together, for the corner of the app most people never reach, and it releases on its own
schedule.

Satsuma finds one that is already installed, or one placed at `tools\LibreOffice` beside
`Satsuma.exe`. Everything else in Satsuma works without it.

<https://www.libreoffice.org>

## The idea

Satsuma is a Windows reimplementation of the interaction in
[Tangerine](https://tangerineformac.com/) for macOS, by Thameem (Leap Studio). None of
Tangerine's code is here; the two differ wherever Windows does. The debt is to the idea,
and it is gladly acknowledged.
