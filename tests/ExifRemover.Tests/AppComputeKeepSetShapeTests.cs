using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the D104 (M2.20.42) cleanup in
/// <c>src/ExifRemover.App/OverlayViewModel.cs</c>. The pre-fix code had
/// a 56-line <c>ComputeKeepSet</c> static method in the App
/// project (WPF-bound). The D104 fix moved the canonical
/// implementation to <c>ExifRemover.Engine.KeepSet.ForFormat</c>
/// and replaced the App's copy with a one-line pass-through delegate,
/// matching the M2.20.40 D102 (FormatBytes) + M2.20.31 D93
/// (KeepSetKey.For) pattern.
/// </summary>
public class AppComputeKeepSetShapeTests
{
    [Fact]
    public void AppComputeKeepSet_IsSingleLinePassThrough()
    {
        // D104 (M2.20.42): the App's ComputeKeepSet must be a
        // one-line pass-through to KeepSet.ForFormat, not a
        // duplicated 56-line implementation.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var vmPath = Path.Combine(path, "src", "ExifRemover.App", "OverlayViewModel.cs");
        Assert.True(File.Exists(vmPath),
            $"Cannot find OverlayViewModel.cs at {vmPath}.");

        var src = File.ReadAllText(vmPath);
        var stripped = StripComments(src);

        Assert.Contains("KeepSet.ForFormat", stripped);
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stripped, @"ComputeKeepSet\s*\([^)]*\)\s*=>").Count;
        Assert.True(matches == 1,
            $"The App's ComputeKeepSet must be a single-line pass-through (1 expression body). " +
            $"Found {matches} matches.");
        Assert.DoesNotContain("set.Add", stripped);
    }

    [Fact]
    public void EngineKeepSet_HasForFormatMethod()
    {
        // D104 (M2.20.42): the canonical implementation must
        // live in Engine.KeepSet.ForFormat.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var keepSetPath = Path.Combine(path, "src", "ExifRemover.Engine", "KeepSet.cs");
        Assert.True(File.Exists(keepSetPath),
            $"Cannot find KeepSet.cs at {keepSetPath}.");

        var src = File.ReadAllText(keepSetPath);

        Assert.Contains("public static class KeepSet", src);
        Assert.Contains("public static HashSet<string> ForFormat", src);
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents.
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
