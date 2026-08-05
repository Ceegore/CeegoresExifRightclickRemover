using System.IO;
using ExifRemover.Engine;
using MetadataExtractor;
using Xunit;

namespace ExifRemover.Tests;

public class JpegStripperTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private string WriteTemp(byte[] bytes, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Strip_MinimalJpeg_PrivacyProfile_LeavesFileUnchanged()
    {
        var src = WriteTemp(FixtureFactory.MinimalJpeg(), $"er-min-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        var result = JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // A clean JPEG should be a no-op: 0 segments dropped, output byte-identical to input.
        Assert.Equal(0, result.DroppedSegments);
        Assert.False(result.Changed);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outPath));

        // Minimal JPEG only has JFIF APP0, DQT, SOF0, DHT, SOS. MetadataExtractor surfaces them
        // as structural entries; the privacy-relevant groups must be absent.
        var inspection = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(inspection.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        Assert.DoesNotContain(inspection.Entries, e => e.Group == MetadataGroups.Icc);
        Assert.DoesNotContain(inspection.Entries, e => e.Group == MetadataGroups.JpegComment);
    }

    [Fact]
    public void Strip_RemovesExifXmpIccAndComment_UnderPrivacyProfile()
    {
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-meta-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        // Pre-condition: source has EXIF (verified) and COM (verified). MetadataExtractor does not
        // always auto-recognize XMP/IPTC segments in hand-rolled fixtures, so we assert those via
        // the stripper's dropped-segment count instead.
        var preInspect = MetadataInspector.Inspect(src);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.Icc);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.JpegComment);

        var result = JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // Fixture has 5 metadata segments (EXIF, XMP, ICC, IPTC, COM). Stripper drops all of them.
        Assert.True(result.DroppedSegments >= 5, $"Expected to drop >=5 segments, got {result.DroppedSegments}.");
        Assert.True(result.Changed);
        Assert.True(File.Exists(outPath));

        // Post-condition: no EXIF/ICC/COM in the output.
        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.Icc);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.JpegComment);

        // The JFIF header should still be present.
        Assert.Contains(post.Entries, e => e.Group == MetadataGroups.Jfif);
    }

    [Fact]
    public void Strip_KeepsIcc_UnderMinimalProfile()
    {
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-icc-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Minimal);

        var post = MetadataInspector.Inspect(outPath);
        // Minimal: ICC profile is kept
        Assert.Contains(post.Entries, e => e.Group == MetadataGroups.Icc);
        // EXIF/COM are still stripped under Minimal too
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.JpegComment);
    }

    [Fact]
    public void Strip_StripsIcc_UnderAllMetadataProfile()
    {
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-all-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.AllMetadata);

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.Icc);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        // JFIF is still kept
        Assert.Contains(post.Entries, e => e.Group == MetadataGroups.Jfif);
    }

    [Fact]
    public void Strip_InputPathEqualsOutputPath_OverwriteFalse_LeavesSourceIntact()
    {
        // D3 (stripper side): the verifier previously did "touch then clear" on the output
        // file, which would destroy the input if the caller passed the same path for both.
        // The stripper itself, when called with input==output and overwriteSource=false,
        // picks a non-clashing sibling via AtomicFile.NextNonClashingPath and leaves the
        // source intact. This test pins that behavior so a future regression to the
        // stripper (e.g. accidentally deleting the source) would be caught.
        var bytes = FixtureFactory.JpegWithExifXmpIccAndComment();
        var src = WriteTemp(bytes, $"er-self-{Guid.NewGuid():N}.jpg");
        var originalBytes = File.ReadAllBytes(src);

        // input == output path, overwrite=false.
        StripPipeline.Strip(src, src, overwriteSource: false, StripProfile.Privacy);

        // The source must still exist and be unchanged: the stripper wrote to a sibling.
        Assert.True(File.Exists(src));
        Assert.Equal(originalBytes, File.ReadAllBytes(src));
        // And a sibling must exist with the stripped content.
        var sibling = Path.Combine(
            Path.GetDirectoryName(src)!,
            Path.GetFileNameWithoutExtension(src) + " (2)" + Path.GetExtension(src));
        Assert.True(File.Exists(sibling), $"Expected sibling at {sibling}");
    }

    [Fact]
    public void Strip_TrimsJunkAfterEoi_DoesNotCopyTrailingGarbage()
    {
        // D4: a malformed JPEG with garbage bytes appended after the EOI marker
        // must have the garbage trimmed, not silently copied through. Otherwise the
        // output is larger than the user expects and the "lossless" claim is
        // misleading (the extra bytes are the corruption signature, not image data).
        var bytes = FixtureFactory.JpegWithJunkAfterEoi();
        var src = WriteTemp(bytes, $"er-junk-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var outBytes = File.ReadAllBytes(outPath);
        // The output must be strictly smaller than the input (the 100 garbage bytes are gone).
        Assert.True(outBytes.Length < bytes.Length, $"output {outBytes.Length} should be smaller than input {bytes.Length} after trimming trailing junk");
        // The output must still be a structurally-valid JPEG ending in EOI.
        Assert.Equal(0xFF, outBytes[^2]);
        Assert.Equal(0xD9, outBytes[^1]);
        // The output must be no larger than the minimal JPEG fixture (no metadata, so a clean
        // copy of MinimalJpeg is the expected size after junk is trimmed).
        var minimal = FixtureFactory.MinimalJpeg();
        Assert.Equal(minimal.Length, outBytes.Length);
    }

    [Fact]
    public void Strip_TruncatedJpeg_ThrowsAndLeavesOriginalUntouched()
    {
        var bytes = FixtureFactory.TruncatedJpeg();
        var src = WriteTemp(bytes, $"er-trunc-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        Assert.ThrowsAny<Exception>(() => JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy));

        // Source should be byte-identical to its original contents.
        Assert.Equal(bytes, File.ReadAllBytes(src));
        // Output should not exist.
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Strip_JpegTruncatedAfterSos_ThrowsAndLeavesOriginalUntouched()
    {
        // JPEG with SOI + APP0(JFIF) + DQT + SOF0 + DHT + SOS + entropy + (truncated, no EOI).
        // The stripper should detect the missing marker and throw, not silently produce a broken file.
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8); // SOI
        bytes.Add(0xFF); bytes.Add(0xE0); bytes.Add(0x00); bytes.Add(0x10);
        for (int i = 0; i < 14; i++) bytes.Add(0); // JFIF APP0
        bytes.Add(0xFF); bytes.Add(0xDB); bytes.Add(0x00); bytes.Add(0x43);
        bytes.Add(0x00); for (int i = 0; i < 64; i++) bytes.Add(0x01); // DQT
        bytes.Add(0xFF); bytes.Add(0xC0); bytes.Add(0x00); bytes.Add(0x0B);
        bytes.AddRange(new byte[] { 0x08, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x11, 0x00 }); // SOF0
        bytes.Add(0xFF); bytes.Add(0xC4); bytes.Add(0x00); bytes.Add(0x26);
        for (int i = 0; i < 36; i++) bytes.Add(0); // DHT (simplified)
        bytes.Add(0xFF); bytes.Add(0xDA); bytes.Add(0x00); bytes.Add(0x0C);
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        bytes.Add(0x00); bytes.Add(0x3F); // entropy, no EOI

        var src = WriteTemp(bytes.ToArray(), $"er-truncmid-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        Assert.ThrowsAny<Exception>(() => JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy));
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(src));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Strip_App1SegmentTruncatedWithinPayload_ThrowsAndLeavesOriginalUntouched()
    {
        // D65: a malformed JPEG whose APP1 segment-length field claims more bytes of payload
        // than the file actually contains. The pre-fix stripper would silently seek past EOF
        // (the SkipExactly method did not check Position + count > Length on the seekable
        // branch), then the next ReadMarker would throw a confusing "no marker" error. The
        // post-fix stripper fails fast at the skip itself with a clear "during segment skip"
        // message — same defensive pattern as the PNG stripper's SkipExactly.
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8); // SOI
        bytes.Add(0xFF); bytes.Add(0xE1); bytes.Add(0x00); bytes.Add(0x14);
        // APP1 marker with segLen = 20 (so payloadLen = 18), but we only write 4 bytes of
        // payload. The stripper tries to skip 14 more bytes that don't exist.
        for (int i = 0; i < 4; i++) bytes.Add(0);

        var src = WriteTemp(bytes.ToArray(), $"er-truncinseg-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        var ex = Assert.ThrowsAny<Exception>(() => JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy));
        // The error message identifies the failure as a "segment skip" overrun, not a "no
        // marker" condition. This matters for users debugging a bad file — the old
        // "no marker" message pointed at a symptom, not the root cause.
        Assert.Contains("segment skip", ex.Message);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(src));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Strip_OverwriteSource_ReplacesOriginalAtomically()
    {
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-ovr-{Guid.NewGuid():N}.jpg");
        var originalLength = new FileInfo(src).Length;

        var result = JpegMetadataStripper.Strip(src, Path.Combine(Path.GetTempPath(), "ignored.jpg"), overwriteSource: true, StripProfile.Privacy);

        Assert.True(result.OverwroteSource);
        Assert.Equal(src, result.OutputPath);
        Assert.True(File.Exists(src));
        Assert.True(new FileInfo(src).Length <= originalLength, "Overwritten file should not grow.");

        var post = MetadataInspector.Inspect(src);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifIfd0);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.Xmp);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.Icc);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.JpegComment);
    }

    [Fact]
    public void Strip_IsIdempotent()
    {
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-idem-{Guid.NewGuid():N}.jpg");
        var out1 = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        var out2 = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.AddRange(new[] { out1, out2 });

        var r1 = JpegMetadataStripper.Strip(src, out1, overwriteSource: false, StripProfile.Privacy);
        var r2 = JpegMetadataStripper.Strip(out1, out2, overwriteSource: false, StripProfile.Privacy);

        // Second run should drop 0 segments.
        Assert.Equal(0, r2.DroppedSegments);
        Assert.False(r2.Changed);
    }

    [Fact]
    public void Strip_PreservesByteStuffedScanVerbatim_AndDropsMetadata()
    {
        // Regression for the silent-corruption bug: the scan contains 0xFF00 stuffing and an
        // RST marker. The stripper must copy the scan byte-for-byte while dropping metadata.
        var bytes = FixtureFactory.JpegWithStuffedScanAndMetadata();
        var src = WriteTemp(bytes, $"er-stuff-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        var result = JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        Assert.True(result.DroppedSegments >= 2, $"Expected APP1+COM dropped, got {result.DroppedSegments}.");

        // The scan (from the SOS marker to EOF) must be byte-identical: no stuff bytes lost.
        var inTail = TailFromFirstSos(bytes);
        var outTail = TailFromFirstSos(File.ReadAllBytes(outPath));
        Assert.Equal(inTail, outTail);
    }

    [Fact]
    public void Strip_MultiScanProgressiveJpeg_Succeeds_AndPreservesAllScans()
    {
        // Regression for the progressive-JPEG bug: the old streaming logic threw on the 2nd scan.
        var bytes = FixtureFactory.ProgressiveLikeJpegWithExif();
        var src = WriteTemp(bytes, $"er-prog-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        var result = JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        Assert.True(result.DroppedSegments >= 1, "Expected EXIF dropped.");
        // Both scans, from the first SOS onward, are preserved verbatim.
        var inTail = TailFromFirstSos(bytes);
        var outTail = TailFromFirstSos(File.ReadAllBytes(outPath));
        Assert.Equal(inTail, outTail);
    }

    private static byte[] TailFromFirstSos(byte[] jpeg)
    {
        for (int i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
            {
                return jpeg[i..];
            }
        }
        throw new InvalidOperationException("No SOS marker found in fixture.");
    }

    [Fact]
    public void Strip_RandomFuzzInput_NeverThrowsForValidJpegHeader()
    {
        // Fuzz: 100 iterations of random JPEG-like inputs. The bytes are randomized
        // FIRST, then the first three are overwritten with a valid SOI / SOI-marker
        // prefix so the stripper enters the segment-walk loop. (The previous version
        // of this test set the SOI BEFORE rng.NextBytes, which clobbered it; the
        // test was almost never actually exercising a valid-JPEG fuzz path. See
        // _temp11.md "H1" for the audit's catch of that bug — the audit added new
        // C1/C3 regression tests but did not fix the fuzz test itself.)
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            var bytes = new byte[rng.Next(100, 8192)];
            rng.NextBytes(bytes);
            // Now stamp a valid SOI prefix on top of the random payload.
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
            var src = WriteTemp(bytes, $"er-fuzz-{Guid.NewGuid():N}.jpg");
            var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.jpg");
            _tempFiles.Add(outPath);

            try
            {
                JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // Expected on malformed inputs. Source must remain byte-identical.
                Assert.Equal(bytes, File.ReadAllBytes(src));
            }
        }
    }

    [Fact]
    public void Strip_PreservesStuffedBytesInEntropyScan_ByteForByte()
    {
        // C1 regression: real camera JPEGs contain 0xFF 0x00 byte-stuffing sequences in the
        // entropy-coded scan. The stripper must copy the scan verbatim (not parse it) so the
        // 0x00 stuff byte is preserved. Any other behavior corrupts the bitstream.
        var src = WriteTemp(FixtureFactory.JpegWithStuffedScanAndMetadata(), $"er-stuff-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-stuff-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var srcBytes = File.ReadAllBytes(src);
        var outBytes = File.ReadAllBytes(outPath);

        // Count the 0xFF 0x00 byte-stuffing pairs in each. The stripper must preserve every one.
        // D98 (M2.20.36): collapsed to the shared StreamHelpers.CountStuffedFf00
        // helper (the pre-fix code had a local function here, byte-identical
        // to 3 other copies in the codebase — verifier, SelfTest, and a
        // second local function later in this same file).
        int srcStuffed = StreamHelpers.CountStuffedFf00(srcBytes);
        int outStuffed = StreamHelpers.CountStuffedFf00(outBytes);
        Assert.Equal(srcStuffed, outStuffed);
        // The output must remain a structurally-valid JPEG: SOI ... EOI.
        Assert.Equal(0xFF, outBytes[0]);
        Assert.Equal(0xD8, outBytes[1]);
        Assert.Equal(0xFF, outBytes[^2]);
        Assert.Equal(0xD9, outBytes[^1]);
    }

    [Fact]
    public void Strip_ProgressiveJpegWithMultipleScans_Succeeds()
    {
        // C3 regression: progressive JPEGs (SOF2) have multiple SOS markers. The stripper
        // must copy the entire file from the first SOS onward as bytes, handling all subsequent
        // scans and EOI without parsing entropy data.
        var src = WriteTemp(FixtureFactory.ProgressiveLikeJpegWithExif(), $"er-prog-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-prog-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var srcBytes = File.ReadAllBytes(src);
        var outBytes = File.ReadAllBytes(outPath);
        // The stripper must NOT throw and must produce a structurally-valid JPEG.
        Assert.Equal(0xFF, outBytes[0]);
        Assert.Equal(0xD8, outBytes[1]);
        Assert.Equal(0xFF, outBytes[^2]);
        Assert.Equal(0xD9, outBytes[^1]);
        // Output is <= source in size (only metadata removed).
        Assert.True(outBytes.Length <= srcBytes.Length);
    }

    [Fact]
    public void Strip_OverwriteInPlace_OriginalFileStaysValid()
    {
        // C2 regression: the "Overwrite source" path atomically replaces the original with
        // the stripped output. If the stripper corrupted the scan, the original would be
        // destroyed. After strip+overwrite, a freshly-decoded file must still have a valid
        // SOI..EOI structure and contain no 0xFF 0x00 byte-stuffing loss.
        var src = WriteTemp(FixtureFactory.JpegWithStuffedScanAndMetadata(), $"er-ovr-{Guid.NewGuid():N}.jpg");
        var originalBytes = File.ReadAllBytes(src);

        JpegMetadataStripper.Strip(src, Path.Combine(Path.GetTempPath(), "ignored.jpg"), overwriteSource: true, StripProfile.Privacy);

        var afterBytes = File.ReadAllBytes(src);
        Assert.Equal(0xFF, afterBytes[0]);
        Assert.Equal(0xD8, afterBytes[1]);
        Assert.Equal(0xFF, afterBytes[^2]);
        Assert.Equal(0xD9, afterBytes[^1]);
        // D98 (M2.20.36): collapsed to the shared StreamHelpers.CountStuffedFf00
        // helper. The pre-fix code had a local function here, byte-identical
        // to 3 other copies in the codebase.
        Assert.Equal(StreamHelpers.CountStuffedFf00(originalBytes), StreamHelpers.CountStuffedFf00(afterBytes));
    }

    [Fact]
    public void MetadataInspector_HidesStructuralDirectories()
    {
        // U2: the review grid must only show entries that a strip can actually remove.
        // Structural directories (File / File Type / Huffman) that never change must be hidden.
        var src = WriteTemp(FixtureFactory.JpegWithExifXmpIccAndComment(), $"er-hide-{Guid.NewGuid():N}.jpg");
        var inspection = MetadataInspector.Inspect(src);

        // The "File" and "File Type" directories always exist and are not user-removable.
        Assert.DoesNotContain(inspection.Entries, e => e.Group == "File");
        Assert.DoesNotContain(inspection.Entries, e => e.Group == "File Type");
        Assert.DoesNotContain(inspection.Entries, e => e.Group == "Huffman");
    }

    [Fact]
    public void Strip_JpegWithFillBytes_OutputIsByteIdentical_NoSpuriousChanged()
    {
        // D79 (M2.20.21): the pre-fix ReadMarker consumed any 0xFF "fill bytes" before
        // the marker byte (the JPEG spec allows arbitrary 0xFF padding between segments),
        // but the stripper's segment-walker only wrote `0xFF <marker>` (2 bytes) for
        // every segment — the fill bytes were silently dropped. A JPEG with 0xFF padding
        // would produce a smaller output for no reason, and the Changed flag would be
        // set to true for a file that actually didn't change. The fix: ReadMarker
        // returns the fill byte count via an out parameter, and a local WriteMarker()
        // helper re-emits the fill bytes before the marker.
        //
        // The fixture JpegWithFillBytes() injects 4 fill bytes total: 2 after the SOI,
        // 1 between the JFIF APP0 and the DQT, and 1 before the EOI. The pre-fix
        // stripper would produce a 4-byte-shorter output and trip the Changed flag.
        var bytes = FixtureFactory.JpegWithFillBytes();
        var src = WriteTemp(bytes, $"er-fill-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-fill-out-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        var result = JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // The file has no metadata to strip, so 0 segments dropped.
        Assert.Equal(0, result.DroppedSegments);
        // And the output must be byte-identical to the input — the fill bytes must
        // round-trip through the stripper. A pre-fix stripper would produce a
        // 4-byte-shorter output (3 lost fill bytes in the metadata section; the
        // CopyRestVerbatim path handles entropy-data fill bytes correctly already).
        var outBytes = File.ReadAllBytes(outPath);
        Assert.Equal(bytes.Length, outBytes.Length);
        Assert.Equal(bytes, outBytes);
        // The Changed flag must be false — the file didn't change, only fill bytes
        // were "preserved" (which is what a no-op strip should report).
        Assert.False(result.Changed,
            $"Pre-fix D79 bug: the file is byte-identical to the input but Changed=true " +
            $"because the pre-fix code dropped 4 0xFF fill bytes. " +
            $"Input {bytes.Length} bytes, output {outBytes.Length} bytes.");
    }

    [Fact]
    public void Strip_JpegWithApp14_PreservesApp14_ForCmykColorSpace()
    {
        // D81 (M2.20.23): the pre-fix stripper dropped APP14 (Adobe marker) along with
        // all other APPn segments. APP14 carries the color-transform byte that
        // identifies a JPEG's color space (YCbCr vs YCCK for CMYK). Dropping APP14
        // caused a visible color shift on CMYK JPEGs after stripping. The catalog
        // contract is "Privacy keeps color management" — every other color hint
        // (JFIF, ICC, gAMA, cHRM, sRGB) is kept under at least one profile. The fix:
        // keep APP14 under all profiles. The test pins the contract: a JPEG with
        // APP14 must produce a byte-identical output under all 3 strip profiles.
        var bytes = FixtureFactory.JpegWithApp14();
        var src = WriteTemp(bytes, $"er-app14-{Guid.NewGuid():N}.jpg");
        var outPrivacy = Path.Combine(Path.GetTempPath(), $"er-app14-priv-{Guid.NewGuid():N}.jpg");
        var outAll = Path.Combine(Path.GetTempPath(), $"er-app14-all-{Guid.NewGuid():N}.jpg");
        var outMin = Path.Combine(Path.GetTempPath(), $"er-app14-min-{Guid.NewGuid():N}.jpg");
        _tempFiles.AddRange(new[] { outPrivacy, outAll, outMin });

        // The file has no metadata to drop — APP14 is preserved under every profile.
        // Under the pre-fix code, the stripper would drop APP14 (marking it as a drop
        // along with all other APPn), produce a smaller output, and the user would
        // see a color shift on CMYK JPEGs.
        var rPrivacy = JpegMetadataStripper.Strip(src, outPrivacy, false, StripProfile.Privacy);
        var rAll = JpegMetadataStripper.Strip(src, outAll, false, StripProfile.AllMetadata);
        var rMin = JpegMetadataStripper.Strip(src, outMin, false, StripProfile.Minimal);

        var outPrivacyBytes = File.ReadAllBytes(outPrivacy);
        var outAllBytes = File.ReadAllBytes(outAll);
        var outMinBytes = File.ReadAllBytes(outMin);

        // All 3 profiles must produce a byte-identical output to the input.
        // Pre-fix: Privacy / AllMetadata / Minimal would all drop APP14 (the only
        // "metadata" in this fixture), producing a 16-byte-shorter output.
        Assert.Equal(bytes, outPrivacyBytes);
        Assert.Equal(bytes, outAllBytes);
        Assert.Equal(bytes, outMinBytes);

        // The stripper must report 0 dropped segments under all 3 profiles.
        Assert.Equal(0, rPrivacy.DroppedSegments);
        Assert.Equal(0, rAll.DroppedSegments);
        Assert.Equal(0, rMin.DroppedSegments);
        Assert.False(rPrivacy.Changed);
        Assert.False(rAll.Changed);
        Assert.False(rMin.Changed);
    }
}