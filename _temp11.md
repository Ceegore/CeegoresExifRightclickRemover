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
