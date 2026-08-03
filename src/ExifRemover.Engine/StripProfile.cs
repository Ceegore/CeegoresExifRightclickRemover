namespace ExifRemover.Engine;

public enum StripProfile
{
    Privacy,
    AllMetadata,
    Minimal
}

public static class StripProfileCatalog
{
    public static (string Title, string ShortDescription, string LongDescription) Describe(StripProfile profile) => profile switch
    {
        StripProfile.Privacy => (
            "Privacy",
            "Strip EXIF, IPTC, XMP, ICC profile, comments, and PNG text/time/eXIf. Keep color management.",
            "This preset removes every chunk and segment that can carry personal information: EXIF tags (camera, lens, " +
            "exposure, software), IPTC (caption, byline, copyright), XMP (edit history, software tags), the ICC color " +
            "profile (which can embed device fingerprints), JPEG COM segments, and all PNG text/time/eXIf ancillary " +
            "chunks. Color-management hints (PNG gAMA, cHRM, sRGB, bKGD, sBIT, pHYs and the JPEG JFIF header) are kept " +
            "so colors and dimensions are not affected."),
        StripProfile.AllMetadata => (
            "All metadata",
            "Everything in Privacy, plus ICC and PNG color-management hints. Tiny risk of color shifts on calibrated displays.",
            "Same as Privacy, but additionally strips the ICC profile on JPEG and the PNG color-management chunks " +
            "(iCCP, gAMA, cHRM, sRGB). On displays and printers that depend on these to render color accurately, " +
            "you may see a very slight difference. The JFIF header and pHYs (physical dimensions) are still kept."),
        StripProfile.Minimal => (
            "Minimal",
            "Strip only the obvious textual metadata. Keep ICC profile.",
            "Removes only the obvious textual and EXIF data: JPEG EXIF/XMP/IPTC comments; PNG text/tIME/eXIf chunks. " +
            "The ICC color profile is kept (so device fingerprints may remain). Choose this if you only want to remove " +
            "the most privacy-relevant fields without affecting color processing."),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown strip profile.")
    };
}