namespace ExifRemover.Engine;

public static class JpegMetadataStripper
{
    private const byte MarkerPrefix = 0xFF;

    private static ReadOnlySpan<byte> JfifMagic => new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 };
    // JFXX (JFIF extension) magic prefix: 4A 46 58 58 00. Per the JFIF standard,
    // APP0 segments whose 5-byte prefix is JFXX (instead of JFIF) carry a
    // thumbnail image, not a JFIF header. We keep JFXX segments under the
    // same rules as JFIF (the thumbnail is color-management data, not
    // personal info) — but the prefix is distinct from JFIF.
    private static ReadOnlySpan<byte> JfxxMagic => new byte[] { 0x4A, 0x46, 0x58, 0x58, 0x00 };
    // ICC profile magic prefix: 49 43 43 5F 50 52 4F 46 49 4C 45 00 = "ICC_PROFILE\0"
    // (12 bytes). Per the ICC profile spec, every ICC profile starts with
    // this 12-byte header. APP2 segments whose 12-byte prefix matches are
    // ICC profiles and are kept under the Privacy/Minimal profiles'
    // "keep ICC" rule.
    private static ReadOnlySpan<byte> IccProfileMagic => new byte[]
        { 0x49, 0x43, 0x43, 0x5F, 0x50, 0x52, 0x4F, 0x46, 0x49, 0x4C, 0x45, 0x00 };

    public static StripResult Strip(string sourcePath, string outputPath, bool overwriteSource, StripProfile profile)
    {
        int dropped = 0;
        bool keepIcc = profile == StripProfile.Minimal;
        bool keepJfif = true;

        string actualOutputPath = overwriteSource
            ? AtomicFile.ResolveTempPath(sourcePath)
            : AtomicFile.NextNonClashingPath(outputPath);

        FileStream? input = null;
        try
        {
            // D72: read the original size INSIDE the try block so a missing/inaccessible
            // file produces a single FileNotFoundException from FileInfo with a clear
            // message ("Could not find file 'foo.jpg'.") and lets the catch block run
            // its cleanup. The pre-fix code computed originalSize BEFORE the try, so
            // a race-condition "file deleted between PathFilter.FileExists and Strip"
            // threw FileNotFoundException outside the catch — the temp output file
            // (if any was created by NextNonClashingPath) was orphaned instead of
            // cleaned up. Strip is still expected to throw; we just want the cleanup
            // path to run too.
            long originalSize = new FileInfo(sourcePath).Length;
            input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[2];
            StreamHelpers.ReadExact(input, header, "JPEG");

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
                    int fillByteCount;
                    if (!ReadMarker(input, out byte marker, out fillByteCount))
                    {
                        throw new InvalidDataException($"Unexpected end of file while reading JPEG segments at offset {input.Position}.");
                    }

                    // D79: re-emit any 0xFF fill bytes that ReadMarker consumed before the marker.
                    // The pre-fix code only wrote `0xFF <marker>` (2 bytes) for every segment, which
                    // silently dropped fill bytes. A JPEG with 0xFF padding before the EOI would
                    // produce a smaller output and trip the Changed flag for a file that
                    // actually didn't change. The helper restores byte-for-byte output.
                    void WriteMarker()
                    {
                        for (int i = 0; i < fillByteCount; i++)
                        {
                            output.WriteByte(MarkerPrefix);
                        }
                        output.WriteByte(MarkerPrefix);
                        output.WriteByte(marker);
                    }

                    if (marker == 0xD9)
                    {
                        // EOI before any scan (degenerate, but pass it through and stop).
                        WriteMarker();
                        break;
                    }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        // Standalone markers (TEM / RSTn) carry no length; copy verbatim.
                        WriteMarker();
                        continue;
                    }

                    StreamHelpers.ReadExact(input, lenBuf, "JPEG");
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
                        WriteMarker();
                        output.Write(lenBuf);
                        CopyExactly(input, output, payloadLen);
                        CopyRestVerbatim(input, output);
                        break;
                    }

                    if (ShouldDrop(marker, input, payloadLen, keepIcc, keepJfif, jfifSniff, iccSniff, out _))
                    {
                        // D87 (M2.20.27): the pre-fix code called a private
                        // SkipExactly(int count) here. Now routed through
                        // StreamHelpers.SkipExactly which takes long count.
                        // The implicit widening from int to long is free.
                        StreamHelpers.SkipExactly(input, payloadLen, "JPEG");
                        dropped++;
                        continue;
                    }

                    WriteMarker();
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
            // D86 (M2.20.26): the inline cleanup expression was extracted to
            // AtomicFile.CleanupOrphanedOutput. The semantic is identical:
            // delete the output file if (a) it exists, (b) it's not the same
            // path as the source under the overwrite path. Swallow any
            // cleanup exception so it doesn't mask the stripper exception
            // that this catch is re-throwing.
            AtomicFile.CleanupOrphanedOutput(actualOutputPath, sourcePath, overwriteSource);
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
                // D92 (M2.20.30): best-effort read routed through StreamHelpers.ReadUpTo
                // (was a private ReadUpTo here, byte-identical to the TryReadExact in
                // PngChunkProbe). A short read is acceptable here — if the segment is
                // shorter than the 5-byte JFIF magic prefix, it can't be a JFIF header.
                int read = StreamHelpers.ReadUpTo(input, jfifSniff);
                input.Position = pos;

                if (read >= 5 && jfifSniff.SequenceEqual(JfifMagic))
                {
                    reason = "JFIF";
                    return false;
                }
                if (read >= 5 && jfifSniff.SequenceEqual(JfxxMagic))
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
                // D92 (M2.20.30): routed through StreamHelpers.ReadUpTo (see JFIF comment above).
                // The ICC profile sniff needs 12 bytes; a short read means "not an ICC profile".
                int read = StreamHelpers.ReadUpTo(input, iccSniff);
                input.Position = pos;
                if (read >= 12 && iccSniff.SequenceEqual(IccProfileMagic))
                {
                    reason = "ICC profile";
                    return false;
                }
            }

            // D81 (M2.20.23): APP14 (Adobe marker) is color-management metadata, not personal
            // information. The catalog contract is "Privacy keeps color management" — every
            // other color-management hint (JFIF, ICC, gAMA, cHRM, sRGB) is kept under at
            // least one profile. Dropping APP14 violates that contract and causes a visible
            // color shift on CMYK JPEGs, which identify their color space via APP14's
            // color-transform byte. Fix: keep APP14 under all profiles. Note that
            // MetadataExtractor does not surface APP14 as a directory, so the review grid
            // never shows the entry — this fix is silent from the UI's perspective, but
            // the byte-preservation matters for CMYK decoding.
            if (marker == 0xEE)
            {
                reason = "APP14 (Adobe)";
                return false;
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
    /// Stops at the EOI: any bytes after the EOI in a malformed file are discarded, not
    /// copied through (D4: silently appending garbage would have bloated the output and
    /// broken the "lossless" guarantee on malformed inputs).
    /// </summary>
    private static void CopyRestVerbatim(FileStream input, FileStream output)
    {
        var buf = new byte[64 * 1024];
        bool sawEoi = false;
        int prevByte = -1;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            int writeLen = sawEoi ? 0 : n;
            if (!sawEoi)
            {
                // Boundary case: the previous buffer ended in 0xFF and this buffer starts with 0xD9.
                // The 0xFF was already written in the previous iteration; we only need to emit 0xD9.
                if (prevByte == MarkerPrefix && buf[0] == 0xD9)
                {
                    sawEoi = true;
                    writeLen = 1;
                }
                else
                {
                    for (int i = 1; i < n; i++)
                    {
                        if (buf[i - 1] == MarkerPrefix && buf[i] == 0xD9)
                        {
                            sawEoi = true;
                            // Write up to AND including the 0xD9 byte. Stop there.
                            writeLen = i + 1;
                            break;
                        }
                    }
                }
            }
            if (writeLen > 0)
            {
                output.Write(buf, 0, writeLen);
            }
            if (sawEoi) break;
            prevByte = buf[n - 1];
        }

        if (!sawEoi)
        {
            throw new InvalidDataException("Unexpected end of file while reading entropy-coded JPEG data; no EOI marker reached.");
        }
    }

    private static bool ReadMarker(FileStream input, out byte marker, out int fillByteCount)
    {
        int b1 = input.ReadByte();
        if (b1 == -1) { marker = 0; fillByteCount = 0; return false; }
        if (b1 != MarkerPrefix)
        {
            throw new InvalidDataException($"Expected 0xFF marker byte but got 0x{b1:X2} at file offset {input.Position - 1}.");
        }
        int b2 = input.ReadByte();
        if (b2 == -1) { marker = 0; fillByteCount = 0; return false; }
        int fills = 0;
        while (b2 == 0xFF)
        {
            fills++;
            b2 = input.ReadByte();
            if (b2 == -1) { marker = 0; fillByteCount = fills; return false; }
        }
        marker = (byte)b2;
        fillByteCount = fills;
        return true;
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
}
