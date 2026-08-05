using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the D102 (M2.20.40) cleanup in
/// <c>src/ExifRemover.App/Formatting.cs</c>. The pre-fix code had a
/// 3-line <c>FormatBytes</c> helper in the App project (B / KB / MB
/// only, no GB case). The D102 fix moved the canonical implementation
/// to the Engine project (where it's directly unit-testable) and
/// replaced the App's copy with a single-line pass-through delegate
/// to <c>Engine.Formatting.FormatBytes</c>.
///
/// This test pins the structural contract: the App's
/// <c>Formatting.FormatBytes</c> must be a one-line pass-through, not
/// a duplicated 3-line implementation. A future commit that
/// re-inlines the formatting logic (e.g. "for testing" or "for clarity")
/// would push the function body length above 1 and fail this test.
/// </summary>
public class AppFormattingShapeTests
{
    [Fact]
    public void AppFormatting_IsSingleLinePassThrough()
    {
        // D102 (M2.20.40): the App's Formatting.FormatBytes must
        // be a one-line pass-through to Engine.Formatting.FormatBytes.
        // The pattern: `public static string FormatBytes(long b) => Engine.Formatting.FormatBytes(b);`
        // (possibly with a different parameter name or whitespace, but
        // a single expression body, not a multi-line block).
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var appFormattingPath = Path.Combine(path, "src", "ExifRemover.App", "Formatting.cs");
        Assert.True(File.Exists(appFormattingPath),
            $"Cannot find App/Formatting.cs at {appFormattingPath}.");

        var src = File.ReadAllText(appFormattingPath);
        var stripped = StripComments(src);

        // The pass-through must reference Engine.Formatting.FormatBytes.
        Assert.Contains("Engine.Formatting.FormatBytes", stripped);

        // The pass-through must be a SINGLE expression body (=>), not a
        // multi-line method with a body block. The pattern matches
        // `FormatBytes(long b) => ...` (the expression-body form).
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"FormatBytes\s*\(\s*long\s+\w+\s*\)\s*=>").Count;
        Assert.True(matches == 1,
            $"The App's FormatBytes must be a single-line pass-through (1 expression body). " +
            $"Found {matches} matches. The D102 fix moved the canonical implementation " +
            "to Engine and replaced the App's copy with a pass-through. A future commit " +
            "that re-inlines the formatting logic would push the count above 1.");

        // The App's FormatBytes must NOT contain a multi-line body
        // (the `if ... return ...` chain that the pre-fix code had).
        // We assert by checking for the absence of `if (b < 1024 * 1024)`,
        // which is the pre-fix's mid-range branch — the giveaway that
        // the formatting logic is inlined, not delegated.
        Assert.DoesNotContain("if (b < 1024 * 1024)", stripped);
    }

    [Fact]
    public void EngineFormatting_HasGBCase()
    {
        // D102 (M2.20.40): the pre-fix code only had B / KB / MB
        // cases. A 1.5 GB file would render as "1500.00 MB"
        // instead of "1.46 GB". The fix adds the GB case. This
        // test asserts the GB case is present in the Engine's
        // Formatting.cs (the canonical implementation).
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var engineFormattingPath = Path.Combine(path, "src", "ExifRemover.Engine", "Formatting.cs");
        Assert.True(File.Exists(engineFormattingPath),
            $"Cannot find Engine/Formatting.cs at {engineFormattingPath}.");

        var src = File.ReadAllText(engineFormattingPath);

        // The GB case must be present. The format string is `"{0:0.00} GB"`.
        // We check for the `} GB"` substring (the closing brace of the
        // format placeholder, followed by a space, followed by "GB").
        Assert.Contains("} GB\"", src);
        // Also assert the GB threshold (1024^3 bytes = 1 GB).
        Assert.Contains("1024L * 1024 * 1024", src);
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
