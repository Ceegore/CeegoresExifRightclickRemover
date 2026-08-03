using System.IO;
using ExifRemover.Engine;
using MetadataExtractor;
using Xunit;

namespace ExifRemover.Tests;

public class PngStripperTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private string WriteTemp(byte[] bytes, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Strip_MinimalPng_NoMetadata_FailsCleanly()
    {
        var src = WriteTemp(FixtureFactory.MinimalPng(), $"er-min-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        var result = PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);
        Assert.Equal(0, result.DroppedSegments);
        Assert.False(result.Changed);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(outPath));

        // The minimal PNG has only IHDR/IDAT/IEND — MetadataExtractor still surfaces them as
        // PngDirectory entries (Image Width etc.), but those are structural, not "metadata" in
        // the privacy sense. We assert that no privacy-relevant group remains.
        var inspect = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(inspect.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.DoesNotContain(inspect.Entries, e => e.Group == MetadataGroups.PngTime);
        Assert.DoesNotContain(inspect.Entries, e => e.Group == MetadataGroups.PngExif);
        Assert.DoesNotContain(inspect.Entries, e => e.Group == MetadataGroups.PngIccp);
    }

    [Fact]
    public void Strip_RemovesTextTimeExifIccp_UnderPrivacyProfile_KeepsGama()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-meta-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

// Pre-condition: source has tEXt, tIME, iCCP. (eXIf chunks are opaque to MetadataExtractor's
        // PNG reader and don't surface as tag entries; the stripper still drops the chunk.)
        var preInspect = MetadataInspector.Inspect(src);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngTime);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngIccp);

        var result = PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // tEXt + tIME + eXIf + iCCP = 4 dropped
        Assert.True(result.DroppedSegments >= 4, $"Expected to drop >=4 chunks, got {result.DroppedSegments}.");

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngTime);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngIccp);

        // gAMA must be kept under Privacy profile
        Assert.Contains(post.Entries, e => e.Group == MetadataGroups.PngGama);
    }

    [Fact]
    public void Strip_StripsGama_UnderAllMetadataProfile()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-all-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.AllMetadata);

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngGama);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngText);
    }

    [Fact]
    public void Strip_KeepsIccp_UnderMinimalProfile()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-min2-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Minimal);

        var post = MetadataInspector.Inspect(outPath);
        // Minimal keeps iCCP, but still drops text/time/exif
        Assert.Contains(post.Entries, e => e.Group == MetadataGroups.PngIccp);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngTime);
    }

    [Fact]
    public void Strip_PreservesIdatBytesBitForBit()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-idat-{Guid.NewGuid():N}.png");
        var srcBytes = File.ReadAllBytes(src);
        var srcIdat = ExtractIdatPayload(srcBytes);

        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var outBytes = File.ReadAllBytes(outPath);
        var outIdat = ExtractIdatPayload(outBytes);

        Assert.Equal(srcIdat, outIdat);
    }

    [Fact]
    public void Strip_PreservesIhdrBytesBitForBit()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-ihdr-{Guid.NewGuid():N}.png");
        var srcBytes = File.ReadAllBytes(src);
        var srcIhdr = ExtractChunk(srcBytes, "IHDR");

        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var outBytes = File.ReadAllBytes(outPath);
        var outIhdr = ExtractChunk(outBytes, "IHDR");

        Assert.Equal(srcIhdr, outIhdr);
    }

    [Fact]
    public void Strip_TruncatedPng_ThrowsAndLeavesOriginalUntouched()
    {
        var bytes = FixtureFactory.TruncatedPng();
        var src = WriteTemp(bytes, $"er-trunc-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        Assert.ThrowsAny<Exception>(() => PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy));

        Assert.Equal(bytes, File.ReadAllBytes(src));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Strip_IsIdempotent()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-idem-{Guid.NewGuid():N}.png");
        var out1 = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        var out2 = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.AddRange(new[] { out1, out2 });

        var r1 = PngMetadataStripper.Strip(src, out1, overwriteSource: false, StripProfile.Privacy);
        var r2 = PngMetadataStripper.Strip(out1, out2, overwriteSource: false, StripProfile.Privacy);

        Assert.Equal(0, r2.DroppedSegments);
        Assert.False(r2.Changed);
    }

    [Fact]
    public void Strip_OverwriteSource_ReplacesOriginalAtomically()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-ovr-{Guid.NewGuid():N}.png");
        var originalLength = new FileInfo(src).Length;

        var result = PngMetadataStripper.Strip(src, Path.Combine(Path.GetTempPath(), "ignored.png"), overwriteSource: true, StripProfile.Privacy);

        Assert.True(result.OverwroteSource);
        Assert.Equal(src, result.OutputPath);
        Assert.True(new FileInfo(src).Length <= originalLength);

        var post = MetadataInspector.Inspect(src);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngTime);
    }

    [Fact]
    public void Strip_RecomputesCrcOfKeptChunks_CrcValidates()
    {
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-crc-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // Walk the output chunks and verify CRC for every kept chunk.
        var bytes = File.ReadAllBytes(outPath);
        Assert.True(bytes.Length >= 8);
        int pos = 8;
        int chunksSeen = 0;
        while (pos < bytes.Length)
        {
            int length = (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            int dataStart = pos + 8;
            int crcOffset = dataStart + length;
            if (crcOffset + 4 > bytes.Length) break;

            uint storedCrc = ((uint)bytes[crcOffset] << 24) | ((uint)bytes[crcOffset + 1] << 16)
                             | ((uint)bytes[crcOffset + 2] << 8) | bytes[crcOffset + 3];
            uint computed = ComputeCrc32(bytes, pos + 4, 4 + length);
            Assert.True(storedCrc == computed, $"CRC mismatch in chunk {type}: stored={storedCrc:X8} computed={computed:X8}");

            pos = crcOffset + 4;
            chunksSeen++;
            if (type == "IEND") break;
        }
        Assert.True(chunksSeen >= 3, "Expected IHDR + IDAT + IEND at minimum.");
    }

    // ---------- helpers ----------

    private static byte[] ExtractIdatPayload(byte[] pngBytes)
    {
        var ms = new MemoryStream();
        int pos = 8;
        while (pos < pngBytes.Length)
        {
            int length = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
            int dataStart = pos + 8;
            if (type == "IDAT")
            {
                ms.Write(pngBytes, dataStart, length);
            }
            int next = dataStart + length + 4;
            if (type == "IEND" || next > pngBytes.Length) break;
            pos = next;
        }
        return ms.ToArray();
    }

    private static byte[] ExtractChunk(byte[] pngBytes, string typeName)
    {
        int pos = 8;
        while (pos < pngBytes.Length)
        {
            int length = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
            if (type == typeName)
            {
                var chunk = new byte[8 + length + 4];
                Buffer.BlockCopy(pngBytes, pos, chunk, 0, chunk.Length);
                return chunk;
            }
            int next = pos + 8 + length + 4;
            if (type == "IEND" || next > pngBytes.Length) break;
            pos = next;
        }
        throw new InvalidOperationException($"Chunk {typeName} not found.");
    }

    private static uint ComputeCrc32(byte[] data, int offset, int length)
    {
        const uint poly = 0xEDB88320u;
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (poly ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < length; i++)
        {
            crc = t[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}