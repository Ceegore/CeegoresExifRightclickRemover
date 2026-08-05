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
}
