# ExifRemover

A small Windows 11 right-click tool that strips EXIF, IPTC, XMP, ICC profile, comments, and other metadata from JPEG and PNG images **losslessly** — the pixel data is never modified.

Runs entirely offline. No telemetry. No network calls.

> **Windows PowerShell users:** run the scripts as **`.\install.cmd`** and **`.\uninstall.cmd`** (with the leading `.\`). Windows does not search the current directory for executables by default, so plain `install.cmd` will fail with `CommandNotFoundException`. `cmd.exe` users can also use `.\install.cmd` for consistency.

## What it does

Right-click any `.jpg`, `.jpeg`, or `.png` file in Windows File Explorer, choose **Remove EXIF metadata**, and a small overlay window opens. It lists every metadata entry the image contains (camera make/model, software, GPS, IPTC caption, XMP creator tool, ICC profile, etc.). Click **Remove** and the metadata is stripped in place (or written to `<name>_stripped.<ext>` next to the source, your choice). The image's pixel data is byte-identical to the original.

Multi-select is supported: right-click several images, choose the menu entry once, and the overlay shows a dropdown to review each file's metadata before stripping all of them.

## Installation

1. Build from source (this project does not yet publish binary releases — see [Building from source](#building-from-source) below). The `install.cmd` script moves the published `ExifRemover.exe` and its sibling runtime DLLs into the **repository root**, next to `install.cmd` itself — that folder is the installable folder.
2. After `.\install.cmd build` completes, the repository root contains `ExifRemover.exe` + the .NET runtime DLLs + `install.cmd` + `uninstall.cmd`. Run the installer from there:
   ```
   .\install.cmd
   ```
   You should see:
   ```
   Installing ExifRemover context-menu entries for .jpg, .jpeg, and .png
   Executable: D:\Projects\ExifRemover\ExifRemover.exe
   ...
   Done. Right-click any .jpg / .jpeg / .png file to see "Remove EXIF metadata".
   ```
   Note the leading `.\` — Windows does not search the current directory for executables by default, so `install.cmd` (without `.\`) fails with `CommandNotFoundException`.
3. Right-click any `.jpg`, `.jpeg`, or `.png` file in File Explorer. You should see **Remove EXIF metadata** in the context menu. The entry is registered in three registry locations so it shows up in both the modern Win 11 default context menu and the legacy "Show more options" menu:
   - `HKCU\Software\Classes\Applications\ExifRemover.exe\shell\ExifRemove` — application shell verb (modern menu)
   - `HKCU\Software\Classes\SystemFileAssociations\image\shell\ExifRemove` — image-class entry (modern menu, covers all image types)
   - `HKCU\Software\Classes\SystemFileAssociations\.<ext>\shell\ExifRemove` per extension (legacy "Show more options" menu)

If you want to move the installable folder to a stable location like `C:\Tools\ExifRemover\`, copy **the whole repo root** (including `install.cmd` and `uninstall.cmd` — they locate ExifRemover.exe in their own directory via `%~dp0`). The build output in `bin\Release\net8.0-windows\` is an intermediate artifact left by a plain `dotnet build`; the actual installer (`install.cmd`) expects everything to be in one folder.

To uninstall:
```
.\uninstall.cmd
```
The registry entries are removed and the context menu item disappears. The uninstall is safe to run multiple times.

## Building from source

Requirements: Windows 11, .NET 8 SDK (free, https://dot.net).

```
.\install.cmd build
```

This runs `dotnet publish` (self-contained, `win-x64`) and places `ExifRemover.exe` next to `install.cmd`, alongside the ~70 MB of .NET runtime DLLs it needs to run without .NET installed. The publish is intentionally **not** single-file. Running `.\install.cmd` with no argument builds first (if `ExifRemover.exe` is missing) and then installs the context-menu entries.

For local development with `dotnet`:

```
dotnet build ExifRemover.sln -c Release
dotnet test  tests\ExifRemover.Tests
```

Run the smoke test that doesn't require loading the test DLL (useful when sandbox WDAC policies block newly-built test binaries):

```
dotnet run --project src\ExifRemover.SelfTest -c Release
```

## Strip profiles

The overlay has a dropdown offering three presets. Each row in the metadata grid is annotated **Would be removed** or **Would be kept** so you can see what changes.

| Profile | Strips | Keeps |
|---|---|---|
| **Privacy** (default) | EXIF (IFD0, SubIFD, GPS, Interop, Thumbnail), IPTC, XMP, **ICC profile**, JPEG COM, PNG `tEXt`/`zTXt`/`iTXt`/`tIME`/`eXIf`/`iCCP` | JPEG JFIF header; PNG `gAMA`, `cHRM`, `sRGB`, `bKGD`, `sBIT`, `pHYs`, `tRNS` (color management & print dimensions, so colors don't shift) |
| **All metadata** | Same as Privacy, plus the PNG color-management chunks (`gAMA`, `cHRM`, `sRGB`). ICC profile is also dropped (it was already dropped under Privacy; the only new chunks are the PNG color-management ones). | JPEG JFIF; PNG `pHYs` (physical dimensions) |
| **Minimal** | Only EXIF, IPTC, XMP, JPEG COM, PNG `tEXt`/`zTXt`/`iTXt`/`tIME`/`eXIf` | **ICC profile** and color-management chunks are kept (so device fingerprint data may remain) |

The **Overwrite source** checkbox in the overlay footer controls where the stripped file is written:
- **Unchecked (default)**: writes `<name>_stripped.<ext>` next to the source. If a `_stripped` file already exists, ` (2)`, ` (3)`, etc. are appended. The original is never touched.
- **Checked**: replaces the original atomically (writes to `<name>.exifremover-<guid>.tmp`, then `File.Replace`). On any failure, the original is left untouched.

For multi-file removals, a confirmation dialog appears listing the files. There is a "Don't ask again for this session" option.

After Remove completes, the overlay shows a one-line summary (how many files changed and how many bytes were saved) and re-inspects the files so the grid reflects the now-clean state. The window stays open until you close it (Esc or Cancel).

## Privacy & security

- **Runs 100% offline.** No network access. No telemetry. No analytics. (You can verify this by running the tool with the network unplugged.)
- **Code is open source** (MIT License). You can audit every line before installing.
- **Lossless strip.** The pixel data of your images is never re-encoded. We rewrite only the metadata segments/chunks; the entropy-coded image bytes pass through byte-for-byte.
- **Atomic writes.** Output is written to a sibling `.tmp` file first, then atomically replaces the destination. A power loss or crash mid-strip leaves your original intact.

### About the SmartScreen warning on first run

ExifRemover is an unsigned hobby/open-source build. On first run, Windows SmartScreen will display a warning ("Windows protected your PC — Microsoft Defender SmartScreen prevented an unrecognized app from starting"). This is normal for unsigned binaries from individual developers.

Because you build the exe yourself from source (`.\install.cmd build`), you can confirm exactly what you are running by hashing it:

```
certutil -hashfile ExifRemover.exe SHA256
```

There is no pre-published hash to compare against in this build — the point is that the binary is produced locally from the source in this repository, which you can audit. After the SmartScreen prompt, click **More info → Run anyway**; Windows then remembers the choice for that exact exe path.

### Optional code signing

This build does not include a signing script. If you have your own code-signing certificate (e.g. from signpath.io for open-source projects), sign the published `ExifRemover.exe` with `signtool` yourself and verify the signature. A signed binary eliminates the SmartScreen warning.

## Architecture

- **`src/ExifRemover.Engine/`** — Class library. The metadata-rewriting code.
  - `MetadataInspector.cs` — Reads metadata via [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) and projects it to a flat display DTO.
  - `JpegMetadataStripper.cs` — Lossless JPEG segment rewriter. Walks JPEG markers and drops only the metadata-bearing APPn/COM segments. The image bitstream (SOS … EOI) is streamed verbatim. JFIF APP0 is preserved.
  - `PngMetadataStripper.cs` — Lossless PNG chunk rewriter. Iterates chunks after the 8-byte signature. Critical chunks (`IHDR`/`PLTE`/`IDAT`/`IEND`) are kept byte-for-byte; selected ancillary chunks are dropped. CRC32 is recomputed for every kept chunk.
  - `StripPipeline.cs` — Batch facade used by the UI.
- **`src/ExifRemover.App/`** — WPF overlay window. No business logic; only MVVM and presentation.
- **`src/ExifRemover.SelfTest/`** — Standalone console test runner that exercises the strippers end-to-end (use this when sandbox WDAC blocks loading the freshly-built test DLL).
- **`tests/ExifRemover.Tests/`** — xUnit test suite with fixtures generated at test time.

## What it doesn't do

- No GUI for image viewing or editing.
- No per-tag selection (it's all-or-nothing strip per profile).
- No support for WebP, TIFF, HEIC, AVIF in v1. Only JPEG and PNG.
- No drag-and-drop entry point. (Right-click is the primary entry.)
- No localization yet. Strings are in English.

## License

ExifRemover source code: MIT License (see `LICENSE`).

MetadataExtractor (third-party dependency): Apache License 2.0 (see `THIRD_PARTY_NOTICES.md`).

## Documentation

- [`PLAN.md`](PLAN.md) — design and architecture notes for the current v1.
- [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) — attributions for bundled third-party code.
- [`CHANGELOG.md`](CHANGELOG.md) — release-by-release history of fixes and features.
- [`docs/M2.20-audit-log.md`](docs/M2.20-audit-log.md) — the original adversarial bug-hunt report that produced the 10-round M2.20.x audit series (10 rounds, 40 fixes, +34 tests).
- [`scripts/verify_real_images.py`](scripts/verify_real_images.py) — Python end-to-end verifier (Pillow 10+). Run after `dotnet build verify/ExifRemover.Verifier.csproj -c Release` to confirm the stripper is truly lossless on real camera-style JPEGs.
- [`scripts/gen_test_jpeg.py`](scripts/gen_test_jpeg.py) — generates the EXIF+ICC+COM+XMP test inputs used by `verify_real_images.py`.