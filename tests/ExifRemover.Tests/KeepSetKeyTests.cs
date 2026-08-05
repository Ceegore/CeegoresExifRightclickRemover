using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Tests for <see cref="KeepSetKey.For"/>. D93 (M2.20.31): the pre-fix
/// code had this mapping as a 18-branch <c>if</c>-chain in
/// <c>OverlayViewModel.GetChunkKey</c> — a private static method in
/// the WPF-bound App layer that wasn't directly unit-testable from
/// the Engine test project. The branch-chain's correctness was
/// implied by the stripper tests + a comment, but never asserted.
/// After extraction to <see cref="KeepSetKey"/>, the mapping is a
/// data-driven dictionary in the Engine and the test project can
/// pin every contract directly.
/// </summary>
public class KeepSetKeyTests
{
    [Fact]
    public void For_ExifIfd0Group_ReturnsExifKey()
    {
        // The four EXIF IFD groups (IFD0, SubIFD, Interop, Thumbnail)
        // all collapse to a single "EXIF" key — the stripper's
        // behavior for an EXIF tag is profile-driven (Privacy /
        // Minimal / AllMetadata all drop it), so the grid doesn't
        // need to distinguish the IFDs.
        var entry = new MetadataEntry(MetadataGroups.ExifIfd0, "Make", "Canon", 5, true);
        Assert.Equal("EXIF", KeepSetKey.For(entry));
    }

    [Fact]
    public void For_ExifSubIfdGroup_ReturnsExifKey_ViaPrefix()
    {
        var entry = new MetadataEntry(MetadataGroups.ExifSubIfd, "ISO", "100", 3, true);
        Assert.Equal("EXIF", KeepSetKey.For(entry));
    }

    [Fact]
    public void For_ExifInteropGroup_ReturnsExifKey_ViaPrefix()
    {
        var entry = new MetadataEntry(MetadataGroups.ExifInterop, "InteropIndex", "R98", 7, true);
        Assert.Equal("EXIF", KeepSetKey.For(entry));
    }

    [Fact]
    public void For_ExifThumbnailGroup_ReturnsExifKey_ViaPrefix()
    {
        // D77 (M2.20.20): the ExifThumbnailDirectory is in the
        // "EXIF Thumbnail" group. The grid should treat it the
        // same as the other EXIF IFDs (single "EXIF" key) because
        // the stripper drops the entire APP1 segment including
        // the thumbnail.
        var entry = new MetadataEntry(MetadataGroups.ExifThumbnail, "Compression", "JPEG", 3, true);
        Assert.Equal("EXIF", KeepSetKey.For(entry));
    }

    [Theory]
    [InlineData(MetadataGroups.Iptc, "IPTC")]
    [InlineData(MetadataGroups.Xmp, "XMP")]
    [InlineData(MetadataGroups.Icc, "ICC")]
    [InlineData(MetadataGroups.JpegComment, "COM")]
    [InlineData(MetadataGroups.PngText, "PNGTEXT")]
    [InlineData(MetadataGroups.PngTime, "PNGTIME")]
    [InlineData(MetadataGroups.PngExif, "PNGEXIF")]
    [InlineData(MetadataGroups.PngIccp, "PNGICCP")]
    [InlineData(MetadataGroups.PngHist, "PNGHIST")]
    [InlineData(MetadataGroups.PngSrgb, "PNGSRGB")]
    [InlineData(MetadataGroups.PngChrm, "PNGCHRM")]
    [InlineData(MetadataGroups.PngGama, "PNGGAMA")]
    [InlineData(MetadataGroups.PngPhys, "PNGPHYS")]
    [InlineData(MetadataGroups.PngBkgd, "PNGBKGD")]
    [InlineData(MetadataGroups.PngSbit, "PNGSBIT")]
    [InlineData(MetadataGroups.PngTrns, "PNGTRNS")]
    public void For_KnownGroup_ReturnsExpectedKey(string group, string expectedKey)
    {
        // Each MetadataGroups constant has exactly one expected
        // keep-set key. The [Theory] rows cover every constant
        // in the dictionary.
        var entry = new MetadataEntry(group, "TestName", "TestValue", 1, false);
        Assert.Equal(expectedKey, KeepSetKey.For(entry));
    }

    [Fact]
    public void For_PngUnknownGroup_ReturnsPngUnknownKey_NoSpace()
    {
        // D2 (M2.20.2): the pre-fix branch-chain had an explicit
        // case for PngUnknown because the fallthrough
        // `return entry.Group` would yield "PNG Unknown" (with a
        // space), which never matches the keep-set key
        // "PNGUNKNOWN" (no space). An unknown chunk would show
        // "Would be removed" in the grid even though the stripper
        // keeps it — an H2 lie. The dictionary entry pins the
        // no-space contract.
        var entry = new MetadataEntry(MetadataGroups.PngUnknown, "CustomChunk", "value", 1, false);
        Assert.Equal("PNGUNKNOWN", KeepSetKey.For(entry));
    }

    [Fact]
    public void For_UnknownGroup_ReturnsGroupUnchanged_FailSafe()
    {
        // D51 fail-safe reasoning: a future MetadataExtractor
        // directory that doesn't match any of the explicit cases
        // falls through to `return entry.Group`. The grid will
        // see "Would be removed" for that entry, which is the
        // safe default (we can't be sure the stripper drops the
        // underlying bytes, so we mark it as not in the
        // keep-set). Better a false positive (shows "Would be
        // removed" for a chunk the stripper keeps) than a false
        // negative (shows "Would be kept" for a chunk the
        // stripper drops — an H2 lie).
        var entry = new MetadataEntry("SomeFutureGroup", "FutureTag", "value", 1, false);
        Assert.Equal("SomeFutureGroup", KeepSetKey.For(entry));
    }

    [Fact]
    public void For_EmptyGroup_ReturnsEmptyString()
    {
        // Defensive: an entry with an empty group string. The
        // prefix-match check returns false (empty string doesn't
        // start with "EXIF"), the dictionary lookup misses, and
        // the fallthrough returns the empty group. The grid will
        // treat this as "not in keep-set" — same fail-safe path
        // as the unknown-group test.
        var entry = new MetadataEntry(string.Empty, "NoGroup", "value", 1, false);
        Assert.Equal(string.Empty, KeepSetKey.For(entry));
    }
}
