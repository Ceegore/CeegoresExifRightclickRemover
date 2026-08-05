using System.IO;
using System.Linq;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Source-shape regression tests for <c>src/ExifRemover.App/AboutWindow.xaml.cs</c>.
/// The test project can't include the App's WPF-bound source via
/// <c>&lt;Compile Include&gt;</c> (no WPF in net8.0), so we read the file
/// as text and grep for the patterns we want to pin. Same "static check"
/// pattern as the R17 audit's <c>TestNo&lt;Pattern&gt;</c> tests in the
/// SteamReviewTool project and the M2.20.24/M2.20.25/M2.20.26/M2.20.27
/// source-shape tests in this project.
/// </summary>
public class AboutWindowShapeTests
{
    [Fact]
    public void AboutWindow_HyperlinkRequestNavigate_DoesNotSwallowExceptionSilently()
    {
        // D89 (M2.20.28): the pre-fix code had a bare `catch { }` in
        // Hyperlink_RequestNavigate that silently swallowed any exception
        // from Process.Start. This is the R17-3 pattern (silent error
        // swallow on a user-facing path). A user clicking the hyperlink
        // got no feedback if the launch failed (e.g. no default browser
        // configured). The fix catches the specific exception class and
        // shows a MessageBox with the error message. This test pins the
        // contract: a future commit that re-introduces the bare `catch { }`
        // would fail this test, forcing a conscious decision.
        //
        // The R16 lesson: a regression that re-introduces the bug in
        // the code will still fire the assertion, but the comment
        // explanation does not. We use substring asserts on
        // comment-stripped source.
        var path = LocateAboutWindow();
        Assert.True(File.Exists(path),
            $"Cannot find AboutWindow.xaml.cs at {path}.");

        var source = File.ReadAllText(path);
        var stripped = StripCommentsAndStrings(source);

        // 1. The Hyperlink_RequestNavigate method must exist.
        Assert.Contains("Hyperlink_RequestNavigate", stripped);

        // 2. The method body must NOT contain a bare `catch { }` or
        //    `catch` (no exception class). The fix uses
        //    `catch (Exception ex)` and shows a MessageBox.
        //
        //    The pattern `catch\s*\{` matches a `catch` followed by
        //    optional whitespace, then a `{` (the catch body). It
        //    matches `catch {` and `catch\n{` but NOT
        //    `catch (Exception ex) {` (because the `(` is between
        //    `catch` and `{`). Note: the regex engine is greedy with
        //    `\s*`, but `\s` only matches whitespace, so for
        //    `catch (Exception ex) {` the engine cannot backtrack past
        //    the `(` to find `{` — the match fails. The earlier
        //    attempt with `catch\s*(?!\()` failed because the
        //    negative lookahead backtracked past the consumed
        //    whitespace.
        var catchPattern = new System.Text.RegularExpressions.Regex(@"catch\s*\{");
        var bareCatches = catchPattern.Matches(stripped);
        Assert.Empty(bareCatches);

        // 3. The catch block must show a MessageBox (so the user gets
        //    feedback). The fix uses `MessageBox.Show` with a hint.
        //    We assert the substring is present anywhere in the file
        //    (in this method, but the contract is the same).
        Assert.Contains("MessageBox.Show", stripped);
    }

    private static string LocateAboutWindow()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "ExifRemover.App", "AboutWindow.xaml.cs");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return Path.Combine(dir, "src", "ExifRemover.App", "AboutWindow.xaml.cs");
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "ExifRemover.App", "AboutWindow.xaml.cs");
    }

    /// <summary>
    /// Naive comment-and-string stripper: removes <c>//</c> line
    /// comments, <c>/* ... */</c> block comments, and string literals
    /// (so substrings inside comments or string constants don't
    /// accidentally satisfy the assertion). R16 lesson: a regression
    /// that re-introduces the pattern in a comment would pass a naive
    /// substring check, so we strip both <c>//</c> and <c>/* */</c>
    /// comments and preserve string literals by emptying them out.
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
            // satisfy the assertion.
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
