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

// Pre-condition: source has tEXt, tIME, iCCP, and eXIf. The PNG inspector now
        // surfaces eXIf as its own PngExif group via a small byte-level probe (the
        // stripper has always dropped eXIf, but the inspector used to roll it into
        // PngText, hiding the fact that the EXIF block would be removed). See L4 in
        // _temp11.md for the original audit note.
        var preInspect = MetadataInspector.Inspect(src);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngTime);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngIccp);
        Assert.Contains(preInspect.Entries, e => e.Group == MetadataGroups.PngExif);

        var result = PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        // tEXt + tIME + eXIf + iCCP = 4 dropped
        Assert.True(result.DroppedSegments >= 4, $"Expected to drop >=4 chunks, got {result.DroppedSegments}.");

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngText);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngTime);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngIccp);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngExif);

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
    public void Strip_KeepsUnknownAncillaryChunk_UnderAllProfiles()
    {
        // D2: a "private" ancillary chunk whose type is not in PngMetadataStripper.ShouldDrop's
        // switch (e.g. "tEST") must be kept by the stripper under every profile. The engine
        // falls through to "return false" for any type it doesn't recognize, so the chunk is
        // preserved. The UI's keep set must agree (the OverlayViewModel fix adds PNGUNKNOWN
        // to the always-keep set so the grid shows "Would be kept" for any such chunk the
        // inspector surfaces).
        foreach (var profile in new[] { StripProfile.Privacy, StripProfile.AllMetadata, StripProfile.Minimal })
        {
            var src = WriteTemp(FixtureFactory.PngWithUnknownAncillaryChunk(), $"er-unknown-{profile}-{Guid.NewGuid():N}.png");
            var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
            _tempFiles.Add(outPath);

            PngMetadataStripper.Strip(src, outPath, overwriteSource: false, profile);

            var bytes = File.ReadAllBytes(outPath);
            Assert.True(ContainsChunk(bytes, "tEST"), $"unknown ancillary chunk 'tEST' must be kept under {profile}.");
        }
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

    // ---------- B2 / B8 / B9 / B15 regression tests ----------

    [Fact]
    public void Inspect_SurfacesPngExifAsSeparateGroup()
    {
        // B8 / L4: MetadataExtractor's PNG reader rolls tEXt and eXIf into a single
        // PngText bucket, which would hide the fact that the stripper drops eXIf.
        // The PngChunkProbe adds a PngExif entry whenever an eXIf chunk is present.
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-exif-{Guid.NewGuid():N}.png");
        var inspection = MetadataInspector.Inspect(src);
        Assert.Contains(inspection.Entries, e => e.Group == MetadataGroups.PngExif);
    }

    [Fact]
    public void Inspect_SurfacesPngHistAsSeparateGroup()
    {
        // D69 (M2.20.18): MetadataExtractor's PNG reader has no TagHistogram tag, so a hIST
        // chunk was silently invisible to the grid even though the stripper drops hIST under
        // Privacy/AllMetadata. The PngChunkProbe now adds a PngHist entry whenever a hIST
        // chunk is present, so the user can see "PNG hIST" in the grid and the action column
        // shows "Would be removed" under Privacy/AllMetadata / "Would be kept" under Minimal.
        var src = WriteTemp(FixtureFactory.PngWithHistChunk(), $"er-hist-{Guid.NewGuid():N}.png");
        var inspection = MetadataInspector.Inspect(src);
        var hist = inspection.Entries.SingleOrDefault(e => e.Group == MetadataGroups.PngHist);
        Assert.NotNull(hist);
        Assert.Equal("Palette histogram", hist.Name);
        Assert.Equal(4L, hist.EstimatedSizeBytes);
        Assert.False(hist.IsPrivacySensitive);
    }

    [Fact]
    public void Strip_PngWithHist_HistEntryRemovedAfterStrip()
    {
        // After a Privacy strip, the hIST chunk is gone, so the PngHist entry
        // must also be gone from the post-strip inspection. (If a future maintainer
        // regresses the stripper to keep hIST under Privacy, this test fails — and
        // similarly if the probe ever reports hIST after strip.)
        var src = WriteTemp(FixtureFactory.PngWithHistChunk(), $"er-hist-out-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngHist);
    }

    [Fact]
    public void Strip_PngWithExif_ExifEntryRemovedAfterStrip()
    {
        // After a Privacy strip, the eXIf chunk is gone, so the PngExif entry
        // must also be gone from the post-strip inspection.
        var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-exif-out-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);

        var post = MetadataInspector.Inspect(outPath);
        Assert.DoesNotContain(post.Entries, e => e.Group == MetadataGroups.PngExif);
    }

    [Fact]
    public void PngMetadataStripper_RejectsChunkLengthAboveCap()
    {
        // B9 / B15: a corrupt or malicious PNG claiming a chunk length above the
        // 256 MB cap must be rejected cleanly, not OOM the process by allocating
        // the requested buffer.
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteRawChunk(ms, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04, 0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });
        // A tEXt chunk with a length field set to 2^31-1 (above the cap).
        WriteRawLength(ms, 0x7FFFFFFF);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("tEXt"));
        WriteRawCrc(ms, 0);
        WriteRawChunk(ms, "IEND", Array.Empty<byte>());

        var src = WriteTemp(ms.ToArray(), $"er-huge-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        Assert.Throws<InvalidDataException>(() =>
            PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy));
    }

    [Fact]
    public void Strip_KeepsTpngTrnsUnderEveryProfile()
    {
        // B2: the stripper keeps tRNS regardless of profile. After every strip
        // variant, tRNS must still be in the output.
        foreach (var profile in new[] { StripProfile.Privacy, StripProfile.AllMetadata, StripProfile.Minimal })
        {
            var src = WriteTemp(FixtureFactory.PngWithTextTimeExifIccp(), $"er-trns-{profile}-{Guid.NewGuid():N}.png");
            var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
            _tempFiles.Add(outPath);

            PngMetadataStripper.Strip(src, outPath, overwriteSource: false, profile);

            var bytes = File.ReadAllBytes(outPath);
            Assert.True(ContainsChunk(bytes, "tRNS"), $"tRNS must be kept under {profile}.");
        }
    }

    [Fact]
    public void Strip_AlwaysKeepsPngPhysBkgdSbitTrns_AcrossAllProfiles()
    {
        // B2 (engine side): pHYs, bKGD, sBIT, tRNS are always kept by the stripper,
        // even under the most aggressive profile (AllMetadata). The OverlayViewModel
        // fix (B2 / UI side) makes the review grid show them as "Would be kept"; this
        // test proves the engine actually keeps them.
        foreach (var profile in new[] { StripProfile.Privacy, StripProfile.AllMetadata, StripProfile.Minimal })
        {
            var src = WriteTemp(FixtureFactory.PngWithAlwaysKeptAncillaryChunks(), $"er-keepall-{profile}-{Guid.NewGuid():N}.png");
            var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
            _tempFiles.Add(outPath);

            PngMetadataStripper.Strip(src, outPath, overwriteSource: false, profile);

            var bytes = File.ReadAllBytes(outPath);
            foreach (var chunk in new[] { "pHYs", "bKGD", "sBIT", "tRNS" })
            {
                Assert.True(ContainsChunk(bytes, chunk), $"{chunk} must be kept under {profile}.");
            }
            // The text chunk we added should have been dropped.
            Assert.False(ContainsChunk(bytes, "tEXt"), $"tEXt must be dropped under {profile}.");
        }
    }

    [Fact]
    public void Strip_SkippedChunkDoesNotAllocatePayloadBuffer()
    {
        // Smoke test: stripping a PNG with a large tEXt chunk that gets dropped
        // must succeed. We can't easily prove "no allocation" from a test, but we
        // CAN prove the stripper handles a 1 MB tEXt payload correctly (and the
        // skip-without-allocate path actually runs). A regression to the
        // old "allocate then discard" code would still pass, but the test
        // catches a buffer-blowout if the cap were lowered / removed.
        var large = new byte[1024 * 1024];
        for (int i = 0; i < large.Length; i++) large[i] = (byte)('A' + (i % 26));
        var t = new List<byte>();
        t.AddRange(System.Text.Encoding.ASCII.GetBytes("Comment"));
        t.Add(0);
        t.AddRange(large);

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteRawChunk(ms, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04, 0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });
        WriteRawChunk(ms, "tEXt", t.ToArray());
        WriteRawChunk(ms, "IDAT", new byte[]
        {
            0x78, 0x01, 0x01, 0x06, 0x00, 0xFB, 0xFF, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x6A, 0x6F, 0xC2, 0x68
        });
        WriteRawChunk(ms, "IEND", Array.Empty<byte>());

        var src = WriteTemp(ms.ToArray(), $"er-skip-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        var result = PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);
        Assert.Equal(1, result.DroppedSegments); // exactly the tEXt chunk

        var bytes = File.ReadAllBytes(outPath);
        Assert.False(ContainsChunk(bytes, "tEXt"), "tEXt must be dropped.");
        Assert.True(ContainsChunk(bytes, "IHDR"));
        Assert.True(ContainsChunk(bytes, "IEND"));
    }

    [Fact]
    public void Strip_LargeKeptIdat_AllocatesAndPreservesBytes()
    {
        // D33: the kept-chunk path in PngMetadataStripper allocates
        // `new byte[length]` for each kept chunk (the CRC must be recomputed over
        // the data, and the data must be copied to the output). The existing
        // B9 test (`Strip_SkippedChunkDoesNotAllocatePayloadBuffer`) exercises the
        // SKIP path with a 1 MB tEXt that gets dropped — but the KEPT path
        // (a kept IDAT) is only ever exercised with the few-hundred-byte IDAT
        // in the standard fixture. This test pins the kept-chunk allocation path
        // with a 10 MB IDAT to catch a regression where the buffer is too small
        // (e.g. someone caps it at 1 MB) or the read loop is wrong for multi-MB
        // chunks. The 256 MB cap is independently tested by
        // `PngMetadataStripper_RejectsChunkLengthAboveCap`; 10 MB is well under
        // the cap and well above the "few hundred KB" typical IDAT.
        const int idatSize = 10 * 1024 * 1024; // 10 MB
        var idat = new byte[idatSize];
        // Deterministic fill: pattern that compresses to something non-trivial
        // but doesn't trigger the CRC's all-zero fast path. zlib stream header
        // + a simple repeating pattern.
        idat[0] = 0x78; // zlib CMF (deflate, window 32K)
        idat[1] = 0x01; // zlib FLG (no dictionary, compression level 0)
        for (int i = 2; i < idatSize; i++)
        {
            idat[i] = (byte)((i * 31) ^ (i >> 8));
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteRawChunk(ms, "IHDR", new byte[]
        {
            0x00,0x00,0x00,0x04, 0x00,0x00,0x00,0x04,
            0x08,0x02,0x00,0x00,0x00
        });
        WriteRawChunk(ms, "IDAT", idat);
        WriteRawChunk(ms, "IEND", Array.Empty<byte>());

        var src = WriteTemp(ms.ToArray(), $"er-bigidat-{Guid.NewGuid():N}.png");
        var outPath = Path.Combine(Path.GetTempPath(), $"er-out-{Guid.NewGuid():N}.png");
        _tempFiles.Add(outPath);

        var result = PngMetadataStripper.Strip(src, outPath, overwriteSource: false, StripProfile.Privacy);
        Assert.Equal(0, result.DroppedSegments); // IDAT is always kept
        Assert.False(result.Changed);

        // The output IDAT must be byte-identical to the input IDAT (no re-encoding
        // of pixel data, ever).
        var outBytes = File.ReadAllBytes(outPath);
        var outIdat = ExtractIdatPayload(outBytes);
        Assert.Equal(idatSize, outIdat.Length);
        Assert.Equal(idat, outIdat);
    }

    private static bool ContainsChunk(byte[] pngBytes, string type)
    {
        int pos = 8;
        while (pos + 12 <= pngBytes.Length)
        {
            int length = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16)
                       | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
            var t = System.Text.Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
            if (t == type) return true;
            int next = pos + 8 + length + 4;
            if (t == "IEND" || next > pngBytes.Length) break;
            pos = next;
        }
        return false;
    }

    private static void WriteRawChunk(MemoryStream ms, string type, byte[] data)
    {
        WriteRawLength(ms, data.Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
        ms.Write(data);
        WriteRawCrc(ms, 0);
    }

    private static void WriteRawLength(MemoryStream ms, int length)
    {
        ms.WriteByte((byte)(length >> 24));
        ms.WriteByte((byte)(length >> 16));
        ms.WriteByte((byte)(length >> 8));
        ms.WriteByte((byte)length);
    }

    private static void WriteRawCrc(MemoryStream ms, uint crc)
    {
        ms.WriteByte((byte)(crc >> 24));
        ms.WriteByte((byte)(crc >> 16));
        ms.WriteByte((byte)(crc >> 8));
        ms.WriteByte((byte)crc);
    }
}