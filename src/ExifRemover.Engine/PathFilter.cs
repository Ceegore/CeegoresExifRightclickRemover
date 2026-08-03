namespace ExifRemover.Engine;

/// <summary>
/// File-path filtering for the overlay's input list. Sits in the Engine (not the App)
/// so the keep/drop logic is unit-testable from a non-WPF test project.
///
/// The App's <c>FilterSupported</c> used to do this in a private static method with
/// a blanket <c>catch { }</c> — D8 from the prior round noted that invalid paths
/// (e.g. containing a null byte, which makes <c>Path.GetExtension</c> throw) were
/// silently dropped without surfacing in the dropped-files notice. This class
/// splits the two failure modes (unsupported extension vs. invalid path) and
/// reports each dropped path with a reason, so the overlay can show a useful
/// "ignored 1 unsupported file: foo.png (unsupported file type)" message instead
/// of swallowing the failure.
/// </summary>
public static class PathFilter
{
    public sealed record FilterResult(
        IReadOnlyList<string> Kept,
        IReadOnlyList<Dropped> Dropped);

    public sealed record Dropped(string Path, string Reason);

    /// <summary>
    /// Splits a list of input paths into kept (supported image, full path) and
    /// dropped (with a per-path reason). Dropped reasons:
    ///   - "invalid path: …"      — <c>Path.GetExtension</c> or <c>Path.GetFullPath</c> threw
    ///   - "unsupported file type" — extension is not .jpg/.jpeg/.png
    /// </summary>
    public static FilterResult FilterImagePaths(IEnumerable<string> paths)
    {
        var kept = new List<string>();
        var dropped = new List<Dropped>();
        if (paths is null) return new FilterResult(kept, dropped);

        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p))
            {
                dropped.Add(new Dropped(p ?? string.Empty, "empty path"));
                continue;
            }

            string ext;
            try
            {
                ext = Path.GetExtension(p);
            }
            catch (Exception ex)
            {
                // Path.GetExtension throws ArgumentException for paths containing
                // characters illegal on the platform (e.g. a null byte). The user
                // pasted/dropped a bad path; tell them so instead of swallowing.
                dropped.Add(new Dropped(p, $"invalid path: {ex.Message}"));
                continue;
            }

            if (!IsSupportedImageExtension(ext))
            {
                dropped.Add(new Dropped(p, "unsupported file type"));
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(p);
            }
            catch (Exception ex)
            {
                // Same family as above but for GetFullPath (relative-path resolution
                // can throw on a malformed current directory, e.g. the path refers
                // to a drive that doesn't exist).
                dropped.Add(new Dropped(p, $"invalid path: {ex.Message}"));
                continue;
            }

            kept.Add(full);
        }
        return new FilterResult(kept, dropped);
    }

    /// <summary>
    /// True for the three image extensions the stripper supports (case-insensitive).
    /// Public so other call sites (e.g. the install.cmd registry writer, or future
    /// test fixtures) can reuse the same check.
    /// </summary>
    public static bool IsSupportedImageExtension(string extension)
    {
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
    }
}
