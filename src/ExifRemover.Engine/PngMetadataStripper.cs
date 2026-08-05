namespace ExifRemover.Engine;

public static class PngMetadataStripper
{
    // The PNG signature (8 bytes) used to be duplicated here as a local
    // `Signature` byte array (note: under a DIFFERENT name from the
    // canonical `PngSignature` in ImageFormatDetector, which made the
    // duplication invisible to a `grep PngSignature` sweep). D106
    // (M2.20.44) deletes that copy and references the canonical
    // `ImageFormatDetector.PngSignature` instead.

    // PNG chunk type constants (each chunk type is a 4-byte ASCII string per the
    // PNG spec). The pre-fix code had inline byte comparisons for each chunk type
    // (4 individual byte comparisons per check) at 12 sites across ShouldDrop
    // and Strip. The D101 (M2.20.39) fix extracted them to named constants
    // and uses SequenceEqual for the comparison, matching the pattern
    // established by D100 in JpegMetadataStripper.
    private static ReadOnlySpan<byte> IhdrBytes => "IHDR"u8;
    private static ReadOnlySpan<byte> IendBytes => "IEND"u8;
    // tEXt is the textual metadata chunk (keyword + text). The first byte's
    // case can vary per the PNG spec (a "safe-to-copy" bit), but in practice
    // every implementation uses lowercase t for the textual chunks.
    private static ReadOnlySpan<byte> TextBytes => "tEXt"u8;
    private static ReadOnlySpan<byte> ZtxtBytes => "zTXt"u8;
    private static ReadOnlySpan<byte> ItxtBytes => "iTXt"u8;
    private static ReadOnlySpan<byte> TimeBytes => "tIME"u8;
    private static ReadOnlySpan<byte> ExifBytes => "eXIf"u8;
    private static ReadOnlySpan<byte> IccpBytes => "iCCP"u8;
    private static ReadOnlySpan<byte> HistBytes => "hIST"u8;
    private static ReadOnlySpan<byte> GamaBytes => "gAMA"u8;
    private static ReadOnlySpan<byte> ChrmBytes => "cHRM"u8;
    private static ReadOnlySpan<byte> SrgbBytes => "sRGB"u8;

    /// <summary>
    /// PNG chunk lengths are technically limited to 2^31-1 by the spec, but in practice the
    /// largest legitimate chunk (a single IDAT for a multi-gigapixel image) is well under
    /// a few hundred MB. We cap at 256 MB so a malicious or corrupt file claiming a length
    /// of 2 GB doesn't OOM the process before the per-chunk allocation runs.
    /// </summary>
    public const int MaxChunkLength = 256 * 1024 * 1024;

    public static StripResult Strip(string sourcePath, string outputPath, bool overwriteSource, StripProfile profile)
    {
        int dropped = 0;

        string actualOutputPath = overwriteSource
            ? AtomicFile.ResolveTempPath(sourcePath)
            : AtomicFile.NextNonClashingPath(outputPath);

        bool sawIhdr = false;
        bool sawIend = false;

        FileStream? input = null;
        try
        {
            // D72: read the original size INSIDE the try block so a missing/inaccessible
            // file produces a single FileNotFoundException with a clear message and
            // lets the catch block run its cleanup. See JpegMetadataStripper.Strip
            // for the full rationale — same pattern, same family of bug.
            long originalSize = new FileInfo(sourcePath).Length;
            input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

            Span<byte> sig = stackalloc byte[8];
            StreamHelpers.ReadExact(input, sig, "PNG");
            if (!sig.SequenceEqual(ImageFormatDetector.PngSignature))
            {
                throw new InvalidDataException("Not a PNG file (bad signature).");
            }

            using (var output = new FileStream(actualOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                output.Write(sig);

                Span<byte> header = stackalloc byte[8];
                Span<byte> typeBuf = stackalloc byte[4];
                Span<byte> crcBuf = stackalloc byte[4];
                uint[] crcTable = Crc32.Table;

                while (true)
                {
                    StreamHelpers.ReadExact(input, header, "PNG");
                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    typeBuf[0] = header[4]; typeBuf[1] = header[5]; typeBuf[2] = header[6]; typeBuf[3] = header[7];

                    if (length < 0 || length > MaxChunkLength)
                    {
                        throw new InvalidDataException(
                            $"Invalid PNG chunk length {length} for '{Ascii(typeBuf)}' (max {MaxChunkLength}).");
                    }

                    bool drop = ShouldDrop(typeBuf, profile);

                    if (typeBuf.SequenceEqual(IhdrBytes))
                    {
                        sawIhdr = true;
                        drop = false;
                    }
                    else if (typeBuf.SequenceEqual(IendBytes))
                    {
                        sawIend = true;
                        drop = false;
                    }

                    if (drop)
                    {
                        // Skip the chunk data and 4-byte CRC trailer without allocating
                        // a buffer for the payload — the chunk is being dropped, not rewritten,
                        // so we never need its contents in memory. IEND cannot reach this
                        // branch (it's forced to drop = false above), so the loop naturally
                        // terminates at IEND via the "kept" path on the next iteration.
                        // D87 (M2.20.27): routed through StreamHelpers.SkipExactly which
                        // takes long count. The previous private SkipExactly(long count)
                        // had a Math.Min(count, int.MaxValue) clamp; the helper preserves
                        // that clamp so the non-seekable path's `new byte[...]` allocation
                        // can't overflow on a malicious 2^31-byte chunk length.
                        StreamHelpers.SkipExactly(input, length + 4, "PNG");
                        dropped++;
                        continue;
                    }

                    // We need the chunk's bytes to recompute its CRC and (for kept chunks)
                    // to copy them into the output. Allocate once, use twice.
                    var data = length == 0 ? Array.Empty<byte>() : new byte[length];
                    if (length > 0)
                    {
                        StreamHelpers.ReadExact(input, data, "PNG");
                    }
                    StreamHelpers.ReadExact(input, crcBuf, "PNG");

                    uint crc = Crc32.Compute(crcTable, typeBuf, data);
                    output.Write(header);
                    if (length > 0)
                    {
                        output.Write(data, 0, length);
                    }
                    crcBuf[0] = (byte)(crc >> 24);
                    crcBuf[1] = (byte)(crc >> 16);
                    crcBuf[2] = (byte)(crc >> 8);
                    crcBuf[3] = (byte)(crc);
                    output.Write(crcBuf);

                    if (sawIend)
                    {
                        break;
                    }
                }
            }

            if (!sawIhdr || !sawIend)
            {
                throw new InvalidDataException("PNG is missing IHDR or IEND; not a valid PNG file.");
            }

            input.Dispose();
            input = null;

            if (overwriteSource)
            {
                if (File.Exists(sourcePath))
                {
                    File.Replace(actualOutputPath, sourcePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(actualOutputPath, sourcePath);
                }
                actualOutputPath = sourcePath;
            }

            long outputSize = new FileInfo(actualOutputPath).Length;
            bool changed = dropped > 0 || outputSize != originalSize;

            return new StripResult
            {
                SourcePath = sourcePath,
                OutputPath = actualOutputPath,
                OverwroteSource = overwriteSource,
                OriginalSizeBytes = originalSize,
                OutputSizeBytes = outputSize,
                DroppedSegments = dropped,
                Changed = changed
            };
        }
        catch
        {
            // D86 (M2.20.26): same as JpegMetadataStripper — the inline cleanup
            // expression was extracted to AtomicFile.CleanupOrphanedOutput.
            AtomicFile.CleanupOrphanedOutput(actualOutputPath, sourcePath, overwriteSource);
            throw;
        }
        finally
        {
            input?.Dispose();
        }
    }

    private static bool ShouldDrop(ReadOnlySpan<byte> type, StripProfile profile)
    {
        if ((type[0] & 0x20) == 0)
        {
            return false;
        }

        if (type.SequenceEqual(TextBytes) ||
            type.SequenceEqual(ZtxtBytes) ||
            type.SequenceEqual(ItxtBytes))
        {
            return true;
        }

        if (type.SequenceEqual(TimeBytes) ||
            type.SequenceEqual(ExifBytes))
        {
            return true;
        }

        if (type.SequenceEqual(IccpBytes) ||
            type.SequenceEqual(HistBytes))
        {
            return profile != StripProfile.Minimal;
        }

        if (type.SequenceEqual(GamaBytes) ||
            type.SequenceEqual(ChrmBytes) ||
            type.SequenceEqual(SrgbBytes))
        {
            return profile == StripProfile.AllMetadata;
        }

        return false;
    }

    private static string Ascii(ReadOnlySpan<byte> type)
    {
        return new string(new[] { (char)type[0], (char)type[1], (char)type[2], (char)type[3] });
    }
}

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    public static uint[] Table { get; } = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (Polynomial ^ (c >> 1)) : (c >> 1);
            }
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(uint[] table, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++)
        {
            crc = table[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        }
        for (int i = 0; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}