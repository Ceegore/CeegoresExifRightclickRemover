# Changelog

All notable changes to ExifRemover are recorded here. Versions follow
the `M2.20.x` (audit round) convention used by the project.

## [Unreleased]

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
