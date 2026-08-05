using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileSystem;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Jfif;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.Xmp;
using MetadataExtractor.Formats.Photoshop;

namespace ExifRemover.Engine;

public static class MetadataInspector
{
    public static FileInspection Inspect(string path)
    {
        // D71: DetectFile's File.OpenRead call can throw FileNotFoundException,
        // UnauthorizedAccessException, or IOException (file deleted between
        // PathFilter.FileExists and here, file locked by another process, network
        // share offline, etc.). The previous code called DetectFile OUTSIDE the
        // try/catch, so the exception propagated up to OverlayViewModel.InspectData
        // and crashed the Task.Run with an unhandled error. The user would see
        // a stack trace in the status strip instead of a clean "could not read
        // file: …" message. The fix: detect the format in a dedicated try block
        // and return a FileInspection with a clear Error when the file is
        // inaccessible. Three distinct error shapes are surfaced, in order of
        // specificity:
        //   1. File not found       (FileNotFoundException / DirectoryNotFoundException)
        //   2. Access denied        (UnauthorizedAccessException)
        //   3. Generic I/O failure  (anything else IOException-derived)
        // The user gets a useful "this is why we couldn't read it" message in
        // every case instead of a stack trace.
        ImageFormat format;
        try
        {
            format = ImageFormatDetector.DetectFile(path);
        }
        catch (FileNotFoundException ex)
        {
            return new FileInspection
            {
                Path = path,
                Format = ImageFormat.Unknown,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = 0,
                Error = $"File not found: {ex.Message}"
            };
        }
        catch (DirectoryNotFoundException ex)
        {
            return new FileInspection
            {
                Path = path,
                Format = ImageFormat.Unknown,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = 0,
                Error = $"Directory not found: {ex.Message}"
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FileInspection
            {
                Path = path,
                Format = ImageFormat.Unknown,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = 0,
                Error = $"Access denied: {ex.Message}"
            };
        }
        catch (IOException ex)
        {
            return new FileInspection
            {
                Path = path,
                Format = ImageFormat.Unknown,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = 0,
                Error = $"Could not read file: {ex.Message}"
            };
        }

        if (format == ImageFormat.Unknown)
        {
            return new FileInspection
            {
                Path = path,
                Format = format,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = SafeSize(path),
                Error = "Unsupported file format (only JPEG and PNG are supported)."
            };
        }

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var entries = new List<MetadataEntry>(capacity: 64);
            foreach (var dir in directories)
            {
                MapDirectory(dir, entries);
            }

            // MetadataExtractor's PNG reader doesn't surface eXIf as a separate group — it
            // rolls eXIf into the PngText bucket. The stripper, however, drops eXIf chunks
            // independently of tEXt, so the user would otherwise see a PNG with an embedded
            // EXIF block silently disappear. Run a small byte-level PNG probe and add a
            // PngExif entry whenever an eXIf chunk is actually present, so the grid is honest
            // about what the stripper will remove.
            if (format == ImageFormat.Png)
            {
                PngChunkProbe.ProbeForMissingEntries(path, entries);
            }

            return new FileInspection
            {
                Path = path,
                Format = format,
                Entries = entries,
                FileSizeBytes = SafeSize(path)
            };
        }
        catch (Exception ex)
        {
            return new FileInspection
            {
                Path = path,
                Format = format,
                Entries = Array.Empty<MetadataEntry>(),
                FileSizeBytes = SafeSize(path),
                Error = $"Could not read metadata: {ex.Message}"
            };
        }
    }

    private static void MapDirectory(global::MetadataExtractor.Directory dir, List<MetadataEntry> sink)
    {
        // Structural readouts (file name/size, detected type, JPEG frame geometry, Huffman tables)
        // are intrinsic to the file and not removable metadata. Showing them in the review grid
        // is noise that makes stripping look ineffective, so skip them entirely.
        if (IsStructuralDirectory(dir))
        {
            return;
        }

        foreach (var tag in dir.Tags)
        {
            var group = MapGroup(dir, tag.Type);
            if (IsStructuralGroup(group))
            {
                continue;
            }
            var sensitive = IsPrivacySensitive(dir, tag.Name, group);
            var size = EstimateTagSize(dir, tag.Type);
            sink.Add(new MetadataEntry(group, tag.Name, tag.Description ?? string.Empty, size, sensitive));
        }
    }

    private static bool IsStructuralDirectory(global::MetadataExtractor.Directory dir) =>
        dir is FileMetadataDirectory or FileTypeDirectory or JpegDirectory or HuffmanTablesDirectory;

    private static bool IsStructuralGroup(string group) =>
        group is MetadataGroups.PngIhdr or MetadataGroups.PngPlte
            or MetadataGroups.PngIdat or MetadataGroups.PngIend;

    private static string MapGroup(global::MetadataExtractor.Directory dir, int tagType) => dir switch
    {
        ExifIfd0Directory => MetadataGroups.ExifIfd0,
        ExifSubIfdDirectory => MetadataGroups.ExifSubIfd,
        ExifThumbnailDirectory => MetadataGroups.ExifThumbnail,
        GpsDirectory => MetadataGroups.ExifGps,
        ExifInteropDirectory => MetadataGroups.ExifInterop,
        IptcDirectory => MetadataGroups.Iptc,
        XmpDirectory => MetadataGroups.Xmp,
        IccDirectory => MetadataGroups.Icc,
        JfifDirectory => MetadataGroups.Jfif,
        JpegCommentDirectory => MetadataGroups.JpegComment,
        PhotoshopDirectory => MetadataGroups.Photoshop,
        PngDirectory => MapPngGroup(tagType),
        PngChromaticitiesDirectory => MetadataGroups.PngChrm,
        _ => dir.Name ?? MetadataGroups.Other
    };

    private static string MapPngGroup(int tagType) => tagType switch
    {
        PngDirectory.TagImageWidth or PngDirectory.TagImageHeight
            or PngDirectory.TagBitsPerSample or PngDirectory.TagColorType
            or PngDirectory.TagCompressionType or PngDirectory.TagFilterMethod
            or PngDirectory.TagInterlaceMethod => MetadataGroups.PngIhdr,
        PngDirectory.TagPaletteSize or PngDirectory.TagPaletteHasTransparency => MetadataGroups.PngPlte,
        PngDirectory.TagSrgbRenderingIntent => MetadataGroups.PngSrgb,
        PngDirectory.TagGamma => MetadataGroups.PngGama,
        PngDirectory.TagIccProfileName => MetadataGroups.PngIccp,
        PngDirectory.TagTextualData => MetadataGroups.PngText,
        PngDirectory.TagLastModificationTime => MetadataGroups.PngTime,
        PngDirectory.TagBackgroundColor => MetadataGroups.PngBkgd,
        PngDirectory.TagPixelsPerUnitX or PngDirectory.TagPixelsPerUnitY or PngDirectory.TagUnitSpecifier => MetadataGroups.PngPhys,
        PngDirectory.TagSignificantBits => MetadataGroups.PngSbit,
        _ => MetadataGroups.PngUnknown
    };

    private static bool IsPrivacySensitive(global::MetadataExtractor.Directory dir, string tagName, string group)
    {
        if (group is MetadataGroups.Iptc or MetadataGroups.Xmp or MetadataGroups.ExifGps)
        {
            return true;
        }
        // D77: ExifThumbnailDirectory is missing from this list — the stripper drops
        // the entire APP1 (EXIF) including the thumbnail IFD, but the pre-fix code
        // returned false (not privacy-sensitive) for any tag in that directory, so
        // the review grid showed the thumbnail entries in a non-privacy-sensitive
        // style even though they would be removed. A thumbnail is privacy-sensitive:
        // it can embed a separate image with its own metadata, including GPS
        // coordinates, and the thumbnail can be a different (cropped/edited) version
        // of the original. The user needs to see it flagged. Same exception list
        // (Exif Version / Flashpix Version / Components Configuration) applies, but
        // those tags are IFD0-only — IFD1 only has the thumbnail-specific tags
        // (Compression, XResolution, JPEGInterchangeFormat, …), so the pattern match
        // would return true for every IFD1 tag in practice.
        if (dir is ExifIfd0Directory or ExifSubIfdDirectory or ExifInteropDirectory or ExifThumbnailDirectory)
        {
            return tagName is not ("Exif Version" or "Flashpix Version" or "Components Configuration");
        }
        if (group is MetadataGroups.Icc)
        {
            return true;
        }
        if (group is MetadataGroups.PngText or MetadataGroups.PngTime)
        {
            return true;
        }
        return false;
    }

    private static long? EstimateTagSize(global::MetadataExtractor.Directory dir, int tagType)
    {
        try
        {
            var value = dir.GetObject(tagType);
            if (value is null) return null;
            return value switch
            {
                byte[] bytes => bytes.Length,
                string s => System.Text.Encoding.UTF8.GetByteCount(s),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// One-pass byte-level scan of a PNG file's chunks. Used to surface metadata chunks that
/// <see cref="MetadataExtractor"/> rolls into a generic bucket (eXIf specifically — MetadataExtractor
/// surfaces both tEXt and eXIf under PngText, hiding the fact that the stripper will drop the
/// eXIf block too). Keeps the review grid honest about what will actually be removed.
/// </summary>
internal static class PngChunkProbe
{
    // PNG chunk type constants (4-byte ASCII strings per the PNG spec). The
    // pre-fix code allocated a string per chunk (`new string(new[] { ... })`)
    // and compared strings. The D101 (M2.20.39) fix uses byte comparison
    // against named constants, matching the PngMetadataStripper pattern.
    // The PNG signature (8 bytes) used to be duplicated here as a local
    // `PngSignature` byte array; D106 (M2.20.44) deletes that copy and
    // references the canonical `ImageFormatDetector.PngSignature` instead.
    private static ReadOnlySpan<byte> ExifBytes => "eXIf"u8;
    private static ReadOnlySpan<byte> HistBytes => "hIST"u8;
    private static ReadOnlySpan<byte> IendBytes => "IEND"u8;

    public static void ProbeForMissingEntries(string path, List<MetadataEntry> sink)
    {
        // Only add an entry if MetadataExtractor didn't already surface one. The gaps
        // today are eXIf (rolled into PngText) and hIST (no PngDirectory.TagHistogram);
        // if MetadataExtractor ever adds separate surfacing for either, this becomes a no-op.
        if (sink.Any(e => e.Group == MetadataGroups.PngExif)
            || sink.Any(e => e.Group == MetadataGroups.PngHist))
        {
            return;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            Span<byte> sig = stackalloc byte[8];
            Span<byte> header = stackalloc byte[8];
            // D92 (M2.20.30): routed through StreamHelpers.ReadUpTo (was a private
            // TryReadExact here, byte-identical to the private ReadUpTo in
            // JpegMetadataStripper). Best-effort read: a short signature read means
            // "not a PNG" and we return without surfacing an entry.
            int n = StreamHelpers.ReadUpTo(fs, sig);
            if (n < 8 || !sig.SequenceEqual(ImageFormatDetector.PngSignature))
            {
                return;
            }

            while (true)
            {
                // D92: same helper, different contract — we expect 8 bytes and
                // bail on short read (the stream is malformed or truncated).
                if (StreamHelpers.ReadUpTo(fs, header) < 8) return;

                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length < 0 || length > int.MaxValue) return; // malformed
                // D101 (M2.20.39): use a span over the 4 type bytes directly
                // instead of allocating a string per chunk. The pre-fix code
                // did `new string(new[] { (char)header[4], ... })` per chunk,
                // allocating a string and 4 boxed char objects per iteration
                // (every PNG chunk). The byte-comparison approach is allocation-
                // free and matches the PngMetadataStripper pattern.
                var typeSpan = header.Slice(4, 4);

                if (typeSpan.SequenceEqual(ExifBytes))
                {
                    sink.Add(new MetadataEntry(
                        MetadataGroups.PngExif,
                        "EXIF block",
                        $"Embedded EXIF data ({length} bytes)",
                        EstimatedSizeBytes: length,
                        IsPrivacySensitive: true));
                }

                // D69 (M2.20.18): the hIST chunk is a palette histogram. MetadataExtractor
                // doesn't surface it as a tag (no PngDirectory.TagHistogram), and the stripper
                // drops it under Privacy/AllMetadata. Without this probe the user has no
                // way to know hIST exists or that it would be removed. Surface it here so the
                // grid shows it; the keep-set (PNGHIST, Minimal-only) marks it "Would be
                // removed" under Privacy/AllMetadata and "Would be kept" under Minimal.
                if (typeSpan.SequenceEqual(HistBytes))
                {
                    sink.Add(new MetadataEntry(
                        MetadataGroups.PngHist,
                        "Palette histogram",
                        $"Histogram of palette entries ({length} bytes)",
                        EstimatedSizeBytes: length,
                        IsPrivacySensitive: false));
                }

                // Advance past the chunk data and the 4-byte CRC trailer.
                long skip = (long)length + 4;
                if (fs.Position + skip > fs.Length) return;
                fs.Seek(skip, SeekOrigin.Current);

                if (typeSpan.SequenceEqual(IendBytes)) return;
            }
        }
        catch
        {
            // Probe failures must NEVER fail an inspect: a user inspecting a slightly-malformed
            // PNG should still see the MetadataExtractor entries, just without the eXIf hint.
        }
    }
}