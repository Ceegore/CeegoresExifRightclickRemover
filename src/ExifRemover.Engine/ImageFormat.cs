namespace ExifRemover.Engine;

/// <summary>
/// Detected image container format.
/// </summary>
public enum ImageFormat
{
    Unknown,
    Jpeg,
    Png
}

public static class ImageFormatDetector
{
    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        return ImageFormat.Unknown;
    }

    public static ImageFormat DetectFile(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        int read = fs.Read(head);
        return Detect(head[..read]);
    }
}