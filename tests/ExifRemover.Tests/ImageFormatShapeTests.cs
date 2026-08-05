using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the D103 (M2.20.41) cleanup in
/// <c>src/ExifRemover.Engine/ImageFormat.cs</c>. The pre-fix code had
/// the PNG signature (8 bytes: 89 50 4E 47 0D 0A 1A 0A) hardcoded as
/// 8 inline byte comparisons. The fix extracted the signature to a
/// named <c>PngSignature</c> constant and used <c>SequenceEqual</c>,
/// matching the pattern established by D99 (verifier) and D101
/// (PngChunkProbe, PngMetadataStripper).
/// </summary>
public class ImageFormatShapeTests
{
    [Fact]
    public void ImageFormatDetector_UsesPngSignatureConstant()
    {
        // D103 (M2.20.41): the pre-fix code had 8 inline byte
        // comparisons for the PNG signature. The fix extracted
        // the signature to a named constant and used
        // SequenceEqual. A regression that re-inlines the
        // comparison would fail this test.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var detectorPath = Path.Combine(path, "src", "ExifRemover.Engine", "ImageFormat.cs");
        Assert.True(File.Exists(detectorPath),
            $"Cannot find ImageFormat.cs at {detectorPath}.");

        var src = File.ReadAllText(detectorPath);
        var stripped = StripComments(src);

        // The constant must be present.
        Assert.Contains("PngSignature", stripped);
        // The constant must be used in the comparison.
        Assert.Contains("SequenceEqual(PngSignature)", stripped);
    }

    [Fact]
    public void ImageFormatDetector_NoInlinePngSignatureByteComparisons()
    {
        // D103 (M2.20.41): the pre-fix code had inline byte
        // comparisons for the PNG signature
        // (`header[0] == 0x89 && ...`). The fix removes all
        // inline byte comparisons for the signature. A
        // regression that re-inlines them would push the
        // count above 0.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var detectorPath = Path.Combine(path, "src", "ExifRemover.Engine", "ImageFormat.cs");
        Assert.True(File.Exists(detectorPath),
            $"Cannot find ImageFormat.cs at {detectorPath}.");

        var src = File.ReadAllText(detectorPath);
        var stripped = StripComments(src);

        // The pattern: `header[N] == 0x..` for any N and any byte.
        // Post-fix this should be 0 matches (the PNG check uses
        // SequenceEqual against the named constant; the JPEG
        // check has 3 inline byte comparisons, but those are
        // for a 3-byte signature, not the 8-byte PNG signature,
        // and they're a separate concern — D103 only cleans up
        // the PNG signature).
        var pngInline = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"header\[\d+\]\s*==\s*0x[89]").Count;
        Assert.True(pngInline == 0,
            $"Found {pngInline} inline byte comparisons on `header[N] == 0x89` " +
            "(the 0x89 byte is the first byte of the PNG signature). " +
            "The D103 fix extracted the PNG signature to a named " +
            "PngSignature constant and uses SequenceEqual. A regression " +
            "that re-inlines the signature would push the count above 0.");
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (the standard M2.20.26+ pattern).
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
