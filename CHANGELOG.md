# Changelog

All notable changes to ExifRemover are recorded here. Versions follow
the `M2.20.x` (audit round) convention used by the project.

## [Unreleased]

## M2.20.29 — 2026-08-05 — 360° audit round 26 (verifier IsValidJpeg/IsValidPng + LocateVerifier off-by-one)
- **D90** `verify/Program.cs` — the pre-fix code unconditionally
  called `IsValidJpeg(outBytes)` to determine the
  `output_decodes=yes|no` line. The Python harness only ran JPEG
  inputs, so the bug never fired, but a PNG input would produce a
  perfectly-valid PNG output and the verifier would still report
  `output_decodes=no` (because the PNG signature is not the
  JPEG signature). Fix: detect the input format via
  `ImageFormatDetector.Detect(bytes)` and call the right
  validator (added `IsValidPng` for PNG outputs). The stripper
  preserves the input format (JPEG in → JPEG out, PNG in → PNG
  out), so the output's format is the same as the input's. A
  new `output_format={Jpeg|Png}` line is also emitted so the
  Python harness can verify the format.
  New integration test
  `VerifierProcessTests.Verifier_PngInput_ReportsOutputDecodesYes`
  runs the real verifier on a PNG and asserts the
  `output_decodes=yes` and `output_format=Png` lines.
- **D91** `tests/ExifRemover.Tests/VerifierProcessTests.cs:LocateVerifier`
  — the pre-fix code used `for (int i = 0; i < 6; i++)` to walk
  the directory tree looking for the verifier exe. The test DLL
  is at `<repo>/tests/ExifRemover.Tests/bin/Debug/net8.0/win-x64/`
  which is 6 levels below the repo root, so the loop checked 6
  levels but the verifier lives at the 7th level (the repo
  root). Result: the loop never found the verifier, the
  `if (verifier is null) return;` short-circuit fired, and the
  test was effectively a no-op. Both the existing
  `Verifier_InputPathEqualsOutputPath_DoesNotDestroyInput` test
  AND the new `Verifier_PngInput_ReportsOutputDecodesYes` test
  were affected. Fix: bump the loop bound to 8. The D90 test
  exposed this latent bug because the test only takes 5ms
  when it short-circuits but ~250ms when it actually runs the
  verifier; the discrepancy tipped off the audit.
  This is an off-by-one in the loop bound — a textbook
  R17-3 pattern (silent failure on a test path). The test
  passed for years because nobody noticed the 5ms runtime
  (vs. the expected ~1s). The fix is one line.
- xUnit: 103 → 104 tests (+1 for D90). SelfTest: 16/16 stable.
- Real-image verifier: ALL CHECKS PASSED.

## M2.20.28 — 2026-08-05 — 360° audit round 25 (AboutWindow hyperlink: show MessageBox on failure)
- **D89** `src/ExifRemover.App/AboutWindow.xaml.cs:Hyperlink_RequestNavigate`
  — the pre-fix code had a bare `catch { }` that silently
  swallowed any exception from `Process.Start`. This is the
  R17-3 pattern (silent error swallow on a user-facing path):
  a user clicks the "MetadataExtractor" hyperlink, the
  launch fails (e.g. no default browser configured, AV
  blocked the launch, malformed URI), and the user gets no
  feedback. The fix catches the specific `Exception` class
  and shows a `MessageBox` with the error message and a
  hint to configure a default browser. The user gets
  actionable feedback instead of wondering why nothing
  happened.
  New source-shape regression test
  `AboutWindowShapeTests.AboutWindow_HyperlinkRequestNavigate_DoesNotSwallowExceptionSilently`
  reads `AboutWindow.xaml.cs` as text, strips comments AND
  string literals (R16 lesson), and uses the regex
  `catch\s*\{` to detect any bare-catch pattern. A future
  commit that re-introduces the bare `catch { }` would
  fail the test, forcing a conscious decision. (The earlier
  attempt with `catch\s*(?!\()` failed due to regex
  backtracking — see the test's inline comment for the
  detailed analysis. The final regex `catch\s*\{` is
  immune to the backtracking issue because `\s` only
  matches whitespace, so the engine cannot backtrack past
  the `(` in `catch (Exception ex)`.)
- xUnit: 102 → 103 tests (+1 for D89). SelfTest: 16/16 stable.
- Real-image verifier: ALL CHECKS PASSED.

## M2.20.27 — 2026-08-05 — 360° audit round 24 (extract `SkipExactly` + dead `_byPath` field)
- **D87** `src/ExifRemover.Engine/StreamHelpers.cs:SkipExactly` (new)
  — the pre-fix code declared a private `SkipExactly` method in BOTH
  `JpegMetadataStripper.cs` and `PngMetadataStripper.cs`. The two
  copies were nearly identical (same algorithm, same error-message
  shape) but with a signature difference: the JPEG side took
  `int count` (because JPEG segLen is uint16, max 65535, so int is
  sufficient), while the PNG side took `long count` (because PNG
  chunk length is int32, +4 for the CRC trailer could in theory push
  past int.MaxValue, and the `new byte[count]` allocation in the
  non-seekable path would throw on overflow). The PNG side had a
  `Math.Min(count, int.MaxValue)` clamp to defend against this. Same
  D83-style DRY-drift risk. Fix: extracted to a shared
  `StreamHelpers.SkipExactly` that always takes `long count` and
  always applies the clamp. The JPEG call sites pass
  `(long)payloadLen` (implicit widening from int to long is free).
  The post-fix helper is one implementation instead of two, with the
  same trust-but-verify bounds check (D65), the same error-message
  shape, and the same overflow defense.
  New regression tests in `StreamHelpersTests.cs`:
  `SkipExactly_SeekableStream_AdvancesPositionByCount` (the basic
  seek case),
  `SkipExactly_SeekableStream_CountPastEnd_Throws` (the bounds-check
  case — D65 contract),
  `SkipExactly_NonSeekableStream_AdvancesPositionByCount` (the
  read-loop case via a `NonSeekableStream` wrapper),
  `SkipExactly_NonSeekableStream_StreamShorterThanCount_Throws`
  (the read-loop bounds case),
  `SkipExactly_ContextTagAppearsInExceptionMessage` (the
  format-tag-in-message contract),
  `SkipExactly_ZeroCount_NoOp` (the zero-skip boundary),
  `SkipExactly_LargeCount_NearIntMaxValue_DoesNotOverflowBufferAllocation`
  (the `int.MaxValue` clamp contract — a count of
  `int.MaxValue + 1L` against a 10-byte stream must throw the
  bounds-check error, NOT OOM trying to allocate the buffer).
- **D88** `src/ExifRemover.App/OverlayViewModel.cs` — the pre-fix
  code declared a `private readonly Dictionary<string, FileEntryViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);`
  field that was used in the constructor for the D78 case-insensitive
  dedup (`ContainsKey` + indexer-set), but never read anywhere else.
  Same R17-2 (dead field) pattern as D85 (`_allPaths`) and D82
  (`StripResult.Warning`): a private field that survived multiple
  audit rounds because it was never exercised outside the
  constructor. The fix moved the dictionary into the constructor as
  a local `seen` variable. The `OrdinalIgnoreCase` comparer is
  preserved (Windows path semantics: "FOO.jpg" and "foo.jpg" refer
  to the same file).
  New source-shape regression test
  `OverlayViewModelShapeTests.OverlayViewModel_DoesNotDeclareDeadByPathField`
  reads the source file as text, strips comments, and asserts the
  field name is NOT present.
- xUnit: 94 → 102 tests (+8 for D87+D88). SelfTest: 16/16 stable.
- Real-image verifier: ALL CHECKS PASSED.

## M2.20.26 — 2026-08-05 — 360° audit round 23 (extract `CleanupOrphanedOutput` + remove dead `_allPaths` field)
- **D85** `src/ExifRemover.App/OverlayViewModel.cs` — the pre-fix code
  declared a `private readonly List<string> _allPaths;` field that was
  assigned in the constructor (via `paths.ToList()`) and iterated two
  lines later, but never read anywhere else in the codebase. The
  field is a textbook R17-2 (dead code) finding: a private field that
  survived multiple audit rounds because it was never exercised
  outside the constructor. The fix removed both the field and the
  `paths.ToList()` allocation (an unnecessary per-instance List
  allocation that added GC pressure), iterating the `paths`
  parameter directly. The pattern is the same R17-2 finding that D82
  caught for `StripResult.Warning`.
  New source-shape regression test
  `OverlayViewModelShapeTests.OverlayViewModel_DoesNotDeclareDeadAllPathsField`
  reads the source file as text, strips comments, and asserts the
  field name is NOT present. A future commit that re-introduces the
  dead field would fail the test, forcing a conscious decision. The
  test uses a naive comment stripper (R16 lesson: a regression that
  re-introduces the pattern in a comment would pass a naive
  substring check, so we strip both `//` and `/* */` comments and
  preserve string literals).
- **D86** `src/ExifRemover.Engine/AtomicFile.cs:CleanupOrphanedOutput`
  (new) — the pre-fix code had a one-liner
  `try { if (File.Exists(actualOutputPath) && (!overwriteSource || actualOutputPath != sourcePath)) File.Delete(actualOutputPath); } catch { }`
  in BOTH `JpegMetadataStripper.cs` and `PngMetadataStripper.cs`. The
  two copies were byte-identical. This is the same D83-style
  DRY-drift pattern: a future contributor who updates the cleanup
  logic (e.g. adds a retry on lock, switches to a `File.Delete` that
  waits for an AV scan, logs the cleanup failure) would have to
  remember to update the other copy too. The fix: extract the
  helper. Both strippers' catch blocks now call
  `AtomicFile.CleanupOrphanedOutput(actualOutputPath, sourcePath, overwriteSource)`.
  The helper preserves the original semantics: delete the orphan
  only if it exists AND it's not the same path as the source under
  the overwrite path; swallow any cleanup exception so it doesn't
  mask the original stripper exception.
  New regression tests in `AtomicFileTests.cs`:
  `CleanupOrphanedOutput_FileExists_OverwriteFalse_DeletesFile`
  (the basic case),
  `CleanupOrphanedOutput_FileExists_OverwriteTrue_DifferentPath_DeletesFile`
  (the overwrite-with-temp-file case),
  `CleanupOrphanedOutput_FileExists_OverwriteTrue_SamePath_DoesNotDelete`
  (the safety case where the source must not be deleted),
  `CleanupOrphanedOutput_FileDoesNotExist_NoOp` (the pre-write
  no-op case),
  `CleanupOrphanedOutput_FileLocked_DoesNotThrow` (the
  AV-lock case — the helper's internal catch swallows the
  exception so it doesn't mask the original stripper exception).
- xUnit: 88 → 94 tests (+6 for D85+D86). SelfTest: 16/16 stable.
- Real-image verifier: ALL CHECKS PASSED.

## M2.20.25 — 2026-08-05 — 360° audit round 22 (extract `ReadExact` + `ResolveTempPath` helpers)
- **D83** `AtomicFile.ResolveTempPath` (extracted) — the pre-fix
  code declared a private `ResolveTempPath(string sourcePath)` in BOTH
  `JpegMetadataStripper.cs` and `PngMetadataStripper.cs`. The two
  copies were byte-identical (5 lines each, including the
  `Guid.NewGuid()`-suffixed `.{name}.exifremover-<guid>.tmp`
  filename pattern). This is the same DRY-drift pattern that R17 of
  the SteamReviewTool audit found for back-door in-memory setters: a
  future contributor who updates the temp-name scheme (e.g. to use a
  stronger random source, to add a creation timestamp, to put the
  temp file in a sibling directory instead of the source directory)
  would have to remember to update the other copy too. Missed updates
  silently diverge. Fix: extracted the helper to
  `AtomicFile.ResolveTempPath`. Both strippers now call the shared
  helper. The leading "." in the filename (Windows hidden-file
  convention) and the `exifremover-{guid}.tmp` suffix (makes an
  orphaned temp file attributable) are preserved.
  New regression tests in `AtomicFileTests.cs`:
  `ResolveTempPath_PutsTempFileInSameDirectory` (asserts the temp
  path's directory is the same as the source's),
  `ResolveTempPath_IncludesOriginalFilename` (asserts the leaf
  contains the original filename, has the `.tmp` extension, and
  starts with `.`),
  `ResolveTempPath_TwoCalls_ProduceDifferentPaths` (asserts the
  GUID suffix is unique per call).
- **D84** `StreamHelpers.ReadExact` (new) — the pre-fix code
  declared a private `ReadExact(Stream, Span<byte>)` in BOTH
  `JpegMetadataStripper.cs` and `PngMetadataStripper.cs`. The two
  copies were byte-identical except for the error-message string
  ("JPEG stream" vs "PNG stream"). The same DRY-drift risk as D83:
  a future contributor who changes the implementation (e.g. wraps
  the throw in a custom exception type, adds a max-bytes guard,
  switches to `Stream.ReadAtLeast` from .NET 8) would have to
  remember to update both copies. Fix: extracted to a new file
  `src/ExifRemover.Engine/StreamHelpers.cs` with a `context`
  parameter (the error message becomes
  `"Unexpected end of {context} stream."`). The stripper call
  sites pass `"JPEG"` or `"PNG"` so the user-facing error message
  is unchanged.
  New regression tests in `StreamHelpersTests.cs`:
  `ReadExact_ReadsAllBytes_AndFillsBuffer` (the happy path),
  `ReadExact_EmptyStream_ZeroByteBuffer_Succeeds` (the no-op
  boundary),
  `ReadExact_StreamShorterThanBuffer_Throws` (the defining
  behavior vs `Stream.Read`),
  `ReadExact_EmptyStream_NonEmptyBuffer_Throws` (the other
  boundary),
  `ReadExact_ContextTagAppearsInExceptionMessage` (pins the
  format-specific tag in the error message),
  `ReadExact_LargeStream_ReadsAcrossMultipleLoopIterations` (a
  50,000-byte read that forces at least one full loop iteration,
  regression for an off-by-one in the `total` update).
  The test project's csproj needed a new `<Compile Include>` for
  the new `StreamHelpers.cs` (the test project embeds Engine
  sources via `<Compile Include>` per the WDAC sandbox
  workaround — adding a new Engine file requires a corresponding
  test-project include; the same addition is required in
  `ExifRemover.SelfTest.csproj`).
- xUnit: 79 → 88 tests (+9 for D83+D84). SelfTest: 16/16 stable.
- Real-image verifier: ALL CHECKS PASSED (refactor is
  behavior-preserving — the byte-level outputs of both strippers
  are identical to the pre-refactor outputs).

## M2.20.24 — 2026-08-05 — 360° audit round 21 (dead `Warning` property removal)
- **D82** `src/ExifRemover.Engine/StripPipeline.cs:StripResult` —
  the pre-fix code declared a `Warning` property on `StripResult`
  that was never set or read by any caller. It was a placeholder
  for "warning that the strip succeeded but with caveats" (e.g.
  "ICC profile was malformed but we kept it"). The placeholder was
  added in the initial import (commit `605a2d0`) and survived 18
  audit rounds because it was never exercised. The placeholder has
  been removed; if a real warning is ever needed, add it back as a
  concrete property with a specific contract (when it's set, when
  it's read, what it means, and which caller consumes it). A
  postmortem comment block now lives in the source where the
  property used to be, so a future contributor who goes looking
  for it understands the history. The dead property was a
  refactor-drift risk: a future commit could have set it from
  one place and read it from a different place, with neither
  the production code path nor the tests exercising the
  contract — the bug would only surface at runtime.
  New regression test
  `StripPipelineTests.StripResult_HasNoDeadWarningProperty` uses
  reflection to assert that the `Warning` property does NOT
  exist on `StripResult` — a future commit that re-introduces
  the dead property would fail this test loudly, forcing a
  conscious decision about whether the property is actually
  needed (with a concrete contract for when it's set, when it's
  read, and what it means).
- xUnit: 78 → 79 tests (+1 for D82). SelfTest: 16/16 stable.

## M2.20.23 — 2026-08-05 — 360° audit round 20 (APP14 / CMYK color shift)
- **D81** `src/ExifRemover.Engine/JpegMetadataStripper.cs:ShouldDrop` —
  the pre-fix code dropped APP14 (Adobe marker) along with all other
  APPn segments. APP14 carries the color-transform byte that identifies
  a JPEG's color space — YCbCr (value 1) for normal JPEGs, YCCK
  (value 2) for CMYK JPEGs. Dropping APP14 caused a visible color
  shift on CMYK JPEGs after stripping: the encoder's color-transform
  flag is lost, so most viewers fall back to YCbCr decoding and render
  the image with wrong colors. The catalog contract is
  "Privacy keeps color management" — every other color hint (JFIF,
  ICC, gAMA, cHRM, sRGB) is kept under at least one profile. The
  pre-fix code violated that contract for APP14. Fix: return false
  (keep) for marker 0xEE in the APPn branch of `ShouldDrop`. The
  post-fix code preserves APP14 under all 3 strip profiles (Privacy,
  AllMetadata, Minimal). Note that `MetadataExtractor` does not
  surface APP14 as a directory, so the review grid never shows the
  entry — this fix is silent from the UI's perspective, but the
  byte-preservation matters for CMYK decoding.
  New fixture `FixtureFactory.JpegWithApp14()` (a minimal JPEG with
  an APP14 Adobe segment with color-transform byte = 2 for YCCK) and
  new test
  `JpegStripperTests.Strip_JpegWithApp14_PreservesApp14_ForCmykColorSpace`
  (asserts the output is byte-identical to the input under all 3
  strip profiles and `DroppedSegments == 0`).
- xUnit: 77 → 78 tests (+1 for D81). SelfTest: 16/16 stable.

## M2.20.22 — 2026-08-05 — 360° audit round 19 (iCCP double-surfacing probe)
- **D80** (proactive) `tests/ExifRemover.Tests/IccGroupingTests.cs` —
  no source bug was found, but the audit probed a latent risk: PNG
  iCCP chunks can surface BOTH a `PngDirectory` entry (the chunk
  name) and an `IccDirectory` entry (the parsed ICC metadata). If
  `MetadataExtractor` ever starts surfacing both for a PNG with
  iCCP, the current `MapGroup` would route them to two different
  groups ("PNG iCCP" and "ICC Profile") with conflicting keep-set
  membership under the Minimal profile (the PNG one is kept, the
  ICC one isn't), producing a UI lie for the same iCCP data. The
  audit checked the existing `PngWithTextTimeExifIccp` fixture:
  `MetadataExtractor` currently surfaces only the `PngIccp` entry
  (the `Icc` entry is not created because the fixture's compressed
  ICC profile is too small to parse). The new test pins this
  behavior — if a future `MetadataExtractor` version adds the
  `Icc` entry, the test fails loudly with a clear diagnostic
  message naming the iCCP double-surfacing pattern. The fix
  (suppress the duplicate `Icc` entry for PNGs) is a one-liner
  in `MetadataInspector.PngChunkProbe` once it becomes necessary.
- 0 source-code changes; +2 forward-looking defensive tests.
  xUnit: 75 → 77 tests (+2 for the iCCP probe). SelfTest: 16/16
  stable.

## M2.20.21 — 2026-08-05 — 360° audit round 18 (0xFF fill bytes)
- **D79** `src/ExifRemover.Engine/JpegMetadataStripper.cs` —
  the `ReadMarker` helper consumed any `0xFF` "fill bytes" before
  the actual marker byte (the JPEG spec allows arbitrary `0xFF`
  padding between segments, and many encoders insert 1-2 fill bytes
  before the EOI marker to align the file to a 2-byte boundary), but
  the stripper's segment-walker only wrote `0xFF <marker>` (2 bytes)
  for every segment. The fill bytes were silently dropped. A JPEG
  with `0xFF` padding before the EOI would produce a smaller output
  for no reason, and the `Changed` field was set to `true` for a
  file that actually didn't change. The fix: `ReadMarker` returns the
  fill byte count via an `out` parameter, and a local `WriteMarker()`
  helper re-emits the fill bytes before the marker. The new helper
  is used for every marker write site (EOI, SOS, standalone TEM/RSTn,
  regular segments). The `CopyRestVerbatim` post-SOS path was
  already correct (the for-loop finds `0xFF 0xD9` byte-pairs and
  includes both bytes in `writeLen`).
  New fixture `FixtureFactory.JpegWithFillBytes()` (a minimal JPEG
  with 4 fill bytes: 2 after the SOI, 1 between the JFIF APP0 and
  the DQT, 1 before the EOI) and new test
  `JpegStripperTests.Strip_JpegWithFillBytes_OutputIsByteIdentical_NoSpuriousChanged`
  in `tests/ExifRemover.Tests/JpegStripperTests.cs` (asserts the
  output is byte-identical to the input and the `Changed` flag is
  `false`).
- xUnit: 74 → 75 tests (+1 for D79). SelfTest: 16/16 stable.

## M2.20.20 — 2026-08-05 — 360° audit round 17 (edge cases)
- **D71** `src/ExifRemover.Engine/MetadataInspector.cs:Inspect` — the
  `ImageFormatDetector.DetectFile(path)` call was OUTSIDE the inspector's
  try/catch, so a missing / inaccessible / locked / directory file
  produced an unhandled `FileNotFoundException` /
  `UnauthorizedAccessException` / `IOException` that propagated to
  `OverlayViewModel.InspectData`'s `Task.Run` and surfaced as a confusing
  stack trace in the status strip. The fix wraps DetectFile in a
  dedicated try block with 4 catch arms (FileNotFound, DirectoryNotFound,
  UnauthorizedAccess, generic IOException), each returning a
  `FileInspection` with a clear, user-readable Error message and
  `Format = Unknown`. The three error shapes are intentionally distinct
  so a "file doesn't exist" doesn't show up as "access denied" and vice
  versa. Two new tests in
  `tests/ExifRemover.Tests/InspectorEdgeCasesTests.cs`:
  `Inspect_NonExistentFile_ReturnsErrorNotThrow` and
  `Inspect_DirectoryPath_ReturnsErrorNotThrow`.
- **D72** `src/ExifRemover.Engine/JpegMetadataStripper.cs:Strip` and
  `PngMetadataStripper.cs:Strip` — the `new FileInfo(sourcePath).Length`
  call was OUTSIDE the stripper's try/catch (it sat between the
  `actualOutputPath` assignment and the `try { ... }` block). The catch
  block's cleanup logic (delete the temp output if it exists) therefore
  didn't run for FileInfo errors, and any future code added to the
  catch (logging, telemetry) would silently miss those failures. Fix:
  move the FileInfo call inside the try block. Behavioural change is
  minimal (the exception type and message are the same) but the cleanup
  path now runs uniformly for every file-access error. The
  `Strip_NonExistentJpegSource_…` and `Strip_NonExistentPngSource_…`
  tests pin the contract: missing source → I/O exception → no orphan
  output file.
- **D77** `src/ExifRemover.Engine/MetadataInspector.cs:IsPrivacySensitive` —
  the EXIF-directory pattern listed `ExifIfd0Directory`,
  `ExifSubIfdDirectory`, and `ExifInteropDirectory`, but was missing
  `ExifThumbnailDirectory`. The stripper drops the entire APP1 (EXIF),
  which includes the thumbnail IFD, so the Action column correctly
  showed "Would be removed" — but the sensitivity styling (the "this
  is privacy-sensitive" colour/icon) said "not privacy-sensitive"
  because the function returned `false` for any tag in
  ExifThumbnailDirectory. A thumbnail is privacy-sensitive (it can
  embed a separate image with its own metadata, including GPS
  coordinates; it can be a different/cropped/edited version of the
  original). Fix: add `ExifThumbnailDirectory` to the EXIF-directory
  pattern. The "Exif Version / Flashpix Version / Components
  Configuration" exclusion list is harmless for IFD1 — those tags are
  IFD0-only and never appear in ExifThumbnailDirectory, so every
  ExifThumbnailDirectory tag is now correctly marked privacy-sensitive.
  New fixture `FixtureFactory.JpegWithExifThumbnail` (a JPEG with
  IFD0 + IFD1 + an embedded 1x1 thumbnail JPEG) and two new tests in
  `InspectorEdgeCasesTests.cs`:
  `Inspect_ExifThumbnailDirectory_AllEntriesArePrivacySensitive`
  (proves the new pattern entry) and
  `Strip_JpegWithExifThumbnail_ThumbnailIstripped` (proves the
  stripper still drops the thumbnail after the fix).
- **D78** `src/ExifRemover.App/OverlayViewModel.cs` constructor —
  the path loop added a `FileEntryViewModel` for every input path even
  when two entries referred to the same file (e.g. "foo.jpg" and
  "FOO.jpg" from a multi-select, or a hand-typed duplicate in the
  registry). `_byPath` already used `OrdinalIgnoreCase` for lookup, but
  the loop didn't check `_byPath.ContainsKey(p)` before adding to
  `_files` — so the same file appeared twice in the ComboBox, and the
  stripper processed it twice (the second call would either fail
  because the source was modified, or write a duplicate
  "_stripped (2)" sibling). Fix: dedupe with `_byPath.ContainsKey(p)`
  before adding the VM. The fix is in WPF code (not directly
  unit-testable from the net8.0 test project), but the same contract
  is exercised by the batch tests (which use the same
  `StripPipeline.StripBatch` and never produce duplicate siblings).
- xUnit: 66 → 74 tests (+8 for D71 / D72 / D77). SelfTest: 16/16
  stable.

## M2.20.19 — 2026-08-05 — 360° audit round 16
- **D70** `install.cmd` `:do_build` subroutine silently swallowed
  `del` / `move` / `rmdir` failures via the `>nul 2>&1` family of
  error-suppression patterns (same anti-pattern M2.20.17 D68 fixed
  for `reg add`). If the previous `ExifRemover.exe` was running, or
  any sibling DLL was locked by an AV scan / indexer / other process,
  the build would silently fail to update the output, and the user
  would then run the OLD `ExifRemover.exe` with no idea the new
  build never landed. The script's final `echo Build complete.` was
  unconditional. A second anti-pattern — `dir ... | findstr /v "File"`
  — actively hid the `File Not Found` text that `dir` prints when no
  files matched, so even a totally-failed publish would not surface
  the missing exe. Fix: errorlevel checks after every `del` / `move`
  / `rmdir` step with actionable error messages, plus a final
  `if not exist ExifRemover.exe` sanity check that prints a clear
  "Build did not produce ExifRemover.exe" error. The
  `findstr /v "File"` hack is removed. Also improved the
  `:RegAdd` error message from M2.20.17 D68: removed the misleading
  "Re-run with admin rights" hint (ExifRemover registers per-user
  in HKCU; admin rights are not required, and the actual cause of a
  failed reg add is almost always AV / indexer / process lock).
- 0 source-code changes; 0 new tests (D70 is a `.cmd` script fix,
  manually verified by locking ExifRemover.exe and watching the new
  error message + `errorlevel 1` exit).

## M2.20.18 — 2026-08-04 — 360° audit round 15
- **D69** `src/ExifRemover.Engine/MetadataInspector.cs:PngChunkProbe` —
  the PNG `hIST` chunk (palette histogram) was silently invisible to the
  review grid. `MetadataExtractor`'s PNG reader has no `TagHistogram` tag,
  so a hIST chunk surfaced zero entries even though `PngMetadataStripper`
  drops hIST under Privacy/AllMetadata and keeps it under Minimal. The
  user had no way to know the file contained a hIST or that the stripper
  would remove it. The PngChunkProbe (already used to surface the
  rolled-into-PngText eXIf chunk) now also adds a PngHist entry when
  a hIST chunk is present. The review grid shows "PNG hIST" with the
  correct "Would be removed" / "Would be kept" action per profile (the
  existing PNGHIST keep-set, Minimal-only, is unchanged). The hIST
  behavior itself (drop under Privacy/AllMetadata, keep under Minimal)
  is preserved — the fix is only that the chunk is now visible to the
  user. Two new tests in `tests/ExifRemover.Tests/PngStripperTests.cs`:
  `Inspect_SurfacesPngHistAsSeparateGroup` proves the probe now
  surfaces hIST with the right group/name/size; `Strip_PngWithHist_
  HistEntryRemovedAfterStrip` proves the post-strip inspection no
  longer shows hIST (a future regression to the stripper that re-adds
  hIST would also be caught because the chunk would still be present
  in the output bytes). Both tests are adversarially verified
  (reverting the probe addition makes the surfacing test fail with
  "No PngHist entry found").
- xUnit: 64 → 66 tests (+2 for D69's hIST surface/remove cases).

## M2.20.17 — 2026-08-04 — 360° audit round 14
- **D67** `src/ExifRemover.App/app.manifest` contained a fabricated
  Windows 11 `supportedOS` GUID (`{e1b086e2-5834-4d6b-a0c5-321d5705261c}`).
  This GUID is NOT a Microsoft-published value. Microsoft's official
  position (per https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests
  and the `SbSupportedOsList` symbol in ntdll.dll) is that Windows 10/11
  share the same supportedOS GUID
  (`{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`), and no separate Win 11
  GUID has been published. The fake GUID was added by the M2.20.7
  D48 round (10 rounds ago) with a claim that it was "the official
  Microsoft-published value" — that claim was incorrect and
  contradicted Microsoft's docs. Fix: removed the fabricated line,
  added comments clarifying that the Win 10 GUID covers Win 11 +
  Server 2016/2019/2022. Two new tests
  (`AppManifest_AllSupportedOsGuids_AreMicrosoftPublished` and
  `AppManifest_DoesNotDeclareFabricatedWin11Guid`) pin the
  supportedOS list to the 5 Microsoft-published GUIDs so a future
  "let me add a Win 12 / Server 2025 GUID" suggestion fails loudly.
- **D68** `install.cmd` silently swallowed any `reg add` error
  via the `>nul 2>&1` pattern. A failed registry write (e.g. AV
  lock, permissions) would be invisible to the user — the script
  would print "Done." even when half the keys were missing. Fix:
  every `reg add` now goes through a new `:RegAdd` helper that
  surfaces the error and aborts the install with `exit /b 1` on
  the first failure. The pre-fix pattern was a security-adjacent
  bug: a context-menu shell verb that's half-registered can leave
  the user with a confusing "no menu entry" UX and no idea why.
- xUnit: 62 → 64 tests (+2 for D67's "no fabrication" guard).
- Meta-note: D67 is a textbook case of why repeated adversarial
  audits matter. The M2.20.7 D48 "fix" was confidently landed
  with a "Microsoft-published value" claim, and 10 subsequent
  rounds approved it. The M2.20.17 round caught it by going to
  the actual Microsoft source instead of trusting the prior
  audit log.

## M2.20.16 — 2026-08-04 — 360° audit round 13
- **D64** `README.md` §Installation step 2-3 contradicted §Building
  from source: the old text told the user to `cd` into
  `bin\Release\net8.0-windows\` and run `.\install.cmd` from there,
  but `install.cmd` lives in the repo root and uses `%~dp0` to find
  `ExifRemover.exe` in its own directory. The user following the old
  instructions would land in a folder with no `install.cmd`. Section
  rewritten to make the actual install path clear: the installable
  folder is the repo root after `.\install.cmd build`, and any
  relocation must move the whole folder (including the `.cmd` files).
- **D65** `src/ExifRemover.Engine/JpegMetadataStripper.cs:SkipExactly`
  was missing the trust-but-verify EOF check that the PNG stripper
  already has (PngMetadataStripper.cs:231-234). A malformed JPEG whose
  segment-length field claims more bytes than the file contains would
  silently seek past EOF, then the next `ReadMarker` would throw a
  less-informative "no marker" error. Fix: explicit `Position + count >
  Length` check before the seek, matching the PNG version's pattern.
  New test `Strip_App1SegmentTruncatedWithinPayload_…` pins the
  improved error message and the "source untouched, no output file"
  contract.
- **D66** `.github/workflows/build.yml` — bumped the three
  deprecated actions: `actions/checkout@v4` → `@v5`, `actions/setup-
  dotnet@v4` → `@v5`, `actions/setup-python@v5` → `@v6`. The v4/v5
  majors were deprecated to Node 20 (forced to run on Node 24);
  the bumped majors target Node 24 natively and clear the cosmetic
  deprecation warning that appeared in the previous CI runs.
- xUnit: 61 → 62 tests (+1 for D65's truncated-APP1 case).

## M2.20.15 — 2026-08-04 — 360° audit round 12

### Changed
- Project license changed from AGPL-3.0-or-later to **MIT License** to align
  with the upstream public remote. See `LICENSE` for the full text and
  `README.md` for the high-level statement.
- Repository layout tidied: audit log moved to `docs/M2.20-audit-log.md`,
  Python tools moved to `scripts/`. The repo root now contains only build
  files, docs, and the two `.cmd` installer scripts.

### Added
- `CHANGELOG.md` (this file).
- `.github/workflows/build.yml` — CI workflow: build + xUnit + SelfTest
  on Windows and Ubuntu runners, .NET 8.

## M2.20.15 — 2026-08-04 — 360° audit round 12
- **D60** `src/ExifRemover.App/AboutWindow.xaml:37` still said "ExifRemover
  itself is licensed under AGPL-3.0-or-later" — the in-app About dialog was
  the fourth place that drifted from the actual MIT license (M2.20.11b fixed
  LICENSE, M2.20.11c fixed `README.md:126`, M2.20.12 fixed `README.md:85`,
  this round fixes the About dialog). Users who clicked the "?" button saw
  the old AGPL-3.0 text.
- **D61** `src/ExifRemover.App/OverlayWindow.xaml` — the `StatusText` TextBlock
  bound to the post-strip summary did not have `TextWrapping="Wrap"`. The
  summary string uses `\n` for newlines (the failure-list lines in a multi-
  file strip) and a long failure message would have run off the right edge
  of the status strip. Fix: add `TextWrapping="Wrap"` so the multi-line
  summary wraps within the column.
- **D62** `src/ExifRemover.Engine/AtomicFile.cs` — the `private static
  TryDelete(string path)` method was never called. Both strippers use
  `File.Delete` directly with their own try/catch in the outer `catch`
  block. Dead code; deleted outright.
- **D63** `PLAN.md:226` (Build & ship §7) still described the project as
  licensed under AGPL-3.0-or-later. Fourth AGPL→MIT drift (D60 was the
  in-app About dialog, D63 was the planning doc).
- 0 source-code or test changes for D60/D61/D62/D63 (D62 is a code change
  but it's a 12-line dead-code deletion, no tests required).

## M2.20.11b — 2026-08-04 — adopt MIT license
- LICENSE file replaced with the MIT text from the upstream remote
  (`Copyright (c) 2026 Ceegor`). README License section updated to match.

## M2.20.11 — 2026-08-04 — tidy pass
- Working-tree cleanup: 246 gitignored `bin/`, `obj/`, and published-exe
  artifacts (≈152 MB) moved to the OS Trash. No tracked files changed.
  No code or doc changes.

## M2.20.10 — 2026-08-04 — 360° audit round 10
- **D58** PLAN.md §7 corrected: the `sign.cmd` template was claimed to be
  included; the project does not ship one. Section now describes the
  opt-in workflow for users who have their own code-signing certificate.
- **D59** README §Installation: the `[Releases page](#)` placeholder was
  removed. The project does not yet publish binary releases; users build
  from source. Section rewritten to point at the build-from-source
  instructions.
- 0 source-code or test changes. First audit round that is pure
  documentation drift.

## M2.20.9 — 2026-08-04 — 360° audit round 9
- **D56** `AtomicFile.Replace` was never called and the unused
  `tempContent` parameter was misleading. Method deleted outright (not
  refactored into stripper usage).
- **D53 / D57** Test-gap fill: 10 new tests across 2 new test files.
  - `tests/ExifRemover.Tests/AtomicFileTests.cs` (5 cases) covers
    `AtomicFile.NextNonClashingPath` (DesiredFree, DesiredTaken,
    DesiredAndFirstSiblingTaken, NoExtension, HolesInSequence).
  - `tests/ExifRemover.Tests/StripProfileTests.cs` (5 cases) covers
    `StripProfileCatalog.Describe` (Privacy, AllMetadata, Minimal,
    LongDescription_AlwaysPopulated, UnknownEnumValue_Throws).
- xUnit: 51 → 61 tests.

## M2.20.8 — 2026-08-04 — 360° audit round 8
- **D51** `MetadataGroups.Other` was marked "Would be removed" but the
  stripper may not actually drop arbitrary unknown groups. Added `"Other"`
  to the keep-set unconditionally (fail-safe: never claim we'll drop
  something we might not).
- **D52** `FormatBytes` was duplicated in `EntryRow` and `OverlayWindow`.
  Extracted to `src/ExifRemover.App/Formatting.cs`. Both call sites now
  use the helper.

## M2.20.7 — 2026-08-04 — 360° audit round 7
- **D47** Misleading `AssertThrowsAny<T>` alias in SelfTest was deleted;
  3 call sites updated to use `AssertThrows<Exception>`.
- **D48** `app.manifest` was missing the Windows 11 `supportedOS` GUID.
  Added `{e1b086e2-5834-4d6b-a0c5-321d5705261c}` (Microsoft-published
  value).
- **D49** `.gitignore` had three dead patterns (`verify_*.png`,
  `verify_*.jpg`, `gen_*.jpg`) that did not match anything the Python
  scripts actually produce. Deleted.
- **D50** Dead `<None Include="Fixtures\**\*">` in `ExifRemover.Tests.csproj`
  (the test project generates fixtures at test time, none are committed).
  Deleted the empty ItemGroup.

## M2.20.6 — 2026-08-04 — 360° audit round 6
- **D40** Unused `MonoText` style in `Resources/Theme.xaml` deleted.
- **D41** PLAN.md §10 claimed `Directory.Build.props` existed; it does
  not. Rewritten to match the per-csproj reality (each csproj sets
  `Nullable`, `LangVersion`, `ImplicitUsings`, `TreatWarningsAsErrors`
  individually).
- **D43** PLAN.md §6 claimed fixtures are committed; they are generated
  at test time. Rewritten to describe the generation-at-test-time model.
- **D46** `SafeInvoke` caught the narrow `TaskCanceledException`. Widened
  to `OperationCanceledException` (the base class) for forward
  compatibility.

## M2.20.5 — 2026-08-04 — 360° audit round 5
- **D36** `FilterText` setter updated `VisibleEntryCount` but not
  `StatusText`. Result: stale count displayed after filtering. Fix:
  call `UpdateStatusFromEntries()` after `EntriesView.Refresh()`.
- **D37** `verify/StripperLib.cs` was never called and would silently
  return a 0-byte stub. Deleted.
- **D38** `install.cmd` `CMD_EXE` and `CMD_PCT` variables were set but
  never used. Deleted the dead assignments.
- **D39** `PngMetadataStripper` drop branch had an unreachable
  `if (sawIend) break;` (the loop condition already handles it). Deleted.
- **L1** README §Strip Profiles: the "AllMetadata — same as Privacy, plus
  ICC" wording was misleading (Privacy already drops ICC). Reworded to
  describe the actual delta: the PNG color-management chunks
  (`gAMA`/`cHRM`/`sRGB`).

## M2.20.4 — 2026-08-04 — 360° audit round 4
- **D15** `Dispatcher.Invoke` in 6 `Task.Run` callbacks would throw
  `TaskCanceledException` if the window closed mid-strip. New
  `SafeInvoke(Action)` helper wraps every dispatcher call:
  `HasShutdownStarted` check + `OperationCanceledException` catch.
  Two-layer guard.
- **D31** `PathFilter.IsSupportedImageExtension` did a strict comparison
  that rejected `"photo.jpg "` (trailing space from a real-world
  drag-drop). Fix: `TrimEnd()` before compare.
- **D32** No test for corrupt JPEG (valid extension, bad header) in a
  batch. New `StripBatch_CorruptJpegWithValidExtension_RecordsFailure_AndContinuesBatch`.
- **D33** No test for the kept-chunk allocation path with a non-trivial
  IDAT. New `Strip_LargeKeptIdat_AllocatesAndPreservesBytes` (10 MB IDAT).
- **D35** (Documented) APP14 (Adobe marker) is dropped by the JPEG
  stripper. CMYK images may show a small color shift on decode.
  Documented in `docs/M2.20-audit-log.md` as a v1 design limitation.
  Not fixed in this round.
- xUnit: 47 → 51 tests.

## M2.20.3 — 2026-08-04 — 360° audit round 3
- **D9** `SetNonFatalNotice` was overwritten by the VM's `StatusText`
  getter on the next property-change notification. Fix: the getter now
  composes `NonFatalNotice + base`; the setter stores only the base.
  The XAML binding always reads the computed value.
- **D10** `SetNonFatalNotice` was overwritten by `Loaded` re-pushing the
  base subtitle. Fix: bind `SubtitleText` to a new `VM.SubtitleText`
  composed property. All code paths (`Loaded`, `ShowFatal`,
  `SetNonFatalNotice`) go through the VM, so the notice is preserved.
- **D11** Remove button could fire during the initial inspect of a
  freshly-opened overlay. Fix: disable Remove while inspect is in
  flight.
- **D12** `FilterSupported` was silent on invalid paths. New
  `PathFilter` class (in Engine, not App) so the keep/drop logic is
  unit-testable from a non-WPF test project. Per-path reasons are now
  reported.
- **D13** `CopyValue` / `CopyRow` did not catch `COMException` from the
  Windows clipboard. Added try/catch in both.
- **D14 / D7** No re-inspect path after strip. New "↻" Re-inspect
  button in the header (next to "?"). Disabled while any operation is
  in flight. New `VM.Reinspect()` method.
- xUnit: 39 → 47 tests.

## M2.20.2 — 2026-08-04 — 360° audit round 2
- **D1** `SetNonFatalNotice` dead code path (MainWindow reference was
  null at the time of call). Fix: reorder `App_Startup` so MainWindow is
  created before the method can be invoked.
- **D2** `PngUnknown` UI lied: the chunk was marked "Would be removed"
  but the stripper would keep it. Fix: added `PNgUnknown` to the keep
  set and to `GetChunkKey`.
- **D3** Verifier touched and then cleared the input file when
  `input == output`. Fix: removed the no-op clear.
- **D4** `CopyRestVerbatim` wrote junk past the EOI marker (every byte
  after 0xD9). Fix: stop at EOI. Boundary case (0xFF in previous buffer,
  0xD9 at start of current) writes only 0xD9 (the 0xFF was already
  written in the previous iteration).
- **D5** `Task.Run` in `OverlayWindow_Loaded` had unobserved exceptions.
  Fix: wrap `InspectAll` in try/catch, surface errors via
  `Dispatcher.Invoke`.
- **D6** README "All metadata" wording was ambiguous. Reworded to make
  the per-profile delta explicit.
- xUnit: 35 → 39 tests.

## M2.20.1 — 2026-08-04 — 360° audit pass (round 1)
- **11 fixes** spanning the JPEG and PNG strippers, the overlay VM, the
  SelfTest harness, and the project `.gitignore`.
- **+8 xUnit tests**, total 27 → 35.
- **ICC verifier** added: the verifier project now reads and reports
  the ICC profile size delta across the three strip profiles, so a
  regression where Privacy/AllMetadata stops dropping ICC (or Minimal
  starts dropping it) fails the verifier loudly.
- **WDAC sandbox workaround**: the test, SelfTest, and verifier projects
  embed the Engine sources via `<Compile Include>` rather than
  referencing `ExifRemover.Engine.dll` (which the sandbox WDAC policy
  blocks with `0x800711C7` on freshly-built binaries).

## 605a2d0 — 2026-08-04 — initial import
- First commit on `main`. Project imported in its pre-audit state
  (the 11 critical findings later enumerated in
  `docs/M2.20-audit-log.md` were all live at this point).
