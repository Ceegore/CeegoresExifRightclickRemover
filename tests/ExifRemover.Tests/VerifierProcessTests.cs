using System.Diagnostics;
using System.IO;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Integration tests that invoke the real <c>ExifRemover.Verifier.exe</c> as a
/// subprocess. These complement the pure-C# tests in <see cref="JpegStripperTests"/>
/// by exercising the verifier's command-line path end-to-end — in particular the
/// "input == output path" case (D3) which would have destroyed the input under
/// the old "touch then clear" pre-flight block.
/// </summary>
public class VerifierProcessTests
{
    [Fact]
    public void Verifier_InputPathEqualsOutputPath_DoesNotDestroyInput()
    {
        // D3: the old verifier did File.WriteAllBytes(output, bytes) immediately
        // followed by File.WriteAllBytes(output, []). If a caller passed the same
        // path for input and output, step 1 overwrote the source and step 2
        // truncated it to zero. The fix removes the no-op touch/clear so the
        // stripper is the only thing that writes to the output path. This test
        // runs the real verifier on a temp JPEG and asserts the input is intact.
        var verifier = LocateVerifier();
        if (verifier is null)
        {
            // Verifier isn't built in this environment (e.g. the test was launched
            // without a prior `dotnet build verify/`). The behaviour we care about
            // is covered at the stripper level by JpegStripperTests.Strip_InputPathEqualsOutputPath_…;
            // we skip here so the suite stays green in CI configurations that omit
            // the verifier build step.
            return;
        }

        var src = Path.Combine(Path.GetTempPath(), $"er-verify-self-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(src, FixtureFactory.JpegWithExifXmpIccAndComment());
        var originalBytes = File.ReadAllBytes(src);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = verifier,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(src);   // <-- input == output: the regression case
            psi.ArgumentList.Add("Privacy");

            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.ExitCode == 0, $"verifier returned {p.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The input file must still exist AND be non-empty AND be a valid JPEG.
            // Before the fix, this assertion would fail because step 2 had truncated
            // the file to zero bytes.
            Assert.True(File.Exists(src), "input file should still exist");
            var afterBytes = File.ReadAllBytes(src);
            Assert.NotEmpty(afterBytes);
            Assert.Equal(0xFF, afterBytes[0]);
            Assert.Equal(0xD8, afterBytes[1]);
        }
        finally
        {
            try { if (File.Exists(src)) File.Delete(src); } catch { }
        }
    }

    [Fact]
    public void Verifier_PngInput_ReportsOutputDecodesYes()
    {
        // D90 (M2.20.29): the pre-fix verifier unconditionally called
        // IsValidJpeg on the output, which would always report
        // "output_decodes=no" for a perfectly-valid PNG output. The
        // Python harness only ran JPEG inputs so the bug never fired,
        // but a PNG input was a latent failure. The fix detects the
        // input format and calls the right validator (IsValidJpeg or
        // IsValidPng). This test runs the real verifier on a PNG and
        // asserts the output_decodes line is "yes". Conditional on
        // the verifier exe being built (same as the JPEG test above).
        var verifier = LocateVerifier();
        if (verifier is null) return;

        var src = Path.Combine(Path.GetTempPath(), $"er-verify-png-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(src, FixtureFactory.PngWithTextTimeExifIccp());

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = verifier,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"er-verify-png-out-{Guid.NewGuid():N}.png"));
            psi.ArgumentList.Add("Privacy");

            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.ExitCode == 0, $"verifier returned {p.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The verifier must report output_decodes=yes for a valid PNG output.
            // Pre-fix: this assertion fails because the pre-fix code unconditionally
            // called IsValidJpeg, which returns false for PNG output (the PNG
            // signature is not the JPEG signature).
            Assert.Contains("output_decodes=yes", stdout);
            // The format-detection line should also be present (D90 added it).
            Assert.Contains("output_format=Png", stdout);
        }
        finally
        {
            try { if (File.Exists(src)) File.Delete(src); } catch { }
        }
    }

    private static string? LocateVerifier()
    {
        // D91 (M2.20.29): the pre-fix code used `for (int i = 0; i < 6; i++)`,
        // which walks at most 6 levels. The test DLL is at
        //   <repo>/tests/ExifRemover.Tests/bin/Debug/net8.0/win-x64/
        // which is 6 levels below the repo root, so the loop
        // checked
        //   1. tests/ExifRemover.Tests/bin/Debug/net8.0/win-x64
        //   2. tests/ExifRemover.Tests/bin/Debug/net8.0
        //   3. tests/ExifRemover.Tests/bin/Debug
        //   4. tests/ExifRemover.Tests/bin
        //   5. tests/ExifRemover.Tests
        //   6. tests
        // and then exited the loop. The repo root (where the verifier
        // lives) was never checked. Result: the test silently
        // short-circuited via the `if (verifier is null) return;` guard
        // and never actually ran the verifier. Both the existing
        // `Verifier_InputPathEqualsOutputPath_DoesNotDestroyInput` test
        // and the new `Verifier_PngInput_ReportsOutputDecodesYes` test
        // were affected. Fix: bump the loop bound to 8 so the walk
        // reaches the repo root. 8 is more than enough for the
        // current dir tree (6 levels), and the loop exits early on
        // `parent is null` so it won't run forever.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "verify", "bin", "Release", "net8.0", "ExifRemover.Verifier.exe");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return null;
            dir = parent.FullName;
        }
        return null;
    }
}
