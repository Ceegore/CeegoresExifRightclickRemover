using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Tests for <see cref="PathFilter"/>. D12: the App's <c>FilterSupported</c> used to
/// swallow every exception with a blanket <c>catch { }</c> — so an invalid path (one
/// containing a character illegal on the platform, e.g. a null byte) was silently
/// dropped and never appeared in the dropped-files notice. PathFilter splits the two
/// failure modes (unsupported extension vs. invalid path) and reports each with a
/// reason; this suite pins that contract.
/// </summary>
public class PathFilterTests
{
    [Fact]
    public void FilterImagePaths_KeepsSupportedExtensions()
    {
        var result = PathFilter.FilterImagePaths(new[] { "a.jpg", "b.jpeg", "C.PNG" });
        Assert.Equal(3, result.Kept.Count);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void FilterImagePaths_DropsUnsupportedExtensions_WithReason()
    {
        var result = PathFilter.FilterImagePaths(new[] { "a.txt", "b.docx", "c" });
        Assert.Empty(result.Kept);
        Assert.Equal(3, result.Dropped.Count);
        Assert.All(result.Dropped, d => Assert.Equal("unsupported file type", d.Reason));
    }

    [Fact]
    public void FilterImagePaths_HandlesNullBytePath_AsInvalid()
    {
        // Path.GetExtension throws ArgumentException for paths containing a null byte
        // (the path "looks" like a .jpg to the user but the OS rejects it). The
        // previous code swallowed this and reported it as "unsupported file type"
        // (technically true but misleading — the extension is supported, the path
        // is bad). PathFilter reports it as "invalid path" with the actual exception
        // message so the user knows the path is malformed, not the wrong type.
        var bad = "photo\0.jpg";
        var result = PathFilter.FilterImagePaths(new[] { bad });
        Assert.Empty(result.Kept);
        Assert.Single(result.Dropped);
        Assert.Equal(bad, result.Dropped[0].Path);
        Assert.StartsWith("invalid path", result.Dropped[0].Reason);
    }

    [Fact]
    public void FilterImagePaths_EmptyOrNullPath_IsDroppedAsEmpty()
    {
        var result = PathFilter.FilterImagePaths(new string?[] { null, "" }!);
        Assert.Empty(result.Kept);
        Assert.Equal(2, result.Dropped.Count);
        Assert.All(result.Dropped, d => Assert.Equal("empty path", d.Reason));
    }

    [Fact]
    public void FilterImagePaths_MixedInput_KeepsKeptAndDropsDropped()
    {
        var result = PathFilter.FilterImagePaths(new[] { "a.jpg", "b.txt", "c.png", "d\0.jpg" });
        // Kept: a.jpg, c.png (full paths).
        Assert.Equal(2, result.Kept.Count);
        // Dropped: b.txt (unsupported), d\0.jpg (invalid path).
        Assert.Equal(2, result.Dropped.Count);
        var unsupported = result.Dropped.Single(d => d.Path == "b.txt");
        Assert.Equal("unsupported file type", unsupported.Reason);
        var invalid = result.Dropped.Single(d => d.Path == "d\0.jpg");
        Assert.StartsWith("invalid path", invalid.Reason);
    }

    [Fact]
    public void FilterImagePaths_NullInput_DoesNotThrow()
    {
        // Defensive: App's SplitArgs guarantees non-null entries, but PathFilter
        // is a public utility and shouldn't NRE on a null IEnumerable.
        var result = PathFilter.FilterImagePaths((IEnumerable<string>)null!);
        Assert.Empty(result.Kept);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void IsSupportedImageExtension_AllThreeExtensions_AreSupported()
    {
        Assert.True(PathFilter.IsSupportedImageExtension(".jpg"));
        Assert.True(PathFilter.IsSupportedImageExtension(".jpeg"));
        Assert.True(PathFilter.IsSupportedImageExtension(".png"));
        Assert.True(PathFilter.IsSupportedImageExtension(".JPG"));
        Assert.True(PathFilter.IsSupportedImageExtension(".Jpeg"));
    }

    [Fact]
    public void IsSupportedImageExtension_NonImageExtensions_AreNotSupported()
    {
        Assert.False(PathFilter.IsSupportedImageExtension(".txt"));
        Assert.False(PathFilter.IsSupportedImageExtension(".docx"));
        Assert.False(PathFilter.IsSupportedImageExtension(""));
        Assert.False(PathFilter.IsSupportedImageExtension(".webp"));
    }

    [Fact]
    public void FilterImagePaths_TrailingSpaceInExtension_KeepsTheFile()
    {
        // D31: a file named "photo.jpg " (trailing space) has Path.GetExtension
        // return ".jpg " (with space), which the previous strict comparison
        // rejected as "unsupported file type". Such files are valid images with a
        // path oddity (command-line tools can create them; Windows Explorer
        // generally can't, but PowerShell can). The fix trims trailing whitespace
        // from the extension before checking, so the file is kept and reported
        // with its full path.
        var result = PathFilter.FilterImagePaths(new[] { "photo.jpg ", "clean.png\t" });
        Assert.Equal(2, result.Kept.Count);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void IsSupportedImageExtension_TrimsTrailingWhitespace()
    {
        // D31: the public IsSupportedImageExtension helper is also more forgiving
        // — ".jpg " (with space) is now considered supported. The other direction
        // (leading space) is not normalized; ". jpg" is not a valid extension.
        Assert.True(PathFilter.IsSupportedImageExtension(".jpg "));
        Assert.True(PathFilter.IsSupportedImageExtension(".png\t"));
        Assert.True(PathFilter.IsSupportedImageExtension(".jpeg   "));
        Assert.False(PathFilter.IsSupportedImageExtension(". jpg"));
        Assert.False(PathFilter.IsSupportedImageExtension("jpg")); // no dot
    }
}
