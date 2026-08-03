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
        var format = ImageFormatDetector.DetectFile(path);

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
        if (dir is ExifIfd0Directory or ExifSubIfdDirectory or ExifInteropDirectory)
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
    private static readonly byte[] PngSignature = new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    };

    public static void ProbeForMissingEntries(string path, List<MetadataEntry> sink)
    {
        // Only add an entry if MetadataExtractor didn't already surface one. Today the
        // gap is eXIf; if MetadataExtractor ever adds separate surfacing, this becomes a no-op.
        if (sink.Any(e => e.Group == MetadataGroups.PngExif))
        {
            return;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            Span<byte> sig = stackalloc byte[8];
            Span<byte> header = stackalloc byte[8];
            int n = fs.Read(sig);
            if (n < 8 || !sig.SequenceEqual(PngSignature))
            {
                return;
            }

            while (true)
            {
                if (TryReadExact(fs, header) < 8) return;

                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length < 0 || length > int.MaxValue) return; // malformed
                var type = new string(new[] { (char)header[4], (char)header[5], (char)header[6], (char)header[7] });

                if (type == "eXIf")
                {
                    sink.Add(new MetadataEntry(
                        MetadataGroups.PngExif,
                        "EXIF block",
                        $"Embedded EXIF data ({length} bytes)",
                        EstimatedSizeBytes: length,
                        IsPrivacySensitive: true));
                }

                // Advance past the chunk data and the 4-byte CRC trailer.
                long skip = (long)length + 4;
                if (fs.Position + skip > fs.Length) return;
                fs.Seek(skip, SeekOrigin.Current);

                if (type == "IEND") return;
            }
        }
        catch
        {
            // Probe failures must NEVER fail an inspect: a user inspecting a slightly-malformed
            // PNG should still see the MetadataExtractor entries, just without the eXIf hint.
        }
    }

    private static int TryReadExact(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = s.Read(buffer.Slice(total));
            if (read == 0) return total;
            total += read;
        }
        return total;
    }
}