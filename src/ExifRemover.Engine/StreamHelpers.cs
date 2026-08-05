namespace ExifRemover.Engine;

/// <summary>
/// Shared stream-reading helpers used by both <see cref="JpegMetadataStripper"/> and
/// <see cref="PngMetadataStripper"/>. Centralized so a future change (e.g. wrapping
/// the throw in a custom exception type, adding a max-bytes guard, switching to
/// <c>Stream.ReadAtLeast</c> from .NET 8) is applied uniformly to both strippers.
/// D83 (M2.20.25): the pre-fix code declared <c>ReadExact(Stream, Span&lt;byte&gt;)</c>
/// as a private method in BOTH <c>JpegMetadataStripper.cs</c> and
/// <c>PngMetadataStripper.cs</c>. The two copies were byte-identical except for the
/// error-message string ("JPEG stream" vs "PNG stream"). This is the same DRY-drift
/// pattern that R17 of the SteamReviewTool audit found for back-door in-memory
/// setters: a future contributor who updates one copy (e.g. to add a timeout, to
/// wrap the exception, to use <c>Stream.ReadExactly</c> from .NET 7+) would have
/// to remember to update the other copy too — and a missed update would silently
/// keep the two strippers on different stream-reading paths.
/// </summary>
internal static class StreamHelpers
{
    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="s"/>.
    /// Unlike <c>Stream.Read(Span&lt;byte&gt;)</c>, which may return fewer bytes than
    /// requested (the underlying source is allowed to do short reads), this loops
    /// until the buffer is full or the stream is exhausted. Throws
    /// <see cref="EndOfStreamException"/> with a context-tagged message if the
    /// stream ends before the buffer is full.
    /// </summary>
    /// <param name="s">Source stream.</param>
    /// <param name="buffer">Destination buffer. Read fills it completely.</param>
    /// <param name="context">
    /// Short tag inserted into the error message ("Unexpected end of {context} stream.")
    /// so the user knows which stream truncated. Pass "JPEG" or "PNG" from the
    /// stripper call sites.
    /// </param>
    public static void ReadExact(Stream s, Span<byte> buffer, string context)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n == 0)
            {
                throw new EndOfStreamException($"Unexpected end of {context} stream.");
            }
            total += n;
        }
    }
}
