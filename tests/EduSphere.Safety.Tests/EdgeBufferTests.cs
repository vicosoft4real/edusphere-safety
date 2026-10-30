using System.Security.Cryptography;
using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeBufferTests
{
    // Fixed 32-byte keys, TEST-ONLY (never production secrets).
    private static byte[] TestKey(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset T0 =
        new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AppendSegment_RoundTrip_ReturnsPlaintext()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 1_000_000);
        var plain = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var id = buffer.AppendSegment(T0, T0.AddSeconds(2), plain);
        var back = buffer.GetSegment(id);
        Assert.Equal(plain, back);
    }

    [Fact]
    public void AppendSegment_WrongKey_ThrowsCryptographic()
    {
        var dir = NewTempDir();
        var writer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = writer.AppendSegment(T0, T0.AddSeconds(2), new byte[] { 9, 9, 9 });
        var reader = new EvidenceBuffer(dir, TestKey(0x22), boundedBytes: 1_000_000);
        Assert.ThrowsAny<CryptographicException>(() => reader.GetSegment(id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Ctor_BadKeyLength_Throws(int length)
    {
        Assert.Throws<ArgumentException>(
            () => new EvidenceBuffer(NewTempDir(), new byte[length], boundedBytes: 1000));
    }

    [Fact]
    public void Ctor_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EvidenceBuffer(NewTempDir(), null!, boundedBytes: 1000));
    }

    [Fact]
    public void Eviction_DropsOldestSealedFirst_ManifestStaysConsistent()
    {
        // Each plaintext payload is 100 bytes; the AES-GCM envelope adds 28 bytes
        // (12 nonce + 16 tag), so 3 segments use 384 bytes. A 300-byte bound keeps 2.
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 300);
        var payload = Enumerable.Repeat((byte)7, 100).ToArray();
        var id1 = buffer.AppendSegment(T0, T0.AddSeconds(1), payload);
        buffer.SealSegment(id1);
        var id2 = buffer.AppendSegment(T0.AddSeconds(1), T0.AddSeconds(2), payload);
        buffer.SealSegment(id2);
        var id3 = buffer.AppendSegment(T0.AddSeconds(2), T0.AddSeconds(3), payload);
        buffer.SealSegment(id3);

        var ids = buffer.Segments.Select(s => s.SegmentId).ToArray();
        Assert.DoesNotContain(id1, ids);
        Assert.Contains(id2, ids);
        Assert.Contains(id3, ids);

        // Manifest consistency: every listed segment decrypts; the evicted file is gone.
        Assert.Equal(payload, buffer.GetSegment(id2));
        Assert.Equal(payload, buffer.GetSegment(id3));
        Assert.Throws<KeyNotFoundException>(() => buffer.GetSegment(id1));
        Assert.True(buffer.UsedBytes <= 300);
    }

    [Fact]
    public void Eviction_ActiveSegmentExempt_NeverDeletedWhileUnsealed()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 150);
        var payload = Enumerable.Repeat((byte)7, 100).ToArray();
        var active = buffer.AppendSegment(T0, T0.AddSeconds(1), payload);
        // Unsealed (active) segment must survive even though it alone exceeds the bound.
        Assert.Contains(active, buffer.Segments.Select(s => s.SegmentId));
        Assert.Equal(payload, buffer.GetSegment(active));
    }

    [Fact]
    public void Manifest_ReloadAfterRestart_SeesSameSegments()
    {
        var dir = NewTempDir();
        var first = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = first.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 1, 2, 3 });
        first.SealSegment(id);

        var second = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var segs = second.Segments;
        Assert.Single(segs);
        Assert.Equal(id, segs[0].SegmentId);
        Assert.True(segs[0].Sealed);
        Assert.Equal(new byte[] { 1, 2, 3 }, second.GetSegment(id));
        Assert.Equal(64, segs[0].Sha256Hex.Length); // SHA256 hex recorded
    }

    [Fact]
    public void Manifest_RecordsTimes_AndSha256()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 1_000_000);
        var plain = new byte[] { 4, 5, 6 };
        var id = buffer.AppendSegment(T0, T0.AddSeconds(5), plain);
        var seg = buffer.Segments.Single(s => s.SegmentId == id);
        Assert.Equal(T0, seg.StartEventTimeUtc);
        Assert.Equal(T0.AddSeconds(5), seg.EndEventTimeUtc);
        var expected = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();
        Assert.Equal(expected, seg.Sha256Hex);
        Assert.False(seg.Sealed);
    }

    private static PendingUpload Upload(string edgeId, int attempt = 0) => new(
        edgeId, "zone-a", 2, 42, T0, T0.AddSeconds(-1), "seg-00000001", attempt);

    [Fact]
    public void Spool_RestartResume_ReloadsPendingUploads()
    {
        var path = Path.Combine(NewTempDir(), "spool.jsonl");
        var spool = new UploadSpool(path);
        spool.Enqueue(Upload("edge-1"));
        spool.Enqueue(Upload("edge-2"));

        var resumed = new UploadSpool(path);
        Assert.Equal(2, resumed.Count);
        Assert.Equal("edge-1", resumed.PeekOldest()!.EdgeEventId);
    }

    [Fact]
    public void Spool_DrainOldestFirst_FileStaysValidJsonl()
    {
        var path = Path.Combine(NewTempDir(), "spool.jsonl");
        var spool = new UploadSpool(path);
        spool.Enqueue(Upload("edge-1"));
        spool.Enqueue(Upload("edge-2"));
        spool.NoteAttempt("edge-1");

        Assert.True(spool.Remove("edge-1"));
        Assert.Equal(1, spool.Count);
        Assert.Equal("edge-2", spool.PeekOldest()!.EdgeEventId);

        // Atomic rewrite left a valid file: a fresh instance resumes with attempt intact.
        var resumed = new UploadSpool(path);
        Assert.Equal("edge-2", resumed.PeekOldest()!.EdgeEventId);
        Assert.False(spool.Remove("missing-id"));
    }

    [Fact]
    public void DelayedLabelling_ThresholdBoundary_IsStrictlyGreater()
    {
        var threshold = TimeSpan.FromSeconds(60);
        Assert.False(UploadSpool.IsDelayed(T0, T0.AddSeconds(60), threshold));
        Assert.True(UploadSpool.IsDelayed(T0, T0.AddSeconds(60).AddTicks(1), threshold));
        Assert.False(UploadSpool.IsDelayed(T0, T0.AddSeconds(59), threshold));
    }

    [Fact]
    public void DelayedLabelling_InstanceThreshold_UsedByHelper()
    {
        var path = Path.Combine(NewTempDir(), "spool.jsonl");
        var spool = new UploadSpool(path, delayThreshold: TimeSpan.FromSeconds(60));
        Assert.False(spool.IsDelayedAt(T0, T0.AddSeconds(10)));
        Assert.True(spool.IsDelayedAt(T0, T0.AddMinutes(5)));
    }

    [Fact]
    public void Envelope_NoncesUnique_SamePlaintextDecrypts()
    {
        var dir = NewTempDir();
        var buffer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var plain = new byte[] { 10, 20, 30, 40 };
        var id1 = buffer.AppendSegment(T0, T0.AddSeconds(1), plain);
        var id2 = buffer.AppendSegment(T0.AddSeconds(1), T0.AddSeconds(2), plain);
        // Random nonces: identical plaintexts must produce distinct envelopes.
        Assert.NotEqual(
            File.ReadAllBytes(Path.Combine(dir, id1 + ".mp4")),
            File.ReadAllBytes(Path.Combine(dir, id2 + ".mp4")));
        Assert.Equal(plain, buffer.GetSegment(id1));
        Assert.Equal(plain, buffer.GetSegment(id2));
    }

    [Fact]
    public void Envelope_TamperedBytes_ThrowsCryptographic()
    {
        var dir = NewTempDir();
        var buffer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = buffer.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 1, 2, 3, 4 });
        var path = Path.Combine(dir, id + ".mp4");
        var envelope = File.ReadAllBytes(path);
        envelope[15] ^= 0xFF; // Flip a ciphertext byte (past the 12-byte nonce).
        File.WriteAllBytes(path, envelope);
        Assert.ThrowsAny<CryptographicException>(() => buffer.GetSegment(id));
    }

    [Fact]
    public void Manifest_TamperedHash_ThrowsCryptographic()
    {
        var dir = NewTempDir();
        var first = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = first.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 5, 6, 7 });
        var manifestPath = Path.Combine(dir, "manifest.json");
        var manifest = File.ReadAllText(manifestPath);
        var seg = first.Segments.Single(s => s.SegmentId == id);
        File.WriteAllText(manifestPath, manifest.Replace(
            seg.Sha256Hex, new string('0', 64), StringComparison.Ordinal));
        var second = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        Assert.Throws<CryptographicException>(() => second.GetSegment(id));
    }

    [Fact]
    public void Spool_CorruptLine_ReopenSkipsAndCounts()
    {
        var path = Path.Combine(NewTempDir(), "spool.jsonl");
        var spool = new UploadSpool(path);
        spool.Enqueue(Upload("edge-1"));
        File.AppendAllText(path, "this is not json{{{\n");
        File.AppendAllText(path, "\n"); // Blank lines are ignored, not counted.
        File.AppendAllText(path, "{\"null\": 1, \"unclosed\":\n");
        spool.Enqueue(Upload("edge-2"));

        var resumed = new UploadSpool(path);
        Assert.Equal(2, resumed.Count);
        Assert.Equal(2, resumed.SkippedCorruptLines);
        Assert.Equal("edge-1", resumed.PeekOldest()!.EdgeEventId);

        // Restart still works: drain and enqueue proceed normally.
        Assert.True(resumed.Remove("edge-1"));
        resumed.NoteAttempt("edge-2");
        Assert.Equal(1, resumed.All.Single().AttemptCount);
    }

    [Fact]
    public void Manifest_Corrupt_QuarantinedStartsEmptyFlagged()
    {
        var dir = NewTempDir();
        var first = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = first.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 8, 8, 8 });
        var segmentFile = Path.Combine(dir, id + ".mp4");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "CORRUPT{{{");

        var second = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        Assert.True(second.ManifestWasCorrupt);
        Assert.Empty(second.Segments);

        // Quarantine: exactly one manifest.corrupt-<ticks> file; manifest.json rewritten valid.
        var quarantined = Directory.GetFiles(dir, "manifest.corrupt-*");
        var quarantine = Assert.Single(quarantined);
        Assert.Equal("CORRUPT{{{", File.ReadAllText(quarantine));
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));

        // Segment files remain on disk for manual recovery; new appends do not clobber them.
        Assert.True(File.Exists(segmentFile));
        var fresh = second.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 9 });
        Assert.NotEqual(id, fresh);
        Assert.True(File.Exists(segmentFile));
        Assert.Equal(new byte[] { 9 }, second.GetSegment(fresh));
    }

    [Fact]
    public void Seal_UnknownId_ThrowsKeyNotFound()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 1000);
        Assert.Throws<KeyNotFoundException>(() => buffer.SealSegment("seg-99999999"));
    }

    [Fact]
    public void Get_UnknownId_ThrowsKeyNotFound()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 1000);
        Assert.Throws<KeyNotFoundException>(() => buffer.GetSegment("seg-99999999"));
    }

    [Fact]
    public void Pin_UnknownId_ThrowsKeyNotFound()
    {
        var buffer = new EvidenceBuffer(NewTempDir(), TestKey(0x11), boundedBytes: 1000);
        Assert.Throws<KeyNotFoundException>(() => buffer.Pin("seg-99999999"));
    }

    [Fact]
    public void NoteAttempt_UnknownId_IsNoOp()
    {
        var spool = new UploadSpool(Path.Combine(NewTempDir(), "spool.jsonl"));
        spool.Enqueue(Upload("edge-1"));
        spool.NoteAttempt("missing-id"); // Must not throw; queued entry untouched.
        Assert.Equal(1, spool.Count);
        Assert.Equal(0, spool.PeekOldest()!.AttemptCount);
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        var spool = new UploadSpool(Path.Combine(NewTempDir(), "spool.jsonl"));
        Assert.False(spool.Remove("missing-id"));
    }

    [Fact]
    public void Spool_DuplicateEnqueue_SameEdgeIdRejected()
    {
        var spool = new UploadSpool(Path.Combine(NewTempDir(), "spool.jsonl"));
        spool.Enqueue(Upload("edge-dup"));
        Assert.Throws<InvalidOperationException>(() => spool.Enqueue(Upload("edge-dup")));
        Assert.Equal(1, spool.Count);
    }

    [Fact]
    public void Unpin_IsIdempotent_UnknownOrRepeatedNoThrow()
    {
        var dir = NewTempDir();
        var buffer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 1_000_000);
        var id = buffer.AppendSegment(T0, T0.AddSeconds(1), new byte[] { 1 });
        buffer.Unpin("seg-never-known"); // Unknown: no-op, never throws.
        buffer.Pin(id);
        Assert.True(buffer.IsPinned(id));
        buffer.Unpin(id);
        buffer.Unpin(id); // Repeated: no-op.
        Assert.False(buffer.IsPinned(id));
    }

    [Fact]
    public void Pin_BlocksEviction_UnpinAllowsIt()
    {
        // 100-byte payloads -> 128-byte envelopes; a 300-byte bound retains 2 sealed.
        var dir = NewTempDir();
        var buffer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 300);
        var payload = Enumerable.Repeat((byte)7, 100).ToArray();
        var id1 = buffer.AppendSegment(T0, T0.AddSeconds(1), payload);
        buffer.SealSegment(id1);
        var id2 = buffer.AppendSegment(T0.AddSeconds(1), T0.AddSeconds(2), payload);
        buffer.SealSegment(id2);
        buffer.Pin(id1); // Oldest is pinned before pressure: eviction must skip it.
        var id3 = buffer.AppendSegment(T0.AddSeconds(2), T0.AddSeconds(3), payload);
        buffer.SealSegment(id3);

        var ids = buffer.Segments.Select(s => s.SegmentId).ToArray();
        Assert.Contains(id1, ids);
        Assert.DoesNotContain(id2, ids); // Unpinned middle segment evicted instead.
        Assert.Contains(id3, ids);
        Assert.True(File.Exists(Path.Combine(dir, id1 + ".mp4")));
        Assert.False(File.Exists(Path.Combine(dir, id2 + ".mp4")));

        buffer.Unpin(id1);
        var id4 = buffer.AppendSegment(T0.AddSeconds(3), T0.AddSeconds(4), payload);
        buffer.SealSegment(id4);
        Assert.DoesNotContain(id1, buffer.Segments.Select(s => s.SegmentId));
        Assert.False(File.Exists(Path.Combine(dir, id1 + ".mp4")));
    }

    [Fact]
    public void Eviction_DeletesSegmentFileFromDisk()
    {
        var dir = NewTempDir();
        var buffer = new EvidenceBuffer(dir, TestKey(0x11), boundedBytes: 300);
        var payload = Enumerable.Repeat((byte)7, 100).ToArray();
        var id1 = buffer.AppendSegment(T0, T0.AddSeconds(1), payload);
        buffer.SealSegment(id1);
        var id2 = buffer.AppendSegment(T0.AddSeconds(1), T0.AddSeconds(2), payload);
        buffer.SealSegment(id2);
        var id3 = buffer.AppendSegment(T0.AddSeconds(2), T0.AddSeconds(3), payload);
        buffer.SealSegment(id3);
        Assert.False(File.Exists(Path.Combine(dir, id1 + ".mp4")));
        Assert.True(File.Exists(Path.Combine(dir, id3 + ".mp4")));
    }

    [Fact]
    public void Ctor_KeyMutatedAfterwards_BufferUnaffected()
    {
        var key = TestKey(0x11);
        var buffer = new EvidenceBuffer(NewTempDir(), key, boundedBytes: 1_000_000);
        Array.Clear(key); // Caller mutates its array: the buffer holds a defensive copy.
        var plain = new byte[] { 3, 1, 4 };
        var id = buffer.AppendSegment(T0, T0.AddSeconds(1), plain);
        Assert.Equal(plain, buffer.GetSegment(id));
    }
}
