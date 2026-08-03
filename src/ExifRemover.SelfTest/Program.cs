using System.Text;
using ExifRemover.Engine;
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
                var bytes = ExifRemover.Tests.FixtureFactory.JpegWithExifXmpIccAndComment();
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.JpegWithExifXmpIccAndComment());
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.JpegWithExifXmpIccAndComment());
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
                var bytes = ExifRemover.Tests.FixtureFactory.TruncatedJpeg();
                var src = Path.Combine(Path.GetTempPath(), "er-trunc.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-trunc-out.jpg");
                File.WriteAllBytes(src, bytes);
                try
                {
                    AssertThrowsAny<Exception>(() => JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy));
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.JpegWithExifXmpIccAndComment());
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
                var original = ExifRemover.Tests.FixtureFactory.JpegWithStuffedScanAndMetadata();
                File.WriteAllBytes(src, original);
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var stripped = File.ReadAllBytes(outPath);
                    int Count(ReadOnlySpan<byte> d)
                    {
                        int n = 0;
                        for (int i = 0; i < d.Length - 1; i++)
                            if (d[i] == 0xFF && d[i + 1] == 0x00) n++;
                        return n;
                    }
                    if (Count(stripped) != Count(original))
                        throw new Exception($"stuffed 0xFF00 count changed: orig={Count(original)} out={Count(stripped)}");
                    if (stripped[0] != 0xFF || stripped[1] != 0xD8 || stripped[^2] != 0xFF || stripped[^1] != 0xD9)
                        throw new Exception("output not a valid JPEG");
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Progressive-style multi-scan JPEG does not throw",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-prog.jpg");
                var outPath = Path.Combine(Path.GetTempPath(), "er-prog-out.jpg");
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.ProgressiveLikeJpegWithExif());
                try
                {
                    JpegMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy);
                    var stripped = File.ReadAllBytes(outPath);
                    if (stripped[0] != 0xFF || stripped[1] != 0xD8 || stripped[^2] != 0xFF || stripped[^1] != 0xD9)
                        throw new Exception("output not a valid JPEG");
                }
                finally { TryDelete(src); TryDelete(outPath); }
            });

        Test("JpegStripper: Overwrite-in-place preserves stuffed 0xFF00 sequences",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-ovr.jpg");
                var original = ExifRemover.Tests.FixtureFactory.JpegWithStuffedScanAndMetadata();
                File.WriteAllBytes(src, original);
                try
                {
                    JpegMetadataStripper.Strip(src, Path.Combine(Path.GetTempPath(), "ignored.jpg"), true, StripProfile.Privacy);
                    var after = File.ReadAllBytes(src);
                    int Count(ReadOnlySpan<byte> d)
                    {
                        int n = 0;
                        for (int i = 0; i < d.Length - 1; i++)
                            if (d[i] == 0xFF && d[i + 1] == 0x00) n++;
                        return n;
                    }
                    if (Count(after) != Count(original))
                        throw new Exception("stuffed 0xFF00 lost during overwrite");
                }
                finally { TryDelete(src); }
            });

        Test("PngStripper: Privacy removes tEXt/tIME/eXIf/iCCP, keeps gAMA",
            () =>
            {
                var src = Path.Combine(Path.GetTempPath(), "er-png-meta.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-meta-out.png");
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp());
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp());
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp());
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
                var bytes = ExifRemover.Tests.FixtureFactory.TruncatedPng();
                var src = Path.Combine(Path.GetTempPath(), "er-png-trunc.png");
                var outPath = Path.Combine(Path.GetTempPath(), "er-png-trunc-out.png");
                File.WriteAllBytes(src, bytes);
                try
                {
                    AssertThrowsAny<Exception>(() => PngMetadataStripper.Strip(src, outPath, false, StripProfile.Privacy));
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp());
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
                File.WriteAllBytes(src, ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp());
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
                    AssertThrowsAny<Exception>(() => StripPipeline.Strip(txt, Path.Combine(Path.GetTempPath(), "er-bogus-out"), false, StripProfile.Privacy));
                }
                finally { TryDelete(txt); }
            });

        Test("Static: format detection works",
            () =>
            {
                var jpg = ExifRemover.Tests.FixtureFactory.JpegWithExifXmpIccAndComment();
                if (ImageFormatDetector.Detect(jpg) != ImageFormat.Jpeg)
                    throw new Exception("JPEG not detected");
                var png = ExifRemover.Tests.FixtureFactory.PngWithTextTimeExifIccp();
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

    private static void AssertThrowsAny<T>(Action action) where T : Exception
    {
        AssertThrows<T>(action);
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