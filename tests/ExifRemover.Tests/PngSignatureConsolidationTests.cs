using System.IO;
using System.Linq;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Project-wide source-shape regression tests for D106 (M2.20.44).
/// The pre-fix code had 4 copies of the 8-byte PNG signature
/// (89 50 4E 47 0D 0A 1A 0A) across 4 files:
///   1. src/ExifRemover.Engine/ImageFormat.cs (added in M2.20.41 D103)
///   2. src/ExifRemover.Engine/MetadataInspector.cs (PngChunkProbe)
///   3. src/ExifRemover.Engine/PngMetadataStripper.cs (under the
///      DIFFERENT name `Signature` — invisible to a `grep PngSignature` sweep)
///   4. verify/Program.cs (verifier)
/// The 4× duplicate is a textbook "module-level constant duplicates are a
/// drift trap" finding. D106 (M2.20.44) consolidates the 4 copies to a
/// single canonical `public static readonly byte[] PngSignature` in
/// `ExifRemover.Engine.ImageFormatDetector` and deletes the 3 other
/// copies.
/// </summary>
public class PngSignatureConsolidationTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var probe = Path.Combine(dir, "ExifRemover.sln");
            if (File.Exists(probe)) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Fact]
    public void PngSignature_EngineHasExactlyOneLiteral()
    {
        // D106 (M2.20.44): the PNG signature byte literal
        // (0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) should appear
        // in EXACTLY 1 place across all Engine source files
        // (the canonical definition in ImageFormat.cs).
        // The pre-fix code had 3 copies in the Engine
        // (ImageFormat.cs, MetadataInspector.cs, PngMetadataStripper.cs).
        // A regression that re-introduces a duplicate would fail this test.
        var root = RepoRoot();
        var engineDir = Path.Combine(root, "src", "ExifRemover.Engine");
        Assert.True(Directory.Exists(engineDir),
            $"Cannot find Engine source dir at {engineDir}.");

        var csFiles = Directory.GetFiles(engineDir, "*.cs");
        var literal = "0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A";
        var hits = csFiles
            .SelectMany(f => Enumerable.Range(0, 1).Select(_ => (file: f, src: File.ReadAllText(f))))
            .Where(t => t.src.Contains(literal))
            .Select(t => Path.GetFileName(t.file))
            .ToList();

        Assert.Single(hits);
        Assert.Equal("ImageFormat.cs", hits[0]);
    }

    [Fact]
    public void PngSignature_VerifierHasNoLiteral()
    {
        // D106 (M2.20.44): the verifier (verify/Program.cs) should NOT
        // contain the inline PNG signature byte literal. The pre-fix code
        // had its own local `PngSignature` field in the verifier; the
        // post-fix code references `ImageFormatDetector.PngSignature`
        // (the canonical Engine constant). A regression that re-introduces
        // the verifier's local field would fail this test.
        var root = RepoRoot();
        var verifierPath = Path.Combine(root, "verify", "Program.cs");
        Assert.True(File.Exists(verifierPath),
            $"Cannot find verifier source at {verifierPath}.");

        var src = File.ReadAllText(verifierPath);
        var literal = "0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A";
        Assert.DoesNotContain(literal, src);
    }

    [Fact]
    public void PngSignature_ImageFormatDetectorConstantIsPublicByteArray()
    {
        // D106 (M2.20.44): the canonical PngSignature must be
        // (a) public (so the verifier — a different assembly —
        //     can reference it via ImageFormatDetector.PngSignature),
        // (b) a byte[] (not a ReadOnlySpan<byte> property) so callers
        //     can do both `SequenceEqual(PngSignature)` and
        //     `Slice(0, 8).SequenceEqual(PngSignature)` without
        //     worrying about span lifetimes.
        var root = RepoRoot();
        var detectorPath = Path.Combine(root, "src", "ExifRemover.Engine", "ImageFormat.cs");
        Assert.True(File.Exists(detectorPath),
            $"Cannot find ImageFormat.cs at {detectorPath}.");

        var src = File.ReadAllText(detectorPath);

        // The canonical declaration. The D103 (M2.20.41) form was
        // `private static ReadOnlySpan<byte> PngSignature => new byte[] {...}`
        // (a private property). The D106 (M2.20.44) form is
        // `public static readonly byte[] PngSignature = {...}` (a public
        // field). The test pins the public field form.
        Assert.Contains("public static readonly byte[] PngSignature", src);
    }
}
