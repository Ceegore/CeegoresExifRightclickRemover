namespace ExifRemover.Engine;

/// <summary>
/// Builds the per-format, per-profile keep-set used by the overlay grid's
/// "Would be kept" / "Would be removed" indicator. The keep-set is the
/// set of chunk-type keys (e.g. "JFIF", "ICC", "PNGPHYS") that the
/// stripper would NOT remove under a given (format, profile) combination.
///
/// D104 (M2.20.42): the pre-fix <c>ComputeKeepSet</c> was a 56-line
/// static method in <c>OverlayViewModel</c> (WPF-bound, App project).
/// The App project's xUnit test project can't include its sources via
/// <c>&lt;Compile Include&gt;</c> (no WPF in net8.0), so the keep-set
/// logic was untested at the unit level — only indirectly through the
/// stripper tests + integration. The fix moves the canonical
/// implementation to Engine (where it's directly testable) and
/// replaces the App's copy with a one-line pass-through delegate,
/// matching the M2.20.40 D102 (FormatBytes) + M2.20.31 D93
/// (KeepSetKey.For) pattern.
///
/// The keep-set logic has subtle invariants (see the D2 / D51 / D77
/// comments in <c>OverlayViewModel.cs</c> for the full reasoning). A
/// future refactor that diverges the keep-set from the stripper's
/// actual behavior would silently mis-classify entries — "Would be
/// removed" would render for an entry the stripper kept, or vice
/// versa. The D104 fix adds 12 direct unit tests (every format ×
/// profile combo) to pin the contract.
/// </summary>
public static class KeepSet
{
    /// <summary>
    /// Build the keep-set for a given (format, profile) combination.
    /// The set contains the chunk-type keys (per <see cref="KeepSetKey.For"/>)
    /// that the stripper would NOT remove. Entries whose
    /// <see cref="KeepSetKey.For"/> returns a key in the set render
    /// "Would be kept" in the grid; entries whose key is NOT in the
    /// set render "Would be removed".
    /// </summary>
    /// <param name="format">
    /// The image format (Jpeg / Png / null). When null, only the
    /// format-independent "Other" key is in the set (D51 fail-safe
    /// for unknown formats).
    /// </param>
    /// <param name="profile">
    /// The strip profile (Privacy / AllMetadata / Minimal).
    /// </param>
    /// <returns>
    /// A new <see cref="HashSet{T}"/> with <see cref="StringComparer.Ordinal"/>
    /// comparer. Never null, never empty (always at least contains "Other").
    /// </returns>
    public static HashSet<string> ForFormat(ImageFormat? format, StripProfile profile)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // D51: fail-safe default for any entry that falls through to the "Other" group
        // (MetadataGroups.Other = "Other"). MapGroup's _ => dir.Name ?? MetadataGroups.Other
        // fallback fires when MetadataExtractor surfaces a directory that doesn't match any
        // of the explicit cases. The stripper operates on bytes, not on MetadataExtractor's
        // directory abstraction, so we can't be 100% sure the stripper drops the underlying
        // bytes — marking "Other" as kept is the safe default. If we don't know what the
        // directory represents, don't claim the stripper will remove it. (Same fail-safe
        // reasoning as the "PNGUNKNOWN" entry for the PNG path.)
        set.Add("Other");
        if (format == ImageFormat.Jpeg)
        {
            set.Add("JFIF");
            // ICC is kept only under Minimal; Privacy and AllMetadata both strip it
            // (must match JpegMetadataStripper, where keepIcc == (profile == Minimal)).
            if (profile == StripProfile.Minimal)
            {
                set.Add("ICC");
            }
        }
        else if (format == ImageFormat.Png)
        {
            // Chunks the stripper ALWAYS keeps regardless of profile (must mirror
            // PngMetadataStripper.ShouldDrop, which never returns true for these types).
            set.Add("PNGPHYS");
            set.Add("PNGBKGD");
            set.Add("PNGSBIT");
            set.Add("PNGTRNS");

            // D2: any chunk MetadataExtractor surfaces as "PNG Unknown" (e.g. a newer
            // PngDirectory tag that doesn't match a known case in MapPngGroup, or a
            // custom ancillary chunk) is kept by the stripper — PngMetadataStripper.ShouldDrop
            // only returns true for tEXt/zTXt/iTXt/tIME/eXIf/iCCP/hIST/gAMA/cHRM/sRGB, and
            // falls through to "return false" (keep) for anything else. The grid must
            // match that contract: an unknown ancillary chunk must show as "Would be kept",
            // never "Would be removed" (H2 lie).
            set.Add("PNGUNKNOWN");

            // Color-management chunks: kept under Privacy/Minimal, stripped under AllMetadata.
            if (profile != StripProfile.AllMetadata)
            {
                set.Add("PNGSRGB");
                set.Add("PNGCHRM");
                set.Add("PNGGAMA");
            }

            // iCCP and hIST: kept only under Minimal.
            if (profile == StripProfile.Minimal)
            {
                set.Add("PNGICCP");
                set.Add("PNGHIST");
            }
        }
        return set;
    }
}
