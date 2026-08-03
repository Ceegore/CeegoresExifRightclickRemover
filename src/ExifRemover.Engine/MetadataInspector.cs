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