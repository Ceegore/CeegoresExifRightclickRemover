namespace ExifRemover.Engine;

internal static class AtomicFile
{
    public static void Replace(string destination, string tempContent, Action<string> writeContent)
    {
        var dir = Path.GetDirectoryName(destination)
                  ?? throw new ArgumentException("Destination has no directory.", nameof(destination));
        var name = Path.GetFileName(destination);
        var tempPath = Path.Combine(dir, $".{name}.exifremover-{Guid.NewGuid():N}.tmp");

        try
        {
            writeContent(tempPath);
            if (File.Exists(destination))
            {
                File.Replace(tempPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destination);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static string NextNonClashingPath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var dir = Path.GetDirectoryName(desiredPath)
                  ?? throw new ArgumentException("Path has no directory.", nameof(desiredPath));
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}