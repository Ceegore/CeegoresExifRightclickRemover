namespace ExifRemover.Engine;

/// <summary>
/// Maps a <see cref="MetadataEntry"/>'s <c>Group</c> string to the
/// keep-set key used by <c>OverlayViewModel</c>'s grid. The grid asks
/// "is this entry in the keep-set for the current strip profile?"
/// — the keep-set is keyed on short uppercase strings ("EXIF", "IPTC",
/// "PNGEXIF", …) rather than the user-facing display string
/// ("EXIF IFD0", "IPTC", "PNG eXIf", …).
///
/// Pre-fix: this mapping was an 18-branch <c>if</c>-chain in
/// <c>OverlayViewModel.GetChunkKey</c> — one <c>if (entry.Group ==
/// MetadataGroups.Iptc) return "IPTC";</c> per known group. Two
/// problems:
///   1. The mapping logic was in the App layer (a WPF-bound project),
///      so it was not directly unit-testable from the Engine test
///      project. Every keep-set correctness test had to be exercised
///      indirectly (via the stripper tests + a comment in the test
///      file).
///   2. Adding a new group to <see cref="MetadataGroups"/> required
///      adding a matching <c>if</c>-branch in <c>GetChunkKey</c>;
///      the type system didn't enforce the parity. Forgetting the
///      <c>GetChunkKey</c> update would mean the new group falls
///      through to <c>return entry.Group</c> (which for "PNG Unknown"
///      yields a space-separated string that never matches the
///      keep-set key — the D2 finding).
///
/// Post-fix: a single <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
/// mapping every known <see cref="MetadataGroups"/> constant to its
/// keep-set key, with the "EXIF" prefix-match (one <c>StartsWith</c>
/// check covers IFD0, SubIFD, Interop, Thumbnail) preserved as a
/// special case. The dictionary lives in the Engine so the test
/// project can pin every entry's contract directly. The App layer
/// keeps a one-line <c>GetChunkKey</c> that just calls this helper.
///
/// The fallthrough for unknown groups (<c>return entry.Group</c>) is
/// preserved — same D51 fail-safe reasoning: a new MetadataExtractor
/// directory that doesn't match any of the explicit cases should not
/// silently map to a wrong keep-set key.
/// </summary>
public static class KeepSetKey
{
    private static readonly System.Collections.Generic.Dictionary<string, string> _groupToKey =
        new(System.StringComparer.Ordinal)
        {
            // Non-PNG, non-EXIF groups
            [MetadataGroups.Iptc] = "IPTC",
            [MetadataGroups.Xmp] = "XMP",
            [MetadataGroups.Icc] = "ICC",
            [MetadataGroups.JpegComment] = "COM",
            // PNG ancillary groups
            [MetadataGroups.PngText] = "PNGTEXT",
            [MetadataGroups.PngTime] = "PNGTIME",
            [MetadataGroups.PngExif] = "PNGEXIF",
            [MetadataGroups.PngIccp] = "PNGICCP",
            [MetadataGroups.PngHist] = "PNGHIST",
            [MetadataGroups.PngSrgb] = "PNGSRGB",
            [MetadataGroups.PngChrm] = "PNGCHRM",
            [MetadataGroups.PngGama] = "PNGGAMA",
            [MetadataGroups.PngPhys] = "PNGPHYS",
            [MetadataGroups.PngBkgd] = "PNGBKGD",
            [MetadataGroups.PngSbit] = "PNGSBIT",
            [MetadataGroups.PngTrns] = "PNGTRNS",
            // D2 (M2.20.2): "PNG Unknown" must map to "PNGUNKNOWN" (no space).
            // The fallthrough `return entry.Group` would yield "PNG Unknown"
            // (with a space), which never matches the keep-set key the
            // ComputeKeepSet helper adds. An unknown chunk would show
            // "Would be removed" in the grid even though the stripper keeps
            // it — an H2 lie. The explicit entry pins the contract.
            [MetadataGroups.PngUnknown] = "PNGUNKNOWN",
        };

    /// <summary>
    /// Maps <paramref name="entry"/>'s group to the keep-set key.
    /// The four EXIF IFD groups ("EXIF IFD0", "EXIF SubIFD", "EXIF Interop",
    /// "EXIF Thumbnail") all collapse to a single "EXIF" key via a
    /// prefix match — the stripper's behavior for an EXIF tag is
    /// profile-driven (Privacy drops it, Minimal drops it, AllMetadata
    /// drops it), so the grid doesn't need to distinguish the IFDs.
    /// </summary>
    public static string For(MetadataEntry entry)
    {
        if (entry.Group.StartsWith("EXIF", System.StringComparison.Ordinal))
        {
            return "EXIF";
        }
        return _groupToKey.TryGetValue(entry.Group, out var key) ? key : entry.Group;
    }
}
