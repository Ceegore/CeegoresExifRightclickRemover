namespace ExifRemover.Engine;

public static class JpegMetadataStripper
{
    private const byte MarkerPrefix = 0xFF;

    private static ReadOnlySpan<byte> JfifMagic => new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 };

    public static StripResult Strip(string sourcePath, string outputPath, bool overwriteSource, StripProfile profile)
    {
        long originalSize = new FileInfo(sourcePath).Length;
        int dropped = 0;
        bool keepIcc = profile == StripProfile.Minimal;
        bool keepJfif = true;

        string actualOutputPath = overwriteSource
            ? ResolveTempPath(sourcePath)
            : AtomicFile.NextNonClashingPath(outputPath);

        FileStream? input = null;
        try
        {
            input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[2];
            ReadExact(input, header);

            Span<byte> lenBuf = stackalloc byte[2];
            Span<byte> jfifSniff = stackalloc byte[5];
            Span<byte> iccSniff = stackalloc byte[12];
            if (header[0] != MarkerPrefix || header[1] != 0xD8)
            {
                throw new InvalidDataException("Not a JPEG file (missing SOI marker).");
            }

            using (var output = new FileStream(actualOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                output.Write(header);

                while (true)
                {
                    if (!ReadMarker(input, out byte marker))
                    {
                        throw new InvalidDataException($"Unexpected end of file while reading JPEG segments at offset {input.Position}.");
                    }

                    if (marker == 0xD9)
                    {
                        // EOI before any scan (degenerate, but pass it through and stop).
                        output.WriteByte(MarkerPrefix);
                        output.WriteByte(marker);
                        break;
                    }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        // Standalone markers (TEM / RSTn) carry no length; copy verbatim.
                        output.WriteByte(MarkerPrefix);
                        output.WriteByte(marker);
                        continue;
                    }

                    ReadExact(input, lenBuf);
                    int segLen = (lenBuf[0] << 8) | lenBuf[1];
                    if (segLen < 2)
                    {
                        throw new InvalidDataException($"Invalid JPEG segment length {segLen} for marker 0x{marker:X2}.");
                    }
                    int payloadLen = segLen - 2;

                    if (marker == 0xDA)
                    {
                        // Start Of Scan. All metadata segments precede the scan, so once we reach
                        // it we are done editing: write the SOS header, then copy every remaining
                        // byte of the file verbatim. This preserves the entropy-coded bitstream
                        // byte-for-byte (including 0xFF00 byte-stuffing and RSTn markers) and any
                        // further scans of a progressive JPEG, ending with the EOI marker.
                        output.WriteByte(MarkerPrefix);
                        output.WriteByte(marker);
                        output.Write(lenBuf);
                        CopyExactly(input, output, payloadLen);
                        CopyRestVerbatim(input, output);
                        break;
                    }

                    if (ShouldDrop(marker, input, payloadLen, keepIcc, keepJfif, jfifSniff, iccSniff, out _))
                    {
                        SkipExactly(input, payloadLen);
                        dropped++;
                        continue;
                    }

                    output.WriteByte(MarkerPrefix);
                    output.WriteByte(marker);
                    output.Write(lenBuf);
                    CopyExactly(input, output, payloadLen);
                }
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

    private static bool ShouldDrop(byte marker, FileStream input, int payloadLen, bool keepIcc, bool keepJfif, Span<byte> jfifSniff, Span<byte> iccSniff, out string reason)
    {
        _ = payloadLen;
        reason = string.Empty;

        if (marker == 0xFE)
        {
            reason = "COM (comment)";
            return true;
        }

        if (marker >= 0xE0 && marker <= 0xEF)
        {
            if (marker == 0xE0 && keepJfif)
            {
                long pos = input.Position;
                int read = ReadUpTo(input, jfifSniff);
                input.Position = pos;

                if (read >= 5 && jfifSniff.SequenceEqual(JfifMagic))
                {
                    reason = "JFIF";
                    return false;
                }
                if (read >= 5 && jfifSniff[0] == 0x4A && jfifSniff[1] == 0x46 && jfifSniff[2] == 0x58 && jfifSniff[3] == 0x58 && jfifSniff[4] == 0x00)
                {
                    reason = "JFXX";
                    return false;
                }
                reason = "APP0 (non-JFIF)";
                return true;
            }

            if (marker == 0xE2 && keepIcc)
            {
                long pos = input.Position;
                int read = ReadUpTo(input, iccSniff);
                input.Position = pos;
                if (read >= 12)
                {
                    if (iccSniff[0] == 0x49 && iccSniff[1] == 0x43 && iccSniff[2] == 0x43 && iccSniff[3] == 0x5F
                        && iccSniff[4] == 0x50 && iccSniff[5] == 0x52 && iccSniff[6] == 0x4F && iccSniff[7] == 0x46
                        && iccSniff[8] == 0x49 && iccSniff[9] == 0x4C && iccSniff[10] == 0x45 && iccSniff[11] == 0x00)
                    {
                        reason = "ICC profile";
                        return false;
                    }
                }
            }

            reason = $"APP{marker - 0xE0}";
            return true;
        }

        reason = "kept";
        return false;
    }

    /// <summary>
    /// Copies the remainder of the input stream (the entropy-coded scan data, any subsequent
    /// progressive scans, and the trailing EOI) to the output verbatim. Validates that an EOI
    /// (0xFFD9) marker is present so a truncated scan is rejected rather than silently written.
    /// </summary>
    private static void CopyRestVerbatim(FileStream input, FileStream output)
    {
        var buf = new byte[64 * 1024];
        bool sawEoi = false;
        int prevByte = -1;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            if (!sawEoi)
            {
                if (prevByte == MarkerPrefix && buf[0] == 0xD9)
                {
                    sawEoi = true;
                }
                for (int i = 1; i < n && !sawEoi; i++)
                {
                    if (buf[i - 1] == MarkerPrefix && buf[i] == 0xD9)
                    {
                        sawEoi = true;
                    }
                }
            }
            output.Write(buf, 0, n);
            prevByte = buf[n - 1];
        }

        if (!sawEoi)
        {
            throw new InvalidDataException("Unexpected end of file while reading entropy-coded JPEG data; no EOI marker reached.");
        }
    }

    private static bool ReadMarker(FileStream input, out byte marker)
    {
        int b1 = input.ReadByte();
        if (b1 == -1) { marker = 0; return false; }
        if (b1 != MarkerPrefix)
        {
            throw new InvalidDataException($"Expected 0xFF marker byte but got 0x{b1:X2} at file offset {input.Position - 1}.");
        }
        int b2 = input.ReadByte();
        if (b2 == -1) { marker = 0; return false; }
        while (b2 == 0xFF)
        {
            b2 = input.ReadByte();
            if (b2 == -1) { marker = 0; return false; }
        }
        marker = (byte)b2;
        return true;
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n == 0) throw new EndOfStreamException("Unexpected end of JPEG stream.");
            total += n;
        }
    }

    private static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static void CopyExactly(Stream src, Stream dst, int count)
    {
        var buf = new byte[Math.Min(count, 64 * 1024)];
        int remaining = count;
        while (remaining > 0)
        {
            int take = Math.Min(remaining, buf.Length);
            int n = src.Read(buf, 0, take);
            if (n == 0) throw new EndOfStreamException("Unexpected end of JPEG stream during segment copy.");
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }

    private static void SkipExactly(Stream s, int count)
    {
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        var buf = new byte[Math.Min(count, 64 * 1024)];
        int remaining = count;
        while (remaining > 0)
        {
            int n = s.Read(buf, 0, Math.Min(remaining, buf.Length));
            if (n == 0) throw new EndOfStreamException("Unexpected end of JPEG stream during segment skip.");
            remaining -= n;
        }
    }

    private static string ResolveTempPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileName(sourcePath);
        return Path.Combine(dir, $".{name}.exifremover-{Guid.NewGuid():N}.tmp");
    }
}
