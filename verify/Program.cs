// Standalone real-image round-trip verifier.
// Reads a JPEG/PNG from stdin, runs the real StripPipeline on it, and emits
// a verification report to stdout. Used by verify_real_images.py to confirm
// C1/C2/C3 are fixed against actual camera-style JPEGs produced by Pillow.

using System;
using System.IO;
using ExifRemover.Engine;

namespace ExifRemover.Verifier;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: verifier <input> <output> <profile>");
            return 2;
        }
        var input = args[0];
        var output = args[1];
        var profile = Enum.Parse<StripProfile>(args[2], true);

        var bytes = File.ReadAllBytes(input);

        // D3: Do NOT pre-create or pre-clear the output file. The previous "touch then clear"
        // block (File.WriteAllBytes(output, bytes) followed by File.WriteAllBytes(output, []))
        // served no purpose — the stripper with overwriteSource=false picks a non-clashing
        // sibling via AtomicFile.NextNonClashingPath, so a pre-existing output path is not
        // what gets written to. Worse, if a caller passed the same path for input and output
        // (e.g. an out-of-place test that the python harness doesn't currently do, but a
        // future caller might), the touch step would overwrite the source and the clear
        // step would then truncate it to zero bytes — destroying the input. Letting the
        // stripper own the output path avoids both the no-op and the destruction.

        try
        {
            var pre = MetadataInspector.Inspect(input);
            var preCount = pre.Entries.Count;
            var preBytes = bytes.Length;

            StripResult result;
            try
            {
                result = StripPipeline.Strip(input, output, false, profile);
            }
            catch (Exception stripEx)
            {
                Console.WriteLine($"STRIP_EXCEPTION: {stripEx.GetType().Name}: {stripEx.Message}");
                Console.WriteLine($"input_bytes={preBytes}");
                Console.WriteLine($"pre_metadata_entries={preCount}");
                return 3;
            }
            // The stripper may write to a non-clashing sibling (e.g. out (2).jpg) when
            // the requested output already exists. Read the actual output path the stripper
            // chose so we verify the right file.
            var outBytes = File.ReadAllBytes(result.OutputPath);
            output = result.OutputPath;

            // Check the OUTPUT file BEFORE the catch block deletes it. Also output the
            // result details.
            Console.WriteLine($"strip_output_path={result.OutputPath} input={input} output={output}");
            Console.WriteLine($"strip_overwrote={result.OverwroteSource} original_size={result.OriginalSizeBytes} output_size={result.OutputSizeBytes}");

            var post = MetadataInspector.Inspect(output);
            var postCount = post.Entries.Count;

            // Find the entropy region: from the first SOS to the EOI. This is the region
            // we copy verbatim. The bytes BEFORE the SOS (headers, metadata) differ
            // because the stripper removes metadata. Bytes AFTER the SOS (entropy) must
            // be byte-for-byte identical between input and output — that's the "lossless"
            // guarantee.
            int firstSosInput = FindFirstSos(bytes);
            int firstSosOutput = FindFirstSos(outBytes);
            int entropyMismatch = -1;
            if (firstSosInput > 0 && firstSosOutput > 0)
            {
                int maxLen = Math.Min(bytes.Length - firstSosInput, outBytes.Length - firstSosOutput);
                for (int i = 0; i < maxLen; i++)
                {
                    if (bytes[firstSosInput + i] != outBytes[firstSosOutput + i])
                    {
                        entropyMismatch = i;
                        break;
                    }
                }
            }

            Console.WriteLine($"input_bytes={preBytes} output_bytes={outBytes.Length}");
            Console.WriteLine($"pre_metadata_entries={preCount} post_metadata_entries={postCount}");
            Console.WriteLine($"dropped_segments={result.DroppedSegments}");
            Console.WriteLine($"entropy_first_sos_input_offset={firstSosInput} output={firstSosOutput}");
            Console.WriteLine($"entropy_mismatch_offset={entropyMismatch}");
            // D90 (M2.20.29): the pre-fix code unconditionally called IsValidJpeg
            // on the output. The Python harness only runs JPEG inputs so the bug
            // never fired, but a PNG input would produce a perfectly-valid PNG
            // output and the verifier would still report "output_decodes=no"
            // because the PNG signature is not the JPEG signature. Fix: detect
            // the input format (JPEG vs PNG) and call the right validator.
            // The stripper preserves the input format, so the output's format
            // is the same as the input's.
            var outputFormat = ImageFormatDetector.Detect(bytes);
            var outputValid = outputFormat == ImageFormat.Jpeg
                ? IsValidJpeg(outBytes)
                : outputFormat == ImageFormat.Png
                    ? IsValidPng(outBytes)
                    : false; // unknown format — neither validator applies
            Console.WriteLine($"output_format={outputFormat}");
            Console.WriteLine($"output_decodes={(outputValid ? "yes" : "no")}");
            Console.WriteLine($"stuffed_ff00_count_input={StreamHelpers.CountStuffedFf00(bytes)} output={StreamHelpers.CountStuffedFf00(outBytes)}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int FindFirstSos(byte[] b)
    {
        for (int i = 0; i < b.Length - 1; i++)
            if (b[i] == 0xFF && b[i + 1] == 0xDA) return i;
        return -1;
    }

    private static bool IsValidJpeg(byte[] b)
    {
        return b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[^2] == 0xFF && b[^1] == 0xD9;
    }

    /// <summary>
    /// D90 (M2.20.29): the pre-fix verifier hard-coded IsValidJpeg for the
    /// <c>output_decodes</c> check, which would always report <c>no</c> for a
    /// PNG output (the PNG signature is not the JPEG signature). This helper
    /// is the PNG equivalent: a PNG starts with the 8-byte signature
    /// (89 50 4E 47 0D 0A 1A 0A) and ends with the 4-byte IEND chunk
    /// (length-prefixed, with type "IEND" and a 4-byte CRC). We don't
    /// validate the CRC (the stripper recomputed it during the rewrite),
    /// just the signature + IEND presence.
    /// </summary>
    private static bool IsValidPng(byte[] b)
    {
        // The full IEND chunk trailer is 12 bytes: a 4-byte big-endian
        // length (0 for IEND — IEND has no payload), the 4-byte ASCII
        // type "IEND", and a 4-byte CRC. A buffer of < 12 bytes cannot
        // contain the IEND trailer, so the check is moot below that
        // length. The signature alone is 8 bytes, so the combined minimum
        // is 8 + 12 = 20 bytes; the L146 check handles both bounds.
        if (b.Length < 20) return false;
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A (8 bytes).
        if (!b.AsSpan(0, 8).SequenceEqual(ImageFormatDetector.PngSignature)) return false;
        // IEND: a 4-byte length (0), a 4-byte type ("IEND"), and a 4-byte CRC.
        // The length field for IEND is 0 (no payload), so the 4 bytes before
        // "IEND" must be 0x00 0x00 0x00 0x00.
        int iendOffset = b.Length - 12;
        if (b[iendOffset] != 0x00 || b[iendOffset + 1] != 0x00 ||
            b[iendOffset + 2] != 0x00 || b[iendOffset + 3] != 0x00) return false;
        // IEND type as ASCII. Using a constant string for clarity instead
        // of 4 individual char comparisons.
        if (!b.AsSpan(iendOffset + 4, 4).SequenceEqual(IendTypeBytes)) return false;
        // CRC bytes can be anything (the stripper recomputed them).
        return true;
    }

    // PNG signature: 89 50 4E 47 0D 0A 1A 0A (per RFC 2083 / ISO/IEC 15948).
    // The first 4 bytes are also intentionally chosen to include
    // high-bit and control characters to detect bad file transfers.
    //
    // D106 (M2.20.44): the pre-fix code had its own local `PngSignature`
    // byte[] here, duplicating the canonical one in
    // `ExifRemover.Engine.ImageFormatDetector.PngSignature`. The verifier
    // embeds the Engine sources via `<Compile Include>`, so it can
    // reference the Engine's public constant directly. The 4× duplicate
    // (Engine x3 + Verifier x1) is now consolidated to a single source.

    // IEND chunk type as ASCII bytes ("IEND" = 49 45 4E 44).
    // Using a byte array (not a string) for the SequenceEqual comparison
    // against the buffer — strings would require an Encoding round-trip.
    private static readonly byte[] IendTypeBytes = "IEND"u8.ToArray();
}
