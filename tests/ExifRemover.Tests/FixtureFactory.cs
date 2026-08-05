using System.Text;
using ExifRemover.Engine;

namespace ExifRemover.Tests;

/// <summary>
/// Builds minimal but spec-valid JPEG/PNG files in memory with known metadata, then exercises
/// the strippers. These fixtures are generated at test time so the repository stays
/// text-only and the tests are reproducible.
/// </summary>
internal static class FixtureFactory
{
    /// <summary>
    /// Builds a tiny but valid baseline JPEG with ONLY structural segments — no metadata.
    /// </summary>
    public static byte[] MinimalJpeg()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);  // SOI
        AppendApp0Jfif(bytes);
        AppendDqt(bytes);
        AppendSof0(bytes, 4, 4);
        AppendDht(bytes);
        AppendSos(bytes);
        AppendEntropy(bytes);
        bytes.Add(0xFF); bytes.Add(0xD9);  // EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// Builds a tiny but valid baseline JPEG with EXIF, XMP, ICC, IPTC, COM, plus the structural
    /// segments (JFIF APP0, DQT, SOF0, DHT, SOS + minimal entropy, EOI).
    /// </summary>
    public static byte[] JpegWithExifXmpIccAndComment()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);  // SOI
        AppendApp0Jfif(bytes);
        AppendApp1Exif(bytes);
        AppendApp1Xmp(bytes);
        AppendApp2Icc(bytes);
        AppendApp13Iptc(bytes);
        AppendCom(bytes, "Created on Windows 11, user John Doe, machine DESKTOP-JDOE");
        AppendDqt(bytes);
        AppendSof0(bytes, 4, 4);
        AppendDht(bytes);
        AppendSos(bytes);
        AppendEntropy(bytes);
        bytes.Add(0xFF); bytes.Add(0xD9);  // EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// Baseline JPEG with metadata whose entropy-coded scan contains real 0xFF data bytes
    /// (encoded as 0xFF00 stuffing) and a restart marker (0xFFD0). Regression fixture for the
    /// bug where stuff bytes were dropped and the JPEG was silently corrupted.
    /// </summary>
    public static byte[] JpegWithStuffedScanAndMetadata()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);  // SOI
        AppendApp0Jfif(bytes);
        AppendApp1Exif(bytes);
        AppendCom(bytes, "Created on DESKTOP-JDOE by John Doe");
        AppendDqt(bytes);
        AppendSof0(bytes, 4, 4);
        AppendDht(bytes);
        AppendSos(bytes);
        // Entropy data containing 0xFF00 byte-stuffing and an RST0 (0xFFD0) marker.
        bytes.AddRange(new byte[] { 0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0x00, 0x78, 0x9A });
        bytes.Add(0xFF); bytes.Add(0xD9);  // EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// Progressive-style JPEG with EXIF and TWO scans (two SOS segments), each with its own
    /// entropy data including 0xFF00 stuffing. Regression fixture for the bug where the second
    /// scan was misread as marker segments and the stripper threw.
    /// </summary>
    public static byte[] ProgressiveLikeJpegWithExif()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);  // SOI
        AppendApp1Exif(bytes);
        AppendDqt(bytes);
        AppendSof2(bytes, 4, 4);           // SOF2 = progressive
        AppendDht(bytes);
        AppendSos(bytes);                  // first scan
        bytes.AddRange(new byte[] { 0xAA, 0xFF, 0x00, 0xBB });
        AppendSos(bytes);                  // second scan
        bytes.AddRange(new byte[] { 0xCC, 0xFF, 0x00, 0xDD });
        bytes.Add(0xFF); bytes.Add(0xD9);  // EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// Truncated JPEG: SOI + APP1 (EXIF) + nothing else. Used to verify the stripper throws cleanly
    /// and doesn't corrupt the original.
    /// </summary>
    public static byte[] TruncatedJpeg()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);
        AppendApp1Exif(bytes);
        return bytes.ToArray();
    }

    /// <summary>
    /// JPEG with an EXIF block that includes an IFD1 (thumbnail) directory.
    /// IFD0 has Make/Model/Software; IFD1 has the standard thumbnail tags
    /// (Compression=6/JPEG, JPEGInterchangeFormat pointing at the embedded
    /// thumbnail bytes, JPEGInterchangeFormatLength = thumbnail byte count).
    /// The thumbnail is a valid 1x1 baseline JPEG (re-uses the byte pattern
    /// from <see cref="MinimalJpeg"/>) so MetadataExtractor's EXIF parser
    /// will surface an ExifThumbnailDirectory for the test to inspect.
    /// D77 in M2.20.20: the inspector must mark every ExifThumbnailDirectory
    /// tag as privacy-sensitive (the stripper drops the whole APP1, including
    /// the thumbnail IFD). The pre-fix code returned false for any tag in
    /// ExifThumbnailDirectory because that directory was missing from the
    /// privacy-sensitive check, so the grid styled the thumbnail entries as
    /// not privacy-sensitive even though they would be removed.
    /// </summary>
    public static byte[] JpegWithExifThumbnail()
    {
        var bytes = new List<byte>();
        bytes.Add(0xFF); bytes.Add(0xD8);  // SOI
        AppendApp1ExifWithThumbnail(bytes);
        AppendDqt(bytes);
        AppendSof0(bytes, 4, 4);
        AppendDht(bytes);
        AppendSos(bytes);
        AppendEntropy(bytes);
        bytes.Add(0xFF); bytes.Add(0xD9);  // EOI
        return bytes.ToArray();
    }

    /// <summary>
    /// Valid minimal JPEG (D4 fixture) with 100 bytes of trailing garbage appended AFTER the EOI.
    /// Used to verify the stripper trims trailing junk instead of silently copying it into the
    /// output. A previous implementation of <c>CopyRestVerbatim</c> wrote every byte from the
    /// first SOS to EOF, so a malformed input with garbage past the EOI produced an output that
    /// was larger than necessary and lied about "lossless": the file still decoded, but the
    /// extra bytes were the corruption signature, not the image.
    /// </summary>
    public static byte[] JpegWithJunkAfterEoi()
    {
        var bytes = new List<byte>(MinimalJpeg());
        for (int i = 0; i < 100; i++) bytes.Add(0xAA);
        return bytes.ToArray();
    }

    /// <summary>
    /// Minimal PNG with only the critical chunks. 4x4 RGB.
    /// </summary>
    public static byte[] MinimalPng()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });
        WritePngChunk(bw, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });
        WritePngChunk(bw, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>
    /// PNG with metadata: tEXt, tIME, eXIf, iCCP. Plus a color-management chunk (gAMA) which
    /// must be KEPT under the Privacy profile. Plus tRNS which must always be KEPT.
    /// </summary>
    public static byte[] PngWithTextTimeExifIccp()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });

        // tEXt "Software" -> "Adobe Photoshop 25.0 (Windows)"
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("Software"));
        t.Add(0);
        t.AddRange(Encoding.UTF8.GetBytes("Adobe Photoshop 25.0 (Windows)"));
        WritePngChunk(bw, "tEXt", t.ToArray());

        WritePngChunk(bw, "tIME", new byte[] { 0x07, 0xE6, 0x0A, 0x14, 0x0E, 0x2B, 0x1E });

        var exif = new List<byte>();
        exif.AddRange(Encoding.ASCII.GetBytes("Exif"));
        exif.Add(0); exif.Add(0);
        exif.AddRange(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 });
        WritePngChunk(bw, "eXIf", exif.ToArray());

        var icc = new List<byte>();
        icc.AddRange(Encoding.ASCII.GetBytes("sRGB IEC61966-2.1"));
        icc.Add(0);
        icc.Add(0);
        icc.AddRange(new byte[] { 0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01 });
        WritePngChunk(bw, "iCCP", icc.ToArray());

        WritePngChunk(bw, "gAMA", new byte[] { 0x00, 0x00, 0xB1, 0x8F });

        WritePngChunk(bw, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });

        WritePngChunk(bw, "tRNS", new byte[] { 0x00 });

        WritePngChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    /// <summary>
    /// PNG that includes every ancillary chunk the stripper ALWAYS keeps regardless of
    /// profile (pHYs, bKGD, sBIT, tRNS) plus the always-dropped text/time/eXIf/iCCP.
    /// Used by Strip_AlwaysKeepsPngPhysBkgdSbitTrns_AcrossAllProfiles to verify the
    /// engine side of the B2 fix: the stripper never drops these chunks under any
    /// profile, even AllMetadata.
    /// </summary>
    public static byte[] PngWithAlwaysKeptAncillaryChunks()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });

        // pHYs: 4 bytes pixels-per-unit-X, 4 bytes pixels-per-unit-Y, 1 byte unit specifier
        WritePngChunk(bw, "pHYs", new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01 });

        // bKGD: 2-byte palette index (for color type 3) — index 0 transparent
        WritePngChunk(bw, "bKGD", new byte[] { 0x00, 0x00 });

        // sBIT: 3 bytes (one per channel) — significant bits per sample
        WritePngChunk(bw, "sBIT", new byte[] { 0x08, 0x08, 0x08 });

        // tRNS: 2-byte transparent palette index
        WritePngChunk(bw, "tRNS", new byte[] { 0x00, 0x00 });

        // Add a privacy chunk (tEXt) so we can verify "always-kept" survives the strip.
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("Software"));
        t.Add(0);
        t.AddRange(Encoding.UTF8.GetBytes("TestSoft 1.0"));
        WritePngChunk(bw, "tEXt", t.ToArray());

        WritePngChunk(bw, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });

        WritePngChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    /// <summary>
    /// PNG with a hIST (palette histogram) chunk. hIST is dropped under Privacy and
    /// AllMetadata but kept under Minimal — a behavior the user can't see unless the
    /// PngChunkProbe surfaces it (D69 in M2.20.18). Used by
    /// Inspect_SurfacesPngHistAsSeparateGroup to prove the inspector now reports hIST.
    ///
    /// Uses color type 2 (truecolor RGB) with the standard IDAT bytes from the
    /// PngWithTextTimeExifIccp fixture so MetadataExtractor's PNG reader is happy.
    /// The hIST chunk in a truecolor PNG is technically invalid (hIST is only
    /// meaningful for indexed-color images, per the PNG spec), but the inspector
    /// does not validate spec semantics — it just walks chunks and reports them.
    /// The point of this fixture is to exercise the surface path, not the
    /// real-world validity of the chunk.
    /// </summary>
    public static byte[] PngWithHistChunk()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00  // bit depth 8, color type 2 (truecolor RGB)
        });

        // hIST: 4 bytes of histogram data (2 bytes per palette entry frequency).
        // Per the PNG spec, hIST is only valid for color type 3; we use it in a
        // color-type-2 PNG anyway to exercise the surface path. The stripper and
        // inspector don't validate spec semantics.
        WritePngChunk(bw, "hIST", new byte[] { 0x00, 0x10, 0x00, 0x20 });

        WritePngChunk(bw, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });

        WritePngChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    /// <summary>
    /// PNG that includes a "private" ancillary chunk ("tEST") whose first byte is
    /// lowercase (ancillary) but whose remaining bytes are mixed-case — that's a
    /// valid "private" chunk by the PNG spec. The stripper's ShouldDrop has no case
    /// for "tEST" so it falls through to <c>return false</c> (keep). This is the D2
    /// regression fixture: the engine and the UI's keep set both treat unknown
    /// ancillary chunks as "kept", never "removed".
    /// </summary>
    public static byte[] PngWithUnknownAncillaryChunk()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });

        WritePngChunk(bw, "tEST", new byte[] { 0x00, 0x00, 0x00, 0x00 });

        WritePngChunk(bw, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });

        WritePngChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    /// <summary>
    /// Truncated PNG: signature + IHDR + nothing else.
    /// </summary>
    public static byte[] TruncatedPng()
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WritePngChunk(bw, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04,
            0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });
        return ms.ToArray();
    }

    // ---------- JPEG helpers ----------

    private static void AppendSegment(List<byte> b, byte marker, byte[] payload)
    {
        b.Add(0xFF); b.Add(marker);
        int len = payload.Length + 2;
        b.Add((byte)(len >> 8));
        b.Add((byte)(len & 0xFF));
        b.AddRange(payload);
    }

    private static void AppendApp0Jfif(List<byte> b) =>
        AppendSegment(b, 0xE0, new byte[]
        {
            0x4A,0x46,0x49,0x46,0x00,
            0x01,0x01,
            0x00,
            0x00,0x01, 0x00,0x01,
            0x00,0x00
        });

    private static void AppendApp1Exif(List<byte> b)
    {
        // Build TIFF stream byte-by-byte, in big-endian (since we declare MM byte order).
        using var tiff = new MemoryStream();
        // TIFF header
        tiff.WriteByte(0x4D); tiff.WriteByte(0x4D); // big-endian
        WriteBe16(tiff, 42);
        WriteBe32(tiff, 8); // IFD0 offset
        // IFD0: 3 entries
        WriteBe16(tiff, 3);
        uint dataOff = 8 + 2 + 12 * 3 + 4;
        WriteIfdEntry(tiff, 0x010F, 2, 6, dataOff);    // Make
        WriteIfdEntry(tiff, 0x0110, 2, 6, dataOff + 6); // Model
        WriteIfdEntry(tiff, 0x0131, 2, 6, dataOff + 12); // Software
        WriteBe32(tiff, 0); // next IFD = 0
        // String data (all exactly 6 bytes including null terminator)
        var make = new byte[] { (byte)'C', (byte)'a', (byte)'n', (byte)'o', (byte)'n', 0 };
        var model = new byte[] { (byte)'E', (byte)'O', (byte)'S', (byte)' ', (byte)'R', 0 };
        var sw = new byte[] { (byte)'E', (byte)'O', (byte)'S', (byte)' ', (byte)'1', 0 };
        tiff.Write(make, 0, 6);
        tiff.Write(model, 0, 6);
        tiff.Write(sw, 0, 6);

        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("Exif"));
        payload.Add(0); payload.Add(0);
        payload.AddRange(tiff.ToArray());
        AppendSegment(b, 0xE1, payload.ToArray());
    }

    /// <summary>
    /// EXIF APP1 with a thumbnail (IFD1 + a small embedded JPEG). Used by
    /// JpegWithExifThumbnail so the inspector can surface an
    /// ExifThumbnailDirectory for the D77 test. The thumbnail is the
    /// MinimalJpeg fixture (a 1x1 baseline JPEG) — same byte pattern that's
    /// used as the main image elsewhere, so we know MetadataExtractor's
    /// parser is happy with it.
    ///
    /// Layout (offsets relative to start of the TIFF stream, not the JPEG):
    ///   0..7    : TIFF header ("MM", 42, offset to IFD0=8)
    ///   8..9    : IFD0 entry count = 3
    ///  10..45   : IFD0 entries (Make, Model, Software)
    ///  46..49   : IFD0 next-IFD pointer -> ifd1Off (= 68)
    ///  50..67   : IFD0 string data (3 * 6 bytes)
    ///  68..69   : IFD1 entry count = 3
    ///  70..105  : IFD1 entries (Compression, JPEGInterchangeFormat, JPEGInterchangeFormatLength)
    /// 106..109  : IFD1 next-IFD = 0
    /// 110..    : JPEG thumbnail data (MinimalJpeg bytes)
    /// </summary>
    private static void AppendApp1ExifWithThumbnail(List<byte> b)
    {
        var thumbnail = MinimalJpeg();
        uint tiffIfd0Off = 8;
        uint tiffIfd0DataOff = tiffIfd0Off + 2 + 3 * 12 + 4; // count + 3 entries + next-IFD
        uint tiffIfd1Off = tiffIfd0DataOff + 3 * 6;          // 3 string data slots of 6 bytes each
        uint tiffJpegThumbOff = tiffIfd1Off + 2 + 3 * 12 + 4; // count + 3 entries + next-IFD
        uint tiffJpegThumbLen = (uint)thumbnail.Length;

        using var tiff = new MemoryStream();
        // TIFF header
        tiff.WriteByte(0x4D); tiff.WriteByte(0x4D); // big-endian
        WriteBe16(tiff, 42);
        WriteBe32(tiff, tiffIfd0Off);

        // IFD0: 3 entries, all with the same 6-byte ASCII string shape
        WriteBe16(tiff, 3);
        WriteIfdEntry(tiff, 0x010F, 2, 6, tiffIfd0DataOff);     // Make
        WriteIfdEntry(tiff, 0x0110, 2, 6, tiffIfd0DataOff + 6);  // Model
        WriteIfdEntry(tiff, 0x0131, 2, 6, tiffIfd0DataOff + 12); // Software
        WriteBe32(tiff, tiffIfd1Off); // next IFD -> IFD1

        // IFD0 string data (each exactly 6 bytes including null terminator)
        var make = new byte[] { (byte)'C', (byte)'a', (byte)'n', (byte)'o', (byte)'n', 0 };
        var model = new byte[] { (byte)'E', (byte)'O', (byte)'S', (byte)' ', (byte)'R', 0 };
        var sw = new byte[] { (byte)'E', (byte)'O', (byte)'S', (byte)' ', (byte)'1', 0 };
        tiff.Write(make, 0, 6);
        tiff.Write(model, 0, 6);
        tiff.Write(sw, 0, 6);

        // IFD1: 3 entries, all with values that fit in the 4-byte value field
        // (SHORT and LONG types). No external data — the JPEG thumbnail lives
        // at the end of the TIFF stream, referenced by JPEGInterchangeFormat.
        WriteBe16(tiff, 3);
        WriteIfdEntry(tiff, 0x0103, 3, 1, 6);                  // Compression = 6 (JPEG)
        WriteIfdEntry(tiff, 0x0201, 4, 1, tiffJpegThumbOff);   // JPEGInterchangeFormat = offset
        WriteIfdEntry(tiff, 0x0202, 4, 1, tiffJpegThumbLen);   // JPEGInterchangeFormatLength = length
        WriteBe32(tiff, 0); // next IFD = 0

        // JPEG thumbnail bytes (a real 1x1 baseline JPEG)
        tiff.Write(thumbnail, 0, thumbnail.Length);

        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("Exif"));
        payload.Add(0); payload.Add(0);
        payload.AddRange(tiff.ToArray());
        AppendSegment(b, 0xE1, payload.ToArray());
    }

    private static void WriteIfdEntry(Stream s, ushort tag, ushort type, uint count, uint valueOffset)
    {
        WriteBe16(s, tag);
        WriteBe16(s, type);
        WriteBe32(s, count);
        WriteBe32(s, valueOffset);
    }

    private static void WriteBe16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteBe32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void AppendApp1Xmp(List<byte> b)
    {
        var ns = "http://ns.adobe.com/xap/1.0/\0";
        var xmp = "<?xpacket begin='' id=''?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmp:CreatorTool='Adobe Lightroom 13' xmp:CreateDate='2026-01-01T00:00:00Z'/></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
        var payload = new byte[Encoding.ASCII.GetByteCount(ns) + Encoding.UTF8.GetByteCount(xmp)];
        var enc = Encoding.ASCII.GetBytes(ns);
        Array.Copy(enc, 0, payload, 0, enc.Length);
        enc = Encoding.UTF8.GetBytes(xmp);
        Array.Copy(enc, 0, payload, Encoding.ASCII.GetByteCount(ns), enc.Length);
        AppendSegment(b, 0xE1, payload);
    }

    private static void AppendApp2Icc(List<byte> b)
    {
        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("ICC_PROFILE\0"));
        payload.Add(0x01); payload.Add(0x01);
        for (int i = 0; i < 128; i++) payload.Add((byte)(i ^ 0xAA));
        AppendSegment(b, 0xE2, payload.ToArray());
    }

    private static void AppendApp13Iptc(List<byte> b)
    {
        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes("Photoshop 3.0"));
        payload.Add(0x00);
        payload.AddRange(new byte[] { 0x38, 0x42, 0x49, 0x4D, 0x04, 0x04, 0x00, 0x00 });
        payload.AddRange(new byte[] { 0x1C, 0x02, 0x05, 0x00, 0x04, (byte)'J', (byte)'o', (byte)'h', (byte)'n' });
        AppendSegment(b, 0xED, payload.ToArray());
    }

    private static void AppendCom(List<byte> b, string text) =>
        AppendSegment(b, 0xFE, Encoding.ASCII.GetBytes(text));

    private static void AppendDqt(List<byte> b)
    {
        var payload = new byte[1 + 64];
        payload[0] = 0x00;
        for (int i = 0; i < 64; i++) payload[1 + i] = 1;
        AppendSegment(b, 0xDB, payload);
    }

    private static void AppendSof0(List<byte> b, int width, int height)
    {
        AppendSegment(b, 0xC0, new byte[]
        {
            0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8),  (byte)width,
            0x01, 0x01, 0x11, 0x00
        });
    }

    private static void AppendSof2(List<byte> b, int width, int height)
    {
        // SOF2 (progressive DCT) — same payload shape as SOF0.
        AppendSegment(b, 0xC2, new byte[]
        {
            0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8),  (byte)width,
            0x01, 0x01, 0x11, 0x00
        });
    }

    private static void AppendDht(List<byte> b)
    {
        var payload = new byte[36];
        payload[0] = 0x00;
        payload[1] = 1;
        for (int i = 2; i < 17; i++) payload[i] = 0;
        payload[17] = 0x00;
        payload[18] = 0x10;
        payload[19] = 1;
        for (int i = 20; i < 35; i++) payload[i] = 0;
        payload[35] = 0x00;
        AppendSegment(b, 0xC4, payload);
    }

    private static void AppendSos(List<byte> b)
    {
        b.Add(0xFF); b.Add(0xDA);
        var sos = new byte[] { 0x00, 0x0C, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00 };
        b.AddRange(sos);
    }

    private static void AppendEntropy(List<byte> b)
    {
        // 1-bit DC code '0' + 1-bit AC EOB code '0', padded with 1 bits to byte boundary.
        // Resulting byte sequence: 0x00, 0x3F (0011_1111).
        b.Add(0x00);
        b.Add(0x3F);
    }

    // ---------- PNG helpers ----------

    private static void WritePngChunk(BinaryWriter bw, string type, byte[] data)
    {
        // PNG spec: 4-byte big-endian length
        bw.Write((byte)(data.Length >> 24));
        bw.Write((byte)(data.Length >> 16));
        bw.Write((byte)(data.Length >> 8));
        bw.Write((byte)data.Length);
        var t = Encoding.ASCII.GetBytes(type);
        bw.Write(t);
        bw.Write(data);
        // CRC over type + data, written as big-endian 4 bytes (PNG spec).
        var crc = Crc32Compute(t, data);
        bw.Write((byte)(crc >> 24));
        bw.Write((byte)(crc >> 16));
        bw.Write((byte)(crc >> 8));
        bw.Write((byte)(crc & 0xFF));
    }

    private static uint Crc32Compute(byte[] type, byte[] data)
    {
        const uint poly = 0xEDB88320u;
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (poly ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++) crc = t[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < data.Length; i++) crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}