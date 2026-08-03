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
}