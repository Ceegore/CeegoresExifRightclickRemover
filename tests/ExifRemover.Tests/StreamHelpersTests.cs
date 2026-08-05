using System.IO;
using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Direct unit tests for <see cref="StreamHelpers.ReadExact"/>. D84 (M2.20.25):
/// the pre-fix code had a private <c>ReadExact(Stream, Span&lt;byte&gt;)</c> in
/// BOTH strippers, byte-identical except for the error-message string. After
/// extraction to <see cref="StreamHelpers"/>, the strippers call a single
/// implementation with the format tag passed in. The stripper tests exercise
/// <c>ReadExact</c> indirectly (every truncated-input test goes through it), but
/// no direct test pinned the helper's contract — these tests close the gap.
/// </summary>
public class StreamHelpersTests
{
    [Fact]
    public void ReadExact_ReadsAllBytes_AndFillsBuffer()
    {
        // Sanity: a 10-byte stream, a 10-byte buffer, expect all 10 bytes read.
        var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using var ms = new MemoryStream(bytes);
        var buffer = new byte[10];

        StreamHelpers.ReadExact(ms, buffer, "TEST");

        Assert.Equal(bytes, buffer);
        Assert.Equal(10, ms.Position);
    }

    [Fact]
    public void ReadExact_EmptyStream_ZeroByteBuffer_Succeeds()
    {
        // Boundary: a zero-byte buffer is a no-op (the loop body never runs).
        // The pre-fix per-stripper code had the same property; this test pins it.
        using var ms = new MemoryStream();
        var buffer = new byte[0];

        StreamHelpers.ReadExact(ms, buffer, "TEST");

        // No exception, position is still 0.
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void ReadExact_StreamShorterThanBuffer_Throws()
    {
        // The defining behavior of ReadExact vs Stream.Read: a short stream
        // must produce EndOfStreamException, not silently return a short
        // read. Without ReadExact the stripper's segLen math would operate
        // on a buffer that wasn't fully populated, leading to silent
        // corruption (the stripper would re-emit a partially-read segment
        // header to the output, producing a file that decodes as garbage).
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.ReadExact(ms, buffer, "TEST"));
        Assert.Contains("TEST", ex.Message);
    }

    [Fact]
    public void ReadExact_EmptyStream_NonEmptyBuffer_Throws()
    {
        // The other boundary: stream has no bytes, buffer wants some. The
        // first Read returns 0, the loop throws immediately. This is a
        // sub-case of "stream shorter than buffer" but worth pinning
        // separately because the implementation's first-iteration path
        // is distinct (the loop's while-condition is `total < buffer.Length`,
        // which is true on entry, so we enter the loop and then the read
        // returns 0).
        using var ms = new MemoryStream();
        var buffer = new byte[5];

        Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.ReadExact(ms, buffer, "JPEG"));
    }

    [Fact]
    public void ReadExact_ContextTagAppearsInExceptionMessage()
    {
        // D84 (M2.20.25): the pre-fix per-stripper code had the error-message
        // string hardcoded ("JPEG stream" / "PNG stream"). The post-fix
        // signature takes a `context` parameter so the stripper controls
        // the tag. This test pins the contract: a "JPEG" tag produces a
        // "JPEG" message, a "PNG" tag produces a "PNG" message. A future
        // contributor who changes the formatting (e.g. adds a file path
        // to the message) would update both call sites consistently
        // because there's only one helper.
        using var ms = new MemoryStream();
        var buffer = new byte[1];

        var exJpeg = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.ReadExact(ms, buffer, "JPEG"));
        var exPng = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.ReadExact(ms, buffer, "PNG"));

        Assert.Contains("JPEG", exJpeg.Message);
        Assert.Contains("PNG", exPng.Message);
    }

    [Fact]
    public void ReadExact_LargeStream_ReadsAcrossMultipleLoopIterations()
    {
        // Regression: a stream whose size forces the loop to iterate multiple
        // times. A naive ReadExact that fails to update `total` correctly
        // would either spin forever (off-by-one in the while-condition) or
        // throw a premature EndOfStreamException. We test with 50,000 bytes
        // — well over the 64KB read buffer that .NET typically uses
        // internally, forcing the loop to iterate at least once.
        var bytes = new byte[50_000];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i & 0xFF);
        }
        using var ms = new MemoryStream(bytes);
        var buffer = new byte[50_000];

        StreamHelpers.ReadExact(ms, buffer, "TEST");

        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void ReadUpTo_StreamFillsBuffer_ReturnsFullCount()
    {
        // D92 (M2.20.30): the pre-fix code had a hand-rolled TryReadExact in
        // PngChunkProbe (MetadataInspector.cs) AND a hand-rolled ReadUpTo in
        // JpegMetadataStripper.ShouldDrop (the jfifSniff / iccSniff sniff paths).
        // Both were byte-identical except for the method name. After extraction
        // to StreamHelpers.ReadUpTo, both call sites use a single implementation.
        // The happy path: a 10-byte stream and a 10-byte buffer returns 10.
        var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using var ms = new MemoryStream(bytes);
        var buffer = new byte[10];

        int read = StreamHelpers.ReadUpTo(ms, buffer);

        Assert.Equal(10, read);
        Assert.Equal(bytes, buffer);
        Assert.Equal(10, ms.Position);
    }

    [Fact]
    public void ReadUpTo_StreamShorterThanBuffer_ReturnsShortCount_DoesNotThrow()
    {
        // D92: the defining behavior of ReadUpTo vs ReadExact. A short stream
        // produces the actual count (not an exception). This is the semantic
        // difference that made the Jpeg stripper's sniff paths and the
        // PngChunkProbe both want a best-effort read: a truncated or
        // under-sized file is a valid "not the expected format" signal,
        // not an error condition. If we used ReadExact here, the stripper
        // would surface an EndOfStreamException to the user for files that
        // just don't have a JFIF/ICC magic prefix.
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        int read = StreamHelpers.ReadUpTo(ms, buffer);

        Assert.Equal(3, read);
        Assert.Equal(1, buffer[0]);
        Assert.Equal(2, buffer[1]);
        Assert.Equal(3, buffer[2]);
        Assert.Equal(0, buffer[3]); // remaining bytes untouched
    }

    [Fact]
    public void ReadUpTo_EmptyStream_NonEmptyBuffer_ReturnsZero_DoesNotThrow()
    {
        // D92: empty-stream boundary. The ReadExact equivalent throws;
        // ReadUpTo returns 0. The PngChunkProbe uses this to detect a
        // truncated file (signature is 0 bytes) and bail out without
        // surfacing an exception to the inspect path.
        using var ms = new MemoryStream();
        var buffer = new byte[5];

        int read = StreamHelpers.ReadUpTo(ms, buffer);

        Assert.Equal(0, read);
    }

    [Fact]
    public void ReadUpTo_EmptyBuffer_NoOp_ReturnsZero()
    {
        // D92: zero-byte buffer boundary. The while-loop's condition
        // (total < buffer.Length) is false on entry, so the loop body
        // never runs and the method returns 0.
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[0];

        int read = StreamHelpers.ReadUpTo(ms, buffer);

        Assert.Equal(0, read);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void ReadUpTo_LargeStream_ReadsAcrossMultipleLoopIterations()
    {
        // D92: a stream whose size forces the loop to iterate multiple
        // times. Same regression shape as ReadExact's large-stream test —
        // pin the loop's `total += n` update against an off-by-one. We use
        // 50,000 bytes so the underlying Stream.Read call likely returns
        // in multiple chunks (depending on the stream's internal buffer).
        var bytes = new byte[50_000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        using var ms = new MemoryStream(bytes);
        var buffer = new byte[50_000];

        int read = StreamHelpers.ReadUpTo(ms, buffer);

        Assert.Equal(50_000, read);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void SkipExactly_SeekableStream_AdvancesPositionByCount()
    {
        // D87 (M2.20.27): the pre-fix code had a private SkipExactly in BOTH
        // strippers with slightly different signatures (int vs long count). After
        // extraction, both strippers call this shared helper with their format
        // tag. The seekable path is O(1): Stream.Seek advances the position
        // directly, no read loop. The pre-fix PNG SkipExactly had this
        // comment "Trust-but-verify: even on a seekable stream, a chunk
        // length that runs past EOF would put Position past Length, which is
        // illegal for the next read." We pin both the advance and the
        // bounds-check contract.
        using var ms = new MemoryStream(new byte[1000]);
        var startPos = ms.Position;
        var startLen = ms.Length;

        StreamHelpers.SkipExactly(ms, 500, "TEST");

        Assert.Equal(500, ms.Position);
        // The stream is unchanged otherwise.
        Assert.Equal(startLen, ms.Length);
    }

    [Fact]
    public void SkipExactly_SeekableStream_CountPastEnd_Throws()
    {
        // D65 / D87: the bounds check on the seekable path. A malformed
        // stream whose segment-length field claims more bytes than remain
        // would put Position past Length, which is illegal for the next
        // read. The helper throws EndOfStreamException with an
        // accurate "during segment skip" message.
        using var ms = new MemoryStream(new byte[100]);
        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.SkipExactly(ms, 200, "TEST"));
        Assert.Contains("during segment skip", ex.Message);
        Assert.Contains("TEST", ex.Message);
    }

    [Fact]
    public void SkipExactly_NonSeekableStream_AdvancesPositionByCount()
    {
        // The non-seekable path: wrap a MemoryStream in a non-seekable
        // wrapper. .NET's MemoryStream can be made non-seekable, but a
        // simpler way is to use a stream subclass that overrides CanSeek.
        // We use a small inline subclass for the test.
        var bytes = new byte[1000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        using var ms = new MemoryStream(bytes);
        using var nonSeekable = new NonSeekableStream(ms);

        StreamHelpers.SkipExactly(nonSeekable, 500, "TEST");

        Assert.Equal(500, nonSeekable.Position);
    }

    [Fact]
    public void SkipExactly_NonSeekableStream_StreamShorterThanCount_Throws()
    {
        // The non-seekable path's bounds check: if the read loop returns 0
        // before the skip completes, the helper throws.
        using var ms = new MemoryStream(new byte[100]);
        using var nonSeekable = new NonSeekableStream(ms);

        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.SkipExactly(nonSeekable, 200, "TEST"));
        Assert.Contains("during segment skip", ex.Message);
    }

    [Fact]
    public void SkipExactly_ContextTagAppearsInExceptionMessage()
    {
        // D87: same as ReadExact — the context tag appears in the error
        // message. A future contributor who changes the formatting (e.g.
        // adds a file path) would update both call sites consistently
        // because there's only one helper.
        using var ms = new MemoryStream(new byte[10]);
        var exJpeg = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.SkipExactly(ms, 100, "JPEG"));
        var exPng = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.SkipExactly(ms, 100, "PNG"));

        Assert.Contains("JPEG", exJpeg.Message);
        Assert.Contains("PNG", exPng.Message);
    }

    [Fact]
    public void SkipExactly_ZeroCount_NoOp()
    {
        // Boundary: a zero-byte skip is a no-op. The non-seekable path's
        // while-loop's condition (`remaining > 0`) is false on entry, so
        // the loop body never runs.
        using var ms = new MemoryStream(new byte[100]);
        StreamHelpers.SkipExactly(ms, 0, "TEST");
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void SkipExactly_LargeCount_NearIntMaxValue_DoesNotOverflowBufferAllocation()
    {
        // D87: the pre-fix PNG SkipExactly had a Math.Min(count, int.MaxValue)
        // clamp to defend against `new byte[count]` overflowing on a 32-bit
        // runtime when count is near long.MaxValue. This test pins the
        // clamp: a count of (int.MaxValue + 1L) would, without the clamp,
        // allocate 2 GB and OOM. With the clamp, the allocation is bounded
        // to int.MaxValue (still ~2 GB on 64-bit, but the loop reads
        // through it without crashing). We use a count that's just above
        // int.MaxValue so the seekable path's bounds check fires first
        // (the stream is much shorter than the count) and we don't
        // actually allocate the buffer.
        using var ms = new MemoryStream(new byte[10]);
        // The count exceeds int.MaxValue but is bounded by the seekable
        // path's bounds check (the stream is only 10 bytes). The helper
        // should throw with the "during segment skip" message — NOT OOM
        // trying to allocate a buffer.
        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.SkipExactly(ms, (long)int.MaxValue + 1L, "TEST"));
        Assert.Contains("during segment skip", ex.Message);
    }

    // ====================================================================
    // CountStuffedFf00 — D98 (M2.20.36)
    // ====================================================================
    // The pre-fix code had a hand-rolled `int CountStuffed(byte[] b)` (or
    // a local function with the same body) in 4 locations: verify/Program.cs
    // (CountStuffed), src/ExifRemover.SelfTest/Program.cs (CountStuffedFf00,
    // added in M2.20.33 D95), and 2 local functions in JpegStripperTests.cs.
    // The M2.20.33 D95 audit found 2 sites in SelfTest but missed the
    // verifier and the xUnit tests. The M2.20.36 project-wide sweep
    // promoted the helper to StreamHelpers and replaced all 4 sites.
    //
    // The SelfTest has a direct test for the helper, but the SelfTest is a
    // console app — not the xUnit test project. These xUnit tests pin the
    // helper's contract in the test project that's actually wired into CI.
    // The pre-M2.20.36 local copies in JpegStripperTests.cs had no direct
    // test coverage; the test was an end-to-end stripper run that compared
    // stuffed-byte counts before/after, which would silently pass with a
    // broken helper (the broken helper would return the same wrong count
    // for both source and output). Same M2.20.33 D95 lesson: shared helpers
    // need direct unit tests, not just integration coverage.

    [Fact]
    public void CountStuffedFf00_EmptySpan_ReturnsZero()
    {
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CountStuffedFf00_SingleByte_ReturnsZero()
    {
        // A single 0xFF has no following byte to pair with — must NOT count.
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(new byte[] { 0xFF }));
    }

    [Fact]
    public void CountStuffedFf00_FollowedByNonZeroByte_ReturnsZero()
    {
        // 0xFF followed by anything other than 0x00 is a real marker, not
        // a byte-stuffing escape — must NOT count.
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(new byte[] { 0xFF, 0xAB }));
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(new byte[] { 0xFF, 0xDA })); // SOS marker
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(new byte[] { 0xFF, 0xD9 })); // EOI marker
    }

    [Fact]
    public void CountStuffedFf00_InterleavedPairs_ReturnsExactCount()
    {
        // 3 stuffed pairs interleaved with non-stuffed bytes.
        var data = new byte[] { 0xFF, 0x00, 0xAB, 0xCD, 0xFF, 0x00, 0xEF, 0xFF, 0x00, 0x12 };
        Assert.Equal(3, StreamHelpers.CountStuffedFf00(data));
    }

    [Fact]
    public void CountStuffedFf00_TrailingFfWithNoFollowingByte_DoesNotOvercount()
    {
        // Trailing 0xFF with no following byte must not count (it has no
        // pair to form). The pre-fix local functions had a `data.Length - 1`
        // loop bound that naturally handles this; this test pins the bound.
        var data = new byte[] { 0xFF, 0x00, 0xFF };
        Assert.Equal(1, StreamHelpers.CountStuffedFf00(data));
    }

    [Fact]
    public void CountStuffedFf00_AllOnes_ReturnsZero()
    {
        // No 0x00 byte at all — no stuffed pairs possible.
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(data));
    }

    [Fact]
    public void CountStuffedFf00_AllZeros_ReturnsZero()
    {
        // No 0xFF byte at all — no stuffed pairs possible.
        var data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(0, StreamHelpers.CountStuffedFf00(data));
    }

    [Fact]
    public void CountStuffedFf00_OverlappingCandidates_OnlyCountsFf00Not00Ff()
    {
        // Edge case: `0x00 0xFF 0x00` — the middle 0xFF is followed by 0x00
        // (count it), but the trailing 0x00 has no following 0xFF (don't
        // count it). The total is 1, not 2 (which would be the case if
        // the loop counted every 0x00 followed by 0xFF).
        var data = new byte[] { 0x00, 0xFF, 0x00 };
        Assert.Equal(1, StreamHelpers.CountStuffedFf00(data));
    }

    // --- CopyExactly (D108, M2.20.46) ---------------------------------------
    //
    // The pre-fix code had `CopyExactly(Stream, Stream, int)` as a private
    // method in `JpegMetadataStripper.cs` (used 2x in the segment-walker
    // to copy segment payloads verbatim). The D108 fix moves the helper
    // to `StreamHelpers` with the same `context` parameter as `ReadExact`
    // and `SkipExactly`, so a future contributor who updates the copy
    // strategy (e.g. uses `Stream.CopyTo` on .NET 8, adds a progress
    // callback, switches to `RandomAccess.Copy`) updates the shared
    // helper once and both strippers benefit. The stripper tests exercise
    // `CopyExactly` indirectly (every segment-walker test goes through it),
    // but no direct test pinned the helper's contract — these tests close
    // the gap.

    [Fact]
    public void CopyExactly_StreamToStream_CopiesAllBytes()
    {
        // Sanity: a 10-byte source, a 10-byte count, expect all 10 bytes
        // copied to the destination in order.
        var srcBytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using var src = new MemoryStream(srcBytes);
        using var dst = new MemoryStream();

        StreamHelpers.CopyExactly(src, dst, 10, "TEST");

        Assert.Equal(srcBytes, dst.ToArray());
        Assert.Equal(10, src.Position);
    }

    [Fact]
    public void CopyExactly_StreamShorterThanCount_Throws()
    {
        // Defense: a 5-byte source with a 10-byte count must throw, not
        // silently copy fewer bytes (the pre-fix `Stream.Read` returns
        // 0 on EOF, and the loop checks for that). The `context` tag
        // must appear in the error message so the user knows which
        // stream truncated.
        var srcBytes = new byte[] { 1, 2, 3, 4, 5 };
        using var src = new MemoryStream(srcBytes);
        using var dst = new MemoryStream();

        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.CopyExactly(src, dst, 10, "TEST"));
        Assert.Contains("TEST", ex.Message);
        Assert.Contains("segment copy", ex.Message);
    }

    [Fact]
    public void CopyExactly_EmptyStream_NonZeroCount_Throws()
    {
        // Defense: an empty source with a non-zero count must throw.
        // Same as the truncated case — the loop's first `Read` returns
        // 0, the guard fires.
        using var src = new MemoryStream(Array.Empty<byte>());
        using var dst = new MemoryStream();

        var ex = Assert.Throws<EndOfStreamException>(() =>
            StreamHelpers.CopyExactly(src, dst, 1, "EMPTY"));
        Assert.Contains("EMPTY", ex.Message);
    }

    [Fact]
    public void CopyExactly_ZeroCount_NoOp()
    {
        // Edge case: count == 0 must be a no-op (no reads, no writes).
        // This matches the `SkipExactly(long, 0)` no-op pattern from
        // D87 and the M2.20.34 D96 UX-correction lesson (don't silently
        // skip the user's intent).
        using var src = new MemoryStream(new byte[] { 1, 2, 3 });
        using var dst = new MemoryStream();

        StreamHelpers.CopyExactly(src, dst, 0, "TEST");

        Assert.Empty(dst.ToArray());
        Assert.Equal(0, src.Position);
    }

    [Fact]
    public void CopyExactly_LargeCount_ReadsAcrossMultipleLoopIterations()
    {
        // Loop-exercise case: a 200 KB source (3+ chunks at 64 KB each)
        // must copy all bytes, with the loop's `take = Math.Min(remaining,
        // buf.Length)` correctly handing the tail (200 KB - 3*64 KB = 8 KB).
        // The 64 KB scratch buffer is implementation-defined (see
        // `StreamHelpers.CopyExactly`'s `Math.Min(count, 64 * 1024)`),
        // so we don't pin a specific buffer size in the test — we just
        // verify the count is satisfied and all bytes are copied.
        var srcBytes = new byte[200 * 1024];
        for (int i = 0; i < srcBytes.Length; i++) srcBytes[i] = (byte)(i & 0xFF);
        using var src = new MemoryStream(srcBytes);
        using var dst = new MemoryStream();

        StreamHelpers.CopyExactly(src, dst, srcBytes.Length, "TEST");

        Assert.Equal(srcBytes, dst.ToArray());
        Assert.Equal(srcBytes.Length, src.Position);
    }

    [Fact]
    public void CopyExactly_NonSeekableSource_StillCopiesAllBytes()
    {
        // The CopyExactly implementation does NOT branch on CanSeek (it
        // always uses a read loop, not Stream.CopyTo which optimizes the
        // seekable case). This test pins that behavior so a future
        // refactor that adds a seekable fast path doesn't break the
        // non-seekable contract. Uses the NonSeekableStream test
        // helper from SkipExactly's tests.
        var srcBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        using var src = new NonSeekableStream(new MemoryStream(srcBytes));
        using var dst = new MemoryStream();

        StreamHelpers.CopyExactly(src, dst, srcBytes.Length, "TEST");

        Assert.Equal(srcBytes, dst.ToArray());
    }

    /// <summary>
    /// Test helper: a Stream wrapper that always reports <c>CanSeek = false</c>,
    /// so the SkipExactly helper takes the read-loop path even when the
    /// underlying stream supports seeking.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
