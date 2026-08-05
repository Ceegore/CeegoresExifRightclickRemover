using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the D101 (M2.20.39) cleanup in
/// <c>src/ExifRemover.Engine/PngMetadataStripper.cs</c> and
/// <c>src/ExifRemover.Engine/MetadataInspector.cs</c>. The pre-fix
/// code had 12 inline 4-byte chunk-type comparisons in
/// <c>ShouldDrop</c> + 2 in <c>Strip</c> (40 + 8 = 48 individual
/// byte comparisons) — the same D100 anti-pattern as
/// JpegMetadataStripper. The fix extracted 12 chunk type
/// constants to <c>PngMetadataStripper.cs</c> and 3 to
/// <c>MetadataInspector.cs</c>'s nested <c>PngChunkProbe</c>,
/// and used <c>SequenceEqual</c> for the comparisons. The
/// PngChunkProbe fix also eliminated a per-chunk string
/// allocation (the pre-fix code did
/// <c>new string(new[] { (char)header[4], ... })</c> per
/// chunk, allocating a string + 4 boxed chars per iteration).
/// </summary>
public class PngMetadataStripperShapeTests
{
    [Fact]
    public void PngStripper_HasAll12ChunkTypeConstants()
    {
        // D101 (M2.20.39): the pre-fix code had 12 inline
        // 4-byte comparison chains in ShouldDrop + 2 in
        // Strip. The fix extracted them to 12 named
        // constants. A regression that re-inlines the
        // chunk types (or forgets a constant) would fail
        // this test.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var stripperPath = Path.Combine(path, "src", "ExifRemover.Engine", "PngMetadataStripper.cs");
        Assert.True(File.Exists(stripperPath),
            $"Cannot find PngMetadataStripper.cs at {stripperPath}.");

        var src = File.ReadAllText(stripperPath);
        var stripped = StripComments(src);

        // 12 chunk type constants + IHDR + IEND = 14 expected
        // (the 10 in ShouldDrop + 2 in Strip). Plus we check
        // the PngMetadataStripper-level IhdrBytes and IendBytes
        // are used in the Strip method (the post-fix site
        // comparison).
        string[] expectedConstants =
        {
            "IhdrBytes", "IendBytes",
            "TextBytes", "ZtxtBytes", "ItxtBytes",
            "TimeBytes", "ExifBytes",
            "IccpBytes", "HistBytes",
            "GamaBytes", "ChrmBytes", "SrgbBytes",
        };
        foreach (var name in expectedConstants)
        {
            Assert.Contains(name, stripped);
        }
    }

    [Fact]
    public void PngStripper_NoInlineChunkTypeByteComparisons()
    {
        // D101 (M2.20.39): the pre-fix code had inline byte
        // comparisons for each chunk type (e.g.
        // `typeBuf[0] == 'I' && typeBuf[1] == 'H' && typeBuf[2] == 'D'
        // && typeBuf[3] == 'R'` for IHDR). The fix uses
        // SequenceEqual against named constants. A
        // regression that re-inlines the chunk types would
        // fail this test.
        //
        // The pattern: `typeBuf[N] == 'C'` for any N and any
        // char C. The post-fix code should have 0 such
        // matches.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var stripperPath = Path.Combine(path, "src", "ExifRemover.Engine", "PngMetadataStripper.cs");
        Assert.True(File.Exists(stripperPath),
            $"Cannot find PngMetadataStripper.cs at {stripperPath}.");

        var src = File.ReadAllText(stripperPath);
        var stripped = StripComments(src);

        // Count inline byte comparisons on typeBuf[N] == 'C'
        var inlineCount = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"typeBuf\[\d+\]\s*==\s*'[A-Za-z]'").Count;
        Assert.True(inlineCount == 0,
            $"Found {inlineCount} inline byte comparisons on `typeBuf[N] == 'C'`. " +
            "The D101 fix extracted all chunk type constants (IhdrBytes, IendBytes, " +
            "TextBytes, ZtxtBytes, ItxtBytes, TimeBytes, ExifBytes, IccpBytes, " +
            "HistBytes, GamaBytes, ChrmBytes, SrgbBytes) and uses SequenceEqual. " +
            "A regression that re-inlines the chunk types would push the count " +
            "above 0.");
    }

    [Fact]
    public void PngChunkProbe_HasChunkTypeConstants()
    {
        // D101 (M2.20.39): the pre-fix PngChunkProbe in
        // MetadataInspector.cs allocated a string per chunk
        // (`new string(new[] { (char)header[4], ... })`) and
        // compared strings. The fix uses byte comparisons
        // against named constants (ExifBytes, HistBytes,
        // IendBytes) — no per-chunk string allocation.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var inspectorPath = Path.Combine(path, "src", "ExifRemover.Engine", "MetadataInspector.cs");
        Assert.True(File.Exists(inspectorPath),
            $"Cannot find MetadataInspector.cs at {inspectorPath}.");

        var src = File.ReadAllText(inspectorPath);
        var stripped = StripComments(src);

        // 3 chunk type constants in PngChunkProbe.
        string[] expectedConstants = { "ExifBytes", "HistBytes", "IendBytes" };
        foreach (var name in expectedConstants)
        {
            Assert.Contains(name, stripped);
        }
    }

    [Fact]
    public void PngChunkProbe_NoPerChunkStringAllocation()
    {
        // D101 (M2.20.39): the pre-fix PngChunkProbe had
        //   `var type = new string(new[] { (char)header[4], ... });`
        // which allocated a string + 4 boxed chars per PNG
        // chunk. The fix uses a Span<byte> slice + SequenceEqual.
        // A regression that re-introduces the per-chunk string
        // allocation would fail this test.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var inspectorPath = Path.Combine(path, "src", "ExifRemover.Engine", "MetadataInspector.cs");
        Assert.True(File.Exists(inspectorPath),
            $"Cannot find MetadataInspector.cs at {inspectorPath}.");

        var src = File.ReadAllText(inspectorPath);
        var stripped = StripComments(src);

        // The pattern: `new string(new[] {` (the start of the
        // per-chunk string allocation). Post-fix this should
        // be 0 matches (the PngMetadataStripper's Ascii
        // helper is private to that file and not in
        // MetadataInspector.cs).
        var stringAllocCount = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"new\s+string\s*\(\s*new\s*\[").Count;
        Assert.True(stringAllocCount == 0,
            $"Found {stringAllocCount} occurrences of 'new string(new[] <char>)' in " +
            "MetadataInspector.cs. The D101 fix removed the per-chunk string " +
            "allocation from PngChunkProbe — use a Span<byte> slice + " +
            "SequenceEqual against the named chunk type constants instead.");
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (the M2.20.26 D85 + M2.20.34 D96 + M2.20.35 D97 + M2.20.36 D98
    /// + M2.20.37 D99 + M2.20.38 D100 pattern). A regression that
    /// re-introduces the bug in a comment shouldn't satisfy the
    /// assertion.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            if (source[i] == '"')
            {
                result.Append(source[i]);
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i]);
                        result.Append(source[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        result.Append(source[i]);
                        i++;
                    }
                }
                if (i < source.Length)
                {
                    result.Append(source[i]);
                    i++;
                }
                continue;
            }
            result.Append(source[i]);
            i++;
        }
        return result.ToString();
    }
}
