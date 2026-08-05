namespace ExifRemover.Engine;

/// <summary>
/// Detected image container format.
/// </summary>
public enum ImageFormat
{
    Unknown,
    Jpeg,
    Png
}

public static class ImageFormatDetector
{
    // D103 (M2.20.41): the pre-fix code had the PNG signature
    // (8 bytes: 89 50 4E 47 0D 0A 1A 0A) hardcoded as 8 inline byte
    // comparisons. The same D99 / D100 / D101 pattern (extract named
    // constants + SequenceEqual) is applied here. The M2.20.37 D99 fix
    // extracted the PNG signature in the verifier; the M2.20.39 D101
    // fix extracted it in MetadataInspector.PngChunkProbe and in
    // PngMetadataStripper. The D103 fix does the same here.
    //
    // D106 (M2.20.44): the M2.20.41 D103 fix created PngSignature here,
    // but the 3 other copies (MetadataInspector.cs:277, PngMetadataStripper.cs:5,
    // verify/Program.cs:171) were not deleted. The agent-memory "module-level
    // constant duplicates are a drift trap" rule applies: 4 copies of the
    // PNG signature across 4 files is a 4× drift surface. The PngMetadataStripper
    // copy is even under a different name (`Signature` not `PngSignature`),
    // making it invisible to grep for `PngSignature`. Fix: make the canonical
    // constant `public static readonly byte[]` (not a private property), and
    // delete the 3 duplicate copies. The byte[] type works for both
    // `Slice(0, 8).SequenceEqual(...)` and `SequenceEqual(...)` callers because
    // byte[] implicitly converts to ReadOnlySpan<byte>.
    public static readonly byte[] PngSignature =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (header.Length >= 8 && header.Slice(0, 8).SequenceEqual(PngSignature))
        {
            return ImageFormat.Png;
        }

        return ImageFormat.Unknown;
    }

    public static ImageFormat DetectFile(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        int read = fs.Read(head);
        return Detect(head[..read]);
    }
}