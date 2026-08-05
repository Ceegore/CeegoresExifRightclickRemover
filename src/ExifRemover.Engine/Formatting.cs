namespace ExifRemover.Engine;

using System.Globalization;

/// <summary>
/// Human-readable byte counts for the overlay grid ("Size" column) and the post-strip
/// summary message. Centralized so the entry-row grid and the OverlayWindow summary agree
/// on the formatting (one place to change thresholds, locale, etc.).
///
/// Previously each call site had its own private <c>FormatBytes</c> — D52 in M2.20.8
/// extracted them into a single helper. D102 (M2.20.40) further moved the helper from
/// the App project to the Engine project so the formatting rules can be unit-tested
/// from the Engine test project (the App project is WPF-bound and the xUnit test
/// project can't include its sources via <c>&lt;Compile Include&gt;</c>).
/// </summary>
public static class Formatting
{
    /// <summary>
    /// Format a byte count as a human-readable string with binary-prefixed units
    /// (B / KB / MB / GB). Pre-fix the function only handled B / KB / MB — a 1.5 GB
    /// file would render as "1500.00 MB" instead of "1.46 GB". D102 (M2.20.40)
    /// adds the GB case. Negative values are clamped to "0 B" (a defensive guard
    /// against signed-int overflow in upstream callers). The format strings use
    /// <see cref="CultureInfo.InvariantCulture"/> so a user with a non-English
    /// culture (e.g. German, where the decimal separator is <c>,</c> instead of
    /// <c>.</c>) still gets the expected <c>"1.46 GB"</c> string — the user-facing
    /// display would have rendered <c>"1,46 GB"</c> with the pre-fix code, which
    /// broke the D102 unit tests and was inconsistent with the rest of the
    /// codebase (which uses <c>.</c> as the decimal separator throughout, e.g.
    /// the verifier's <c>"{0.0}"</c> format strings).
    /// </summary>
    /// <param name="b">Byte count. Negative values are treated as 0.</param>
    /// <returns>A string like "0 B", "512 B", "1.5 KB", "12.34 MB", "1.46 GB".</returns>
    public static string FormatBytes(long b)
    {
        if (b <= 0) return "0 B";
        if (b < 1024) return $"{b.ToString(CultureInfo.InvariantCulture)} B";
        if (b < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", b / 1024.0);
        if (b < 1024L * 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", b / 1024.0 / 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", b / 1024.0 / 1024.0 / 1024.0);
    }
}

