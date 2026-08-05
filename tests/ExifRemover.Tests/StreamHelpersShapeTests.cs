using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the <c>StreamHelpers.CountStuffedFf00</c>
/// promotion in D98 (M2.20.36). The pre-fix code had a hand-rolled
/// <c>int CountStuffed(byte[] b)</c> (or local function with the same
/// body) in 4 locations:
/// <list type="bullet">
///   <item>verify/Program.cs (CountStuffed private static)</item>
///   <item>src/ExifRemover.SelfTest/Program.cs (CountStuffedFf00 private
///     static, added in M2.20.33 D95)</item>
///   <item>tests/ExifRemover.Tests/JpegStripperTests.cs (2 local functions
///     inside test methods)</item>
/// </list>
/// All 4 copies were byte-identical (5 lines: an `int n = 0;`, a `for`
/// loop, an `if (data[i] == 0xFF && data[i + 1] == 0x00) n++;`, a
/// `return n;`).
///
/// The M2.20.33 D95 audit found 2 sites in SelfTest but missed the
/// verifier and the xUnit tests. The M2.20.36 project-wide sweep promoted
/// the helper to <c>StreamHelpers.CountStuffedFf00</c> and replaced all
/// 4 sites. This test pins the refactor's structural contract: the
/// hand-rolled body should appear exactly 1 time (inside
/// <c>StreamHelpers.CountStuffedFf00</c>); a future commit that
/// re-introduces a private copy anywhere else would fail the test with
/// a clear "Found N" diagnostic naming the count delta.
/// </summary>
public class StreamHelpersShapeTests
{
    [Fact]
    public void CountStuffedFf00_HandRolledBody_AppearsExactlyOnce()
    {
        // The body of the helper is the 5-line pattern:
        //   int n = 0;
        //   for (int i = 0; i < data.Length - 1; i++)
        //       if (data[i] == 0xFF && data[i + 1] == 0x00) n++;
        //   return n;
        // We grep for the most distinctive line (the `if` with both 0xFF
        // and 0x00 comparisons) and assert it appears exactly 1 time
        // across the 4 source files that previously had copies.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);

        var files = new[]
        {
            Path.Combine(path, "src", "ExifRemover.Engine", "StreamHelpers.cs"),
            Path.Combine(path, "src", "ExifRemover.SelfTest", "Program.cs"),
            Path.Combine(path, "verify", "Program.cs"),
            Path.Combine(path, "tests", "ExifRemover.Tests", "JpegStripperTests.cs"),
        };
        foreach (var f in files)
        {
            Assert.True(File.Exists(f),
                $"Cannot find source file at {f}.");
        }

        int totalMatches = 0;
        var locations = new System.Text.StringBuilder();
        foreach (var f in files)
        {
            var src = File.ReadAllText(f);
            // Strip line + block comments so the test isn't fooled by a
            // regression that re-introduces the pattern inside a comment.
            var stripped = StripComments(src);
            // The distinctive inner line of the helper body. The pattern
            // matches `if (data[i] == 0xFF && data[i + 1] == 0x00) n++;`
            // (and minor whitespace variations). It would NOT match a
            // future refactor that uses a different loop bound (e.g.
            // `< data.Length`) or a different accumulator variable name,
            // but those would be observable behavioral changes anyway.
            int count = System.Text.RegularExpressions.Regex.Matches(
                stripped, @"data\[i\]\s*==\s*0xFF\s*&&\s*data\[i\s*\+\s*1\]\s*==\s*0x00").Count;
            if (count > 0)
            {
                locations.AppendLine($"  {f}: {count}");
            }
            totalMatches += count;
        }

        Assert.True(totalMatches == 1,
            $"The hand-rolled CountStuffedFf00 body should appear exactly 1 time " +
            $"(inside StreamHelpers.CountStuffedFf00). Found {totalMatches}:\n{locations}" +
            "If a new caller needs this counter, call StreamHelpers.CountStuffedFf00 directly — " +
            "do NOT re-introduce a private copy. The M2.20.33 D95 + M2.20.36 D98 fix collapsed " +
            "4 hand-rolled copies into a single shared helper.");
    }

    [Fact]
    public void Verifier_NoLongerDeclaresLocalCountStuffed()
    {
        // The pre-fix verifier had `private static int CountStuffed(byte[] b)`.
        // The post-fix verifier uses StreamHelpers.CountStuffedFf00 directly.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var verifierPath = Path.Combine(path, "verify", "Program.cs");
        Assert.True(File.Exists(verifierPath),
            $"Cannot find verifier source at {verifierPath}.");

        var src = File.ReadAllText(verifierPath);
        // The local-function declaration pattern: `int CountStuffed(byte[] b)`
        // or `private static int CountStuffed(byte[] b)`. The post-fix
        // verifier should NOT have this — the helper lives in StreamHelpers.
        var stripped = StripComments(src);
        Assert.DoesNotContain("int CountStuffed(byte[]", stripped);
        // The post-fix verifier should call the shared helper.
        Assert.Contains("StreamHelpers.CountStuffedFf00", stripped);
    }

    [Fact]
    public void SelfTest_NoLongerDeclaresLocalCountStuffedFf00()
    {
        // The pre-fix SelfTest had `private static int CountStuffedFf00(ReadOnlySpan<byte> data)`.
        // The M2.20.33 D95 fix extracted the local; the M2.20.36 D98 fix
        // promoted it to StreamHelpers and removed the SelfTest's local.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        path = Path.GetFullPath(path);
        var selfTestPath = Path.Combine(path, "src", "ExifRemover.SelfTest", "Program.cs");
        Assert.True(File.Exists(selfTestPath),
            $"Cannot find SelfTest source at {selfTestPath}.");

        var src = File.ReadAllText(selfTestPath);
        var stripped = StripComments(src);
        Assert.DoesNotContain("int CountStuffedFf00(ReadOnlySpan<byte>", stripped);
        // The post-fix SelfTest should call the shared helper at every
        // previously-local call site.
        Assert.Contains("StreamHelpers.CountStuffedFf00", stripped);
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (the M2.20.26 D85 + M2.20.34 D96 + M2.20.35 D97 pattern). The
    /// contract: a regression that re-introduces the body inside a
    /// comment shouldn't satisfy the assertion; a regression that
    /// re-introduces it in code SHOULD satisfy the assertion (and fail
    /// the test).
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
