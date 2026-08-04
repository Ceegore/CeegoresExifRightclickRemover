namespace ExifRemover.Engine;

internal static class AtomicFile
{
    /// <summary>
    /// Returns a path in the same directory as <paramref name="desiredPath"/> that does
    /// not collide with any existing file. If the desired path is free, returns it
    /// unchanged; otherwise tries "name (2).ext", "name (3).ext", … up to "name (9999).ext".
    /// If all 9998 numbered slots are taken (vanishingly rare), falls back to
    /// "name_{guid}.ext".
    /// </summary>
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