namespace ExifRemover.Engine;

public sealed record MetadataEntry(
    string Group,
    string Name,
    string Value,
    long? EstimatedSizeBytes,
    bool IsPrivacySensitive);

public sealed class FileInspection
{
    public required string Path { get; init; }
    public required ImageFormat Format { get; init; }
    public required IReadOnlyList<MetadataEntry> Entries { get; init; }
    public required long FileSizeBytes { get; init; }
    public string? Error { get; init; }

    public bool HasMetadata => Entries.Count > 0;
}

public static class MetadataGroups
{
    public const string ExifIfd0 = "EXIF IFD0";
    public const string ExifSubIfd = "EXIF SubIFD";
    public const string ExifGps = "EXIF GPS";
    public const string ExifInterop = "EXIF Interop";
    public const string ExifThumbnail = "EXIF Thumbnail";
    public const string Iptc = "IPTC";
    public const string Xmp = "XMP";
    public const string Icc = "ICC Profile";
    public const string Jfif = "JFIF";
    public const string Photoshop = "Photoshop";
    public const string JpegComment = "JPEG Comment";
    public const string PngText = "PNG Text";
    public const string PngTime = "PNG Time";
    public const string PngExif = "PNG eXIf";
    public const string PngIccp = "PNG iCCP";
    public const string PngHist = "PNG hIST";
    public const string PngPhys = "PNG pHYs";
    public const string PngSrgb = "PNG sRGB";
    public const string PngChrm = "PNG cHRM";
    public const string PngGama = "PNG gAMA";
    public const string PngBkgd = "PNG bKGD";
    public const string PngSbit = "PNG sBIT";
    public const string PngTrns = "PNG tRNS";
    public const string PngPlte = "PNG PLTE";
    public const string PngIdat = "PNG IDAT";
    public const string PngIhdr = "PNG IHDR";
    public const string PngIend = "PNG IEND";
    public const string PngUnknown = "PNG Unknown";
    public const string Other = "Other";
}