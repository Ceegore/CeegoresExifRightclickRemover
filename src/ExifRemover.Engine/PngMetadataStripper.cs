namespace ExifRemover.Engine;

public static class PngMetadataStripper
{
    private static readonly byte[] Signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

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
            ? ResolveTempPath(sourcePath)
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
            ReadExact(input, sig);
            if (!sig.SequenceEqual(Signature))
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
                    ReadExact(input, header);
                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    typeBuf[0] = header[4]; typeBuf[1] = header[5]; typeBuf[2] = header[6]; typeBuf[3] = header[7];

                    if (length < 0 || length > MaxChunkLength)
                    {
                        throw new InvalidDataException(
                            $"Invalid PNG chunk length {length} for '{Ascii(typeBuf)}' (max {MaxChunkLength}).");
                    }

                    bool drop = ShouldDrop(typeBuf, profile);

                    if (typeBuf[0] == 'I' && typeBuf[1] == 'H' && typeBuf[2] == 'D' && typeBuf[3] == 'R')
                    {
                        sawIhdr = true;
                        drop = false;
                    }
                    else if (typeBuf[0] == 'I' && typeBuf[1] == 'E' && typeBuf[2] == 'N' && typeBuf[3] == 'D')
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
                        SkipExactly(input, length + 4);
                        dropped++;
                        continue;
                    }

                    // We need the chunk's bytes to recompute its CRC and (for kept chunks)
                    // to copy them into the output. Allocate once, use twice.
                    var data = length == 0 ? Array.Empty<byte>() : new byte[length];
                    if (length > 0)
                    {
                        ReadExact(input, data);
                    }
                    ReadExact(input, crcBuf);

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
            try { if (File.Exists(actualOutputPath) && (!overwriteSource || actualOutputPath != sourcePath)) File.Delete(actualOutputPath); } catch { }
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

        if (type[0] == 't' && (type[1] == 'E' || type[1] == 'e') && type[2] == 'X' && type[3] == 't')
        {
            return true;
        }
        if (type[0] == 'z' && type[1] == 'T' && type[2] == 'X' && type[3] == 't')
        {
            return true;
        }
        if (type[0] == 'i' && type[1] == 'T' && type[2] == 'X' && type[3] == 't')
        {
            return true;
        }

        if (type[0] == 't' && type[1] == 'I' && type[2] == 'M' && type[3] == 'E')
        {
            return true;
        }

        if (type[0] == 'e' && type[1] == 'X' && type[2] == 'I' && type[3] == 'f')
        {
            return true;
        }

        if (type[0] == 'i' && type[1] == 'C' && type[2] == 'C' && type[3] == 'P')
        {
            return profile != StripProfile.Minimal;
        }
        if (type[0] == 'h' && type[1] == 'I' && type[2] == 'S' && type[3] == 'T')
        {
            return profile != StripProfile.Minimal;
        }

        if (type[0] == 'g' && type[1] == 'A' && type[2] == 'M' && type[3] == 'A')
        {
            return profile == StripProfile.AllMetadata;
        }
        if (type[0] == 'c' && type[1] == 'H' && type[2] == 'R' && type[3] == 'M')
        {
            return profile == StripProfile.AllMetadata;
        }
        if (type[0] == 's' && type[1] == 'R' && type[2] == 'G' && type[3] == 'B')
        {
            return profile == StripProfile.AllMetadata;
        }

        return false;
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n == 0) throw new EndOfStreamException("Unexpected end of PNG stream.");
            total += n;
        }
    }

    private static void SkipExactly(Stream s, long count)
    {
        if (s.CanSeek)
        {
            // Trust-but-verify: even on a seekable stream, a chunk length that runs past EOF
            // would put Position past Length, which is illegal for the next read.
            if (s.Position + count > s.Length)
            {
                throw new EndOfStreamException("Unexpected end of PNG stream during chunk skip.");
            }
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        var buf = new byte[Math.Min((int)Math.Min(count, int.MaxValue), 64 * 1024)];
        long remaining = count;
        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, buf.Length);
            int n = s.Read(buf, 0, take);
            if (n == 0) throw new EndOfStreamException("Unexpected end of PNG stream during chunk skip.");
            remaining -= n;
        }
    }

    private static string Ascii(ReadOnlySpan<byte> type)
    {
        return new string(new[] { (char)type[0], (char)type[1], (char)type[2], (char)type[3] });
    }

    private static string ResolveTempPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileName(sourcePath);
        return Path.Combine(dir, $".{name}.exifremover-{Guid.NewGuid():N}.tmp");
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