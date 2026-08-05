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

    [Fact]
    public void ResolveTempPath_PutsTempFileInSameDirectory()
    {
        // D83 (M2.20.25): the pre-fix code had a private ResolveTempPath in BOTH
        // strippers. The two copies were byte-identical. After extraction to
        // AtomicFile.ResolveTempPath, both strippers call the shared helper and
        // the temp file lands in the same directory as the source. This test
        // pins the contract: a JPEG source at dir/foo.jpg produces a temp path
        // at dir/.<rest>.tmp, not at the process's CWD or at %TEMP%.
        var source = Path.Combine(_tempDir, "photo.jpg");
        var result = AtomicFile.ResolveTempPath(source);

        // Same directory.
        Assert.Equal(_tempDir, Path.GetDirectoryName(result));
    }

    [Fact]
    public void ResolveTempPath_IncludesOriginalFilename()
    {
        // The temp file should embed the original filename so an orphaned temp
        // file (e.g. after a process crash) is attributable to the source via
        // the name alone — even if the .tmp extension is preserved and the file
        // is hidden by the leading dot.
        var source = Path.Combine(_tempDir, "myphoto.jpg");
        var result = AtomicFile.ResolveTempPath(source);

        // The leaf must contain the original filename.
        var leaf = Path.GetFileName(result);
        Assert.Contains("myphoto.jpg", leaf);
        // And the .tmp extension must be present.
        Assert.Equal(".tmp", Path.GetExtension(leaf));
        // And the leading "." (hidden-file convention on Windows) must be present.
        Assert.StartsWith(".", leaf);
    }

    [Fact]
    public void ResolveTempPath_TwoCalls_ProduceDifferentPaths()
    {
        // The temp path includes a Guid, so two calls in quick succession must
        // produce different paths — otherwise a back-to-back strip+overwrite
        // cycle would have a 1-in-2^32 chance of colliding.
        var source = Path.Combine(_tempDir, "photo.jpg");
        var a = AtomicFile.ResolveTempPath(source);
        var b = AtomicFile.ResolveTempPath(source);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CleanupOrphanedOutput_FileExists_OverwriteFalse_DeletesFile()
    {
        // D86: when overwriteSource=false, the output is a sibling file
        // (e.g. "photo_stripped.jpg" or "photo (2).jpg"). If the strip
        // throws after writing the partial output, the catch block must
        // delete the orphan. The pre-fix code had this logic inlined in
        // both strippers; the post-fix routes through this helper.
        var outputPath = Path.Combine(_tempDir, "photo_stripped.jpg");
        File.WriteAllBytes(outputPath, new byte[] { 0x01, 0x02 });

        AtomicFile.CleanupOrphanedOutput(outputPath, sourcePath: "/dev/null/source.jpg", overwriteSource: false);

        Assert.False(File.Exists(outputPath), "Output file should be deleted.");
    }

    [Fact]
    public void CleanupOrphanedOutput_FileExists_OverwriteTrue_DifferentPath_DeletesFile()
    {
        // D86: when overwriteSource=true, the output is initially a temp file
        // (e.g. ".photo.jpg.exifremover-<guid>.tmp"). If the strip throws
        // BEFORE the File.Replace, the catch block must delete the temp file
        // (which is a different path from the source). The condition
        // `actualOutputPath != sourcePath` is the trigger.
        var tempPath = Path.Combine(_tempDir, ".photo.jpg.exifremover-abc123.tmp");
        var sourcePath = Path.Combine(_tempDir, "photo.jpg");
        File.WriteAllBytes(tempPath, new byte[] { 0x01, 0x02 });

        AtomicFile.CleanupOrphanedOutput(tempPath, sourcePath, overwriteSource: true);

        Assert.False(File.Exists(tempPath), "Temp file should be deleted.");
    }

    [Fact]
    public void CleanupOrphanedOutput_FileExists_OverwriteTrue_SamePath_DoesNotDelete()
    {
        // D86: when overwriteSource=true AND the strip succeeded (so the
        // File.Replace has already moved the temp file to the source
        // path), the catch block must NOT delete the source. The condition
        // `actualOutputPath != sourcePath` is false in this case, so the
        // delete is skipped. This protects the just-written file from
        // being deleted by the catch block (which would happen if the
        // actualOutputPath was updated to sourcePath after a successful
        // strip and then re-entered the catch — but that shouldn't
        // happen, but we want the helper to be safe regardless).
        var path = Path.Combine(_tempDir, "photo.jpg");
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02 });

        AtomicFile.CleanupOrphanedOutput(path, path, overwriteSource: true);

        Assert.True(File.Exists(path), "File at source path must NOT be deleted.");
    }

    [Fact]
    public void CleanupOrphanedOutput_FileDoesNotExist_NoOp()
    {
        // D86: if the strip throws before writing ANY output, the catch
        // block runs against a non-existent path. The helper must no-op
        // (not throw, not delete anything). File.Delete is a no-op for
        // non-existent files but we check existence first to avoid a
        // pointless syscall.
        var path = Path.Combine(_tempDir, "never-existed.jpg");
        Assert.False(File.Exists(path));

        AtomicFile.CleanupOrphanedOutput(path, sourcePath: "/dev/null/source.jpg", overwriteSource: false);

        // Still doesn't exist (and no exception was thrown).
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupOrphanedOutput_FileLocked_DoesNotThrow()
    {
        // D86: the helper's internal try/catch must swallow any exception
        // (e.g. the file is locked by an AV scan). A cleanup failure must
        // not mask the original stripper exception that the caller's catch
        // block is re-throwing. The test: open a file with FileShare.None
        // (exclusive lock), then try to clean it up. The helper must
        // return normally (not throw).
        var path = Path.Combine(_tempDir, "locked.jpg");
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02 });

        // Hold an exclusive lock on the file.
        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // Cleanup must not throw.
        var ex = Record.Exception(() =>
            AtomicFile.CleanupOrphanedOutput(path, sourcePath: "/dev/null/source.jpg", overwriteSource: false));
        Assert.Null(ex);

        // The file still exists (the lock prevented the delete).
        Assert.True(File.Exists(path));
    }
}
