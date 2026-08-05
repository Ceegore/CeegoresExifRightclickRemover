namespace ExifRemover.Engine;

internal static class AtomicFile
{
    /// <summary>
    /// Returns a path in the same directory as <paramref name="desiredPath"/> that does
    /// not collide with any existing file. If the desired path is free, returns it
    /// unchanged; otherwise tries "name (2).ext", "name (3).ext", … up to "name (9999).ext".
    /// If all 9998 numbered slots are taken (vanishingly rare), falls back to
    /// "name_{guid}.ext".
    /// </summary>
    public static string NextNonClashingPath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var dir = Path.GetDirectoryName(desiredPath)
                  ?? throw new ArgumentException("Path has no directory.", nameof(desiredPath));
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    /// <summary>
    /// Generates a hidden, GUID-suffixed temp file path in the same directory as
    /// <paramref name="sourcePath"/>. Used by both strippers as the atomic-write
    /// destination for the <c>overwriteSource</c> path: write the stripped output
    /// to this temp file, then <c>File.Replace</c> it over the original.
    ///
    /// D83 (M2.20.25): the pre-fix code declared this helper as a private method in
    /// BOTH <c>JpegMetadataStripper.cs</c> and <c>PngMetadataStripper.cs</c>. The two
    /// copies were byte-identical. This is the same DRY-drift pattern that R17 of
    /// the SteamReviewTool audit found for back-door in-memory setters: a future
    /// contributor who updates the temp-name scheme (e.g. to use a stronger random
    /// source, to add a creation timestamp, to put the temp file in a sibling
    /// directory instead of the source directory) would have to remember to update
    /// the other copy too. Missed updates silently diverge.
    ///
    /// The leading "." in the filename keeps the temp file hidden from a normal
    /// Explorer view (a "." prefix is the Windows convention for hidden files). The
    /// <c>exifremover-{guid}.tmp</c> suffix makes the temp file attributable even
    /// if the stripper crashed mid-write and the catch-block cleanup didn't run.
    /// </summary>
    public static string ResolveTempPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileName(sourcePath);
        return Path.Combine(dir, $".{name}.exifremover-{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Deletes an orphaned output file left behind by a stripper that threw.
    /// Called from the strippers' catch block (after a failed strip) so the
    /// sibling or temp file doesn't survive as garbage on the user's disk.
    ///
    /// D86 (M2.20.26): the pre-fix code had a one-liner
    /// <c>try { if (File.Exists(actualOutputPath) &amp;&amp; (!overwriteSource || actualOutputPath != sourcePath)) File.Delete(actualOutputPath); } catch { }</c>
    /// in BOTH <c>JpegMetadataStripper.cs</c> and <c>PngMetadataStripper.cs</c>.
    /// The two copies were byte-identical. This is the same D83-style DRY-drift
    /// pattern: a future contributor who updates the cleanup logic (e.g. adds
    /// a retry on lock, switches to a <c>File.Delete</c> that waits for an AV
    /// scan, logs the cleanup failure) would have to remember to update the
    /// other copy too. Missed updates silently diverge the two strippers'
    /// failure-recovery paths.
    ///
    /// The conditions for deletion:
    ///   * <paramref name="actualOutputPath"/> must exist (File.Delete is a
    ///     no-op for non-existent files but we check explicitly to avoid a
    ///     pointless syscall).
    ///   * Either <paramref name="overwriteSource"/> is false (the output
    ///     is a sibling, not the source — safe to delete), OR
    ///     <paramref name="actualOutputPath"/> differs from
    ///     <paramref name="sourcePath"/> (it's a temp file, not the original).
    /// The internal try/catch swallows any exception (e.g. file is locked by
    /// an AV scan) so a failed cleanup doesn't mask the original stripper
    /// exception that the catch block is re-throwing.
    /// </summary>
    public static void CleanupOrphanedOutput(string actualOutputPath, string sourcePath, bool overwriteSource)
    {
        try
        {
            if (File.Exists(actualOutputPath) && (!overwriteSource || actualOutputPath != sourcePath))
            {
                File.Delete(actualOutputPath);
            }
        }
        catch
        {
            // Best-effort cleanup. A failure here (e.g. the file is locked by
            // an AV scan) should not mask the original stripper exception
            // that the caller's catch block is about to re-throw.
        }
    }
}