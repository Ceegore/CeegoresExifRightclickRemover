using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the D100 (M2.20.38) cleanup in
/// <c>src/ExifRemover.Engine/JpegMetadataStripper.cs</c>. The pre-fix
/// code had two inline byte-comparison patterns for the JFXX and ICC
/// profile magic prefixes — 5 and 12 individual byte comparisons,
/// respectively. The fix extracted both to named constants
/// (<c>JfxxMagic</c>, <c>IccProfileMagic</c>) and used
/// <c>SequenceEqual</c> for the comparison, matching the pattern
/// already in use for the JFIF magic (the <c>JfifMagic</c>
/// constant extracted earlier).
///
/// This test class is the same M2.20.32 / M2.20.37 / M2.20.36
/// "source-shape pin" pattern: read the file as text, count
/// occurrences of the pattern we want to assert, fail with a
/// clear diagnostic if the count is off. A regression that
/// re-inlines the magic bytes would fail the test.
/// </summary>
public class JpegMetadataStripperShapeTests
{
    [Fact]
    public void JpegStripper_JfxxMagic_UsesConstant()
    {
        // D100 (M2.20.38): the pre-fix code had 5 inline byte
        // comparisons for the JFXX magic prefix:
        //   jfifSniff[0] == 0x4A && jfifSniff[1] == 0x46 &&
        //   jfifSniff[2] == 0x58 && jfifSniff[3] == 0x58 &&
        //   jfifSniff[4] == 0x00
        // The fix extracted the prefix to a `JfxxMagic` constant
        // and used `jfifSniff.SequenceEqual(JfxxMagic)`. A
        // regression that re-inlines the comparison would fail
        // this test.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var stripperPath = Path.Combine(path, "src", "ExifRemover.Engine", "JpegMetadataStripper.cs");
        Assert.True(File.Exists(stripperPath),
            $"Cannot find JpegMetadataStripper.cs at {stripperPath}.");

        var src = File.ReadAllText(stripperPath);
        var stripped = StripComments(src);

        // The constant must be present.
        Assert.Contains("JfxxMagic", stripped);
        // The constant must be used in the comparison.
        Assert.Contains("SequenceEqual(JfxxMagic)", stripped);
    }

    [Fact]
    public void JpegStripper_IccProfileMagic_UsesConstant()
    {
        // D100 (M2.20.38): the pre-fix code had 12 inline byte
        // comparisons for the ICC profile magic prefix:
        //   iccSniff[0] == 0x49 && iccSniff[1] == 0x43 &&
        //   iccSniff[2] == 0x43 && iccSniff[3] == 0x5F &&
        //   iccSniff[4] == 0x50 && iccSniff[5] == 0x52 &&
        //   iccSniff[6] == 0x4F && iccSniff[7] == 0x46 &&
        //   iccSniff[8] == 0x49 && iccSniff[9] == 0x4C &&
        //   iccSniff[10] == 0x45 && iccSniff[11] == 0x00
        // The fix extracted the prefix to an `IccProfileMagic`
        // constant and used `iccSniff.SequenceEqual(IccProfileMagic)`.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var stripperPath = Path.Combine(path, "src", "ExifRemover.Engine", "JpegMetadataStripper.cs");
        Assert.True(File.Exists(stripperPath),
            $"Cannot find JpegMetadataStripper.cs at {stripperPath}.");

        var src = File.ReadAllText(stripperPath);
        var stripped = StripComments(src);

        // The constant must be present.
        Assert.Contains("IccProfileMagic", stripped);
        // The constant must be used in the comparison.
        Assert.Contains("SequenceEqual(IccProfileMagic)", stripped);
    }

    [Fact]
    public void JpegStripper_NoInlineMagicByteComparisons()
    {
        // D100 (M2.20.38): the pre-fix code had inline byte
        // comparisons for the JFXX (5 bytes) and ICC (12 bytes)
        // magic prefixes. The fix removed all inline byte
        // comparisons and uses named constants. This test
        // asserts that the inline byte comparison pattern
        // (specifically: `jfifSniff[N] == 0x..` or
        // `iccSniff[N] == 0x..` outside the constant
        // definitions) is gone.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var stripperPath = Path.Combine(path, "src", "ExifRemover.Engine", "JpegMetadataStripper.cs");
        Assert.True(File.Exists(stripperPath),
            $"Cannot find JpegMetadataStripper.cs at {stripperPath}.");

        var src = File.ReadAllText(stripperPath);
        var stripped = StripComments(src);

        // The pattern: `jfifSniff[N] == 0x..` for any N. This
        // should be 0 matches post-fix (the JFIF, JFXX, and ICC
        // checks all use SequenceEqual against named constants).
        var jfifInline = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"jfifSniff\[\d+\]\s*==\s*0x[0-9A-Fa-f]+").Count;
        Assert.True(jfifInline == 0,
            $"Found {jfifInline} inline byte comparisons on `jfifSniff[N] == 0x..`. " +
            "The D100 fix removed all inline byte comparisons for the JFIF / JFXX / ICC " +
            "magic prefixes — use the named constants (JfifMagic, JfxxMagic, IccProfileMagic) " +
            "with SequenceEqual instead.");

        // The pattern: `iccSniff[N] == 0x..` for any N. Should
        // also be 0 matches post-fix.
        var iccInline = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"iccSniff\[\d+\]\s*==\s*0x[0-9A-Fa-f]+").Count;
        Assert.True(iccInline == 0,
            $"Found {iccInline} inline byte comparisons on `iccSniff[N] == 0x..`. " +
            "The D100 fix removed all inline byte comparisons for the ICC profile magic " +
            "prefix — use the named IccProfileMagic constant with SequenceEqual instead.");
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (the M2.20.26 D85 + M2.20.34 D96 + M2.20.35 D97 + M2.20.36 D98
    /// + M2.20.37 D99 pattern). A regression that re-introduces the
    /// bug in a comment shouldn't satisfy the assertion.
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
