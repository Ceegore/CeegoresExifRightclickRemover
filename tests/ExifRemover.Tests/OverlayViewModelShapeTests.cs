using System.IO;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for <c>src/ExifRemover.App/OverlayViewModel.cs</c>.
/// The test project can't include the App's WPF-bound source via
/// <c>&lt;Compile Include&gt;</c> (no WPF in net8.0), so we read the file
/// as text and grep for the patterns we want to pin. This is the same
/// "static check" pattern as the R17 audit's <c>TestNo&lt;Pattern&gt;</c>
/// tests in the SteamReviewTool project.
/// </summary>
public class OverlayViewModelShapeTests
{
    [Fact]
    public void OverlayViewModel_DoesNotDeclareDeadAllPathsField()
    {
        // D85 (M2.20.26): the pre-fix code declared
        //   `private readonly List<string> _allPaths;`
        // in OverlayViewModel. The field was assigned in the constructor
        // (via `paths.ToList()`) and then iterated two lines later, but
        // it was never read anywhere else in the codebase. The field is
        // a textbook R17-2 (dead code) finding — a private field that
        // survived multiple audit rounds because it was never exercised
        // outside the constructor. The fix removed both the field and
        // the `paths.ToList()` allocation, iterating the `paths` parameter
        // directly. This test pins the contract: a future commit that
        // re-introduces the dead field would fail this test, forcing a
        // conscious decision about whether the field is actually needed.
        var path = LocateOverlayViewModel();
        Assert.True(File.Exists(path),
            $"Cannot find OverlayViewModel.cs at {path}. The test lives in tests/ExifRemover.Tests/ and expects the file at src/ExifRemover.App/OverlayViewModel.cs relative to the repo root.");

        var source = File.ReadAllText(path);

        // Strip line comments so a regression that re-introduces the
        // field inside a comment doesn't accidentally pass the test.
        // (The R16 lesson: a regression that re-introduces the bug in
        // the code will still fire the assertion, but the comment
        // explanation does not. We strip the comments here for parity.)
        var stripped = StripComments(source);

        Assert.DoesNotContain("_allPaths", stripped);
    }

    [Fact]
    public void OverlayViewModel_DoesNotDeclareDeadByPathField()
    {
        // D88 (M2.20.27): the pre-fix code declared a
        //   `private readonly Dictionary<string, FileEntryViewModel> _byPath = new(...);`
        // field on OverlayViewModel. The field was used in the constructor
        // for the D78 case-insensitive dedup (ContainsKey + indexer-set),
        // but never read anywhere else. Same R17-2 (dead field) pattern as
        // D85 (`_allPaths`) and D82 (`StripResult.Warning`): a private field
        // that survived multiple audit rounds because it was never exercised
        // outside the constructor. The fix moved the dictionary into the
        // constructor as a local `seen` variable. This test pins the
        // contract: a future commit that re-introduces the field as a
        // private member would fail this test, forcing a conscious decision.
        var path = LocateOverlayViewModel();
        Assert.True(File.Exists(path),
            $"Cannot find OverlayViewModel.cs at {path}.");

        var source = File.ReadAllText(path);
        var stripped = StripComments(source);

        // The field declaration pattern: `private ... _byPath`. The
        // constructor's local `seen` is fine (a different name). We assert
        // the specific field name is NOT present as a private member
        // declaration.
        Assert.DoesNotContain("_byPath", stripped);
    }

    private static string LocateOverlayViewModel()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "ExifRemover.App", "OverlayViewModel.cs");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return Path.Combine(dir, "src", "ExifRemover.App", "OverlayViewModel.cs");
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "ExifRemover.App", "OverlayViewModel.cs");
    }

    /// <summary>
    /// Naive comment stripper: removes <c>//</c> line comments and
    /// <c>/* ... */</c> block comments. Not a full C# parser — the
    /// R16 lesson was that we only need to strip comments to make
    /// substring assertions robust against regressions that re-add the
    /// pattern in a comment. A full parser would over-engineer this.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            // Line comment: // ... \n
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            // Block comment: /* ... */
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            // String literal: "..." — keep contents (don't strip
            // substrings inside strings, in case a regression uses
            // "_allPaths" in a message).
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
