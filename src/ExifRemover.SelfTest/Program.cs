using System.Text;
using ExifRemover.Engine;
using ExifRemover.Tests;
using MetadataExtractor;

namespace ExifRemover.SelfTest;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static int Main(string[] args)
    {
        Console.WriteLine("ExifRemover self-test");
        Console.WriteLine("=====================");
        Console.WriteLine();

        Test("JpegStripper: Privacy removes EXIF/XMP/ICC/COM and keeps JFIF",
            () =>
            {
                var bytes = FixtureFactory.JpegWithExifXmpIccAndComment();
                var src = Path.Combine(Path.GetTempPath(), "er-jpg-test.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-jpg-out.jpg");
                File.WriteAllBytes(src, bytes);
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var post = MetadataInspector.Inspect(outPath);
                    AssertNotContains(post, MetadataGroups.ExifIfd0);
                    AssertNotContains(post, MetadataGroups.Icc);
                    AssertNotContains(post, MetadataGroups.JpegComment);
                    AssertContains(post, MetadataGroups.Jfif);
                }
                finally
                {
                    TryDelete(src);
                    TryDelete(outPath);
                }
            });

        Test("JpegStripper: Minimal keeps ICC profile",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-jpg-min.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-jpg-min-out.jpg");
                File.WriteAllBytes(src, FixtureFactory.JpegWithExifXmpIccAndComment());
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Minimal);
                    var post = MetadataInspector.Inspect(outPath);
                    AssertContains(post, MetadataGroups.Icc);
                    AssertNotContains(post, MetadataGroups.ExifIfd0);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: AllMetadata strips ICC",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-jpg-all.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-jpg-all-out.jpg");
                File.WriteAllBytes(src, FixtureFactory.JpegWithExifXmpIccAndComment());
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.AllMetadata);
                    var post = MetadataInspector.Inspect(outPath);
                    AssertNotContains(post, MetadataGroups.Icc);
                    AssertNotContains(post, MetadataGroups.ExifIfd0);
                    AssertContains(post, MetadataGroups.Jfif);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Truncated input throws, original untouched",
            () =>
            {
                var bytes = FixtureFactory.TruncatedJpeg();
                var src = Path.Combine(Path.GetTempPath(), "er-trunc.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-trunc-out.jpg");
                File.WriteAllBytes(src, bytes);
                try
                {
                    AssertThrows<Exception>(() => JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy));
                    if (!bytes.SequenceEqual(File.ReadAllBytes(src)))
                        throw new Exception("Original bytes changed!");
                    if (File.Exists(outPath))
                        throw new Exception("Output file should not exist.");
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Is idempotent",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-idem.jpg");
                var out1 = Path.Combine(Path.GetTempPath(), "er-idem-1.jpg");
                var out2 = Path.Combine(Path.GetTempPath(), "er-idem-2.jpg");
                File.WriteAllBytes(src, FixtureFactory.JpegWithExifXmpIccAndComment());
                try
                {
                    var r1 = JpegMetadataStripper.Strip(src, out1, false, StripProfile.Privacy);
                    var r2 = JpegMetadataStripper.Strip(out1, out2, false, StripProfile.Privacy);
                    if (r2.DroppedSegments != 0) throw new Exception($"Second pass dropped {r2.DroppedSegments}");
                }
                finally { TryDelete(src); TryDelete(out1); TryDelete(out2); }
            });

        Test("JpegStripper: Stuffed bytes in entropy scan are preserved byte-for-byte",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-stuff.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-stuff-out.jpg");
                var original = FixtureFactory.JpegWithStuffedScanAndMetadata();
                File.WriteAllBytes(src, original);
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var stripped = File.ReadAllBytes(outPath);
                    // D95: collapsed to the CountStuffedFf00 + AssertValidJpeg
                    // helpers on the Program class. Pre-fix, the test defined
                    // a local `int Count(ReadOnlySpan<byte> d)` function and
                    // hand-rolled the 4-byte JPEG signature check inline —
                    // both byte-identical to the "Overwrite-in-place" test
                    // below (L160-168 in the pre-fix file).
                    if (CountStuffedFf00(stripped) != CountStuffedFf00(original))
                        throw new Exception($"stuffed 0xFF00 count changed: orig={CountStuffedFf00(original)} out={CountStuffedFf00(stripped)}");
                    AssertValidJpeg(stripped);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Progressive-style multi-scan JPEG does not throw",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-prog.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-prog-out.jpg");
                File.WriteAllBytes(src, FixtureFactory.ProgressiveLikeJpegWithExif());
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var stripped = File.ReadAllBytes(outPath);
                    // D95: collapsed to the AssertValidJpeg helper.
                    AssertValidJpeg(stripped);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Overwrite-in-place preserves stuffed 0xFF00 sequences",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-ovr.jpg");
                var original = FixtureFactory.JpegWithStuffedScanAndMetadata();
                File.WriteAllBytes(src, original);
                try
                {
                    JpegMetadataStripper.Strip(src, Path.Combine(Path.GetTempPath(), "ignored.jpg"), true, StripProfile.Privacy);
                    var after = File.ReadAllBytes(src);
                    // D95: collapsed to the CountStuffedFf00 helper.
                    if (CountStuffedFf00(after) != CountStuffedFf00(original))
                        throw new Exception("stuffed 0xFF00 lost during overwrite");
                }
                finally { TryDelete(src); }
            });

        Test("PngStripper: Privacy removes tEXt/tIME/eXIf/iCCP, keeps gAMA",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-meta.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-meta-out.png");
                File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());
                try
                {
                    var preInspect = MetadataInspector.Inspect(src);
                    AssertContains(preInspect, MetadataGroups.PngText);
                    AssertContains(preInspect, MetadataGroups.PngTime);
                    AssertContains(preInspect, MetadataGroups.PngIccp);
                    var result = PngMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    if (result.DroppedSegments < 4) throw new Exception($"Dropped {result.DroppedSegments}");
                    var post = MetadataInspector.Inspect(outPath);
                    AssertNotContains(post, MetadataGroups.PngText);
                    AssertNotContains(post, MetadataGroups.PngTime);
                    AssertNotContains(post, MetadataGroups.PngIccp);
                    AssertContains(post, MetadataGroups.PngGama);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("PngStripper: AllMetadata strips gAMA",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-all.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-all-out.png");
                File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());
                try
                {
                    PngMetadataStripper.Strip(src, outPath, false, StripProfile.AllMetadata);
                    var post = MetadataInspector.Inspect(outPath);
                    AssertNotContains(post, MetadataGroups.PngGama);
                    AssertNotContains(post, MetadataGroups.PngText);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("PngStripper: Minimal keeps iCCP",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-min2.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-min2-out.png");
                File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());
                try
                {
                    PngMetadataStripper.Strip(src, outPath, false, StripProfile.Minimal);
                    var post = MetadataInspector.Inspect(outPath);
                    AssertContains(post, MetadataGroups.PngIccp);
                    AssertNotContains(post, MetadataGroups.PngText);
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("PngStripper: Truncated input throws, original untouched",
            () =>
            {
                var bytes = FixtureFactory.TruncatedPng();
                var src = Path.Combine(Path.GetTempPath(), "er-png-trunc.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-trunc-out.png");
                File.WriteAllBytes(src, bytes);
                try
                {
                    AssertThrows<Exception>(() => PngMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy));
                    if (!bytes.SequenceEqual(File.ReadAllBytes(src)))
                        throw new Exception("Original bytes changed!");
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("PngStripper: IDAT bytes preserved bit-for-bit",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-idat.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-idat-out.png");
                File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());
                try
                {
                    var srcBytes = File.ReadAllBytes(src);
                    var srcIdat = ExtractIdat(srcBytes);
                    PngMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var outIdat = ExtractIdat(File.ReadAllBytes(outPath));
                    if (!srcIdat.SequenceEqual(outIdat))
                        throw new Exception("IDAT bytes changed!");
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("PngStripper: Is idempotent",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-idem.png");
                var out1 = Path.Combine(Path.GetTempPath(), "er-png-idem-1.png");
                var out2 = Path.Combine(Path.GetTempPath(), "er-png-idem-2.png");
                File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());
                try
                {
                    PngMetadataStripper.Strip(src, out1, false, StripProfile.Privacy);
                    var r2 = PngMetadataStripper.Strip(out1, out2, false, StripProfile.Privacy);
                    if (r2.DroppedSegments != 0) throw new Exception($"Second pass dropped {r2.DroppedSegments}");
                }
                finally { TryDelete(src); TryDelete(out1); TryDelete(out2); }
            });

        Test("StripPipeline: handles unsupported file gracefully",
            () =>
            {
                var txt = Path.Combine(Path.GetTempPath(), "er-bogus.txt");
                File.WriteAllText(txt, "not an image");
                try
                {
                    AssertThrows<Exception>(() => StripPipeline.Strip(txt, Path.Combine(Path.GetTempPath(), "er-bogus-out"), false, StripProfile.Privacy));
                }
                finally { TryDelete(txt); }
            });

        // D95 (M2.20.33): direct unit test for the CountStuffedFf00 helper
        // that the "Stuffed bytes preserved" tests above now share. Pre-fix,
        // the helper was a local function inside two test methods, so the
        // contract was only ever exercised through end-to-end stripper runs
        // (a count regression in the helper would surface as a confusing
        // "stuffed 0xFF00 count changed" error, not a clear "this is the
        // helper that's wrong" message). The new test pins the helper's
        // contract directly: empty input → 0, single byte → 0, FF not
        // followed by 00 → 0, multiple 0xFF00 pairs → exact count, FF at
        // the trailing edge (no following byte) → not counted.
        Test("Helpers: CountStuffedFf00 counts byte-stuffed 0xFF00 pairs",
            () =>
            {
                if (CountStuffedFf00(ReadOnlySpan<byte>.Empty) != 0)
                    throw new Exception("Empty span should return 0");
                if (CountStuffedFf00(new byte[] { 0xFF }) != 0)
                    throw new Exception("Single byte should return 0 (no following byte to pair with)");
                if (CountStuffedFf00(new byte[] { 0xFF, 0xAB }) != 0)
                    throw new Exception("0xFF followed by non-0x00 should not count");
                // 3 stuffed pairs interleaved with non-stuffed bytes.
                if (CountStuffedFf00(new byte[] { 0xFF, 0x00, 0xAB, 0xCD, 0xFF, 0x00, 0xEF, 0xFF, 0x00, 0x12 }) != 3)
                    throw new Exception("Expected 3 stuffed 0xFF00 pairs");
                // Trailing 0xFF with no following byte — should not count.
                if (CountStuffedFf00(new byte[] { 0xFF, 0x00, 0xFF }) != 1)
                    throw new Exception("Trailing 0xFF with no following byte should not count");
            });

        Test("Static: format detection works",
            () =>
            {
                var jpg = FixtureFactory.JpegWithExifXmpIccAndComment();
                if (ImageFormatDetector.Detect(jpg) != ImageFormat.Jpeg)
                    throw new Exception("JPEG not detected");
                var png = FixtureFactory.PngWithTextTimeExifIccp();
                if (ImageFormatDetector.Detect(png) != ImageFormat.Png)
                    throw new Exception("PNG not detected");
                if (ImageFormatDetector.Detect(new byte[] { 1, 2, 3 }) != ImageFormat.Unknown)
                    throw new Exception("Random bytes should be unknown");
            });

        Console.WriteLine();
        Console.WriteLine($"PASSED: {_passed}, FAILED: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
        }
        throw new Exception($"Expected {typeof(T).Name} but no exception was thrown.");
    }

    private static void AssertContains(FileInspection inspection, string group)
    {
        if (!inspection.Entries.Any(e => e.Group == group))
            throw new Exception($"Expected group '{group}' but it was not present in {inspection.Entries.Count} entries.");
    }

    private static void AssertNotContains(FileInspection inspection, string group)
    {
        if (inspection.Entries.Any(e => e.Group == group))
            throw new Exception($"Expected group '{group}' to be absent but it was present.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// D95 (M2.20.33): counts the number of <c>0xFF 0x00</c> byte pairs in
    /// <paramref name="data"/>. JPEG entropy-coded segments use <c>0xFF 0x00</c>
    /// as a byte-stuffing escape for any <c>0xFF</c> byte that appears in the
    /// bitstream (so a real marker <c>0xFF xx</c> can never be confused with
    /// raw data). The SelfTest's "Stuffed bytes preserved" tests count these
    /// pairs before and after a strip to verify the stripper didn't accidentally
    /// drop or duplicate any. Pre-fix, the test had a local <c>int Count(ReadOnlySpan&lt;byte&gt;)</c>
    /// function declared inside TWO test methods (byte-identical, 5 lines each).
    /// The M2.20.32 D94 audit walked WPF-bound App code; the M2.20.33 D95 audit
    /// walked SelfTest code, which is integration-test code (not Engine code, not
    /// App code) and had escaped the prior DRY sweeps entirely.
    /// </summary>
    private static int CountStuffedFf00(ReadOnlySpan<byte> data)
    {
        int n = 0;
        for (int i = 0; i < data.Length - 1; i++)
            if (data[i] == 0xFF && data[i + 1] == 0x00) n++;
        return n;
    }

    /// <summary>
    /// D95 (M2.20.33): asserts that <paramref name="data"/> is at least 4 bytes
    /// long and has a valid JPEG signature (SOI marker <c>0xFF 0xD8</c> at the
    /// start, EOI marker <c>0xFF 0xD9</c> at the end). Throws if any check
    /// fails. The pre-fix code hand-rolled the same 4-comparison check at TWO
    /// SelfTest sites (byte-identical, 2 lines each). A future SelfTest that
    /// validates JPEG output would have to copy the check a third time — and a
    /// silent typo (e.g. <c>0xD7</c> instead of <c>0xD8</c>) would let a corrupt
    /// output pass the test. Funneling every check through this helper makes the
    /// contract explicit and grep-able.
    /// </summary>
    private static void AssertValidJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4
            || data[0] != 0xFF || data[1] != 0xD8
            || data[^2] != 0xFF || data[^1] != 0xD9)
        {
            throw new Exception("output not a valid JPEG");
        }
    }

    private static byte[] ExtractIdat(byte[] pngBytes)
    {
        var ms = new MemoryStream();
        int pos = 8;
        while (pos < pngBytes.Length)
        {
            int length = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
            var type = Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
            if (type == "IDAT") ms.Write(pngBytes, pos + 8, length);
            int next = pos + 8 + length + 4;
            if (type == "IEND" || next > pngBytes.Length) break;
            pos = next;
        }
        return ms.ToArray();
    }
}