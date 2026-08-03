using System.IO;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

public class StripPipelineTests : IDisposable
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
    public void StripBatch_TwoFiles_OverwriteFalse_WritesStrippedCopies()
    {
        var jpg = Path.Combine(Path.GetTempPath(), $"er-batch-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(jpg, FixtureFactory.JpegWithExifXmpIccAndComment());
        _tempFiles.Add(jpg);

        var png = Path.Combine(Path.GetTempPath(), $"er-batch-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(png, FixtureFactory.PngWithTextTimeExifIccp());
        _tempFiles.Add(png);

        var report = StripPipeline.StripBatch(new[] { jpg, png }, overwriteSource: false, StripProfile.Privacy);

        Assert.Equal(2, report.Results.Count);
        Assert.Empty(report.Failures);
        // The output files should be at <name>_stripped.<ext> next to the source.
        var expectedJpg = Path.Combine(
            Path.GetDirectoryName(jpg)!,
            Path.GetFileNameWithoutExtension(jpg) + "_stripped" + Path.GetExtension(jpg));
        var expectedPng = Path.Combine(
            Path.GetDirectoryName(png)!,
            Path.GetFileNameWithoutExtension(png) + "_stripped" + Path.GetExtension(png));
        Assert.True(File.Exists(expectedJpg), $"Expected {expectedJpg} to exist");
        Assert.True(File.Exists(expectedPng), $"Expected {expectedPng} to exist");
        _tempFiles.Add(expectedJpg);
        _tempFiles.Add(expectedPng);
    }

    [Fact]
    public void StripBatch_UnsupportedFile_IsReportedAsFailure_AndDoesNotAbortBatch()
    {
        var jpg = Path.Combine(Path.GetTempPath(), $"er-batch2-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(jpg, FixtureFactory.JpegWithExifXmpIccAndComment());
        _tempFiles.Add(jpg);

        var bogus = Path.Combine(Path.GetTempPath(), $"er-batch2-{Guid.NewGuid():N}.txt");
        File.WriteAllText(bogus, "not an image");
        _tempFiles.Add(bogus);

        var report = StripPipeline.StripBatch(new[] { jpg, bogus }, overwriteSource: false, StripProfile.Privacy);

        Assert.Single(report.Results);
        Assert.Single(report.Failures);
        Assert.Equal(bogus, report.Failures[0].Path);
    }

    [Fact]
    public void BatchStripReport_SuccessCount_EqualsResultsCount()
    {
        // B7 / L2: SuccessCount used to be a "non-empty output" check, which would
        // have mis-classified a corrupt-but-nonempty output as success. The fix makes
        // SuccessCount == Results.Count: a StripResult in Results means the stripper
        // returned cleanly (any exception goes to Failures, not Results).
        var jpg = Path.Combine(Path.GetTempPath(), $"er-sc-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(jpg, FixtureFactory.JpegWithExifXmpIccAndComment());
        _tempFiles.Add(jpg);

        var bare = Path.Combine(Path.GetTempPath(), $"er-sc-bare-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(bare, FixtureFactory.MinimalJpeg());
        _tempFiles.Add(bare);

        var report = StripPipeline.StripBatch(new[] { jpg, bare }, overwriteSource: false, StripProfile.Privacy);
        Assert.Equal(2, report.Results.Count);
        Assert.Equal(2, report.SuccessCount);
        Assert.Empty(report.Failures);

        // ChangedCount distinguishes "actually removed something" from "ran cleanly
        // but the file was already clean".
        Assert.Equal(1, report.ChangedCount); // jpg dropped segments, bare did not
    }

    [Fact]
    public void BatchStripReport_SuccessCount_ExcludesFailures()
    {
        // A failure is recorded in Failures (not Results), so SuccessCount must NOT
        // include it. This pins the "result object = success" contract.
        var jpg = Path.Combine(Path.GetTempPath(), $"er-mixed-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(jpg, FixtureFactory.JpegWithExifXmpIccAndComment());
        _tempFiles.Add(jpg);

        var bogus = Path.Combine(Path.GetTempPath(), $"er-mixed-{Guid.NewGuid():N}.txt");
        File.WriteAllText(bogus, "not an image");
        _tempFiles.Add(bogus);

        var report = StripPipeline.StripBatch(new[] { jpg, bogus }, overwriteSource: false, StripProfile.Privacy);
        Assert.Equal(1, report.SuccessCount);
        Assert.Single(report.Failures);
    }
}