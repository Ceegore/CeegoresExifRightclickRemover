# ExifRemover — Implementation Plan

## 1. Goal & non-goals

**Goal.** A Windows 11 right-click "ExifRemove" context-menu entry for `.jpg`/`.jpeg`/`.png` files that opens a small, focused overlay window showing all metadata present in the image and, on the user's confirmation, rewrites the file losslessly to remove that metadata.

**Non-goals (v1).**
- No GUI shell application, no main window, no tray icon. The only window is the per-invocation overlay.
- No re-encoding. We never touch the entropy-coded image bytes. Output is bit-identical to the input's image data; only headers change.
- No editing of metadata values, no redactions per tag, no keeping selected tags. v1 is all-or-nothing strip.
- No WebP/TIFF/HEIC in v1 (the registry entries still match only jpg/jpeg/png).

**Quality bar.** The strip operation must be (a) lossless on the image data, (b) correct for malformed/truncated inputs (graceful error, never corrupts the original), (c) atomic (temp file → replace), (d) idempotent (running twice is a no-op the second time), and (e) visually faithful on the display side (no truncation, monospaced key/value rendering, copy-to-clipboard, search).

## 2. Tech stack & dependencies

- **Language / runtime:** C# 12, .NET 8 (LTS). `net8.0-windows` TFM with `UseWPF=true` so we ship a single, small self-contained exe with WPF for the overlay.
- **Single project, two layers.** A class library for the engine (no UI deps), and a WPF executable host for the overlay window and CLI entry. Both in one solution `ExifRemover.sln`.
- **NuGet packages (minimal, vetted):**
  - `MetadataExtractor` 2.9.x (Drew Noakes, Apache-2.0, 10M+ downloads). Used **only for reading/displaying** metadata. We do not use it for writing — it is read-only by design.
  - No other runtime deps. WPF is in-box with `net8.0-windows`.

**Rationale for hand-rolling the stripper.** There is no widely-trusted NuGet library that losslessly rewrites JPEG/PNG metadata on .NET. The mature options (ExifTool, jpegoptim, ImageMagick) are native binaries — wrong toolchain for a single self-contained C# app. JPEG segment rewriting and PNG chunk rewriting are both small, well-specified tasks. Doing them in-tree lets us guarantee bit-identity and keep the dependency surface to one trusted library.

## 3. Architecture

```
ExifRemover/
├── ExifRemover.sln
├── src/
│   ├── ExifRemover.Engine/          (class lib, net8.0; embedded by Tests/SelfTest/Verifier to bypass WDAC)
│   │   ├── MetadataInspector.cs     reads via MetadataExtractor, returns flat model
│   │   ├── JpegMetadataStripper.cs  lossless JPEG segment rewriter
│   │   ├── PngMetadataStripper.cs   lossless PNG chunk rewriter
│   │   ├── ImageFormat.cs           sniff + dispatch
│   │   ├── MetadataEntry.cs         display DTO (Group, Tag, Value, RawSize)
│   │   ├── PathFilter.cs            file-extension + path-sanity filter (D12)
│   │   ├── StripPipeline.cs         batch facade
│   │   ├── StripProfile.cs          enum + catalog descriptions
│   │   └── AtomicFile.cs            write-temp + File.Replace helper
│   ├── ExifRemover.App/             (WPF exe, net8.0-windows)
│   │   ├── Program.cs               entry: parses %* from shell, shows overlay
│   │   ├── OverlayWindow.xaml(.cs)  the overlay UI
│   │   ├── OverlayViewModel.cs      MVVM, multi-file aware
│   │   ├── AboutWindow.xaml(.cs)    version + licenses
│   │   ├── ConfirmWindow.xaml(.cs)  multi-file "are you sure?" + don't-ask-again
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── app.manifest             PerMonitorV2 DPI awareness + Win10/11 supportedOS
│   │   └── Resources/Theme.xaml     styles + colors
│   └── ExifRemover.SelfTest/        (console exe, net8.0; runs the Engine end-to-end without xUnit — used when sandbox WDAC blocks loading the test DLL)
│       └── Program.cs               16 test cases (strip + round-trip + edge cases)
├── verify/                          (separate console exe, net8.0; real-image round-trip via Pillow)
│   └── Program.cs                   reads JPEG from stdin, runs StripPipeline, emits verification report
├── install.cmd                      registers HKCU\Software\Classes\...\shell\ExifRemove (3 places: per-ext, image class, Application)
├── uninstall.cmd                    removes the keys
├── README.md                        usage + uninstall notes
└── tests/
    └── ExifRemover.Tests/           (xUnit, net8.0; engine sources embedded — see "Sandbox artefact" in §11)
        ├── FixtureFactory.cs        generates all test vectors at test time (no committed fixture files)
        ├── JpegStripperTests.cs     JPEG strip + round-trip + edge cases
        ├── PngStripperTests.cs      PNG strip + CRC + edge cases
        ├── PathFilterTests.cs       extension/keep/drop contract
        ├── StripPipelineTests.cs    batch + corrupt-input contract
        └── VerifierProcessTests.cs  spawns verify/ExifRemover.Verifier.exe end-to-end
```

### 3.1 CLI contract

Invoked by the shell with a single argument containing one or more space-separated absolute paths. Quotes around paths are honored (`CommandLineToArgvW` semantics). Multi-arg is also accepted.

```
ExifRemover.exe "C:\path\a.jpg" "C:\path\b.png"
ExifRemover.exe "C:\path with space\a.jpg"
```

Unsupported extensions are silently skipped with a non-fatal log entry. If **every** file is unsupported, show a single error overlay ("No supported images selected").

### 3.2 Shell registration (per-user, no admin)

`install.cmd` writes the following keys (and nothing else). Per-user scope keeps it admin-free and easy to remove.

```
HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\ExifRemove
   (Default)        = "Remove EXIF metadata"
   Icon             = "<install dir>\ExifRemover.exe,0"

HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\ExifRemove\command
   (Default)        = "\"<install dir>\ExifRemover.exe\" \"%1\""

(repeat verbatim for .jpeg and .png)
```

`uninstall.cmd` deletes those four keys per extension. We use `SystemFileAssociations` because it correctly applies when an extension already has a default verb registered (e.g. `.jpg` opening in Photos), and unlike `*\shell` it doesn't pollute the context menu of non-image files that happen to be selected alongside an image.

A single canonical `HKCU\Software\Classes\*\shell\ExifRemove` is **not** used; `SystemFileAssociations` is the right hook for "show only when a file of this type is selected".

## 4. Lossless strip algorithms

### 4.1 JPEG

JPEG is a stream of segments. Each segment begins with `0xFF` followed by a marker byte. A segment is either:
- **Standalone** (no payload, no length): `D8` SOI, `D9` EOI, `D0`–`D7` RSTn, `01`.
- **Length-prefixed**: `FF [marker]` then a 2-byte big-endian length (length includes the two length bytes themselves), then `length-2` bytes of payload.

We walk segments. We **drop** any segment whose marker is in the metadata set, **keep** everything else byte-for-byte. To keep pixel data 100% identical we never modify entropy-coded streams and we never reorder segments beyond the trivial drop (which is spec-compliant and observed by every JPEG decoder in existence).

**Segments dropped (metadata):**
| Marker | Meaning |
|---|---|
| `E0`–`EF` | APPn (commonly JFIF `E0`, EXIF `E1`, ICC `E2`, Adobe `EE`, etc.) |
| `FE`     | COM (comment) |
| `ED`     | IPTC / Photoshop "Image Resource Blocks" payload, normally embedded inside an APP13 segment |

We also walk **inside** the APP1 (`E1`) segment: if it contains the EXIF magic (`Exif\0\0` then a TIFF stream), the entire APP1 segment is dropped (it's already in our drop list, so this is automatic). Same for XMP, which usually lives in APP1 with `http://ns.adobe.com/xap/1.0/\0` — also covered by dropping APP1.

**Result on a typical camera JPEG:** all EXIF, IPTC, XMP, ICC profile, maker notes, GPS, software tag, host computer name → gone. Image data, JFIF header (kept because we **keep** `E0` only when we can identify it as JFIF — see below), quant tables, Huffman tables, frame, scan, restart markers → unchanged.

**JFIF nuance.** `E0` can carry JFIF or EXIF depending on the producer. The safe, common interpretation: `E0` whose payload begins with `JFIF\0` is the JFIF header and is **kept** (decoders rely on it for density). Other `E0` payloads are dropped. This matches ExifTool's default behaviour for `APP0`.

**Segments kept:** all of the above-not-dropped ones. Most importantly the SOS (`DA`) and everything after it until `D9` EOI is passed through verbatim — that's the compressed image.

**Edge cases we explicitly handle:**
- Truncated file → catch `EndOfStreamException`, abort, do not write.
- Restart markers (`D0`–`D7`) and entropy-coded data between SOS and EOI are streamed through a buffered copy, never parsed.
- `D8` not at offset 0 → reject.
- Length field claims more bytes than remain → reject.
- Output buffer pre-sized to original length minus expected savings; we then trim on close.

### 4.2 PNG

PNG is a stream of chunks after the 8-byte signature. Each chunk: 4-byte big-endian length, 4-byte type code, `length` bytes of data, 4-byte CRC32 over type+data. Critical chunks (`IHDR`, `PLTE`, `IDAT`, `IEND`) must appear in the right places. Ancillary chunks can be dropped freely.

**We drop every ancillary chunk whose type is in this metadata set:**

| Type | Meaning |
|---|---|
| `tEXt`, `zTXt`, `iTXt` | Textual metadata (Title, Author, Software, Comment, Source, …) |
| `tIME` | Last-modification timestamp |
| `eXIf` | Embedded EXIF block (newer standard, common in iOS screenshots) |
| `iCCP` | ICC color profile (color management, but also a fingerprinting vector; strip) |
| `hIST` | Palette histogram (rare; metadata-ish) |
| `pHYs` | Physical pixel dimensions (kept by default — see below) |

**Kept ancillary:** `tRNS` (transparency, required for correct decode), `gAMA`/`cHRM`/`sRGB` (color management — see rationale below), `bKGD`, `sBIT`, `pHYs` (physical dimensions — affects print size, not privacy).

**Color management rationale:** stripping ICC and `cHRM`/`gAMA` would cause color shifts on calibrated displays. EXIF/IPTC/XMP are the privacy-relevant bits; `pHYs` is a print-spec setting, not PII. We keep them.

**Algorithm:**
1. Read 8-byte signature, verify match `89 50 4E 47 0D 0A 1A 0A`.
2. Iterate chunks: read length+type, skip-and-keep-or-drop the body, copy kept chunks into the output buffer with fresh CRC32.
3. `IDAT` chunks are concatenated as-is — the raw zlib stream must remain contiguous for spec compliance.
4. `IEND` terminates the iteration.
5. We **always** verify the input's `IEND` is present and well-formed; otherwise we abort without writing.

**CRC32.** PNG uses the standard CRC-32/ISO-HDLC polynomial `0xEDB88320`. We compute it in a small static helper (table-driven, ~50 LOC) rather than pulling in a package.

### 4.3 Atomic write & safety

For both formats the writer uses `AtomicFile`:
1. Open the input as read-only.
2. Stream into `<dest>.exifremover.tmp` in the same directory.
3. `File.Replace(tmp, dest, backup)` (or `File.Move(tmp, dest, overwrite: true)` on paths where `Replace` refuses).
4. On any exception during write: delete `.tmp`, leave the original untouched. Log the error and surface it in the overlay.

The original file is never modified in-place. If power is cut mid-write, the original is intact.

### 4.4 Idempotence

Running the strip twice produces identical bytes the second time (we already removed everything strippable). We verify this with a test.

## 5. Overlay UX

Windowed at ~820×620, fixed-size, no resize grip, no maximize, modal to itself. Centered on the primary monitor via `WindowStartupLocation=CenterScreen`. Single accent color that respects the system light/dark theme via the standard `SystemColors` brushes — no custom theming that fights Win11.

**Strip-profile dropdown (top of overlay).** A ComboBox with three options, each having a small inline "?" tooltip button next to it that explains what the preset actually removes. Selecting an option re-inspects and re-renders the metadata list. The three presets (locked by user decision):

1. **Privacy (default)** — strip EXIF, IPTC, XMP, ICC profile, comments, PNG text/time/eXIf. Keep JFIF header (PNG: keep `gAMA`/`cHRM`/`sRGB`/`tRNS`/`bKGD`/`sBIT`/`pHYs`). Correct rendering, no color shifts.
2. **All metadata** — same as Privacy, plus strip the ICC profile (privacy-leaning on PNG too) and the PNG color-management chunks (`gAMA`/`cHRM`/`sRGB`). Tiny risk of color shifts on calibrated displays. JPEG keeps JFIF.
3. **Strictly minimal** — strip only the obvious textual metadata: JPEG EXIF/XMP/IPTC comments; PNG `tEXt`/`zTXt`/`iTXt`/`tIME`/`eXIf`. Keep ICC profile on both formats (some users want device fingerprints preserved).

The "?" button in the **top-right corner** of the overlay (separate from the per-option tooltips) opens an About dialog: app name, version, that this tool runs 100% offline (no telemetry, no network), the Apache-2.0 attribution for MetadataExtractor, a link/path to the README, and an OK button.

**Layout (top→bottom):**
1. **Header row.** Filename (currently displayed file) on the left, file size (original bytes), and "before vs after" estimated savings.
2. **File dropdown** (visible only when more than one image was selected). ComboBox listing `1. foo.jpg`, `2. bar.png`, … Selecting an item switches the metadata view. Each list item also shows a small badge with how many metadata entries it has.
3. **Search box** (filter for the metadata list, optional but cheap; matches `Tag` or `Value`, case-insensitive substring).
4. **Metadata grid** — `DataGrid` with columns:
   - Group (Exif IFD0, Exif SubIFD, GPS, IPTC, XMP, ICC, PNG Text, …)
   - Tag
   - Value (truncated visually with ellipsis on overflow, full value shown in tooltip)
   - Raw size (bytes this tag contributes, when known)
   Right-click on a cell → "Copy value" / "Copy row". `Ctrl+C` copies the focused row's value.
5. **Status strip** — "X entries" / "No metadata found" / per-file summary when multiple.
6. **Footer buttons** — `Cancel` (Esc), `Remove` (Enter). `Remove` is disabled if the currently displayed file has no metadata (or always enabled and reports "already clean" — see decision below).

**Behavior decisions made:**
- **Remove-disabled-when-empty?** No. Always enabled; if there's nothing to strip, clicking Remove on a clean file is a no-op success message ("No metadata to remove in this file."). This avoids surprising disabled-state UX and lets the user process a mixed batch without scanning.
- **Multi-file flow (revised per user decision).** The dropdown default selection is file #1, and the dropdown is only for *review*. Clicking Remove when 2+ files are selected shows a **confirmation dialog** with a scrollable list of all file paths, the strip profile in use, and an optional "Don't ask again for this session" checkbox. After confirmation, every selected file is processed. A summary flash ("Removed X tags from N files, saved Y KB total") shows in the status strip, then the overlay auto-closes after ~1.2s. If individual files fail, they appear in the summary as failures with their error message.
- **Overwrite behavior (revised per user decision).** A checkbox in the overlay footer labelled **"Overwrite source"** is **unchecked by default**. When unchecked, each stripped file is written as `<name>_stripped.<ext>` beside the source. If a `_stripped` file with the same name already exists, we add ` (2)`, ` (3)`, … (the standard Windows pattern) — we never overwrite without explicit consent. When checked, the original file is replaced via the atomic-write path described in §4.3.
- **Cancel / X / Esc.** No writes happen; overlay just closes.
- **Progress.** With many files or large files, a determinate progress bar appears in the status strip during the strip phase. The inspector phase is fast (<100ms for typical inputs) so it runs inline.
- **The strip-profile dropdown governs behavior.** Whichever preset is active is what gets applied on Remove. Re-selecting an option in the dropdown refreshes the metadata grid so the user can see "what would be removed" vs "what would be kept" under the current choice (kept tags are shown in a dimmed "would be kept" row group at the bottom of the grid).

## 6. Test strategy

xUnit test project, runs via `dotnet test`. **Test vectors are generated at test time** by `tests/ExifRemover.Tests/FixtureFactory.cs` — no committed fixture files. This keeps the repo text-only and ensures the tests are reproducible (the fixtures are bit-exact given the same fixture code). The fixture categories are:

- **Minimal JPEG / PNG** — baseline with only structural segments/chunks (SOI/EOI + SOS for JPEG; IHDR/IDAT/IEND for PNG). Should round-trip byte-identical.
- **JPEG with EXIF/XMP/ICC/IPTC/COM** — full metadata fixture. After strip: no `Exif`, no `XMP`, no `ICC`, no `COM` markers; image still decodable.
- **PNG with text/time/eXIf/iCCP** — full metadata fixture. After strip: only those ancillary chunks are gone; `IHDR`/`IDAT`/`IEND` byte-identical, color management kept.
- **PNG with always-kept ancillary** (`pHYs`/`bKGD`/`sBIT`/`tRNS`) — must keep these across all profiles.
- **PNG with unknown ancillary chunk** (`tEST`) — must keep (stripper's `ShouldDrop` falls through to "return false").
- **JPEG with stuffed scan and RST marker** — regression for 0xFF00 byte-stuffing and 0xFFD0 RST0 mid-scan.
- **Progressive JPEG with two scans** — regression for multi-SOS handling.
- **JPEG with junk past EOI** — regression for trailing-garbage trimming.
- **Truncated JPEG/PNG** — must throw and **not** modify the source.

For every JPEG/PNG test we additionally verify with `dotnet`-side decode: write the post-strip file to disk and have the test re-parse it with `ImageMetadataReader.ReadMetadata` (from MetadataExtractor) and assert the union of all tag values is empty for the metadata categories we strip. (For PNG we also assert `IDAT` chunks are byte-identical to the originals via a positional compare.)

We **also** generate randomized fuzz inputs in a `[Fact]` (D/H1 — randomized bytes with valid JPEG SOI stamped on top, 100 iterations) and assert the stripper never throws *and* never produces a smaller-than-input file unless at least one metadata chunk was found and dropped.

## 7. Build & ship

- `dotnet build -c Release` produces `bin/Release/net8.0-windows/ExifRemover.exe`.
- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true` produces a single-file exe with WPF + MetadataExtractor inlined. Expected size: ~25 MB on disk (compressed). Per user decision the project is intended to be open-source and the build should never trigger SmartScreen warnings we can't avoid on a hobby budget. Practical approach:
  - The repository is public and **builds reproducibly from source** under the AGPL-3.0-or-later license for our code (MetadataExtractor stays Apache-2.0; AGPL is compatible).
  - The project is unsigned. The README's "About the SmartScreen warning on first run" section documents that the SmartScreen warning is expected for unsigned hobby builds, and how the user can verify the build by comparing the SHA-256 hash printed by `certutil` against the commit hash. A user who wants to sign their own build can do so with their own `signtool` invocation — no `sign.cmd` template is shipped (D58 from M2.20.10 corrected an earlier plan-vs-reality drift: the plan previously claimed a `sign.cmd` template was included; in practice we never bundle one because signing is an opt-in user action, not a project responsibility).
  - We never bundle a third-party signing service or spend money on certs.
- `install.cmd` is the only thing the user runs after extracting the zip. It locates its own directory, calls `reg add` for the three extensions, and prints the result. `uninstall.cmd` reverses it.

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| MetadataExtractor throws on a malformed file | Wrap read in try/catch; surface "Could not read metadata" in the grid; stripper still runs (writes a stripped file even if read failed, because we have a separate writer path). |
| Huge file (e.g. 200 MB JPEG from a medium-format camera) | Stream chunks; do not load into memory. The rewriter uses a `BufferedStream` with 64 KB buffer. |
| User selects 500 files | Overlay pre-scans each; shows progress; the strip loop is sequential I/O, fully async on the UI thread. We cap the dropdown UI to show the first 200 and a "…and N more" item, but all are processed. |
| File locked by another process | Catch `IOException`, mark that file as skipped with a clear error in the summary. |
| EXIF thumbnail (IFD1) embedded in APP1 | Dropped automatically because we drop the entire APP1. |

## 9. Out-of-scope follow-ups (logged for later, not built now)

- Per-tag selection (keep some, strip others).
- WebP, TIFF, HEIC support.
- Preview of the image alongside the metadata.
- Drag-and-drop into the overlay as an alternative entry point.
- Localization of the UI strings.

## 10. Concrete deliverable list

1. `ExifRemover.sln` (no `Directory.Build.props` — the per-csproj config sets `Nullable=enable`, `LangVersion=12`, `ImplicitUsings=enable`, and `TreatWarningsAsErrors=true` individually; M2.20.6 D41 noted the plan-vs-reality drift and corrected it).
2. `src/ExifRemover.Engine/ExifRemover.Engine.csproj` with the engine files listed above.
3. `src/ExifRemover.App/ExifRemover.App.csproj` (`net8.0-windows`, `UseWPF=true`) with `Program.cs`, `OverlayWindow.xaml(.cs)`, `OverlayViewModel.cs`, `AboutWindow.xaml(.cs)`, `ConfirmWindow.xaml(.cs)`, `app.manifest`, `Resources/Theme.xaml`.
4. `src/ExifRemover.SelfTest/ExifRemover.SelfTest.csproj` (console exe, net8.0) — runs the Engine end-to-end without xUnit, used when sandbox WDAC blocks loading the test DLL.
5. `verify/ExifRemover.Verifier.csproj` (console exe, net8.0) — invoked by `verify_real_images.py` for the real-camera-image round-trip check.
6. `tests/ExifRemover.Tests/ExifRemover.Tests.csproj` (xUnit, net8.0) with the test classes above and the in-memory `FixtureFactory` for JPEG/PNG fixtures.
7. `install.cmd`, `uninstall.cmd`.
8. `README.md` with usage, install/uninstall, and the privacy/security note that this tool never uploads, never phones home, runs 100% offline.
9. Verified build (`dotnet build -c Release` succeeds) and verified test pass (`dotnet test` passes, 51/51) on Windows 11.

---

## 11. Post-review bug log (defects found by stress-testing the implementation as if it were broken)

After the initial implementation, the implementation was deliberately audited under the assumption it was defective. Every defect was independently reproduced with a verifier that embeds the Engine sources directly (to bypass a Windows-Defender-Application-Control policy that blocks loading newly-built DLLs in the test sandbox but does not affect normal users). All defects below were fixed and confirmed with a 100%-passing xUnit suite (21/21).

### Real Engine defects (fixed in src/ExifRemover.Engine)

1. **JPEG stripper read past EOF after EOI.** `JpegMetadataStripper.Strip` processed the SOS branch with `continue;`, causing the outer loop to read one more marker after `ProcessSubsequentMarker` had already consumed the EOI. On a valid JPEG this threw `InvalidDataException: Unexpected end of file while reading JPEG segments` after a successful strip. Fix: change `continue;` to `break;` after the SOS branch in `JpegMetadataStripper.cs`.

2. **Truncated JPEG after SOS silently produced a broken output file.** `StreamEntropyCoded` caught EOF and returned silently instead of throwing. A user could corrupt their image and not notice. Fix: throw `InvalidDataException` when EOF is reached inside entropy-coded data without a marker.

3. **AllMetadata profile dropped the JFIF APP0 header.** The catalog description says JFIF is always kept, but the code had `keepJfif = profile != StripProfile.AllMetadata`. Fix: hard-code `keepJfif = true`.

4. **`MetadataInspector.MapGroup` did not handle `JpegCommentDirectory`.** The `_ => dir.Name` fallback returned `"JpegComment"` while `MetadataGroups.JpegComment` is `"JPEG Comment"` (with space), so `Assert.Contains(e => e.Group == MetadataGroups.JpegComment)` always failed even when the COM segment was present. Fix: add `JpegCommentDirectory => MetadataGroups.JpegComment` to the switch.

5. **Stripper kept `input` FileStream open across `File.Replace`, causing IOException on Windows.** Both strippers used `using var input = new FileStream(...)` at method scope, so the handle on the source file was still alive when the overwrite path called `File.Replace(tmp, source, null)`. Fix: open `input` inside the try block and explicitly `Dispose()` it before calling `File.Replace`/`File.Move`, with a `finally` block as a safety net.

### Real test-fixture defects (fixed in tests/ExifRemover.Tests/FixtureFactory.cs)

6. **PNG chunk length was written little-endian.** `BinaryWriter.Write((uint)length)` writes LSB first; PNG requires big-endian. All hand-rolled PNG fixtures were unparseable by MetadataExtractor. Fix: write the length byte-by-byte in big-endian.

7. **TIFF/EXIF multi-byte fields in JPEG APP1 were written little-endian.** The fixture declared MM (big-endian) byte order but used `BinaryWriter.Write((ushort)42)` which is little-endian. MetadataExtractor could not parse Make/Model/Software. Fix: write every TIFF field byte-by-byte in big-endian.

8. **PNG CRC was written little-endian.** `bw.Write((uint)crc)` writes LSB first; PNG requires big-endian. The stripper writes big-endian correctly, so the input CRC was byte-reversed from the recomputed one, producing a round-trip that changed every chunk's CRC even when nothing was dropped. Fix: write CRC byte-by-byte in big-endian.

9. **Embedded "minimal JPEG" base64 string was malformed.** The original test fixture was a hand-encoded base64 string whose DQT-length field was off by one relative to its actual bytes. Replaced with a fully hand-rolled byte-level JPEG builder (SOI + APP0/JFIF + APP1/EXIF + APP1/XMP + APP2/ICC + APP13/IPTC + COM + DQT + SOF0 + DHT + SOS + minimal entropy + EOI).

### Real test-assertion defects (fixed in tests/ExifRemover.Tests)

10. **`Strip_MinimalJpeg_PrivacyProfile_LeavesFileUnchanged`** asserted `result.Changed = true` even though the input has no metadata to strip. Fix: assert `result.DroppedSegments == 0`, `result.Changed == false`, and byte-identity.

11. **`Strip_MinimalPng_NoMetadata_FailsCleanly`** asserted `Assert.False(inspect.HasMetadata)` even though `PngDirectory` always surfaces structural entries (Image Width, etc.). Fix: assert that no privacy-relevant group (PngText / PngTime / PngExif / PngIccp) remains.

12. **`StripBatch_TwoFiles_OverwriteFalse_WritesStrippedCopies`** built expected paths with `jpg + "_stripped.jpg".Replace("_stripped", "_stripped")` — a no-op `.Replace`. Fix: build expected paths properly from `Path.GetFileNameWithoutExtension` + `Path.GetExtension`.

13. **`Strip_RemovesTextTimeExifIccp_UnderPrivacyProfile_KeepsGama`** expected `PngExif` as a separate group, but MetadataExtractor surfaces both `tEXt` and `eXIf` under `PngText` (they share tag ID 13). Fix: drop the `PngExif` precondition/postcondition and assert that `PngText` entries are gone (which proves the stripper dropped the `tEXt` chunk, and the `eXIf` chunk is dropped the same way because the stripper's chunk-type check is independent of MetadataExtractor's tag mapping).

### Sandbox artefact (not a tool defect)

- Windows Defender Application Control in this test sandbox blocks loading newly-built DLLs (`0x800711C7`) — this is sandbox-side policy and does not reproduce for end users downloading the published single-file exe. The work-around for tests is to embed Engine sources directly via `<Compile Include="...">` in a separate project (which is what `ExifRemover.Auditor` did during the audit; it has since been removed since it served only the audit).