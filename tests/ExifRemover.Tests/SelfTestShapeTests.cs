using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for <c>src/ExifRemover.SelfTest/Program.cs</c>.
/// The test project can't include the SelfTest's source via
/// <c>&lt;Compile Include&gt;</c> (SelfTest is a console app, not a class
/// library), so we read the file as text and grep for the patterns we
/// want to pin. Same "static check" pattern as the M2.20.24, M2.20.25,
/// M2.20.26, M2.20.27, M2.20.28, M2.20.32, M2.20.34 source-shape tests
/// in this project.
/// </summary>
public class SelfTestShapeTests
{
    [Fact]
    public void SelfTest_UsesFixtureFactoryWithoutNamespacePrefix()
    {
        // D97 (M2.20.35): the pre-fix code had 16 occurrences of
        // `ExifRemover.Tests.FixtureFactory.X()` calls in
        // SelfTest/Program.cs, all using the fully-qualified namespace
        // prefix. The SelfTest project has the `ExifRemover.Tests`
        // namespace available (it embeds FixtureFactory.cs via
        // `<Compile Include>`), so the prefix is unnecessary noise.
        // The fix added `using ExifRemover.Tests;` to the top of
        // Program.cs and replaced all 16 occurrences with the bare
        // `FixtureFactory.X()` form.
        //
        // This test pins the contract: a future commit that
        // re-introduces the verbose `ExifRemover.Tests.FixtureFactory.`
        // prefix (e.g. a merge conflict that reverts the using
        // directive) would fail the absence check, and a future commit
        // that drops the `using ExifRemover.Tests;` directive would
        // fail the presence check.
        var path = LocateSelfTest();
        Assert.True(File.Exists(path),
            $"Cannot find Program.cs at {path}.");

        var source = File.ReadAllText(path);

        // 1. The `using ExifRemover.Tests;` directive must be present.
        //    We check the comment-stripped source so a future commit
        //    that adds the using inside a comment (e.g. a TODO note)
        //    doesn't accidentally satisfy the assertion.
        var stripped = StripComments(source);
        Assert.Contains("using ExifRemover.Tests;", stripped);

        // 2. The verbose `ExifRemover.Tests.FixtureFactory.` prefix
        //    must NOT appear anywhere in the source. A regression that
        //    re-introduces even one prefixed call would fail this
        //    check with a clear "0 occurrences" diagnostic.
        Assert.DoesNotContain("ExifRemover.Tests.FixtureFactory.", source);

        // 3. Sanity check: at least 1 bare `FixtureFactory.X()` call
        //    exists (otherwise the using is unused and the fix is
        //    incomplete). The pre-fix code had 16 such calls after
        //    dropping the prefix; the post-fix code has the same 16.
        Assert.Contains("FixtureFactory.", source);
    }

    private static string LocateSelfTest()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "ExifRemover.SelfTest", "Program.cs");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return Path.Combine(dir, "src", "ExifRemover.SelfTest", "Program.cs");
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "ExifRemover.SelfTest", "Program.cs");
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (unlike the M2.20.28 D89 / M2.20.32 D94 stripper which empties
    /// them). Same pattern as the M2.20.26 D85
    /// <c>OverlayViewModelShapeTests</c> helper and the M2.20.34 D96
    /// <c>OverlayViewModelStatusTextTests</c> helper.
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
