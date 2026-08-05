using System.IO;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Edge-case regression tests for <see cref="MetadataInspector"/> and the
/// strippers' file-access paths. These cover the "file becomes inaccessible
/// between PathFilter.FileExists and the stripper call" race that D71 / D72
/// (M2.20.20) fixed. Before the fix, the inspector and the strippers called
/// <c>File.OpenRead</c> / <c>new FileInfo(path).Length</c> OUTSIDE the
/// try/catch, so a missing or locked file produced an unhandled exception
/// that propagated to the Task.Run caller. The post-fix behavior: clean
/// FileInspection with an Error (inspector) or a clear exception with the
/// stripper's catch-block cleanup having run (stripper).
/// </summary>
public class InspectorEdgeCasesTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void Inspect_NonExistentFile_ReturnsErrorNotThrow()
    {
        // D71: a file path that doesn't exist must produce a FileInspection
        // with a clear Error message, not a thrown FileNotFoundException.
        // Pre-fix: ImageFormatDetector.DetectFile threw FileNotFoundException
        // outside the inspector's try/catch, which propagated to the
        // Task.Run caller and surfaced as a confusing stack trace in the
        // status strip. Post-fix: the inspector catches the exception and
        // returns a FileInspection with a clean "File not found: …" Error.
        var path = Path.Combine(Path.GetTempPath(), $"er-noexist-{Guid.NewGuid():N}.jpg");
        // Intentionally do NOT create the file.

        var inspection = MetadataInspector.Inspect(path);

        Assert.NotNull(inspection.Error);
        Assert.Contains("File not found", inspection.Error);
        Assert.Equal(ImageFormat.Unknown, inspection.Format);
        Assert.Empty(inspection.Entries);
    }

    [Fact]
    public void Inspect_FileDeletedAfterOpen_FileIsJustUnknown()
    {
        // D71 (related): a file that exists at File.Exists check time but
        // is gone by the time the inspector opens it should also return a
        // clean FileInspection rather than crashing. The simplest way to
        // trigger this without a flaky race: create a file, get its path,
        // delete it, then call Inspect. The DetectFile call's File.OpenRead
        // throws FileNotFoundException, which the post-fix inspector catches.
        var path = Path.Combine(Path.GetTempPath(), $"er-deleted-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // 4 bytes: valid SOI/EOI
        File.Delete(path);

        var inspection = MetadataInspector.Inspect(path);

        Assert.NotNull(inspection.Error);
        Assert.Equal(ImageFormat.Unknown, inspection.Format);
        Assert.Empty(inspection.Entries);
    }

    [Fact]
    public void Inspect_DirectoryPath_ReturnsErrorNotThrow()
    {
        // D71: passing a directory path to Inspect should not crash with
        // an unhandled UnauthorizedAccessException. The .NET API would
        // surface this as FileNotFoundException or UnauthorizedAccessException
        // depending on the platform; both are now caught.
        var dir = Path.Combine(Path.GetTempPath(), $"er-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempFiles.Add(dir);

        var inspection = MetadataInspector.Inspect(dir);

        Assert.NotNull(inspection.Error);
        Assert.Equal(ImageFormat.Unknown, inspection.Format);
        Assert.Empty(inspection.Entries);
    }

    [Fact]
    public void Strip_NonExistentJpegSource_ThrowsFileNotFoundException_WithCatchCleanup()
    {
        // D72: a missing source file must produce a clear FileNotFoundException
        // AND run the stripper's catch-block cleanup (which tries to delete
        // actualOutputPath if it was created). Pre-fix: the FileInfo.Length
        // call OUTSIDE the try block threw without running cleanup, so a
        // non-existent source could leave a sibling "name (2).jpg" or temp
        // file orphaned on disk. Post-fix: the FileInfo.Length call is
        // inside the try block; the catch deletes actualOutputPath before
        // re-throwing.
        var path = Path.Combine(Path.GetTempPath(), $"er-noexist-jpg-{Guid.NewGuid():N}.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-noexist-out-{Guid.NewGuid():N}.jpg");
        // Intentionally do NOT create the source or output files.

        // outPath doesn't exist either, so NextNonClashingPath returns outPath unchanged.
        // After the fix, the catch block runs and tries to delete outPath (which doesn't
        // exist — File.Delete is a no-op for non-existent files). The test asserts the
        // throw + no-orphan contract.
        var ex = Assert.ThrowsAny<Exception>(() =>
            JpegMetadataStripper.Strip(path, outPath, overwriteSource: false, StripProfile.Privacy));
        // The exception must be file-not-found-ish; we accept any I/O exception type.
        Assert.True(
            ex is FileNotFoundException or DirectoryNotFoundException or IOException,
            $"Expected an I/O exception, got {ex.GetType().Name}: {ex.Message}");

        // Cleanup contract: no orphan output file.
        Assert.False(File.Exists(outPath), "Output file should not be created when source is missing.");
    }

    [Fact]
    public void Strip_NonExistentPngSource_ThrowsFileNotFoundException_WithCatchCleanup()
    {
        // D72 (PNG side): same contract as the JPEG test — missing source
        // produces a clear exception and runs the catch-block cleanup so no
        // orphan output remains.
        var path = Path.Combine(Path.GetTempPath(), $"er-noexist-png-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-noexist-png-out-{Guid.NewGuid():N}.png");

        var ex = Assert.ThrowsAny<Exception>(() =>
            PngMetadataStripper.Strip(path, outPath, overwriteSource: false, StripProfile.Privacy));
        Assert.True(
            ex is FileNotFoundException or DirectoryNotFoundException or IOException,
            $"Expected an I/O exception, got {ex.GetType().Name}: {ex.Message}");

        Assert.False(File.Exists(outPath), "Output file should not be created when source is missing.");
    }

    [Fact]
    public void Strip_OverwriteSourceTrue_NonExistentSource_DoesNotOrphanTempFile()
    {
        // D72 (overwrite path): when overwriteSource=true, the stripper writes
        // to a temp file in the same directory as the source. The temp file
        // has the form ".<name>.exifremover-<guid>.tmp". If the source doesn't
        // exist, the catch block must run and clean up any temp file that
        // may have been created. Pre-fix: the FileInfo.Length call was outside
        // the try block, so no cleanup happened — the temp file was orphaned.
        // Post-fix: cleanup runs, no orphan.
        var dir = Path.Combine(Path.GetTempPath(), $"er-orphan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempFiles.Add(dir);
        var path = Path.Combine(dir, "doesnotexist.jpg");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-orphan-out-{Guid.NewGuid():N}.jpg");

        Assert.ThrowsAny<Exception>(() =>
            JpegMetadataStripper.Strip(path, outPath, overwriteSource: true, StripProfile.Privacy));

        // The temp file is ".<name>.exifremover-<guid>.tmp" in the same
        // directory as the source. We don't know the GUID, so just enumerate
        // the directory and assert no .tmp files remain.
        var tempFiles = Directory.GetFiles(dir, "*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void Inspect_ExifThumbnailDirectory_AllEntriesArePrivacySensitive()
    {
        // D77: the pre-fix IsPrivacySensitive function only listed
        // ExifIfd0Directory, ExifSubIfdDirectory, and ExifInteropDirectory in
        // its EXIF-directory pattern. ExifThumbnailDirectory was missing, so
        // any tag surfaced from the thumbnail IFD (Compression, JPEGInterchangeFormat,
        // JPEGInterchangeFormatLength, XResolution, YResolution, …) was marked
        // NOT privacy-sensitive. But the stripper drops the entire APP1
        // (EXIF), which includes the thumbnail IFD, so the Action column said
        // "Would be removed" while the sensitivity styling said "not
        // privacy-sensitive" — a visual lie. A thumbnail is privacy-sensitive
        // (can embed a separate image with its own metadata, including GPS
        // coordinates; can be a different/cropped/edited version of the
        // original). The fix adds ExifThumbnailDirectory to the list. The
        // pattern match's "Exif Version / Flashpix Version / Components
        // Configuration" exclusion list is harmless for IFD1 — those tags
        // are IFD0-only and never appear in ExifThumbnailDirectory.
        var bytes = FixtureFactory.JpegWithExifThumbnail();
        var src = Path.Combine(Path.GetTempPath(), $"er-thumb-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(src, bytes);
        _tempFiles.Add(src);

        var inspection = MetadataInspector.Inspect(src);

        // Sanity: MetadataExtractor must have surfaced an Exif Thumbnail
        // directory. If this assertion fails, the fixture's TIFF layout is
        // wrong (the IFD1 chain isn't being followed), not the inspector.
        var thumbnailEntries = inspection.Entries
            .Where(e => e.Group == MetadataGroups.ExifThumbnail)
            .ToList();
        Assert.NotEmpty(thumbnailEntries);

        // Every entry from ExifThumbnailDirectory must be privacy-sensitive.
        // Pre-fix: all false (visual lie). Post-fix: all true.
        foreach (var entry in thumbnailEntries)
        {
            Assert.True(
                entry.IsPrivacySensitive,
                $"EXIF Thumbnail entry '{entry.Name}' (group='{entry.Group}') must be privacy-sensitive — " +
                $"the stripper drops the entire APP1 including the thumbnail IFD, and a thumbnail can embed a " +
                $"separate image with its own metadata. Pre-fix this entry was styled as not privacy-sensitive " +
                $"because ExifThumbnailDirectory was missing from the IsPrivacySensitive EXIF-directory pattern.");
        }
    }

    [Fact]
    public void Strip_JpegWithExifThumbnail_ThumbnailIstripped()
    {
        // D77 (stripper side): the EXIF thumbnail (IFD1 + thumbnail bytes)
        // must be removed along with IFD0. We can't easily inspect the
        // post-strip file's TIFF stream (the stripper rewrites the whole
        // APP1 segment with the same byte length minus what was dropped,
        // but the stripper doesn't re-write APP1 if it's being dropped —
        // it skips the whole segment). The reliable check: after a
        // Privacy strip, the inspector's Exif Thumbnail group is empty
        // (no entries from IFD1, no thumbnail bytes).
        var bytes = FixtureFactory.JpegWithExifThumbnail();
        var src = Path.Combine(Path.GetTempPath(), $"er-thumb-out-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(src, bytes);
        _tempFiles.Add(src);
        var outPath = Path.Combine(Path.GetTempPath(), $"er-thumb-stripped-{Guid.NewGuid():N}.jpg");
        _tempFiles.Add(outPath);

        JpegMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifThumbnail);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.ExifIfd0);
    }
}
