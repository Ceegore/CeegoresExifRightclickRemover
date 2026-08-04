using System.IO;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Tests for <see cref="AtomicFile.NextNonClashingPath"/>. This is the helper both
/// strippers use to avoid overwriting an existing <c>&lt;name&gt;_stripped.&lt;ext&gt;</c>
/// file when the user runs the strip a second time on the same source. The existing
/// stripper tests exercise it indirectly (via "input == output, overwrite=false" cases),
/// but no direct unit test pinned the helper's contract — D53 in M2.20.9 closed the gap.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"er-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void NextNonClashingPath_DesiredFree_ReturnsDesiredPath()
    {
        var desired = Path.Combine(_tempDir, "photo.jpg");
        // Sanity: the file does not exist.
        Assert.False(File.Exists(desired));

        var result = AtomicFile.NextNonClashingPath(desired);

        Assert.Equal(desired, result);
    }

    [Fact]
    public void NextNonClashingPath_DesiredTaken_ReturnsFirstSibling()
    {
        var desired = Path.Combine(_tempDir, "photo.jpg");
        File.WriteAllBytes(desired, new byte[] { 0x01 });

        var result = AtomicFile.NextNonClashingPath(desired);

        // The expected sibling: "photo (2).jpg" in the same dir.
        var expected = Path.Combine(_tempDir, "photo (2).jpg");
        Assert.Equal(expected, result);
        // And the desired file is unchanged.
        Assert.True(File.Exists(desired));
    }

    [Fact]
    public void NextNonClashingPath_DesiredAndFirstSiblingTaken_ReturnsSecondSibling()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "photo.jpg"), new byte[] { 0x01 });
        File.WriteAllBytes(Path.Combine(_tempDir, "photo (2).jpg"), new byte[] { 0x02 });

        var result = AtomicFile.NextNonClashingPath(Path.Combine(_tempDir, "photo.jpg"));

        Assert.Equal(Path.Combine(_tempDir, "photo (3).jpg"), result);
    }

    [Fact]
    public void NextNonClashingPath_NoExtension_StillProducesSibling()
    {
        // A path with no extension — the helper must still increment with " (2)" (not " (2)" + "").
        File.WriteAllBytes(Path.Combine(_tempDir, "noext"), new byte[] { 0x01 });

        var result = AtomicFile.NextNonClashingPath(Path.Combine(_tempDir, "noext"));

        Assert.Equal(Path.Combine(_tempDir, "noext (2)"), result);
    }

    [Fact]
    public void NextNonClashingPath_HolesInSequence_ReusesTheFirstHole()
    {
        // photo.jpg, photo (2).jpg, photo (4).jpg exist — photo (3) is free.
        File.WriteAllBytes(Path.Combine(_tempDir, "photo.jpg"), new byte[] { 0x01 });
        File.WriteAllBytes(Path.Combine(_tempDir, "photo (2).jpg"), new byte[] { 0x02 });
        File.WriteAllBytes(Path.Combine(_tempDir, "photo (4).jpg"), new byte[] { 0x04 });

        var result = AtomicFile.NextNonClashingPath(Path.Combine(_tempDir, "photo.jpg"));

        // The helper walks i=2, 3, …; at i=3 the file does not exist, so it returns
        // "photo (3).jpg" (reuses the first hole, not the next-after-max).
        Assert.Equal(Path.Combine(_tempDir, "photo (3).jpg"), result);
    }
}
