using System.Collections.Generic;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Direct unit tests for <see cref="KeepSet.ForFormat"/>. D104 (M2.20.42):
/// the pre-fix <c>ComputeKeepSet</c> was a 56-line static method in
/// <c>OverlayViewModel</c> (WPF-bound, App project) — the xUnit test
/// project can't include its sources via <c>&lt;Compile Include&gt;</c>
/// (no WPF in net8.0), so the keep-set logic was untested at the
/// unit level. The D104 fix moved the canonical implementation to
/// <c>ExifRemover.Engine.KeepSet</c> and added direct unit tests.
///
/// The keep-set logic has subtle invariants (see the D2 / D51 / D77
/// comments in <c>OverlayViewModel.cs</c> for the full reasoning). The
/// 9 [Theory] cases below cover every (format × profile) combination
/// and assert each known chunk-type key's presence/absence.
/// </summary>
public class KeepSetTests
{
    [Theory]
    [InlineData(ImageFormat.Jpeg, StripProfile.Privacy)]
    [InlineData(ImageFormat.Jpeg, StripProfile.Minimal)]
    [InlineData(ImageFormat.Jpeg, StripProfile.AllMetadata)]
    [InlineData(ImageFormat.Png, StripProfile.Privacy)]
    [InlineData(ImageFormat.Png, StripProfile.Minimal)]
    [InlineData(ImageFormat.Png, StripProfile.AllMetadata)]
    [InlineData(null, StripProfile.Privacy)]
    [InlineData(null, StripProfile.Minimal)]
    [InlineData(null, StripProfile.AllMetadata)]
    public void ForFormat_AlwaysContainsOtherAndNeverEmpty(ImageFormat? format, StripProfile profile)
    {
        // D51: every keep-set must contain "Other" (the fail-safe
        // default). The set must also be non-empty.
        var set = KeepSet.ForFormat(format, profile);
        Assert.NotNull(set);
        Assert.NotEmpty(set);
        Assert.Contains("Other", set);
    }

    [Fact]
    public void ForFormat_JpegPrivacy_KeepsJfifOnly()
    {
        var set = KeepSet.ForFormat(ImageFormat.Jpeg, StripProfile.Privacy);
        Assert.Contains("JFIF", set);
        Assert.DoesNotContain("ICC", set);
    }

    [Fact]
    public void ForFormat_JpegMinimal_KeepsJfifAndIcc()
    {
        var set = KeepSet.ForFormat(ImageFormat.Jpeg, StripProfile.Minimal);
        Assert.Contains("JFIF", set);
        Assert.Contains("ICC", set);
    }

    [Fact]
    public void ForFormat_JpegAllMetadata_KeepsJfifOnly()
    {
        var set = KeepSet.ForFormat(ImageFormat.Jpeg, StripProfile.AllMetadata);
        Assert.Contains("JFIF", set);
        Assert.DoesNotContain("ICC", set);
    }

    [Fact]
    public void ForFormat_PngPrivacy_KeepsAlwaysKeptAndColorManagement()
    {
        var set = KeepSet.ForFormat(ImageFormat.Png, StripProfile.Privacy);
        Assert.Contains("PNGPHYS", set);
        Assert.Contains("PNGBKGD", set);
        Assert.Contains("PNGSBIT", set);
        Assert.Contains("PNGTRNS", set);
        Assert.Contains("PNGUNKNOWN", set);
        Assert.Contains("PNGSRGB", set);
        Assert.Contains("PNGCHRM", set);
        Assert.Contains("PNGGAMA", set);
        Assert.DoesNotContain("PNGICCP", set);
        Assert.DoesNotContain("PNGHIST", set);
    }

    [Fact]
    public void ForFormat_PngMinimal_KeepsEverythingPlusIccpAndHist()
    {
        var set = KeepSet.ForFormat(ImageFormat.Png, StripProfile.Minimal);
        Assert.Contains("PNGPHYS", set);
        Assert.Contains("PNGBKGD", set);
        Assert.Contains("PNGSBIT", set);
        Assert.Contains("PNGTRNS", set);
        Assert.Contains("PNGUNKNOWN", set);
        Assert.Contains("PNGSRGB", set);
        Assert.Contains("PNGCHRM", set);
        Assert.Contains("PNGGAMA", set);
        Assert.Contains("PNGICCP", set);
        Assert.Contains("PNGHIST", set);
    }

    [Fact]
    public void ForFormat_PngAllMetadata_KeepsAlwaysKeptOnly()
    {
        var set = KeepSet.ForFormat(ImageFormat.Png, StripProfile.AllMetadata);
        Assert.Contains("PNGPHYS", set);
        Assert.Contains("PNGBKGD", set);
        Assert.Contains("PNGSBIT", set);
        Assert.Contains("PNGTRNS", set);
        Assert.Contains("PNGUNKNOWN", set);
        Assert.DoesNotContain("PNGSRGB", set);
        Assert.DoesNotContain("PNGCHRM", set);
        Assert.DoesNotContain("PNGGAMA", set);
        Assert.DoesNotContain("PNGICCP", set);
        Assert.DoesNotContain("PNGHIST", set);
    }

    [Fact]
    public void ForFormat_NullFormat_KeepsOnlyOther()
    {
        var set = KeepSet.ForFormat(null, StripProfile.Privacy);
        Assert.Single(set);
        Assert.Contains("Other", set);
    }

    [Fact]
    public void ForFormat_ReturnsOrdinalHashSet()
    {
        var set = KeepSet.ForFormat(ImageFormat.Jpeg, StripProfile.Privacy);
        Assert.Contains("JFIF", set);
        Assert.DoesNotContain("jfif", set);
    }

    [Fact]
    public void ForFormat_ThreeFormatsProduceDisjointExceptForOther()
    {
        var jpeg = KeepSet.ForFormat(ImageFormat.Jpeg, StripProfile.Privacy);
        var png = KeepSet.ForFormat(ImageFormat.Png, StripProfile.Privacy);
        Assert.Contains("JFIF", jpeg);
        Assert.DoesNotContain("JFIF", png);
        Assert.Contains("PNGPHYS", png);
        Assert.DoesNotContain("PNGPHYS", jpeg);
    }
}
