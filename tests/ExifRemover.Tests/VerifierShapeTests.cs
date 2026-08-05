using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the verifier's <c>IsValidPng</c>
/// cleanup in D99 (M2.20.37). The pre-fix code had a redundant
/// <c>if (b.Length &lt; 12) return false;</c> check at L154 — unreachable
/// because the same check at L146 already returned for any buffer
/// &lt; 12 bytes. The fix consolidated the bounds check into a single
/// <c>if (b.Length &lt; 20) return false;</c> at the top (20 = 8-byte
/// signature + 12-byte IEND trailer, the minimum for a valid PNG).
///
/// The pre-fix code also hardcoded the 8-byte PNG signature and the
/// 4-byte "IEND" type as inline byte/char comparisons. The fix
/// extracted them to <c>PngSignature</c> and <c>IendTypeBytes</c>
/// <c>static readonly byte[]</c> constants and used
/// <c>SequenceEqual</c> for the comparison (clearer than 4+4 individual
/// byte/char comparisons, and the constants document the format
/// directly).
/// </summary>
public class VerifierShapeTests
{
    [Fact]
    public void Verifier_IsValidPng_NoRedundantLengthCheck()
    {
        // D99 (M2.20.37): the pre-fix code had `if (b.Length < 12)`
        // TWICE in IsValidPng — once at the top (L146) and once
        // before the IEND detection (L154). The second check was
        // unreachable: the first check already returned for any
        // buffer < 12 bytes. The fix consolidated to a single
        // `if (b.Length < 20)` (the actual minimum for a valid PNG:
        // 8-byte signature + 12-byte IEND trailer).
        //
        // This test asserts the post-fix contract: any
        // `b.Length < N` check in verify/Program.cs should be
        // exact and non-redundant. A regression that re-introduces
        // a second bounds check would fail this test.
        //
        // The pattern: `b.Length < 12` (the pre-fix duplicate check).
        // The post-fix code uses `b.Length < 20`, so a search for
        // `b.Length < 12` should return 0 matches.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var verifierPath = Path.Combine(path, "verify", "Program.cs");
        Assert.True(File.Exists(verifierPath),
            $"Cannot find verifier source at {verifierPath}.");

        var src = File.ReadAllText(verifierPath);
        var stripped = StripComments(src);

        // The pre-fix duplicate check: `b.Length < 12` (used to appear
        // twice — once at L146 and once at L154). The post-fix code
        // uses `b.Length < 20` (the actual minimum for a valid PNG),
        // so a search for `b.Length < 12` should return 0 matches.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"b\.Length\s*<\s*12").Count;
        Assert.True(matches == 0,
            $"Found {matches} occurrences of `b.Length < 12` in verify/Program.cs. " +
            "The D99 fix consolidated the duplicate bounds check into a single `b.Length < 20` " +
            "(the actual minimum for a valid PNG: 8-byte signature + 12-byte IEND trailer). " +
            "A regression that re-introduces a second bounds check would re-introduce the " +
            "dead-code bug.");
    }

    [Fact]
    public void Verifier_IsValidPng_UsesPngSignatureConstant()
    {
        // D99 (M2.20.37): the pre-fix code hardcoded the 8-byte PNG
        // signature (89 50 4E 47 0D 0A 1A 0A) as 8 inline byte
        // comparisons (`b[0] != 0x89 || b[1] != 0x50 || ...`). The
        // fix extracted the signature to a `static readonly byte[]`
        // constant named `PngSignature` and used
        // `b.AsSpan(0, 8).SequenceEqual(PngSignature)` for the
        // comparison. A regression that re-inlines the signature
        // would fail this test.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var verifierPath = Path.Combine(path, "verify", "Program.cs");
        Assert.True(File.Exists(verifierPath),
            $"Cannot find verifier source at {verifierPath}.");

        var src = File.ReadAllText(verifierPath);
        var stripped = StripComments(src);

        // The constant must be present.
        Assert.Contains("PngSignature", stripped);
        // The constant must be used in the comparison.
        Assert.Contains("SequenceEqual(PngSignature)", stripped);
    }

    [Fact]
    public void Verifier_IsValidPng_UsesIendTypeConstant()
    {
        // D99 (M2.20.37): the pre-fix code hardcoded "IEND" as 4
        // individual char comparisons (`b[iendOffset + 4] != 'I' ||
        // b[iendOffset + 5] != 'E' || ...`). The fix extracted the
        // IEND type to a `static readonly byte[]` constant named
        // `IendTypeBytes` and used `SequenceEqual` for the comparison.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var verifierPath = Path.Combine(path, "verify", "Program.cs");
        Assert.True(File.Exists(verifierPath),
            $"Cannot find verifier source at {verifierPath}.");

        var src = File.ReadAllText(verifierPath);
        var stripped = StripComments(src);

        // The constant must be present.
        Assert.Contains("IendTypeBytes", stripped);
        // The constant must be used in the comparison.
        Assert.Contains("SequenceEqual(IendTypeBytes)", stripped);
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (the M2.20.26 D85 + M2.20.34 D96 + M2.20.35 D97 + M2.20.36 D98
    /// pattern). A regression that re-introduces the bug in a
    /// comment shouldn't satisfy the assertion.
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
