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

    /// <summary>
    /// Best-effort read: reads up to <paramref name="buffer"/>.Length bytes from
    /// <paramref name="s"/> and returns the actual count read (0..buffer.Length).
    /// Unlike <see cref="ReadExact"/>, does NOT throw on short reads — the caller
    /// is expected to check the return value and decide whether a partial fill is
    /// acceptable (e.g. for sniffing, where "didn't have enough bytes to match
    /// a magic prefix" is a valid "no, this isn't the expected format" signal).
    ///
    /// D92 (M2.20.30): the pre-fix code had a hand-rolled
    /// <c>TryReadExact(Stream, Span&lt;byte&gt;)</c> in <c>PngChunkProbe</c>
    /// (in <c>MetadataInspector.cs</c>) AND a hand-rolled
    /// <c>ReadUpTo(Stream, Span&lt;byte&gt;)</c> in <c>JpegMetadataStripper.ShouldDrop</c>
    /// (the jfifSniff / iccSniff sniff paths). The two copies were byte-identical
    /// except for the method name. Same DRY-drift pattern as D83 (ReadExact) and
    /// D87 (SkipExactly): a future contributor who updates one copy (e.g. adds
    /// a max-bytes guard, switches to <c>Stream.ReadAtLeast</c> from .NET 8,
    /// adds a logging side-channel) would have to remember to update the other
    /// copy too — and a missed update would silently keep the two best-effort
    /// read paths divergent.
    ///
    /// The signature takes no <c>context</c> parameter because the method never
    /// throws (so there's no error message to tag). If a future change needs a
    /// tag (e.g. a debug log on partial reads), add it then — adding a
    /// never-read parameter today is dead API surface.
    /// </summary>
    public static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer.Slice(total));
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Skips exactly <paramref name="count"/> bytes from <paramref name="s"/>.
    /// On a seekable stream, uses <c>Stream.Seek</c> (O(1)) and trust-but-verify
    /// the count doesn't run past the end of the stream. On a non-seekable
    /// stream, uses a read loop (O(n)) with a 64 KB buffer, also verifying
    /// the stream doesn't end before the skip completes.
    ///
    /// D87 (M2.20.27): the pre-fix code declared <c>SkipExactly</c> as a
    /// private method in BOTH <c>JpegMetadataStripper.cs</c> and
    /// <c>PngMetadataStripper.cs</c>. The two copies were nearly identical
    /// — same algorithm, same error-message shape ("Unexpected end of {format}
    /// stream during segment skip.") — but with a signature difference:
    /// the JPEG side took <c>int count</c> (because JPEG segLen is uint16,
    /// max 65535, so an int is sufficient), while the PNG side took
    /// <c>long count</c> (because PNG chunk length is int32, +4 for the
    /// CRC trailer could in theory push past int.MaxValue, and the
    /// <c>new byte[count]</c> allocation in the non-seekable path would
    /// throw on overflow). The PNG side had a
    /// <c>Math.Min(count, int.MaxValue)</c> clamp to defend against this.
    ///
    /// The post-fix signature takes <c>long count</c> unconditionally and
    /// applies the clamp. The JPEG call sites pass <c>(long)payloadLen</c>
    /// (implicit widening from int to long is free). The result: one
    /// <c>SkipExactly</c> implementation instead of two, with the same
    /// trust-but-verify bounds check, the same error-message shape, and
    /// the same overflow defense.
    /// </summary>
    public static void SkipExactly(Stream s, long count, string context)
    {
        if (s.CanSeek)
        {
            // Trust-but-verify: a malformed stream whose segment-length field
            // claims more bytes than remain would put Position past Length,
            // which is illegal for the next read and surfaces as a less
            // informative "no marker" error. Catch it here with an accurate
            // "during segment skip" message — same pattern as D65 for JPEG
            // and the original PNG-side comment.
            if (s.Position + count > s.Length)
            {
                throw new EndOfStreamException($"Unexpected end of {context} stream during segment skip.");
            }
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        // Non-seekable path: loop with a 64 KB buffer. The Math.Min clamps
        // `count` to int.MaxValue so the `new byte[...]` allocation can't
        // overflow even on a 32-bit runtime. (On a 64-bit runtime the
        // allocation could in theory succeed for any long, but the read
        // loop's `int take` cast would truncate, and the seekable path
        // would have already caught the bounds violation. The clamp is
        // defense in depth.)
        var buf = new byte[Math.Min((int)Math.Min(count, int.MaxValue), 64 * 1024)];
        long remaining = count;
        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, buf.Length);
            int n = s.Read(buf, 0, take);
            if (n == 0)
            {
                throw new EndOfStreamException($"Unexpected end of {context} stream during segment skip.");
            }
            remaining -= n;
        }
    }
}
