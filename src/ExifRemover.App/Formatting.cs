namespace ExifRemover.App;

/// <summary>
/// Human-readable byte counts for the overlay grid ("Size" column) and the post-strip
/// summary message. Centralized so the EntryRow grid and the OverlayWindow summary agree
/// on the formatting (one place to change thresholds, locale, etc.).
///
/// Previously each call site had its own private <c>FormatBytes</c> — D52 in M2.20.8
/// extracted them into a single internal helper.
/// </summary>
internal static class Formatting
{
    public static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:0.0} KB";
        return $"{b / 1024.0 / 1024.0:0.00} MB";
    }
}
