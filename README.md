# StealthPDF

StealthPDF is a free and open-source PDF editor for Windows. It can view, annotate, edit, OCR, merge, split, compress, stamp, fill, sign, unlock, print, and flatten PDFs locally - no account, no telemetry, and no cloud upload.

**Website & downloads: [stealthpdf.com](https://stealthpdf.com)** · Source code: [github.com/aabhpsy/StealthPDF](https://github.com/aabhpsy/StealthPDF)

> **StealthPDF** is a community fork and rebrand of the original **KillerPDF** project by **Steve the Killer**, released under GPLv3. This fork is maintained by **[@aabhpsy](https://github.com/aabhpsy)**.

## Features

### Viewing & navigation
- High-quality PDF rendering via PDFium.
- Single Page, Continuous, Two-Page, and Grid view modes.
- Tabbed documents with per-tab page, zoom, view, search, annotation, form, and stamp state.
- Full-text search with highlighted results and drag-select text copy.
- Outline/bookmark navigation and clickable links.
- Fit to Width, Fit Page, zoom presets, mouse-wheel zoom, and full-screen mode.
- Recent files on the start screen and Open menu.

### Menus, toolbar, and shortcuts
- Traditional **File / Edit / View / Tools / Help** menu bar plus an icon toolbar.
- Tooltips on toolbar buttons and a keyboard shortcut overlay.
- Localized UI in multiple languages, including English, English (UK), Nepali, Spanish, Chinese, German, French, Turkish, and Bengali.

### Annotate, edit, and mark up
- Add text/typewriter annotations with font, size, color, bold/italic/underline/strike, and optional background fill.
- Edit existing text-based PDF content with a cover-and-text replacement workflow that matches the detected text color and size where possible.
- Draw freehand ink, straight lines, and highlights with configurable color, width, and opacity.
- Insert images as resizable annotations and burn them into the saved PDF.
- Move, resize, multi-select, restyle, and delete annotations in place.

### Stamping
- Add page numbers and watermarks across a page range.
- Add certification stamps with configurable label, name, date/time, signature image, logo, border style, placement, and scale.
- Certification stamp fill is transparent by default; enable the fill box when a document needs a distinct backing panel.
- Stamp state is per document tab so applying a stamp to one PDF does not auto-apply it to another.

### Fill, sign, and forms
- Fill PDF form text fields, checkboxes, and radio buttons.
- Use Fill & Sign tools for free-placement text, checkmarks, and signatures.
- Store reusable signatures or import a one-off signature image.
- Save or flatten filled/signed PDFs.

### OCR (built in, no cloud)
- OCR a whole page or a dragged region straight to the clipboard.
- Make Searchable PDF by adding an invisible text layer over scanned pages.
- Extract all recognized text to `.txt` or `.md`.
- English and Nepali OCR models are bundled for fully offline use; other supported languages can be downloaded on demand and cached.

### Scan and import
- Scan pages from WIA or TWAIN scanners into a new PDF.
- Choose DPI, color mode, flatbed/feeder options, and duplex where supported.
- Import images or combine selected files into a PDF.

### Organize and convert
- Merge PDFs, split/extract pages, rotate pages, crop pages, and reorder pages.
- Compress PDFs using the bundled compression helper.
- Repair damaged PDFs where possible.
- Open password-protected PDFs and save an unlocked/unencrypted copy when permitted.
- Print with preview and save flattened output.

### App and privacy
- Single portable Windows EXE, or self-install per user to `%LOCALAPPDATA%\Programs\StealthPDF\`.
- No administrator rights required for normal use or per-user install.
- Registers as a PDF handler with branded icons when installed.
- Local-only processing: no account, no telemetry, no phone-home.

## Portable or installed?

You can use the same EXE either way:

| | Portable | Installed |
|---|---|---|
| Admin rights | None | None |
| Location | Wherever you place it | `%LOCALAPPDATA%\Programs\StealthPDF\` |
| Start Menu shortcut | No | Yes |
| Desktop shortcut | No | Optional |
| Open PDFs by default | No | Yes, if selected |
| Add/Remove Programs entry | No | Yes |

If you are just trying StealthPDF, run the portable EXE. If you use it regularly, click **Install** for shortcuts, file association, and clean uninstall support.

## Requirements

- Windows 10 or Windows 11, x64.
- No separate runtime install for normal use. The app targets .NET Framework 4.8, which is present on supported Windows releases.

## Download

- **Get the app from the website: [stealthpdf.com](https://stealthpdf.com)** — the downloadable build (portable EXE / per-user installer) is hosted there.
- GitHub hosts the **source code** (and release archives for packagers): <https://github.com/aabhpsy/StealthPDF>
- Also on package managers: `choco install stealthpdf` · `winget install aabhpsy.StealthPDF`

## Build from source

```powershell
git clone https://github.com/aabhpsy/StealthPDF.git
cd StealthPDF
dotnet build KillerPDF/StealthPDF.csproj -c Release
```

For a publish bundle with the single bundled EXE and GPL source zip:

```powershell
dotnet publish KillerPDF/StealthPDF.csproj -c Release
```

Output lands in `KillerPDF/bin/Release/net48/publish/`. Building requires the .NET SDK even though the app output targets .NET Framework 4.8.

## Project naming note

Some internal folders still use the historical `KillerPDF` path for git-history continuity. The shipped application, executable, registry entries, file associations, and user-facing branding are **StealthPDF**.

## Credits

- Original project: **KillerPDF** by [Steve the Killer](https://thekiller.net).
- Fork/rebrand maintainer: [@aabhpsy](https://github.com/aabhpsy).
- Engines and libraries include PDFium, Tesseract OCR, PdfSharpCore, PdfPig, Docnet.Core, NTwain, and PyMuPDF.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

GPLv3. See [LICENSE](LICENSE). StealthPDF is a GPLv3 fork/rebrand; forks, modifications, and redistributions must also provide GPLv3 source.
