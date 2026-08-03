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
        int CountStuffed(ReadOnlySpan<byte> data)
        {
            int n = 0;
            for (int i = 0; i < data.Length - 1; i++)
                if (data[i] == 0xFF && data[i + 1] == 0x00) n++;
            return n;
        }

        int srcStuffed = CountStuffed(srcBytes);
        int outStuffed = CountStuffed(outBytes);
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
        int CountStuffed(ReadOnlySpan<byte> data)
        {
            int n = 0;
            for (int i = 0; i < data.Length - 1; i++)
                if (data[i] == 0xFF && data[i + 1] == 0x00) n++;
            return n;
        }
        Assert.Equal(CountStuffed(originalBytes), CountStuffed(afterBytes));
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
}