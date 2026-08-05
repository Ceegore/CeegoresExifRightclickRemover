using System.IO;
using System.Text;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for the post-strip status text in
/// <c>src/ExifRemover.App/OverlayViewModel.cs</c>. The test project
/// can't include the App's WPF-bound source via <c>&lt;Compile Include&gt;</c>
/// (no WPF in net8.0), so we read the file as text and grep for the
/// patterns we want to pin. Same "static check" pattern as the M2.20.24,
/// M2.20.25, M2.20.26, M2.20.27, M2.20.28, M2.20.32 source-shape tests
/// in this project.
/// </summary>
public class OverlayViewModelStatusTextTests
{
    [Fact]
    public void OverlayViewModel_PostStripStatusText_UsesPreStripSnapshotWording()
    {
        // D96 (M2.20.34): the pre-fix status text read
        //   $"{VisibleEntryCount} of {snap.Count} entries shown (last strip removed all)."
        // which was misleading when:
        //   (1) a filter was active — VisibleEntryCount < snap.Count, and
        //       "last strip removed all" doesn't explain the filter.
        //   (2) the keep-set was non-empty (e.g. Privacy profile keeps JFIF).
        //       "Removed all" implied the strip wiped every metadata entry,
        //       but the strip only removed the non-keep-set entries; keep-set
        //       entries would NOT have been removed.
        // The fix rephrases to "(pre-strip snapshot)" — terse and accurate:
        // the grid is showing the pre-strip entries (all marked "Would be
        // removed"), and the user infers the strip succeeded from the grid
        // state without a misleading claim about WHAT was removed.
        //
        // This test pins the contract: a future commit that re-introduces
        // the misleading "(last strip removed all)" wording would fail the
        // "old wording absent" assertion, and a future commit that drops
        // the new "(pre-strip snapshot)" wording would fail the "new
        // wording present" assertion. The two assertions cover both
        // directions of the contract.
        var path = LocateOverlayViewModel();
        Assert.True(File.Exists(path),
            $"Cannot find OverlayViewModel.cs at {path}.");

        var source = File.ReadAllText(path);
        // Strip comments ONLY (not string literals). The new wording
        // "(pre-strip snapshot)" lives inside a string literal (it's a
        // user-facing message), and stripping string literals would
        // delete the very substring we're trying to assert. The old
        // wording "last strip removed all" lives in a comment (the
        // explanation block we just added), and stripping comments
        // removes it. This is the M2.20.26 D85 `StripComments` pattern
        // (strips line + block comments, KEEPS string contents).
        var stripped = StripComments(source);

        // 1. New wording must be present. The full substring
        //    "entries shown (pre-strip snapshot)" is specific to the
        //    user-facing message format and unlikely to appear in any
        //    other context.
        Assert.Contains("entries shown (pre-strip snapshot)", stripped, System.StringComparison.OrdinalIgnoreCase);

        // 2. Old misleading wording must be absent. The comment-stripped
        //    source contains the new wording but not the old one.
        Assert.DoesNotContain("last strip removed all", stripped, System.StringComparison.OrdinalIgnoreCase);
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
    /// <c>/* ... */</c> block comments. KEEPS string literal contents
    /// (unlike the M2.20.28 D89 / M2.20.32 D94 stripper which empties
    /// them). Same pattern as the M2.20.26 D85 <c>OverlayViewModelShapeTests</c>
    /// helper. The trade-off: a debug log containing the old wording
    /// would falsely satisfy the absence check, but the new wording
    /// (which lives in a string literal) would falsely fail if we
    /// emptied string contents — and the test is primarily about
    /// confirming the new wording is present in the user-facing message.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
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
            // String literal: "..." — KEEP contents (the new wording
            // lives inside a string literal).
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
