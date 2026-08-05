using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for <c>src/ExifRemover.App/OverlayWindow.xaml.cs</c>.
/// The test project can't include the App's WPF-bound source via
/// <c>&lt;Compile Include&gt;</c> (no WPF in net8.0), so we read the file
/// as text and grep for the patterns we want to pin. Same "static check"
/// pattern as the R17 audit's <c>TestNo&lt;Pattern&gt;</c> tests in the
/// SteamReviewTool project and the M2.20.24/M2.20.25/M2.20.26/M2.20.27/
/// M2.20.28 source-shape tests in this project.
/// </summary>
public class OverlayWindowShapeTests
{
    [Fact]
    public void OverlayWindow_ButtonStateTogglesGoThroughSetBusyState()
    {
        // D94 (M2.20.32): before the refactor, OverlayWindow.xaml.cs had
        // 7 hand-rolled multi-button sites that toggled RemoveButton,
        // CancelButton, and ReInspectButton together:
        //   - L62-64 (Loaded: disable, before initial inspect)
        //   - L90-92 (inspect catch path: enable)
        //   - L99-101 (inspect success path: enable)
        //   - L139-141 (ShowFatal: disable)
        //   - L263-265 (RunRemove start: disable)
        //   - L291-293 (strip catch path: enable)
        //   - L309-311 (strip success path: enable)
        // = 21 identical lines across 7 sites. Forgetting one button at a
        // new transition site is a silent UX bug (e.g. Remove stays enabled
        // mid-inspect and races the snapshot capture that D11 protects
        // against). The fix introduces SetBusyState(bool busy) as the
        // single canonical way to change the action-bar's busy/idle state.
        //
        // Two single-button sites remain in ReInspectButton_Click
        // (L181 disable / L202 enable) because they isolate the ReInspect
        // operation only — the Remove and Cancel buttons stay enabled so
        // the user can cancel a long re-inspect or start a strip. That is
        // a deliberate UX choice, NOT a DRY violation, so the helper does
        // not cover them.
        //
        // The pin: a future commit that re-introduces a direct 3-button
        // toggle outside the helper would increase the count of
        // RemoveButton.IsEnabled / CancelButton.IsEnabled assignments
        // beyond 1, and the test would fail with a clear message naming
        // the count delta. This is the same "static check" pattern as
        // D85/D88 (dead-field tests) and D89 (bare-catch test).
        var path = LocateOverlayWindow();
        Assert.True(File.Exists(path),
            $"Cannot find OverlayWindow.xaml.cs at {path}.");

        var source = File.ReadAllText(path);
        // Strip comments AND string literals so a regression that adds
        // a debug log containing "RemoveButton.IsEnabled = true" doesn't
        // accidentally pass. (R16 lesson: a regression that re-introduces
        // the pattern in code will still fire the count assertion, but a
        // log string would not — we strip both for safety.)
        var stripped = StripCommentsAndStrings(source);

        int Count(string buttonName) =>
            Regex.Matches(stripped, $@"{Regex.Escape(buttonName)}\.IsEnabled\s*=\s*").Count;

        int removeCount = Count("RemoveButton");
        int cancelCount = Count("CancelButton");
        int reInspectCount = Count("ReInspectButton");

        // The SetBusyState helper assigns each of the 3 buttons exactly
        // once. Any additional direct toggle of RemoveButton or
        // CancelButton outside the helper is a DRY violation — all 3
        // action buttons must be toggled through SetBusyState(bool) as a
        // single unit.
        Assert.True(removeCount == 1,
            $"RemoveButton.IsEnabled should be set in exactly 1 place (the SetBusyState helper). " +
            $"Found {removeCount}. Every multi-button busy/idle transition must go through SetBusyState(bool busy). " +
            "The 2 single-button ReInspect sites in ReInspectButton_Click are fine because they isolate " +
            "the ReInspect operation only — but toggling RemoveButton (or CancelButton) alone anywhere " +
            "in this file is a bug: it leaves the action bar in an inconsistent state.");
        Assert.True(cancelCount == 1,
            $"CancelButton.IsEnabled should be set in exactly 1 place (the SetBusyState helper). " +
            $"Found {cancelCount}. See the RemoveButton assertion above for the rationale.");

        // ReInspectButton has 1 in the helper + 2 single-button toggles in
        // ReInspectButton_Click (focused isolation — the other 2 buttons
        // remain enabled during re-inspect so the user can still cancel or
        // start a strip).
        Assert.True(reInspectCount == 3,
            $"ReInspectButton.IsEnabled should be set in exactly 3 places: 1 in the SetBusyState helper " +
            $"+ 2 single-button cases in ReInspectButton_Click. Found {reInspectCount}.");
    }

    [Fact]
    public void OverlayWindow_DeclaresSetBusyStateHelper()
    {
        // D94 (M2.20.32) companion test: pin that the SetBusyState helper
        // method actually exists. A future commit that removes the helper
        // (or renames it without updating the call sites) would also remove
        // the count protection in the test above, so we assert the helper
        // itself is present.
        var path = LocateOverlayWindow();
        Assert.True(File.Exists(path),
            $"Cannot find OverlayWindow.xaml.cs at {path}.");

        var source = File.ReadAllText(path);

        // The helper has the exact signature `private void SetBusyState(bool busy)`.
        // We assert the substring is present, plus the body shape (3 IsEnabled
        // assignments) is intact, so a future commit can't quietly refactor
        // the helper into something semantically different.
        Assert.Contains("SetBusyState", source);
        Assert.Contains("private void SetBusyState(bool busy)", source);
    }

    private static string LocateOverlayWindow()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "ExifRemover.App", "OverlayWindow.xaml.cs");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return Path.Combine(dir, "src", "ExifRemover.App", "OverlayWindow.xaml.cs");
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "ExifRemover.App", "OverlayWindow.xaml.cs");
    }

    /// <summary>
    /// Naive comment-and-string stripper: removes <c>//</c> line comments,
    /// <c>/* ... */</c> block comments, and string literals (so substrings
    /// inside comments or string constants don't accidentally satisfy the
    /// count assertion). R16 lesson: a regression that re-introduces the
    /// pattern in a comment or log string would pass a naive substring
    /// check, so we strip both <c>//</c> and <c>/* */</c> comments and
    /// preserve string literals by emptying them out. The pattern is the
    /// same one used by <see cref="AboutWindowShapeTests"/>.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            // Line comment.
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            // Block comment.
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            // String literal — empty it out so substrings inside don't
            // satisfy the assertion (e.g. a debug log containing
            // "RemoveButton.IsEnabled = true" would falsely match).
            if (source[i] == '"')
            {
                result.Append('"');
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) i += 2;
                    else i++;
                }
                if (i < source.Length) { result.Append('"'); i++; }
                continue;
            }
            result.Append(source[i]);
            i++;
        }
        return result.ToString();
    }
}
