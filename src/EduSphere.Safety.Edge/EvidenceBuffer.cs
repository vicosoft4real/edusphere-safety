using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EduSphere.Safety.Edge;

/// <summary>
/// Manifest entry for one buffered evidence segment.
/// </summary>
public sealed record SegmentInfo(
    string SegmentId,
    DateTimeOffset StartEventTimeUtc,
    DateTimeOffset EndEventTimeUtc,
    string Sha256Hex,
    bool Sealed,
    long EncryptedBytes);

/// <summary>
/// File-backed encrypted rolling evidence buffer, rooted at a directory.
/// <para>
/// Layout: one AES-GCM envelope file per segment (<c>&lt;segId&gt;.mp4</c>,
/// containing <c>nonce(12) || ciphertext || tag(16)</c>) plus a single
/// <c>manifest.json</c> recording id, start/end event-time UTC, SHA256 of the
/// plaintext, and the sealed flag. The manifest is rewritten crash-safely
/// (write-temp + atomic rename); a segment file is fully written before the
/// manifest names it, so a crash can at worst orphan an unnamed file.
/// </para>
/// <para>
/// Lifecycle: <see cref="AppendSegment"/> creates an UNSEALED (active) segment;
/// <see cref="SealSegment"/> marks it sealed. Eviction deletes the oldest SEALED
/// segments first (by start event-time) while total envelope bytes exceed
/// <see cref="BoundedBytes"/>. The active (newest unsealed) segment is EXEMPT and
/// is never evicted, so the bound may be transiently exceeded until it is sealed.
/// Pinned segments (see <see cref="Pin"/>) are likewise skipped by eviction;
/// pins are in-memory only and do not survive restart.
/// </para>
/// <para>
/// EVICTION IS DELETION: an evicted segment's envelope file is removed from disk
/// and its manifest entry dropped. Evicted evidence is UNRECOVERABLE — size the
/// bound so the spool/upload path normally drains clips before eviction.
/// </para>
/// <para>
/// CORRUPT MANIFEST RECOVERY: when <c>manifest.json</c> cannot be parsed at open,
/// it is renamed to <c>manifest.corrupt-&lt;utc-ticks&gt;</c> and the buffer starts
/// empty (<see cref="ManifestWasCorrupt"/> is true). Segment envelope files stay on
/// disk: inspect the quarantined manifest, recover what is needed manually, then
/// delete the quarantine file. New segment ids are allocated past any orphaned
/// files, so recovery data is never clobbered.
/// </para>
/// <para>
/// The 32-byte key comes from host config via the constructor; its length is
/// enforced and the key is never written to disk, never logged, and never
/// exposed (no <c>ToString</c> carries it). Decryption with the wrong key fails
/// with <see cref="CryptographicException"/>; a SHA256 mismatch between the
/// envelope plaintext and the manifest also fails that way.
/// </para>
/// </summary>
public sealed class EvidenceBuffer
{
    /// <summary>Required AES-256 key length in bytes.</summary>
    public const int RequiredKeyBytes = 32;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly byte[] _key;
    private readonly string _manifestPath;
    private readonly List<SegmentInfo> _segments = new();
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _nextId;

    /// <summary>Creates (or reopens) a buffer rooted at <paramref name="rootDirectory"/>.</summary>
    /// <exception cref="ArgumentException">Key is not exactly 32 bytes, or bound is negative.</exception>
    public EvidenceBuffer(string rootDirectory, byte[] key, long boundedBytes)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory must not be empty.", nameof(rootDirectory));
        }

        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != RequiredKeyBytes)
        {
            // Deliberately no key material in the message.
            throw new ArgumentException(
                $"Evidence key must be exactly {RequiredKeyBytes} bytes.", nameof(key));
        }

        if (boundedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundedBytes), "Bound must be >= 0.");
        }

        _root = rootDirectory;
        _key = key.ToArray(); // Defensive copy; caller array is never retained.
        BoundedBytes = boundedBytes;
        _manifestPath = Path.Combine(_root, ManifestName);
        Directory.CreateDirectory(_root);
        LoadManifest();
    }

    /// <summary>Maximum total envelope bytes retained (sealed segments only are evictable).</summary>
    public long BoundedBytes { get; }

    /// <summary>
    /// True when the manifest was corrupt at open and was quarantined (see
    /// <c>manifest.corrupt-&lt;ticks&gt;</c> in the root). Segments on disk are preserved
    /// for manual recovery; the live index starts empty.
    /// </summary>
    public bool ManifestWasCorrupt { get; private set; }

    /// <summary>Current total envelope bytes on disk.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_gate)
            {
                return _segments.Sum(static s => s.EncryptedBytes);
            }
        }
    }

    /// <summary>Manifest snapshot, oldest start-time first.</summary>
    public IReadOnlyList<SegmentInfo> Segments
    {
        get
        {
            lock (_gate)
            {
                return _segments.OrderBy(static s => s.StartEventTimeUtc).ToArray();
            }
        }
    }

    /// <summary>
    /// Encrypts <paramref name="mp4Bytes"/> and stores it as a new UNSEALED segment.
    /// Returns the stable segment id (<c>seg-NNNNNNNN</c>, never reused).
    /// </summary>
    public string AppendSegment(DateTimeOffset startEventTimeUtc, DateTimeOffset endEventTimeUtc, byte[] mp4Bytes)
    {
        ArgumentNullException.ThrowIfNull(mp4Bytes);
        if (mp4Bytes.Length == 0)
        {
            throw new ArgumentException("Segment payload must not be empty.", nameof(mp4Bytes));
        }

        if (endEventTimeUtc < startEventTimeUtc)
        {
            throw new ArgumentException("Segment end must not precede start.", nameof(endEventTimeUtc));
        }

        lock (_gate)
        {
            var segId = $"seg-{_nextId:D8}";
            _nextId++;
            var envelope = Encrypt(mp4Bytes);
            File.WriteAllBytes(SegmentPath(segId), envelope);
            _segments.Add(new SegmentInfo(
                segId, startEventTimeUtc, endEventTimeUtc,
                Convert.ToHexString(SHA256.HashData(mp4Bytes)).ToLowerInvariant(),
                Sealed: false, envelope.Length));
            SaveManifestLocked();
            EvictLocked();
            return segId;
        }
    }

    /// <summary>Marks a segment sealed, making it eligible for eviction. Sealing may evict.</summary>
    /// <exception cref="KeyNotFoundException">Unknown segment id.</exception>
    public void SealSegment(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        lock (_gate)
        {
            var index = _segments.FindIndex(s => s.SegmentId == segmentId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Unknown segment: {segmentId}.");
            }

            if (!_segments[index].Sealed)
            {
                _segments[index] = _segments[index] with { Sealed = true };
                SaveManifestLocked();
            }

            EvictLocked();
        }
    }

    /// <summary>
    /// Decrypts and returns the segment plaintext, verifying its SHA256.
    /// Task 5 translates an unknown id to a spool-drop plus an ops note (no infinite retry).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown segment id.</exception>
    /// <exception cref="FileNotFoundException">Manifest names a segment whose file is missing.</exception>
    /// <exception cref="CryptographicException">Wrong key, tampered envelope, or SHA256 mismatch.</exception>
    public byte[] GetSegment(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        SegmentInfo record;
        lock (_gate)
        {
            record = _segments.FirstOrDefault(s => s.SegmentId == segmentId)
                ?? throw new KeyNotFoundException($"Unknown segment: {segmentId}.");
        }

        var path = SegmentPath(segmentId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Segment file missing for {segmentId}.", path);
        }

        var plain = Decrypt(File.ReadAllBytes(path));
        var actual = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(record.Sha256Hex)))
        {
            throw new CryptographicException("Segment integrity check failed (SHA256 mismatch).");
        }

        return plain;
    }

    private string SegmentPath(string segId) => Path.Combine(_root, segId + ".mp4");

    private byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var gcm = new AesGcm(_key, TagBytes);
        gcm.Encrypt(nonce, plain, cipher, tag);
        var envelope = new byte[NonceBytes + cipher.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, envelope, 0, NonceBytes);
        Buffer.BlockCopy(cipher, 0, envelope, NonceBytes, cipher.Length);
        Buffer.BlockCopy(tag, 0, envelope, NonceBytes + cipher.Length, TagBytes);
        return envelope;
    }

    private byte[] Decrypt(byte[] envelope)
    {
        if (envelope.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Segment envelope is truncated.");
        }

        var nonce = envelope.AsSpan(0, NonceBytes).ToArray();
        var cipher = envelope.AsSpan(NonceBytes, envelope.Length - NonceBytes - TagBytes).ToArray();
        var tag = envelope.AsSpan(envelope.Length - TagBytes, TagBytes).ToArray();
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(_key, TagBytes);
        gcm.Decrypt(nonce, cipher, tag, plain); // Wrong key/tag -> CryptographicException.
        return plain;
    }

    /// <summary>
    /// Pins a segment against eviction (Task 5 wiring contract: spool enqueue pins,
    /// successful upload unpins). Pinning an already-pinned id is a no-op.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown segment id.</exception>
    public void Pin(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        lock (_gate)
        {
            if (!_segments.Any(s => s.SegmentId == segmentId))
            {
                throw new KeyNotFoundException($"Unknown segment: {segmentId}.");
            }

            _pinned.Add(segmentId);
        }
    }

    /// <summary>Releases a pin; unknown or repeated ids are no-ops, never throw.</summary>
    public void Unpin(string segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
        {
            return;
        }

        lock (_gate)
        {
            _pinned.Remove(segmentId);
        }
    }

    /// <summary>True when the segment is currently pinned against eviction.</summary>
    public bool IsPinned(string segmentId)
    {
        lock (_gate)
        {
            return _pinned.Contains(segmentId);
        }
    }

    private void EvictLocked()
    {
        while (_segments.Sum(static s => s.EncryptedBytes) > BoundedBytes)
        {
            var victim = _segments
                .Where(s => s.Sealed && !_pinned.Contains(s.SegmentId))
                .OrderBy(static s => s.StartEventTimeUtc)
                .FirstOrDefault();
            if (victim is null)
            {
                break; // Only exempt (active or pinned) segments remain; bound stays exceeded.
            }

            File.Delete(SegmentPath(victim.SegmentId)); // Missing file: no-op, drop the entry anyway.
            _segments.Remove(victim);
            SaveManifestLocked();
        }
    }

    private void LoadManifest()
    {
        if (!File.Exists(_manifestPath))
        {
            _nextId = 1;
            SaveManifestLocked();
            return;
        }

        ManifestDoc? doc = null;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestDoc>(File.ReadAllText(_manifestPath), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            QuarantineManifestLocked();
        }

        if (doc is null || doc.Segments is null)
        {
            // Unparsable or schema-less manifest: quarantine once (if not already) and start empty.
            if (!ManifestWasCorrupt)
            {
                QuarantineManifestLocked();
            }

            _nextId = NextIdPastOrphanedFiles();
            _segments.Clear();
            SaveManifestLocked();
            return;
        }

        _nextId = doc.NextId < 1 ? 1 : doc.NextId;
        _segments.Clear();
        _segments.AddRange(doc.Segments);
    }

    /// <summary>
    /// Moves the unreadable manifest aside for inspection and flags the buffer.
    /// Segment envelope files are left untouched for manual recovery.
    /// </summary>
    private void QuarantineManifestLocked()
    {
        var quarantine = Path.Combine(_root, $"manifest.corrupt-{DateTimeOffset.UtcNow.Ticks}");
        File.Move(_manifestPath, quarantine, overwrite: false);
        ManifestWasCorrupt = true;
    }

    private long NextIdPastOrphanedFiles()
    {
        var max = 0L;
        foreach (var file in Directory.EnumerateFiles(_root, "seg-*.mp4"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("seg-", StringComparison.Ordinal) &&
                long.TryParse(name.AsSpan(4), out var n) && n > max)
            {
                max = n;
            }
        }

        return Math.Max(1, max + 1);
    }

    private void SaveManifestLocked()
    {
        var tmp = Path.Combine(_root, Path.GetRandomFileName());
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new ManifestDoc { NextId = _nextId, Segments = _segments }, Json));
        File.Move(tmp, _manifestPath, overwrite: true); // Atomic rename on one filesystem.
    }

    private sealed class ManifestDoc
    {
        [JsonPropertyName("nextId")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("segments")]
        public List<SegmentInfo> Segments { get; set; } = new();
    }
}

/// <summary>
/// One queued cloud upload: event metadata plus a clip reference, a stable edge
/// event id for cloud idempotency, and the attempt count.
/// </summary>
public sealed record PendingUpload(
    string EdgeEventId,
    string RuleId,
    int RuleVersion,
    long TrackId,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset DetectionTimeUtc,
    string? SegmentId,
    int AttemptCount);

/// <summary>
/// File-backed offline upload spool (<c>spool.jsonl</c>, one JSON object per line).
/// <para>
/// The constructor replays the file, so pending uploads survive an edge restart.
/// <see cref="Enqueue"/> appends one line; <see cref="NoteAttempt"/> and
/// <see cref="Remove"/> rewrite the whole file via write-temp + atomic rename, so
/// a crash during drain never leaves a half-written spool. The uploader drains
/// oldest-first (<see cref="PeekOldest"/>).
/// </para>
/// <para>
/// DELAYED LABELLING (pure, no wall-clock in this library): the uploader stamps
/// the event's ingest time with the transmit time it passes in, and an event is
/// delayed iff <c>transmitTimeUtc - eventTimeUtc &gt; threshold</c> (strictly
/// greater; exactly-at-threshold is on time). Default threshold is 60 s
/// (<see cref="DefaultDelayThreshold"/>), overridable via the constructor. The
/// attempt count correlates with delay but does NOT define it.
/// </para>
/// </summary>
public sealed class UploadSpool
{
    /// <summary>Default delayed-labelling threshold: 60 seconds.</summary>
    public static readonly TimeSpan DefaultDelayThreshold = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly List<PendingUpload> _items = new();
    private readonly object _gate = new();

    /// <summary>Opens (or creates) the spool at <paramref name="spoolFilePath"/> and replays it.</summary>
    public UploadSpool(string spoolFilePath, TimeSpan? delayThreshold = null)
    {
        if (string.IsNullOrWhiteSpace(spoolFilePath))
        {
            throw new ArgumentException("Spool path must not be empty.", nameof(spoolFilePath));
        }

        var threshold = delayThreshold ?? DefaultDelayThreshold;
        if (threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delayThreshold), "Threshold must be >= 0.");
        }

        _path = spoolFilePath;
        DelayThreshold = threshold;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Load();
    }

    /// <summary>Delayed-labelling threshold for <see cref="IsDelayedAt"/>.</summary>
    public TimeSpan DelayThreshold { get; }

    /// <summary>Queued upload count.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Oldest-first snapshot (for tests/diagnostics).</summary>
    public IReadOnlyList<PendingUpload> All
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    /// <summary>Number of corrupt lines skipped at load (torn writes never brick restart).</summary>
    public int SkippedCorruptLines { get; private set; }

    /// <summary>Appends one pending upload (stable edge id required for idempotency).</summary>
    /// <exception cref="InvalidOperationException">An entry with the same edge event id is queued.</exception>
    public void Enqueue(PendingUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (string.IsNullOrWhiteSpace(upload.EdgeEventId))
        {
            throw new ArgumentException("Stable edge event id required.", nameof(upload));
        }

        lock (_gate)
        {
            if (_items.Any(u => u.EdgeEventId == upload.EdgeEventId))
            {
                throw new InvalidOperationException($"Duplicate edge event id: {upload.EdgeEventId}.");
            }

            _items.Add(upload);
            File.AppendAllText(_path, JsonSerializer.Serialize(upload, Json) + "\n");
        }
    }

    /// <summary>Oldest queued upload, or null when empty.</summary>
    public PendingUpload? PeekOldest()
    {
        lock (_gate)
        {
            return _items.FirstOrDefault();
        }
    }

    /// <summary>
    /// Increments the attempt count of one entry and persists it.
    /// Unknown ids are no-ops (the entry may already have drained).
    /// </summary>
    public void NoteAttempt(string edgeEventId)
    {
        if (string.IsNullOrWhiteSpace(edgeEventId))
        {
            return;
        }

        lock (_gate)
        {
            var index = _items.FindIndex(u => u.EdgeEventId == edgeEventId);
            if (index < 0)
            {
                return;
            }

            _items[index] = _items[index] with { AttemptCount = _items[index].AttemptCount + 1 };
            RewriteLocked();
        }
    }

    /// <summary>Removes one entry (successful upload); returns false when absent.</summary>
    public bool Remove(string edgeEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeEventId);
        lock (_gate)
        {
            var index = _items.FindIndex(u => u.EdgeEventId == edgeEventId);
            if (index < 0)
            {
                return false;
            }

            _items.RemoveAt(index);
            RewriteLocked();
            return true;
        }
    }

    /// <summary>
    /// Pure delayed check: <c>transmitTimeUtc - eventTimeUtc &gt; threshold</c>.
    /// The caller passes the transmit time explicitly, so this is fully testable.
    /// </summary>
    public static bool IsDelayed(
        DateTimeOffset eventTimeUtc, DateTimeOffset transmitTimeUtc, TimeSpan threshold) =>
        (transmitTimeUtc - eventTimeUtc) > threshold;

    /// <summary>Delayed check against this spool's <see cref="DelayThreshold"/>.</summary>
    public bool IsDelayedAt(DateTimeOffset eventTimeUtc, DateTimeOffset transmitTimeUtc) =>
        IsDelayed(eventTimeUtc, transmitTimeUtc, DelayThreshold);

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue; // Blank lines are ignored, not counted.
            }

            PendingUpload? item = null;
            try
            {
                item = JsonSerializer.Deserialize<PendingUpload>(line, Json);
            }
            catch (JsonException)
            {
                item = null;
            }

            // A torn final line (power cut mid-append) or a null entry must never
            // brick the restart: skip it, count it, keep the valid queue.
            if (item is null || string.IsNullOrWhiteSpace(item.EdgeEventId))
            {
                SkippedCorruptLines++;
                continue;
            }

            if (_items.Any(u => u.EdgeEventId == item.EdgeEventId))
            {
                SkippedCorruptLines++;
                continue;
            }

            _items.Add(item);
        }
    }

    private void RewriteLocked()
    {
        var dir = Path.GetDirectoryName(_path) ?? ".";
        var tmp = Path.Combine(dir, Path.GetRandomFileName());
        using (var writer = new StreamWriter(tmp))
        {
            foreach (var item in _items)
            {
                writer.WriteLine(JsonSerializer.Serialize(item, Json));
            }
        }

        File.Move(tmp, _path, overwrite: true); // Atomic rename on one filesystem.
    }
}
