using System.IO;
using System.Linq;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Edge-case regression tests for how the inspector groups and labels
/// ICC profile metadata. PNG iCCP chunks surface BOTH a PngDirectory entry
/// (the chunk name) and an IccDirectory entry (the parsed ICC metadata).
/// The pre-fix MapGroup function mapped both to DIFFERENT groups — "PNG iCCP"
/// and "ICC Profile" — and the keep-set only added "PNGICCP" (PNG Minimal)
/// but NOT "ICC" for PNG files. The result: the SAME iCCP data appeared
/// as TWO entries with conflicting action-column labels under Minimal
/// (one "Would be kept", one "Would be removed"). Confusing and wrong.
/// </summary>
public class IccGroupingTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private string WriteTemp(byte[] bytes, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Inspect_PngWithIccpChunk_ReportsIccAndPngIccpOnce_NotDouble()
    {
        // Probe test: a PNG with a single iCCP chunk must surface the ICC
        // metadata exactly once — either as a PngIccp entry or as an Icc
        // entry, but NOT as both. The pre-fix code surfaced BOTH because
        // MapGroup mapped IccDirectory → "ICC Profile" and PngDirectory's
        // TagIccProfileName → "PNG iCCP" with no de-duplication between
        // them. Under Minimal the PngIccp entry shows as "Would be kept"
        // and the Icc entry shows as "Would be removed" for the same
        // iCCP chunk — a clear UI lie.
        //
        // The fix: when both a PngDirectory-tagged iCCP entry and an
        // IccDirectory entry refer to the same data, suppress the
        // duplicate. We assert exactly-one here so a future MetadataExtractor
        // version that adds another duplicate layer fails this test too.
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-iccp-{Guid.NewGuid():N}.png");

        var inspection = MetadataInspector.Inspect(src);

        var iccEntries = inspection.Entries
            .Where(e => e.Group == MetadataGroups.Icc || e.Group == MetadataGroups.PngIccp)
            .ToList();

        // The PngIccp entry IS surfaced (this is the standard entry). The
        // question is whether an additional Icc entry is also surfaced.
        // We allow EITHER a single PngIccp entry (correct) OR a single
        // Icc entry (also acceptable as long as the group maps to the
        // PNG path). We reject the case where BOTH are present.
        var pngIccp = iccEntries.Count(e => e.Group == MetadataGroups.PngIccp);
        var icc = iccEntries.Count(e => e.Group == MetadataGroups.Icc);

        Assert.Equal(1, pngIccp + icc);
        // Pin the canonical case: PngIccp is surfaced (with the standard
        // ICC-profile name metadata). If MetadataExtractor ever drops the
        // PngIccp entry, this test fails loudly so we can add an
        // explicit fallback in MetadataInspector.
        Assert.Equal(1, pngIccp);
        Assert.Equal(0, icc);
    }

    [Fact]
    public void Strip_PngWithIccpChunk_DoesNotDoubleStrip()
    {
        // End-to-end: the stripper must drop the iCCP chunk once, not
        // twice. The pre-fix code path was correct (the stripper walks
        // chunks once) but the inspector double-surfaced the same data.
        // A user clicking Remove would see a single "Would be removed"
        // entry after the fix (where the pre-fix user saw two entries
        // with conflicting actions).
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-iccp-out-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-iccp-stripped-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var post = MetadataInspector.Inspect(outPath);
        var iccCount = post.Entries
            .Count(e => e.Group == MetadataGroups.PngIccp || e.Group == MetadataGroups.Icc);
        Assert.Equal(0, iccCount);
    }
}
