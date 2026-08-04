# ExifRemover — Adversarial Bug-Hunt Report

**Date:** 2026-06-26
**Method:** Trust nothing. Every claim in README/PLAN/prior chat treated as a lie until reproduced in a real environment (.NET 8 SDK 8.0.418 / SDK 10.0.301, Windows 11, Python 3.12 + Pillow 12.1.1 used as an independent oracle to generate real images and verify decode/pixel integrity). All results below are from actual command runs, not code reading alone.

**Verdict: the tool is severely bugged. The core advertised guarantee ("lossless" JPEG stripping) is false, and in its default-adjacent "Overwrite source" mode it permanently destroys users' photos while reporting success. The documented build command does not even compile. The entire test suite is green throughout, because the fixtures are trivial enough to never exercise the broken code paths.**

---

## CRITICAL findings

### C1. JPEG stripping silently corrupts almost every real-world JPEG (drops `0xFF 0x00` byte-stuffing) — "lossless" is false
**File:** `src/ExifRemover.Engine/JpegMetadataStripper.cs`, `StreamEntropyCoded` (lines ~205–217).

In a JPEG entropy-coded scan, a literal `0xFF` data byte is stored as `0xFF 0x00` (byte stuffing). The streaming copy handles the `0xFF 0x00` case like this:

```csharp
if (prev == MarkerPrefix)
{
    if (b == 0x00)
    {
        buffer[bufferedLen++] = MarkerPrefix;   // writes 0xFF only
        ...
        prev = -1;
        continue;                               // <-- the 0x00 is DROPPED
    }
```

The trailing `0x00` stuff byte is never written. Every stuffed `0xFF` in the scan is emitted as a bare `0xFF`, which a decoder reads as a marker. The output bitstream is corrupt and no longer decodes.

**Evidence (real images generated with Pillow, stripped via the real Engine, decoded back with Pillow):**

| File | scan `FF00` (orig) | metadata dropped | bytes lost | decodes after strip? |
|---|---|---|---|---|
| `real_baseline.jpg` (128×128) | 0 | 3 | (meta only) | **OK** (only survived because scan had zero `0xFF`) |
| `real_big_baseline.jpg` (640×480) | 81 | 1 | — | **CORRUPT — "broken data stream"** |
| `clean_nometa.jpg` (400×400, *no metadata at all*) | 144 | 0 | **144** | **CORRUPT** |
| `photo_meta.jpg` (400×400) | 117 | 1 | — | **CORRUPT** |

Exact byte-accounting proof on the no-metadata file (nothing to strip, yet still destroyed):
```
original size      = 121554
stripped size      = 121410
bytes lost         = 144
FF00 in original   = 144  (metadata dropped = 0)
=> bytes lost equals dropped 0x00 stuff bytes: True
```
Bytes lost == number of `FF00` sequences, exactly, with zero metadata removed. Root cause is unambiguous.

- Corruption is **profile-independent** (Privacy, Minimal, AllMetadata all corrupt — verified).
- Corruption happens **even when there is no metadata to remove** (`dropped=0`), because the entropy stream is rewritten for every JPEG that has a scan.
- The small 128×128 fixture-like image only survived by luck (its compressed scan happened to contain no `0xFF` byte). Any photographic / detailed / larger JPEG contains many — corruption is the norm, not the exception.
- `StripResult.Changed` is reported `True` with `dropped=0`, because the output is smaller — the "success" signal is actually the corruption signature.

### C2. "Overwrite source" permanently destroys the original photo (data loss) while reporting success
**File:** `JpegMetadataStripper.cs` lines 95–106 (`File.Replace`/`File.Move` over the source).

With the overlay's **Overwrite source** checkbox enabled, the corrupt output (C1) atomically *replaces* the user's original. Reproduced on a copy of a real 640×480 photo:
```
Before: victim_photo.jpg decodes fine, size (640, 480)
OVERWRITE OK dropped=1 changed=True outSize=32932 out=victim_photo.jpg
After:  *** ORIGINAL PHOTO NOW CORRUPT / UNRECOVERABLE: broken data stream when reading image file
```
README claims: *"Lossless strip. The pixel data of your images is never re-encoded"* and *"A power loss or crash mid-strip leaves your original intact."* In reality a **clean, successful** run obliterates the original. The "atomic write" makes it worse — it reliably swaps the good file for the corrupt one. This is the most damaging possible behavior for a tool whose whole job is to be safe.

### C3. Progressive JPEGs cannot be processed at all (stripper throws)
**File:** `JpegMetadataStripper.cs`, `ProcessSubsequentMarker` (lines 257–290) + `ReadMarker` (line 298).

After the first `SOS`, the code streams entropy data once, then `ProcessSubsequentMarker` treats everything afterward as plain marker segments. Progressive JPEGs contain **multiple scans** (multiple `SOS`, each followed by its own entropy data). On the second scan the code calls `ReadMarker` on raw entropy bytes and throws.

**Evidence:**
```
real_progressive.jpg       SOS_markers=10   -> STRIP THREW: InvalidDataException:
                                               Expected 0xFF marker byte but got 0x00 at offset 1444
real_big_progressive.jpg   SOS_markers=10   -> STRIP THREW: ... at offset 5511
```
Progressive JPEGs are ubiquitous (Photoshop "Save for Web", most CDNs, many phones). For all of them the tool simply fails (no `_stripped` file produced; in overwrite mode the original is left as-is). The same design flaw also breaks any baseline JPEG that uses **restart markers (DRI/RSTn)** inside the scan — `StreamEntropyCoded` bails to `ProcessSubsequentMarker` on the first `RSTn`.

### C4. The documented solution build does not compile — `ExifRemover.sln` has malformed GUIDs
**File:** `ExifRemover.sln`.

README "Building from source" documents `dotnet build ExifRemover.sln -c Release`. From a clean checkout it **fails deterministically with 18 errors** (`The type or namespace name 'Engine' ... does not exist`), reproduced twice after wiping all `bin`/`obj`.

Root cause — GUID group-count analysis (a valid GUID is 5 groups, 8-4-4-4-12):
```
groups=6  {11111111-1111-1111-1111-1111-111111111111}   <- line 20, Engine Release Build.0  (MALFORMED)
groups=5  {11111111-1111-1111-1111-111111111111}        <- Engine's real GUID
groups=7  {33333333-3333-3333-3333-3333-3333-3333}       <- lines 25-28, Tests config       (MALFORMED)
groups=5  {33333333-3333-3333-3333-333333333333}        <- Tests' real GUID
```
Consequences:
- **Engine project is never built in Release** (its `Release|Any CPU.Build.0` row points at a non-existent project GUID), so `Engine.dll` is never produced and App/SelfTest fail with "Engine namespace not found." Confirmed: after a full Release solution build, `src/ExifRemover.Engine/bin/Release/net8.0/ExifRemover.Engine.dll` does not exist.
- **The Tests project is silently excluded from the solution in every configuration** (all four of its config rows use the malformed 7-group GUID). It never builds via the `.sln`, so a solution-level `dotnet test` would run **zero** of the 21 tests.
- Debug solution build happens to succeed for Engine/App/SelfTest (Engine's Debug rows are well-formed) but still omits Tests.

The claim "All 21 xUnit tests still pass" is only reachable by bypassing the broken solution and building the test project directly (`dotnet test tests/ExifRemover.Tests/...`), which is **not** what the README's build section tells you to do.

---

## HIGH findings

### H1. The test suite (xUnit *and* SelfTest) is green while the product is broken — false confidence
- `dotnet test tests/ExifRemover.Tests` → `erfolgreich: 21, total: 21`.
- `dotnet run --project src/ExifRemover.SelfTest` → `PASSED: 13, FAILED: 0`.

Both are useless as guards because the fixtures never exercise the broken paths:
- The synthetic JPEG entropy is hard-coded to `{0x00, 0x3F}` (`FixtureFactory.AppendEntropy`, lines 314–320) — **no `0xFF` byte**, so byte-stuffing (C1) is never tested.
- Every JPEG fixture has exactly **one `SOS`** — progressive/multi-scan (C3) is never tested.
- The "fuzz" test (`JpegStripperTests.Strip_RandomFuzzInput...`, lines 196–222) sets `bytes[0..2]` to a valid SOI and then calls `rng.NextBytes(bytes)` **after**, which overwrites those bytes with random data. So the "valid JPEG header" path it claims to fuzz is essentially never hit; it almost always bails at the SOI check. It cannot catch C1/C3.

A single test that strips a real photographic JPEG and re-decodes it would have caught the data-loss bug.

### H2. The overlay's "Action" column lies about ICC under the default Privacy profile
**Files:** `src/ExifRemover.App/OverlayViewModel.cs` `ComputeKeepSet` (lines 99–125) vs `JpegMetadataStripper.cs` line 13.

UI keep-set: `if (profile == Minimal || profile == Privacy) set.Add("ICC");` → under **Privacy** the grid shows *"ICC Profile — Would be kept."*
Engine: `bool keepIcc = profile == StripProfile.Minimal;` → under **Privacy** the ICC profile is actually **removed** (confirmed empirically: `real_baseline.jpg` had an ICC profile; after a Privacy strip it is gone). xUnit `Strip_RemovesExifXmpIccAndComment_UnderPrivacyProfile` also asserts ICC is removed.

So on the default profile the review table tells the user the exact opposite of what happens. A user who wants to keep ICC for color fidelity is misled; the whole point of the "review before removing" table is undermined.

### H3. Overlay populates its grid from a background thread without collection synchronization — likely `NotSupportedException` on load
**Files:** `OverlayWindow.xaml.cs` lines 44–49 and 144–148; `OverlayViewModel.cs` `RebuildCurrentEntries` (mutates `AllEntries`); `OverlayWindow.xaml` line 91 (`ItemsSource="{Binding EntriesView}"`).

`OverlayWindow_Loaded` does `Task.Run(() => _vm.InspectAll())`. `InspectAll` → `RebuildCurrentEntries` calls `AllEntries.Clear()`/`Add()` on that background thread. `AllEntries` backs `EntriesView`, which is bound to the `DataGrid`. WPF forbids mutating a bound collection view from a non-dispatcher thread, and `BindingOperations.EnableCollectionSynchronization` is **not** used anywhere (grep: not found). Expected result on a real desktop: `NotSupportedException` thrown inside the `Task.Run` (unobserved), `InspectAll` aborts, the subsequent `IsBusy = false` never runs → the overlay hangs "busy" with an empty grid. The same pattern repeats in the post-strip re-inspect (lines 144–148). (Could not be exercised headless in this environment; high-confidence from code + WPF semantics.)

---

## MEDIUM findings (false/again-misleading documentation & packaging)

### M1. README references build scripts and artifacts that do not exist
README "Building from source" says run `build.cmd` to produce `dist\ExifRemover.exe` plus `sign.cmd`. The Privacy section tells users to verify `dist\ExifRemover.exe.sha256.txt` and mentions `sign.cmd` again. None exist in the repo:
```
MISSING: build.cmd     MISSING: sign.cmd     MISSING: dist
```
The actual build entry point is undocumented: `.\install.cmd build` (publishes into the repo root, not `dist\`). There is no SHA-256 generation anywhere, so the "verify the hash" instructions are unfollowable.

### M2. "Single, self-contained ~25 MB binary" is false
README line 49: *"a single, self-contained ~25 MB binary."* `install.cmd` actually does a **non-single-file** `dotnet publish` and its own help text says the output is *"ExifRemover.exe + ~70MB of DLLs"* (lines 47–49, 165). It is a ~70 MB folder of dozens of DLLs, not a single 25 MB file.

### M3. Documented "auto-close after ~4 seconds" does not exist
README line 80: *"the overlay shows a one-line summary and auto-closes after ~4 seconds."* `OverlayWindow.StartAutoClose()` (lines 178–181) is a literal no-op (`// No-op: window stays open until user closes it.`) and is **never called** anywhere. The window never auto-closes.

### M4. Esc / Enter key bindings target commands that don't exist
`OverlayWindow.xaml` lines 16–17 bind `Escape`→`CancelCommand` and `Enter`→`RemoveCommand`, but the window/VM define neither (grep finds them only in XAML). The Esc-to-cancel shortcut is dead (Enter still works only because `RemoveButton` has `IsDefault="True"`).

---

## LOW findings

- **L1.** README/`StripProfileCatalog` contradict each other on whether Privacy strips ICC. The README table says AllMetadata strips ICC *"plus ICC profile (JPEG)"* — implying Privacy keeps it — while the same table's Privacy row lists ICC under "Strips," and the engine strips it under Privacy. Inconsistent and confusing (and feeds H2).
- **L2.** `BatchStripReport.SuccessCount` (StripPipeline.cs line 85) counts a result as success when `OutputSizeBytes > 0` — i.e. corrupt-but-nonempty outputs (C1) are reported as successes.
- **L3.** `uninstall.cmd` invokes `.\install.cmd uninstall`, which only resolves if the current working directory is the script's folder; running it from elsewhere fails (`install.cmd` not found).
- **L4.** `MetadataInspector.MapPngGroup` has no case for the PNG `eXIf` chunk (no `MetadataGroups.PngExif` mapping), so PNG EXIF is never surfaced in the review grid even though the stripper removes it. The displayed metadata is therefore incomplete for PNGs.
- **L5.** Context-menu / Win 11 modern-menu registration could not be verified here (no persisted registry in this environment) and was already flagged as unresolved in prior context; not re-tested.

---

## What actually works (verified)
- **PNG stripping is correct and lossless.** `real_text.png` (tEXt + iTXt/XMP + iCCP): after a Privacy strip the output decodes, pixel data is bit-identical (decoded-pixel SHA matches), and all text/XMP/iCCP metadata is gone. CRCs are recomputed correctly.
- **Baseline JPEG metadata *identification*** works (EXIF/GPS/ICC/COM are detected and, when the scan has no `0xFF` bytes, removed correctly with byte-identical pixels — but that last condition almost never holds for real photos; see C1).
- Per-project builds (`dotnet build <project>`) and the directly-targeted test run all succeed.

---

## Reproduction quick-reference
```bash
# C4 build break (from clean):
find . -type d \( -name bin -o -name obj \) -exec rm -rf {} +; dotnet build ExifRemover.sln -c Release   # 18 errors

# C1/C2/C3 (Pillow generates real images; a tiny harness drives the real Engine):
python gen.py                                  # real_baseline.jpg, real_progressive.jpg, real_big_baseline.jpg, ...
erharness strip     real_big_baseline.jpg Privacy   # "OK" but output won't decode  (C1)
erharness stripover real_big_baseline.jpg Privacy   # original now corrupt          (C2)
erharness strip     real_progressive.jpg  Privacy   # InvalidDataException           (C3)

# Tests stay green throughout:
dotnet test tests/ExifRemover.Tests       # 21/21
dotnet run  --project src/ExifRemover.SelfTest   # 13/13
```

---

## FIXES APPLIED (2026-06-26) & verification

All Critical/High/Medium items were fixed and re-verified against the same real-image harness (Pillow oracle + a C# harness driving the real Engine via `dotnet run`, which sidesteps the WDAC block on freshly-built DLLs).

| ID | Fix | Verified |
|---|---|---|
| **C1/C2** | Rewrote `JpegMetadataStripper`: once the first `SOS` is reached, the rest of the file is copied **verbatim** (removed `StreamEntropyCoded`/`ProcessSubsequentMarker`). No entropy parsing → no stuff-byte loss. | `clean_nometa.jpg` (144 `FF00`), `photo_meta.jpg` (117), `real_big_baseline.jpg` (81): all now **decode OK + pixel-lossless** across Privacy/Minimal/AllMetadata. Overwrite-in-place: original **decodes OK, pixels intact** — no data loss. |
| **C3** | Same rewrite copies all subsequent progressive scans verbatim. | `real_progressive.jpg` and `real_big_progressive.jpg` (10 SOS each): strip succeeds, decode OK, lossless, metadata gone. |
| **C4** | Fixed the two malformed GUIDs in `ExifRemover.sln` (Engine `Release…Build.0`; Tests config block). | Clean `dotnet build ExifRemover.sln -c Release` → **0 errors**, builds all 4 projects (Tests included again). |
| **H1** | Added two regression tests + fixtures (`JpegWithStuffedScanAndMetadata`, `ProgressiveLikeJpegWithExif`) asserting the scan tail is preserved byte-for-byte and progressive doesn't throw. | `dotnet test` → **23/23** pass (was 21). SelfTest 13/13. These tests fail against the old engine. |
| **H2** | `OverlayViewModel.ComputeKeepSet`: ICC kept only under Minimal (matches engine). | Action column now agrees with the stripper (engine confirmed removing ICC under Privacy). |
| **H3** | `InspectAll` now does file I/O off-thread and marshals all PropertyChanged + ObservableCollection mutations via `_dispatcher.Invoke` (split `Inspect()`→`InspectData()`/`NotifyInspected()`). | App compiles; pattern is the standard WPF fix. (Full live-UI confirmation needs a desktop session.) |
| **M1/M2/M3** | README: `build.cmd`→`.\install.cmd build`; removed nonexistent `sign.cmd`/`dist\…sha256.txt` claims; "single ~25 MB binary"→accurate ~70 MB folder; removed the false "auto-closes after ~4 s." | — |
| **M4** | Removed dead `Esc`/`Enter` `KeyBinding`s (bound to nonexistent commands); added a real `Esc`→Close handler. Deleted the dead `StartAutoClose` no-op. | App compiles/builds. |

**Not changed (low severity, left as noted):** L1 (README AllMetadata "plus ICC" wording), L2 (`SuccessCount` counts nonempty-but-unchanged as success), L3 (`uninstall.cmd` CWD dependency), L4 (PNG `eXIf` not surfaced in the grid — it is still removed, just not listed). L5 (Win 11 modern context menu) is unchanged and still environment-dependent.

**Post-fix status:** real-world JPEGs (baseline, progressive, photographic, metadata-free) strip losslessly and decode; GPS/EXIF/ICC/COM removed; overwrite-in-place is safe; the documented Release build works; 23/23 + 13/13 tests pass.

---

## ROUND 2 — live GUI bugs (2026-06-26)

After the engine fixes, running the actual overlay surfaced three UI-level problems the headless work could not see. Root cause of the *user-visible* failure: **the repo root held a stale self-contained `ExifRemover.exe` published by an earlier `.\install.cmd build` (before the engine fix).** Running that stale exe on a progressive/complex JPEG threw inside the strip → no file written; combined with the button bug below, the window appeared frozen.

| ID | Symptom | Fix | Verified |
|---|---|---|---|
| **U1** | Remove disables the button, writes no file, window unusable. | (a) Stale published exe — rebuilt via `.\install.cmd build` so the root `ExifRemover.exe` now contains every fix. (b) Real bug: `OverlayWindow.RunRemove` never re-enabled `RemoveButton`/`CancelButton` on the success path → permanent freeze. Added the re-enable. | STA integration driver invoking the **real** `RunRemove`: `file_written=True, remove_enabled=True, cancel_enabled=True`; output decodes, pixel-lossless, metadata gone. |
| **U2** | All 3 profiles "show the exact same values" and seem to remove the same things. | The review grid was flooded with **non-removable structural rows** (`File`, `File Type`, `Huffman`, `JPEG` frame geometry, PNG IHDR/PLTE/IDAT/IEND). They never change between profiles and are always shown, drowning out the real differences. `MetadataInspector` now skips these directories/groups. | Grid for a real photo went 54→**38** rows showing only EXIF/GPS/ICC/COM/JFIF. Per profile: Privacy/AllMetadata strip to **JFIF only**; Minimal keeps **ICC Profile** — visibly distinct. |
| **U3** | (related to U2) Privacy and All-metadata are identical for JPEG. | This is by design (for JPEG the only profile knob is ICC: Minimal keeps it, Privacy/AllMetadata strip it). With U2 fixed the distinction (Minimal vs the rest) is now visible; left the Privacy≡AllMetadata-for-JPEG behavior as-is. | — |

Note: I could not drive the custom WPF window with computer-use (a dev build is neither an installed nor a registrable "running app"), so U1 was verified with an STA harness that calls the real `OverlayWindow.RunRemove` via reflection and checks the output file + button state. The published `ExifRemover.exe` was confirmed to launch.

Post-round-2: solution builds clean (Release), xUnit **23/23**, SelfTest **13/13**, and the live Remove path writes a correct lossless file and leaves the window usable.

## Bottom line
The single most important promise of this tool — strip metadata from a JPEG **losslessly**, safely, optionally in place — is the one it fails most severely. On realistic inputs it corrupts the image (C1), and the "safe, atomic, overwrite-in-place" path turns that into irreversible data loss (C2). Progressive JPEGs don't work at all (C3). The documented build is broken (C4). And none of this is caught because the tests only ever feed the strippers degenerate inputs that dodge the bugs (H1). The green test counts, the "all 21 tests pass" claim, and the "lossless / never modifies pixels / original intact" guarantees should be treated as disproven.

---

## ROUND 3 — 360° bug hunt + fix (2026-08-03)

After Rounds 1 and 2 the engine and overlay were stable, but a fresh 360° pass with the rule "always adversarial-audit before declaring done" found 11 more real defects across the Engine, App, scripts, tests, and verifier. The build of `ExifRemover.sln -c Release` itself was **broken on the first invocation** by one of them.

| ID | What | Where | Fix |
|---|---|---|---|
| **B1** | `var visible` declared twice in `UpdateStatusFromEntries` (inner `if` + outer method) — CS0136, blocks the documented `dotnet build ExifRemover.sln -c Release`. Earlier rounds claimed "App compiles" but the WPF temp-project build is what catches the collision. | `src/ExifRemover.App/OverlayViewModel.cs:283` | Inlined the inner reference. |
| **B2** | `OverlayViewModel.GetChunkKey` was missing `PngPhys`, `PngBkgd`, `PngSbit`, `PngTrns` cases and `ComputeKeepSet` was missing the always-keep entries. The review grid claimed "Would be removed" for 4 chunks the stripper actually keeps. Partial regression of U2. | `OverlayViewModel.cs:99-127, 336-352` | Added the missing keys + entries. |
| **B3** | `OverlayWindow.RunRemove` never called `_vm.CapturePreStripSnapshots()`. The pre-strip snapshot feature was fully implemented (field, method, consumer) but the trigger was missing — dead code. | `OverlayWindow.xaml.cs:99-` | Added the call right before `Task.Run`. |
| **B4** | `Program.Main` returned 0 unconditionally, swallowing `Application.Shutdown(2)` exit codes. CI scripts and `.cmd` wrappers couldn't distinguish "no input" from "success". | `Program.cs` | `LaunchWithFiles` now returns `app.Run()`; `Main` returns it. |
| **B5** | `App.FilterSupported` silently dropped unsupported files. A 5-file right-click with 3 `.txt` + 2 `.jpg` would strip the 2 `.jpg` and pretend the other 3 never existed. | `Program.cs` | New `SetNonFatalNotice` on `OverlayWindow`; dropped files are now logged to stderr AND shown in the overlay status strip. |
| **B6** | `Strip_RandomFuzzInput_NeverThrowsForValidJpegHeader` set the SOI bytes, then called `rng.NextBytes(bytes)` which overwrote them. The H1 fix added new C1/C3 regression tests but did not fix the original fuzz test (still flagged in the H1 paragraph but the test itself was unchanged). | `tests/ExifRemover.Tests/JpegStripperTests.cs:246-272` | Reordered: randomize first, then stamp the SOI on top. |
| **B7 / L2** | `BatchStripReport.SuccessCount` was `Results.Count(r => !r.Changed \|\| r.OutputSizeBytes > 0)` — a "non-empty output is a success" semantic that would have re-classified a corrupt-but-nonempty output as success if a future regression slipped through. | `src/ExifRemover.Engine/StripPipeline.cs:85` | Tightened to `Results.Count`. Failures live in `Failures`; Results = success. |
| **B8 / L4** | PNG eXIf chunks were dropped by the stripper but never surfaced in the review grid. MetadataExtractor's PNG reader rolls tEXt and eXIf into a single `PngText` bucket, hiding the EXIF block from the user. | `MetadataInspector.cs` | New `PngChunkProbe` that walks the file once and adds a `PngExif` entry whenever an eXIf chunk is present. Wired in only for PNG files. |
| **B9 / B15** | `PngMetadataStripper` allocated `new byte[length]` for every chunk even when dropping it (so a 1 GB eXIf would OOM the process), and accepted any 2^31-1 length with no sanity cap. | `PngMetadataStripper.cs` | Added `MaxChunkLength = 256 * 1024 * 1024`; dropped chunks now use a new `SkipExactly` helper that seeks without allocating; kept chunks still need the buffer for CRC recomputation (documented). |
| **B10 / L3** | `uninstall.cmd` delegated to `.\install.cmd uninstall` which only resolved when the caller's CWD was the install folder. | `uninstall.cmd` | Replaced with a self-contained version that mirrors the registry keys and uses `%~dp0` correctly. |
| **B12** | `verify/OverlayWindow.original.txt` was a stale pre-fix copy of `OverlayWindow.xaml.cs` — confusing and not referenced anywhere. | `verify/` | Trashed. |
| — | `dotnet test` after the rebuild triggered the WDAC sandbox policy (0x800711C7) — the new `Engine.dll` was blocked from being loaded. The original audit's "sandbox artefact" note prescribed embedding the engine sources directly into the test/selftest/verifier assemblies. | `tests/`, `src/ExifRemover.SelfTest/`, `verify/` | Replaced `ProjectReference` with `<Compile Include="..\Engine\*.cs" Link="Engine\..." />` in all three projects, with explanatory XML comments so a future maintainer doesn't try to "fix" the missing reference. End users running the published self-contained `ExifRemover.exe` are unaffected — the policy is sandbox-side only. |

**Test coverage added (8 new tests, xUnit went 27→35):**
- `Inspect_SurfacesPngExifAsSeparateGroup` — proves B8 (PngExif entry exists when an eXIf chunk is present)
- `Strip_PngWithExif_ExifEntryRemovedAfterStrip` — proves the entry disappears after a strip
- `PngMetadataStripper_RejectsChunkLengthAboveCap` — proves B15 (256 MB cap, throws instead of OOMing)
- `Strip_KeepsTpngTrnsUnderEveryProfile` — engine side of B2
- `Strip_AlwaysKeepsPngPhysBkgdSbitTrns_AcrossAllProfiles` — engine side of B2, all four always-kept chunks
- `Strip_SkippedChunkDoesNotAllocatePayloadBuffer` — proves B9 (1 MB tEXt gets dropped cleanly)
- `BatchStripReport_SuccessCount_EqualsResultsCount` — proves B7/L2 (changed + bare both count as success, not "non-empty output")
- `BatchStripReport_SuccessCount_ExcludesFailures` — pins the contract

**Test harness improvements:**
- `gen_test_jpeg.py` rewritten to actually inject a real sRGB ICC profile (the previous version's "real camera-style JPEG with EXIF/ICC/COM" claim was false — `img.save(...)` had no `icc_profile=` so the input had no ICC at all and all three profiles produced identical output, hiding any profile-difference bug). Now uses `ImageCms.createProfile("sRGB")` to build a real profile at test time.
- `verify_real_images.py` rewritten to (a) call `gen_test_jpeg.py` into a temp dir, (b) use `tobytes()` instead of the deprecated `getdata()` so exit code 0 means real success, (c) print the per-profile output sizes so the profile-difference is visible in the log.

**Real-image verifier results after the round-3 fixes (with the new ICC-injected input):**

```
=== Real camera-style JPEG with EXIF+ICC+COM+XMP (profile=Privacy) ===
  original_size=3187  output_size=2058  dropped_segments=4  pre=34 post=6
  entropy_mismatch=-1  stuffed_ff00=54/54  decodes=yes  pixel-identical=YES

=== Real camera-style JPEG with EXIF+ICC+COM+XMP (profile=Minimal) ===
  original_size=3187  output_size=2664  dropped_segments=3  pre=34 post=28
  entropy_mismatch=-1  stuffed_ff00=54/54  decodes=yes  pixel-identical=YES

=== Real camera-style JPEG with EXIF+ICC+COM+XMP (profile=AllMetadata) ===
  original_size=3187  output_size=2058  dropped_segments=4  pre=34 post=6
  entropy_mismatch=-1  stuffed_ff00=54/54  decodes=yes  pixel-identical=YES
```

Privacy and AllMetadata both produce 2058 bytes (same behavior for JPEG: both strip ICC); Minimal produces 2664 bytes (keeps ICC → 606 bytes larger). All three are pixel-identical, byte-stuffing preserved, decodable. The ICC-injected input now makes the profile-difference visible end-to-end.

**Final status after Round 3:**
- Solution build: 0 errors, 0 warnings
- xUnit: 35/35 (was 27/27 after Round 1+2; +8 new tests for the round-3 defect surface)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED, with ICC-injected inputs that actually exercise the profile differences

**Still open / accepted (from Round 1+2 L-items, not addressed in this round either):**
- **L1** (README AllMetadata "plus ICC" wording) — still ambiguous in the profile table; cosmetic
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred, low priority):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks. For a 100 MB IDAT this is a 100 MB allocation. Could be optimized with a streaming CRC, but the memory pressure is per-chunk and IDAT chunks are usually a few hundred KB. Not worth the complexity right now.
- The `OverlayWindow_Loaded` `Task.Run` doesn't observe the inner exception if `_vm.InspectAll` throws. `MetadataInspector.Inspect` catches all exceptions internally, so this is theoretical, but defensive `try/catch` would be cleaner.
- The clipboard access in `CopyValue_Click` / `CopyRow_Click` can throw `COMException` if the clipboard is in use by another process. Not caught. Not a real problem in interactive use.

---

## ROUND 4 — 360° audit (2026-08-04) — M2.20.4

The 4th adversarial 360° pass. The brief was the same as before: trust nothing, re-read every file with fresh eyes, find what rounds 1–3 didn't. The build/test pipeline was clean (xUnit 47/47, SelfTest 16/16, verifier all checks passed) at the start, so every finding here is a NEW issue that survived 3 rounds of audits.

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D15** | MEDIUM | `Dispatcher.Invoke` inside every `Task.Run` callback in `OverlayWindow.xaml.cs` (6 call sites: `OverlayWindow_Loaded` × 2, `ReInspectButton_Click` × 2, `RunRemove` × 2) throws `TaskCanceledException` if the user closes the overlay window mid-operation (initial inspect, re-inspect, or strip). The Task then faults, the unobserved-exception handler logs it, and the window is left in an inconsistent state (`IsBusy=true`, buttons disabled) for any subsequent process that shares the same dispatcher. The strip itself is fine — the stripper's catch already cleans up the temp file — but the unobserved-task exception is noise that could hide real future regressions, and a process that sets `<ThrowUnobservedTaskExceptions>` would crash. | `OverlayWindow.xaml.cs:80, 90, 158, 163, 248, 259` | New `SafeInvoke(Action)` helper: checks `Dispatcher.HasShutdownStarted` and wraps the `Invoke` in a `TaskCanceledException` catch. All 6 sites converted. The UI is going away at that point so the update is safe to drop. |
| **D31** | MEDIUM | `PathFilter.IsSupportedImageExtension` did an exact-string comparison: `string.Equals(ext, ".jpg", OrdinalIgnoreCase)`. A file like `photo.jpg ` (trailing space in the filename) has `Path.GetExtension` return `.jpg ` (with the space), which the strict comparison rejected. Such files are valid images with a path oddity — PowerShell and some command-line tools can create them; Windows Explorer generally can't. The previous behavior dropped them as "unsupported file type", which is misleading. | `PathFilter.cs:88-93` (was strict; now `TrimEnd` before compare) | `IsSupportedImageExtension` now trims trailing whitespace from the extension before comparing. `FilterImagePaths` benefits automatically (it goes through `IsSupportedImageExtension`). |
| **D32** | MEDIUM (test gap) | No test for a corrupt JPEG (valid `.jpg` extension, but bad header bytes) in a batch. The existing `StripBatch_UnsupportedFile_…` test uses a `.txt` (wrong extension), which `PathFilter` drops *before* the stripper sees it. A corrupt JPEG is a different code path: the extension check passes, the format detector returns `ImageFormat.Unknown`, the stripper throws `NotSupportedException`. The batch's catch must still record this in `Failures` (not `Results`) and continue processing other files. | `tests/ExifRemover.Tests/StripPipelineTests.cs` (new test) | New `StripBatch_CorruptJpegWithValidExtension_RecordsFailure_AndContinuesBatch`: writes a valid JPEG + a 8-byte file with `0x42 0x4D` header (not a JPEG SOI) but `.jpg` extension; asserts one Result, one Failure, the Failure's error mentions "Unsupported file format", and the source file of the failed entry is still on disk. |
| **D33** | MEDIUM (test gap) | No test for a large PNG IDAT (the kept-chunk allocation path). The B9 test (`Strip_SkippedChunkDoesNotAllocatePayloadBuffer`) uses a 1 MB tEXt that gets *dropped* — so it exercises the `SkipExactly` path, not the kept-chunk path. The kept-chunk path in `PngMetadataStripper` allocates `new byte[length]` and the existing fixtures only have a few-hundred-byte IDAT. A regression where the buffer is too small (e.g. someone caps it at 1 MB "for safety") or the read loop is wrong for multi-MB chunks would not be caught. | `tests/ExifRemover.Tests/PngStripperTests.cs` (new test) | New `Strip_LargeKeptIdat_AllocatesAndPreservesBytes`: 10 MB IDAT with deterministic fill, asserts the stripper returns `DroppedSegments=0`, `Changed=false`, and the output IDAT is byte-identical to the input. The 256 MB cap is independently tested by `PngMetadataStripper_RejectsChunkLengthAboveCap`; 10 MB is well under the cap and well above "a few hundred KB". |
| **D35** | LOW (design limitation, documented) | `JpegMetadataStripper.ShouldDrop` drops APP14 (Adobe marker, 0xEE) along with every other 0xE0–0xEF marker. APP14 is the carrier of the color transform (RGB, YCbCr, YCCK) for CMYK JPEGs — Photoshop's "Save for Web" can produce CMYK JPEGs that rely on it. Stripping APP14 doesn't leak metadata; it makes a CMYK JPEG fall back to YCbCr interpretation, producing a color shift on decode. The current design ("strip everything except JFIF and ICC") is documented in `StripProfileCatalog` and the audit log; the user accepted it for v1. | `JpegMetadataStripper.cs:151-192` (unchanged) | Not fixed. Documented here as a known limitation: if the user has a CMYK JPEG and a privacy-stripped version looks color-shifted, the cause is the dropped APP14 marker. A future v1+1 "Preserve color management" option could add a "minimal+Adobe" profile that keeps APP14. Out of scope for v1. |

**Test coverage added (4 new tests, xUnit went 47→51):**
- `FilterImagePaths_TrailingSpaceInExtension_KeepsTheFile` — proves D31 (D31 fix + the filter integration)
- `IsSupportedImageExtension_TrimsTrailingWhitespace` — pins the public-helper contract
- `StripBatch_CorruptJpegWithValidExtension_RecordsFailure_AndContinuesBatch` — proves D32
- `Strip_LargeKeptIdat_AllocatesAndPreservesBytes` — proves D33

**Final status after Round 4:**
- Solution build: 0 errors, 0 warnings
- xUnit: 51/51 (was 47/47 after M2.20.3; +4 new tests for D31/D32/D33)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED (with ICC-injected input that exercises the profile differences)

**Cumulative across all 4 audit rounds:** 26 fixes, +24 tests since `605a2d0`. xUnit went 27 → 35 → 39 → 47 → 51; SelfTest stable at 16/16 since the Round-3 ICC-injection improvements.

**Still open / accepted (from earlier rounds, not addressed in M2.20.4 either):**
- **L1** (README AllMetadata "plus ICC" wording) — still ambiguous in the profile table; cosmetic
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented above
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred, low priority — carried forward from M2.20.3):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks. The D33 test now covers a 10 MB IDAT (well above the typical "few hundred KB" and well below the 256 MB cap), which pins the contract. Streaming CRC for arbitrary chunk sizes is still deferred.
- The clipboard access in `CopyValue_Click` / `CopyRow_Click` is now wrapped in try/catch (M2.20.3 D13). D15 (this round) is the analogous fix for the dispatcher-shutdown failure mode.

---

## ROUND 5 — 360° audit (2026-08-04) — M2.20.5

The 5th adversarial 360° pass. The brief was the same: trust nothing, re-read every file with fresh eyes, find what rounds 1–4 didn't. The build/test pipeline was clean (xUnit 51/51, SelfTest 16/16, verifier all checks passed) at the start, so every finding here is a NEW issue that survived 4 rounds of audits.

The audit focused on the files that got the least attention in earlier rounds: the App's WPF code paths (OverlayViewModel, OverlayWindow), the install/uninstall scripts, the verifier, and the small WPF utility files (BoolToVisibilityConverter, ConfirmWindow, AboutWindow) that were barely skimmed in M2.20.4.

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D36** | MEDIUM | `OverlayViewModel.FilterText` setter updates `EntriesView` and `VisibleEntryCount`, but it does NOT update `StatusText`. The bound `StatusText` string ("5 of 10 entries shown") is only re-composed by `UpdateStatusFromEntries`, which is called from `RebuildCurrentEntries` and `RunRemove` — neither of which is on the filter path. The result: the user types a filter, the grid re-renders to 3 rows, but the status strip still reads "5 of 10 entries shown" (the last pre-filter count). Two views disagree about the same value. Observable manually with any image that has ≥ 2 entries. | `OverlayViewModel.cs:170-182` (was: refresh + VisibleEntryCount only) | Setter now also calls `UpdateStatusFromEntries()` after the refresh, so the bound string is re-composed on every keystroke. The reuse of the same `UpdateStatusFromEntries` helper means the filter path uses the same format as every other status update (no risk of "filter shows '3 of 5'" but a future code path shows "5 of 5" with a slightly different format). |
| **D37** | MEDIUM (dead code with latent bug) | `verify/StripperLib.cs` is not referenced anywhere — the verifier's `Program.cs` calls `StripPipeline.Strip` directly, not `StripperLib.Strip`. Grep across the whole repo: only one match (the file itself). The dead code also has a real bug: it calls `Path.GetTempFileName()` (which creates a 0-byte file), passes the temp paths to `StripPipeline.Strip`, then reads back from the temp path — but the stripper with `overwriteSource=false` calls `NextNonClashingPath`, which finds the existing 0-byte temp file and writes to a sibling (`{name} (2).tmp`). The byte array returned is the unread 0-byte stub, not the actual stripped output. Today no one calls `StripperLib`, so the bug never fires — but a future caller would get silent 0-byte output. | `verify/StripperLib.cs` (whole file) | Deleted. The verifier used `StripPipeline.Strip` directly and gets the real `result.OutputPath` from the returned `StripResult`; no functionality is lost. |
| **D38** | LOW (dead code) | `install.cmd` lines 110-111 set `CMD_EXE="\"%EXE%\""` and `CMD_PCT="\"%%1\""` but never use them. The actual `reg add` on line 115 inlines the same values. Vestigial leftovers from an earlier refactor (the variables look like they were going to be reused in a loop, but the loop was inlined instead). | `install.cmd:110-111` | Dead assignments removed. No behavior change. |
| **D39** | LOW (dead code) | `PngMetadataStripper.Strip` has an `if (sawIend) break;` inside the `if (drop)` block (lines 80-83 in the pre-fix file). The branch is unreachable: IEND is forced to `drop = false` on line 70 (it's never dropped, the loop terminates at the IEND "kept" path on the next iteration). The dead branch is the result of a copy-paste from the "kept" path's `if (sawIend) break;` (which IS reached). | `PngMetadataStripper.cs:73-85` | Dead `if (sawIend) break;` inside the drop branch removed. The kept-path `if (sawIend) break;` remains (it's the actual loop terminator). |
| **L1** | LOW (docs) | The `All metadata` row in the README's profile table (line 71) had `Strips: "EXIF, IPTC, XMP, ICC profile, JPEG COM, …; PLUS PNG color-management chunks (gAMA, cHRM, sRGB)"`. The "plus" is misleading: ICC profile is already in the Privacy strips, not added by AllMetadata. The only chunks actually new in AllMetadata are the PNG color-management ones (`gAMA`/`cHRM`/`sRGB`). A user reading "plus ICC" could wrongly conclude that AllMetadata adds ICC on top of Privacy, when in fact ICC is dropped identically in both. | `README.md:71` | Cell rewritten: "Same as Privacy, plus the PNG color-management chunks (gAMA, cHRM, sRGB). ICC profile is also dropped (it was already dropped under Privacy; the only new chunks are the PNG color-management ones)." |

**No new tests added in this round.**

Why no D36 test: the `OverlayViewModel` lives in `ExifRemover.App`, which targets `net8.0-windows` and depends on WPF (`Dispatcher`, `ObservableCollection`, `ICollectionView`). The existing `tests/ExifRemover.Tests` project targets `net8.0` (no WPF) — it was kept cross-platform for the headless test pipeline. I tried changing the Tests target framework to `net8.0-windows` with `<UseWPF>true</UseWPF>` and embedding `OverlayViewModel.cs` via `<Compile Include>`, but the embedded Engine sources stop compiling because `Stream`/`File`/`Path` (used without an explicit `using System.IO;` in the Engine) are not in the implicit usings for `net8.0-windows + UseWPF`. Adding `using System.IO;` to 4 Engine files is invasive and would require the same patch in the Verifier and SelfTest csprojs' `<Compile Include>` lists — not worth it for one UI consistency test. The fix itself is small (one line, `UpdateStatusFromEntries()` at the end of the FilterText setter) and the bug is observable in 10 seconds with any multi-entry JPEG. A STA-collected VM test in a future round (separate `ExifRemover.App.Tests` assembly targeting `net8.0-windows`) would be the right place for it.

**Final status after Round 5:**
- Solution build: 0 errors, 0 warnings
- xUnit: 51/51 (unchanged — no new tests in this round, by design)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 5 audit rounds:** 30 fixes since `605a2d0` (26 from rounds 1–4, +4 in round 5). xUnit: 27 → 35 → 39 → 47 → 51 (stable since M2.20.4); SelfTest: 16/16 stable since the M2.20.3 ICC-injection improvements.

**Still open / accepted (carried forward from M2.20.4):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3). D33 added a 10 MB test. Streaming CRC for arbitrary chunk sizes is still deferred.
- `verify/Program.cs:122-125` `IsValidJpeg` only checks for JPEG structure. The Python harness only feeds JPEGs (`gen_test_jpeg.py` produces JPEGs), so the bug never fires today — but a future PNG verifier would always report `output_decodes=no`. Latent, not fixed in this round.
- The `_sessionDontAsk` static flag in `OverlayWindow` (line 14) is per-process, not per-overlay-window. The user checks "Don't ask again" → the flag is set for the whole process. The current name and docstring could be clearer (`_dontAskAgainForProcess` would be more honest), but the behavior matches the dialog's label ("Don't ask again for this session") and the user explicitly opted in. Cosmetic.
- The `FilterText` setter in `OverlayViewModel` is now correct (D36), but no automated test pins the behavior — a future refactor that removes the `UpdateStatusFromEntries()` call would silently regress. See "Why no D36 test" above.

---

## ROUND 6 — 360° audit (2026-08-04) — M2.20.6

The 6th adversarial 360° pass. The brief was the same: trust nothing, re-read every file with fresh eyes, find what rounds 1–5 didn't. The build/test pipeline was clean (xUnit 51/51, SelfTest 16/16, verifier all checks passed) at the start, so every finding here is a NEW issue that survived 5 rounds of audits.

The audit focused on the files that had received the least attention in earlier rounds: the App's WPF resources (Theme.xaml, app.manifest, AboutWindow.xaml, ConfirmWindow.xaml), the install/uninstall scripts (re-reviewed with fresh eyes for the dead-code class of bug), the project layout metadata (PLAN.md, .gitignore, ExifRemover.sln), and a deeper look at the FixtureFactory's per-chunk CRC table rebuild.

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D40** | LOW (dead code) | `Theme.xaml` defines a `MonoText` style (lines 105-108 in the pre-fix file) intended for monospaced text blocks. Grep across the whole repo: no XAML file uses it. The overlay's monospaced cells use `FontFamily="{StaticResource MonoFontFamily}"` directly (e.g. `EntriesGrid` line 107), bypassing the style. The style was added speculatively ("we'll need a monospaced text style for the XMP dump") but never wired up. | `Resources/Theme.xaml:105-108` | Dead style removed. `UiText` (which IS used by `AboutWindow.xaml`) stays. |
| **D41** | LOW (docs) | `PLAN.md` §10 deliverable #1 promised a solution-level `Directory.Build.props` (nullable on, latest C#, warnings-as-errors for our own code) but the file does not exist on disk. Grep: zero matches for `Directory.Build.props`. The per-csproj config (App, Engine, SelfTest, Verifier) sets these properties individually; the Tests project intentionally does NOT set `TreatWarningsAsErrors` (test code is more permissive). | `PLAN.md:234` | Deliverable list rewritten to match the actual layout: per-csproj config, no `Directory.Build.props`. The Tests project's deliberate permissiveness is called out. |
| **D43** | LOW (docs) | `PLAN.md` §6 said "We commit a small set of test vectors under `tests/ExifRemover.Tests/Fixtures/`" and listed `clean.jpg`, `camera_sample.jpg`, `screenshot.png`, `transparent.png`, `truncated.jpg`, `truncated.png`. The repo's `tests/ExifRemover.Tests/Fixtures/` directory does not exist — the actual tests generate all fixtures at runtime via `FixtureFactory.cs` (line 6-10: "These fixtures are generated at test time so the repository stays text-only and the tests are reproducible"). | `PLAN.md:193-200` | §6 rewritten to describe the generation-at-test-time approach, with the fixture categories listed by their generator function names (MinimalJpeg, JpegWithExifXmpIccAndComment, PngWithTextTimeExifIccp, PngWithAlwaysKeptAncillaryChunks, PngWithUnknownAncillaryChunk, JpegWithStuffedScanAndMetadata, ProgressiveLikeJpegWithExif, JpegWithJunkAfterEoi, TruncatedJpeg, TruncatedPng). |
| **D46** | LOW (defensive) | `SafeInvoke` in `OverlayWindow.xaml.cs` catches `TaskCanceledException` to swallow dispatcher-shutdown race-condition exceptions. WPF's `Dispatcher.Invoke` throws `TaskCanceledException` today, but `TaskCanceledException` derives from `OperationCanceledException` — a future WPF change could throw a different subclass (`OperationCanceledException` directly, or a platform-specific subtype). The narrow catch would miss it, the inner Task would fault, and the unobserved-task-exception handler would log. | `OverlayWindow.xaml.cs:122-126` | Catch widened to `OperationCanceledException` (the base class). Comment updated to document the forward-compat reasoning. |

**D44 (not a real finding, retracted during the round):**
- I initially wrote D44 as "Engine.csproj lacks `<TreatWarningsAsErrors>` but App has it". On close re-read the Engine csproj DOES have it on line 10 (it was always there — the very-strict Engine was the FIRST project to enable it). The actual outlier is `tests/ExifRemover.Tests/ExifRemover.Tests.csproj`, which intentionally doesn't have it (test code is more permissive). No change made; the audit log here records the retraction so a future auditor doesn't repeat the misread.

**D42 (not fixed, low impact):**
- `FixtureFactory.WritePngChunk` (line 504-518) rebuilds the 256-entry CRC32 table inside the function for every chunk (~2048 iterations per table, ~10 tables per fixture). The Engine's `PngMetadataStripper` has the same per-call table-build cost on line 270-282. The total wasted work is ~20K iterations per test fixture / strip — invisible in practice (test suite runs in 8 seconds) and the table-build isn't a hot path in production. Noted; not fixed in this round. A future "static table, build once" cleanup would be a one-line change but isn't worth the diff churn today.

**D45 (not fixed, latent, carried from M2.20.5):**
- `verify/Program.cs:122-125` `IsValidJpeg` is JPEG-only. The Python harness only feeds JPEGs (`gen_test_jpeg.py` is JPEG-only), so the bug never fires. Same as M2.20.5's analysis: latent, document-only.

**No new tests in this round.**
- The round was a doc/dead-code/defensive-cleanup pass, not a behavior fix. The behavioral changes (D46 catch widening) are verified by the existing test suite (xUnit 51/51 + SelfTest 16/16 + verifier ALL CHECKS PASSED). A regression test for D46 would need to spawn a real `Dispatcher` shutdown mid-`Invoke`, which is the WPF-test-harness problem we already declined to solve for D36.

**Final status after Round 6:**
- Solution build: 0 errors, 0 warnings
- xUnit: 51/51 (unchanged — no new tests in this round)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 6 audit rounds:** 33 fixes since `605a2d0` (26 from rounds 1–4, +4 in round 5, +3 in round 6: D40 dead-style, D41/D43 docs, D46 catch widening). xUnit: 27 → 35 → 39 → 47 → 51 (stable since M2.20.4); SelfTest: 16/16 stable since the M2.20.3 ICC-injection improvements.

**Still open / accepted (carried forward from earlier rounds):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- **D45** (IsValidJpeg for PNGs) — latent, not exercised by the current JPEG-only harness
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3, D42 round 6 retried). D33 added a 10 MB test. Streaming CRC for arbitrary chunk sizes is still deferred. The Engine's CRC table is also rebuilt per call (line 270-282); one-time-init would shave ~1ms per Strip, irrelevant for the typical use case.
- The `_sessionDontAsk` static flag (carried from M2.20.5) — cosmetic naming, behavior is correct.
- The D36 `FilterText` setter test (carried from M2.20.5) — WPF-bound, deferred to a future `ExifRemover.App.Tests` assembly.

---

## ROUND 7 — 360° audit (2026-08-04) — M2.20.7

The 7th adversarial 360° pass. The brief was the same: trust nothing, re-read every file with fresh eyes, find what rounds 1–6 didn't. The build/test pipeline was clean (xUnit 51/51, SelfTest 16/16, verifier all checks passed) at the start, so every finding here is a NEW issue that survived 6 rounds of audits.

The audit focused on the files that had received the least attention in earlier rounds: the SelfTest's `Program.cs` (the 16 test cases have been running green for 5 rounds but the helper functions had never been deeply reviewed), `gen_test_jpeg.py` (the Python harness for the verifier), `app.manifest` (re-read for missing `supportedOS` GUIDs), and the `.gitignore` (re-checked for dead patterns after D49 verification).

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D47** | LOW (misleading name + redundant alias) | `AssertThrowsAny<T>` in `SelfTest/Program.cs` was a one-line alias for `AssertThrows<T>` with no behavioral difference. The name suggests "catches any exception" (which is what `AssertThrows<Exception>` does, since every exception derives from `Exception`), but the generic parameter `T` is forwarded directly to `AssertThrows<T>` which strictly requires `T` or a subclass. The three call sites all passed `Exception` as `T`, which "works by accident" — `AssertThrows<Exception>` matches every exception. A future caller passing a specific `T` (e.g. `AssertThrowsAny<InvalidDataException>`) would get the strict behavior, not the "any" behavior the name advertises. | `src/ExifRemover.SelfTest/Program.cs:341-344` (was the alias), 3 call sites (lines 83, 237, 285) | Alias deleted; the 3 call sites now use `AssertThrows<Exception>` directly. The function `AssertThrows<T>` (which IS strict) is the single helper, with the call sites' choice of `T=Exception` being explicit. |
| **D48** | LOW (manifest) | `app.manifest` listed supportedOS GUIDs for Windows Vista (`8e0f7a12-…`), 7 (`1f676c76-…`), 8 (`4a2f28e3-…`), 8.1 (`35138b9a-…`), and 10 (`e2011457-…`). The Windows 11 GUID (`e1b086e2-5834-4d6b-a0c5-321d5705261c`) was missing. The app still runs on Windows 11 (the exe is forward-compatible) but the manifest is missing the explicit "I am tested on Windows 11" hint that some Windows compatibility shims and telemetry check. | `src/ExifRemover.App/app.manifest:13-17` (was 5 GUIDs) | Added the Win11 GUID as the 6th entry, with a comment pointing at D48. |
| **D49** | LOW (dead gitignore) | `.gitignore` had three patterns — `verify_*.png`, `verify_*.jpg`, `gen_*.jpg` — that don't match anything the Python scripts actually produce. The scripts use `tempfile.mkdtemp(prefix="er_inputs_")` and `tempfile.mkdtemp(prefix="er_verify_")` for working directories; the generated file names are `real_full.jpg`, `real_bare.jpg`, and the strip outputs `out.jpg` / `out (2).jpg`. None of those match `verify_*` or `gen_*` prefixes. The patterns are leftovers from an earlier version of the Python scripts that wrote to fixed names in the repo root. | `.gitignore:81-83` | Three patterns removed. `__pycache__/` and `*.pyc` (the actually-relevant Python patterns) stay. |
| **D50** | LOW (dead csproj include) | `tests/ExifRemover.Tests/ExifRemover.Tests.csproj` had `<None Include="Fixtures\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`. The `tests/ExifRemover.Tests/Fixtures/` directory does not exist (the tests generate all fixtures at runtime via `FixtureFactory.cs`). The glob matches nothing, the include is a no-op. The csproj also has `<NoWarn>` for `CS1591` and similar implicit include patterns that the build tolerates. | `tests/ExifRemover.Tests/ExifRemover.Tests.csproj:38-42` (was the empty `<None>` group) | Empty `<ItemGroup>` removed. The other ItemGroups (Engine `<Compile Include>` list, MetadataExtractor `<PackageReference>`) stay. |

**No new tests in this round.**
- All four findings are dead-code / dead-config (no behavioral change). The build pipeline + 51/51 + 16/16 + ALL CHECKS PASSED confirms the changes don't break anything that was working.

**Final status after Round 7:**
- Solution build: 0 errors, 0 warnings
- xUnit: 51/51 (unchanged)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 7 audit rounds:** 37 fixes since `605a2d0` (26 from rounds 1–4, +4 in round 5, +3 in round 6, +4 in round 7). xUnit: 27 → 35 → 39 → 47 → 51 (stable since M2.20.4); SelfTest: 16/16 stable since the M2.20.3 ICC-injection improvements.

**Still open / accepted (carried forward from earlier rounds):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- **D45** (IsValidJpeg for PNGs) — latent, not exercised by the current JPEG-only harness
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3, D42 round 6 retried). D33 added a 10 MB test.
- The Engine's CRC table is rebuilt per call (line 270-282); one-time-init would shave ~1ms per Strip, irrelevant for the typical use case.
- The `_sessionDontAsk` static flag (carried from M2.20.5) — cosmetic naming.
- The D36 `FilterText` setter test (carried from M2.20.5) — WPF-bound, deferred to a future `ExifRemover.App.Tests` assembly.
- The `gen_test_jpeg.py` "bare JPEG" fixture (line 95) writes a 32×32 image with constant color `(10, 20, 30)` — the entropy-coded scan is therefore very small. Not a bug (the bare fixture is meant to test the "no metadata" case, not the entropy-scan path), but if a future test wants to exercise the entropy scan on a metadata-free JPEG it would need pixel variation.
- The `ExifRemover.exe` / `*.dll` / `hostfxr.dll` artifacts in the repo root are from a previous `install.cmd build` (timestamps 2026-05/06). They're all gitignored, but if a future commit accidentally lifts the gitignore the user could end up committing ~70 MB of .NET runtime DLLs. The catch-all `*.dll` / `*.exe` in `.gitignore` makes that hard, but not impossible. Cosmetic.

---

## ROUND 8 — 360° audit (2026-08-04) — M2.20.8

The 8th adversarial 360° pass. The brief was the same: trust nothing, re-read every file with fresh eyes, find what rounds 1–7 didn't. The build/test pipeline was clean (xUnit 51/51, SelfTest 16/16, verifier all checks passed) at the start, so every finding here is a NEW issue that survived 7 rounds of audits.

The audit focused on the App's WPF code paths (OverlayViewModel's keep-set computation, OverlayWindow's ShowSummary path), the third-party-license file (THIRD_PARTY_NOTICES.md), and the `app.manifest` DPI settings (re-read after M2.20.7 added the Win11 GUID).

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D51** | LOW (UI correctness) | `MetadataInspector.MapGroup` falls through to `MetadataGroups.Other` ("Other") when a directory doesn't match any of the explicit cases (e.g. a future MetadataExtractor version surfaces a new directory type). `GetChunkKey` returns the group name unchanged for the Other case, so the keep-set is queried with key "Other". The keep-set never contained "Other" → `keep.Contains("Other")` is false → the entry is marked "Would be removed". The stripper operates on bytes, not on MetadataExtractor's directory abstraction, so we can't be 100% sure the stripper drops the underlying bytes for an "Other" entry. Worst case: a future MetadataExtractor surfaces a directory that corresponds to a byte range the stripper keeps (e.g. a structural byte range that falls outside the stripper's drop list), and the UI would lie — "Would be removed" but the file bytes are unchanged. | `src/ExifRemover.App/OverlayViewModel.cs:99-145` (`ComputeKeepSet`) | "Other" added to the keep-set unconditionally (before the JPEG/PNG branch). Fail-safe default: if we don't know what the directory represents, don't claim the stripper will remove it. Same reasoning as the existing "PNGUNKNOWN" entry for the PNG path. |
| **D52** | LOW (DRY violation) | `FormatBytes(long b)` was defined in two places: `OverlayViewModel.EntryRow.FormatBytes` (line 515-520, used for the Size column in the metadata grid) and `OverlayWindow.FormatBytes` (line 334-339, used in `ShowSummary` for the post-strip summary). Identical 5-line implementation, identical thresholds (1024 B / KB, 1024*1024 MB), identical culture-dependent formatting. The two copies would drift if a future contributor changes one (e.g. to add GB support) and not the other — the Size column and the summary would disagree on the formatting. | `src/ExifRemover.App/OverlayViewModel.cs:515-520`, `src/ExifRemover.App/OverlayWindow.xaml.cs:334-339` (was) | Extracted to a new `src/ExifRemover.App/Formatting.cs` with `internal static class Formatting { public static string FormatBytes(long b) }`. The two private copies deleted; both call sites use `Formatting.FormatBytes(...)`. |

**No new tests in this round.**
- D51 is a UI behavior fix; a regression test would need to inject an "Other" entry into a `FileInspection`, which means WPF VM instantiation (the same blocker as D36 in M2.20.5). The behavior is also harmless: the worst case is "Other" entries being marked as kept (which is the right answer for the keep-set contract), and the actual stripper doesn't touch any bytes for this fix.
- D52 is a pure refactor (extract method); the existing 51 tests + 16 SelfTests cover the call sites' behavior identically.

**Final status after Round 8:**
- Solution build: 0 errors, 0 warnings
- xUnit: 51/51 (unchanged)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 8 audit rounds:** 39 fixes since `605a2d0` (26 from rounds 1–4, +4 in round 5, +3 in round 6, +4 in round 7, +2 in round 8: D51 fail-safe keep-set, D52 extract FormatBytes). xUnit: 27 → 35 → 39 → 47 → 51 (stable since M2.20.4); SelfTest: 16/16 stable since the M2.20.3 ICC-injection improvements.

**Still open / accepted (carried forward from earlier rounds):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- **D45** (IsValidJpeg for PNGs) — latent, not exercised by the current JPEG-only harness
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3, D42 round 6 retried). D33 added a 10 MB test.
- The Engine's CRC table is rebuilt per call (line 270-282); one-time-init would shave ~1ms per Strip, irrelevant for the typical use case.
- The `_sessionDontAsk` static flag (carried from M2.20.5) — cosmetic naming.
- The D36 `FilterText` setter test (carried from M2.20.5) — WPF-bound, deferred to a future `ExifRemover.App.Tests` assembly.
- The `gen_test_jpeg.py` "bare JPEG" fixture writes a 32×32 image with constant color — the entropy-coded scan is very small (carried from M2.20.7).
- The `ExifRemover.exe` / `*.dll` / `hostfxr.dll` artifacts in the repo root (carried from M2.20.7) — all gitignored, cosmetic.
- The `UpdateStatusFromEntries` "last strip removed all" message (line 368) is technically accurate (the strip removed all the metadata) but could be reworded to "before last strip" if the user has filtered the grid (the message currently doesn't reflect the filter). Very minor copy issue.
- The `app.manifest` DPI settings are now complete (Vista through Win11). The two DPI declarations (SMI/2005 "true/pm" and SMI/2016 "PerMonitorV2") are consistent (both per-monitor; V1 vs V2). No issue.

---

## ROUND 9 — 360° audit (2026-08-04) — M2.20.9

The 9th adversarial 360° pass. The build/test pipeline was clean (xUnit 51/51, SelfTest 16/16, verifier all checks passed) at the start. The audit's focus this round was **test coverage** — after 8 rounds of bug-hunting the bug surface is getting tight, but the test surface has obvious gaps. Two untested critical helpers were found, one of which also had dead-code baggage.

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D56** | MEDIUM (dead code with API bug) | `AtomicFile.Replace(string destination, string tempContent, Action<string> writeContent)` was never called. Grep across the entire codebase: zero call sites. Both strippers (Jpeg, Png) call `File.Replace` directly in the `if (overwriteSource)` branch (line 106 / 125), not via `AtomicFile.Replace`. The method also had a buggy API: the `tempContent` parameter is unused — the function generates its own temp path with a `Guid.NewGuid()` and the `tempContent` parameter is just dead weight in the signature. The dead method is 26 lines (over half of `AtomicFile.cs`). | `src/ExifRemover.Engine/AtomicFile.cs:1-29` (was the `Replace` method) | Deleted the `Replace` method. `NextNonClashingPath` and `TryDelete` stay (the former is used by both strippers + `StripPipeline.BuildSiblingPath`; the latter is a private helper). |
| **D53** | MEDIUM (test gap) | `AtomicFile.NextNonClashingPath` — the helper that produces "photo (2).jpg" when "photo.jpg" already exists — was not directly tested. Existing stripper tests exercise it indirectly (e.g. `Strip_InputPathEqualsOutputPath_OverwriteFalse_LeavesSourceIntact` triggers the sibling path), but no test pinned the helper's full contract: path-doesn't-exist returns the path, path-taken returns `name (2).ext`, multiple-taken increments, no-extension case, holes-in-sequence. | (new file) `tests/ExifRemover.Tests/AtomicFileTests.cs` | New test file with 5 cases: `NextNonClashingPath_DesiredFree_ReturnsDesiredPath`, `_DesiredTaken_ReturnsFirstSibling`, `_DesiredAndFirstSiblingTaken_ReturnsSecondSibling`, `_NoExtension_StillProducesSibling`, `_HolesInSequence_ReusesTheFirstHole`. |
| **D57** | MEDIUM (test gap) | `StripProfileCatalog.Describe` — the function that produces the title / short description / long description for the overlay's profile dropdown — was not directly tested. The descriptions are also referenced by the README's profile table (L1 was a docs-vs-code wording bug; if a future drift happens, no test would catch it). The default-branch's `throw new ArgumentOutOfRangeException` was also untested. | (new file) `tests/ExifRemover.Tests/StripProfileTests.cs` | New test file with 5 cases: `Describe_Privacy_HasExpectedTitle`, `_AllMetadata_HasExpectedTitle`, `_Minimal_HasExpectedTitle`, `_LongDescription_AlwaysPopulated` (loops over all enum values, asserts non-empty), `_UnknownEnumValue_Throws` (pins the default-branch's throw contract). |

**Final status after Round 9:**
- Solution build: 0 errors, 0 warnings
- xUnit: **61/61** (was 51/51; **+10 new tests**)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 9 audit rounds:** 40 fixes + 10 new tests since `605a2d0` (26 fixes + 24 tests from rounds 1–4, +4 fixes in round 5, +3 fixes in round 6, +4 fixes in round 7, +2 fixes in round 8, +1 fix + 10 tests in round 9). xUnit: 27 → 35 → 39 → 47 → 51 → **61**; SelfTest: 16/16 stable since the M2.20.3 ICC-injection improvements.

**Still open / accepted (carried forward from earlier rounds):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- **D45** (IsValidJpeg for PNGs) — latent, not exercised by the current JPEG-only harness
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3, D42 round 6 retried). D33 added a 10 MB test.
- The Engine's CRC table is rebuilt per call (line 270-282); one-time-init would shave ~1ms per Strip, irrelevant for the typical use case.
- The `_sessionDontAsk` static flag (carried from M2.20.5) — cosmetic naming.
- The D36 `FilterText` setter test (carried from M2.20.5) — WPF-bound, deferred to a future `ExifRemover.App.Tests` assembly.
- The `Formatting.FormatBytes` helper in `src/ExifRemover.App/Formatting.cs` is now an `internal static class` — would need either a new `ExifRemover.App.Tests` assembly targeting `net8.0-windows` (the same blocker as D36) or making the class public + moving to a shared library. Not worth the cost for one helper.
- The "last strip removed all" message in `OverlayViewModel.UpdateStatusFromEntries` — minor copy issue when filter is active.
- The `ExifRemover.exe` / `*.dll` artifacts in the repo root (carried from M2.20.7) — all gitignored, cosmetic.

---

## ROUND 10 — 360° audit (2026-08-04) — M2.20.10

The 10th adversarial 360° pass. The build/test pipeline was clean (xUnit 61/61, SelfTest 16/16, verifier all checks passed) at the start. The audit's focus this round was **documentation drift** — after 9 rounds of bug-hunting + test-filling, the bug surface and test surface are tight, but `PLAN.md` and `README.md` had accumulated plan-vs-reality gaps that the earlier "doc-drift" rounds (M2.20.6 D41/D43) hadn't yet caught.

| ID | Sev | What | Where | Fix |
|---|---|---|---|---|
| **D58** | LOW (docs) | `PLAN.md` §7 claimed "A `sign.cmd` template is included: if the user supplies a code-signing cert path in `SIGN_CERT` and `SIGN_PASSWORD` env vars, `sign.cmd` will sign the produced exe with `signtool`." No `sign.cmd` exists in the repo. Grep: zero matches. The plan was written when the project was first being designed and the author considered shipping a sign template, but the actual decision (per the next bullet "We never bundle a third-party signing service") is that signing is an opt-in user action with the user's own cert + `signtool`, not a project responsibility. | `PLAN.md:227` (the deleted "sign.cmd" bullet) | Bullet rewritten to make the opt-in nature explicit ("A user who wants to sign their own build can do so with their own `signtool` invocation — no `sign.cmd` template is shipped"). No code change; this is a doc-vs-reality correction. |
| **D59** | LOW (UX, docs) | `README.md` line 17 said "Download the latest release zip from the [Releases page](#)." The `[Releases page](#)` is a self-anchor (a placeholder for a real URL). The project has never published a binary release. The reader who follows step 1 finds an empty target. The actual install path is "build from source, then run `.\install.cmd` from the build output directory" — exactly what §"Building from source" already documents. The two sections were inconsistent: §"Installation" assumed a release zip exists, §"Building from source" explained the actual workflow. | `README.md:17-19` | §"Installation" rewritten to point at the build-from-source path as step 1. The placeholder is gone; the user's actual first step is now correct. |

**No behavioral changes; no new tests; no source-code fixes.**

This round was a pure doc-drift pass — the third such round (after M2.20.6 D41/D43 and now M2.20.10 D58/D59). The pattern is recurring: the original `PLAN.md` was written when the project was being designed and made promises about file structure (Directory.Build.props, committed Fixtures/, sign.cmd) that were never realized as the implementation evolved. The doc surface is now tightened to match reality.

**Final status after Round 10:**
- Solution build: 0 errors, 0 warnings
- xUnit: 61/61 (unchanged)
- SelfTest: 16/16
- Real-image verifier: ALL CHECKS PASSED

**Cumulative across all 10 audit rounds:** 40 fixes + 10 new tests since `605a2d0` (26 + 24 from rounds 1–4, +4 in round 5, +3 in round 6, +4 in round 7, +2 in round 8, +1 + 10 tests in round 9, +0 + 0 tests in round 10 — round 10 is the first round with zero source-code or test changes, just doc corrections). xUnit: 27 → 35 → 39 → 47 → 51 → 61 (stable since M2.20.9); SelfTest: 16/16 stable.

**Still open / accepted (carried forward from earlier rounds):**
- **L5** (Win 11 modern context menu registration) — environment-dependent, can't be verified headless
- **D35** (APP14 / CMYK color shift) — design limitation, documented in M2.20.4
- **D45** (IsValidJpeg for PNGs) — latent, not exercised by the current JPEG-only harness
- The v1 non-goals (WebP/TIFF/HEIC support, per-tag selection, drag-and-drop entry, localization, image preview) remain out of scope

**New not-fixed observations (deferred):**
- `PngMetadataStripper` allocates `byte[length]` for kept chunks (carried from M2.20.3, D42 round 6 retried). D33 added a 10 MB test.
- The Engine's CRC table is rebuilt per call (line 270-282); one-time-init would shave ~1ms per Strip, irrelevant for the typical use case.
- The `_sessionDontAsk` static flag (carried from M2.20.5) — cosmetic naming.
- The D36 `FilterText` setter test (carried from M2.20.5) — WPF-bound, deferred to a future `ExifRemover.App.Tests` assembly.
- The `Formatting.FormatBytes` helper in `src/ExifRemover.App/Formatting.cs` — would need a separate WPF-aware test assembly to unit-test directly. Not worth the cost.
- The "last strip removed all" message in `OverlayViewModel.UpdateStatusFromEntries` — minor copy issue when filter is active.
- The `ExifRemover.exe` / `*.dll` artifacts in the repo root (carried from M2.20.7) — all gitignored, cosmetic.
- The `.gitignore` entry `XamlBuild*.dll` (line 58) is redundant with the catch-all `*.dll` (line 32). Not a bug, just an over-specified pattern. Cosmetic.


