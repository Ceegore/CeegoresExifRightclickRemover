namespace ExifRemover.Engine;

public sealed class StripResult
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required bool OverwroteSource { get; init; }
    public required long OriginalSizeBytes { get; init; }
    public required long OutputSizeBytes { get; init; }
    public required int DroppedSegments { get; init; }
    public required bool Changed { get; init; }
    public string? Warning { get; init; }

    public long SavedBytes => Math.Max(0, OriginalSizeBytes - OutputSizeBytes);
}

public static class StripPipeline
{
    public static FileInspection Inspect(string path) => MetadataInspector.Inspect(path);

    public static StripResult Strip(string sourcePath, string outputPath, bool overwriteSource, StripProfile profile)
    {
        var format = ImageFormatDetector.DetectFile(sourcePath);
        return format switch
        {
            ImageFormat.Jpeg => JpegMetadataStripper.Strip(sourcePath, outputPath, overwriteSource, profile),
            ImageFormat.Png => PngMetadataStripper.Strip(sourcePath, outputPath, overwriteSource, profile),
            _ => throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(sourcePath)}. Only JPEG and PNG are supported.")
        };
    }

    public static BatchStripReport StripBatch(
        IReadOnlyList<string> sourcePaths,
        bool overwriteSource,
        StripProfile profile,
        IProgress<(int Done, int Total, string CurrentFile)>? progress = null)
    {
        var results = new List<StripResult>(sourcePaths.Count);
        var failures = new List<(string, string)>();

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            var path = sourcePaths[i];
            progress?.Report((i, sourcePaths.Count, Path.GetFileName(path)));

            try
            {
                string outPath = overwriteSource
                    ? path
                    : BuildSiblingPath(path);

                var result = Strip(path, outPath, overwriteSource, profile);
                results.Add(result);
            }
            catch (Exception ex)
            {
                failures.Add((path, ex.Message));
            }
        }

        progress?.Report((sourcePaths.Count, sourcePaths.Count, string.Empty));

        return new BatchStripReport
        {
            Results = results,
            Failures = failures
        };
    }

    public static string BuildSiblingPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath)
                  ?? throw new ArgumentException("Source path has no directory.", nameof(sourcePath));
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        var desired = Path.Combine(dir, $"{name}_stripped{ext}");
        return AtomicFile.NextNonClashingPath(desired);
    }
}

public sealed class BatchStripReport
{
    public required IReadOnlyList<StripResult> Results { get; init; }
    public required IReadOnlyList<(string Path, string Error)> Failures { get; init; }

    /// <summary>
    /// Number of files for which the stripper returned a StripResult (i.e. a result object —
    /// not an exception). "Changed" or "unchanged" both count; a corrupt-but-nonempty output
    /// (the historical L2 footgun) is intentionally NOT singled out here: the byte-stuffing and
    /// C1/C2 regressions are guarded by the real-image verifier and the xUnit regression tests,
    /// not by a counter that would happily mis-classify them. A real success is recorded
    /// per-file in <see cref="Results"/>; failures are in <see cref="Failures"/>.
    /// </summary>
    public int SuccessCount => Results.Count;

    /// <summary>Number of files that were actually modified by the stripper (something was dropped).</summary>
    public int ChangedCount => Results.Count(r => r.Changed);

    public long TotalOriginalBytes => Results.Sum(r => r.OriginalSizeBytes);
    public long TotalOutputBytes => Results.Sum(r => r.OutputSizeBytes);
    public long TotalSavedBytes => Math.Max(0, TotalOriginalBytes - TotalOutputBytes);
}