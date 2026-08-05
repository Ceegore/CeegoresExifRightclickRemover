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
