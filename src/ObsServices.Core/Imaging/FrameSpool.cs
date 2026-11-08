using System.Text.Json;
using System.Text.Json.Serialization;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Imaging;

/// <summary>One frame the spool holds: where its bytes are and how far the service has got with it.</summary>
public sealed class SpoolEntry {
    public string Hash { get; set; } = string.Empty;

    public long Bytes { get; set; }

    /// <summary>Absolute path of the FITS file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The spool wrote this file (a copy of the host's); it may delete it on release. A referenced file is never deleted.</summary>
    public bool Owned { get; set; }

    public double CapturedAt { get; set; }

    public double AnnouncedAt { get; set; }

    public ImageKind Kind { get; set; } = ImageKind.Other;

    public string Camera { get; set; } = string.Empty;

    public string? TaskId { get; set; }

    /// <summary>The service answered the last chunk with <c>received</c>.</summary>
    public bool Uploaded { get; set; }

    public double? UploadedAt { get; set; }

    public string? FileName { get; set; }
}

/// <summary>
/// The frames an agent holds for the service, on disk: a directory of FITS files and an index that survives
/// restarts, so the inventory sent after a reconnect is honest. Frames leave when the service releases them
/// (the file is deleted when the spool owns it and <see cref="DeleteOnRelease"/> is on) or, over the cap,
/// when they have been uploaded and are only waiting for the release.
/// </summary>
public sealed class FrameSpool {
    private const string IndexName = "spool.json";

    private static readonly JsonSerializerOptions IndexJson = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly object gate = new();
    private readonly Dictionary<string, SpoolEntry> entries = new();

    public FrameSpool(string directory) {
        Directory = System.IO.Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
        IndexPath = System.IO.Path.Combine(Directory, IndexName);
        Load();
    }

    public string Directory { get; }

    public string IndexPath { get; }

    /// <summary>Cap on the bytes of the files the spool owns; uploaded frames are evicted oldest first when over it. Null = no cap.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Delete an owned file once the service holds the frame durably.</summary>
    public bool DeleteOnRelease { get; set; } = true;

    /// <summary>Raised after every change, for status displays.</summary>
    public event Action? Changed;

    public IReadOnlyList<SpoolEntry> Entries {
        get { lock (gate) return entries.Values.OrderBy(e => e.AnnouncedAt).ToList(); }
    }

    /// <summary>Announced, not yet received by the service.</summary>
    public IReadOnlyList<SpoolEntry> Pending {
        get { lock (gate) return entries.Values.Where(e => !e.Uploaded).OrderBy(e => e.AnnouncedAt).ToList(); }
    }

    public int Count {
        get { lock (gate) return entries.Count; }
    }

    public long QueuedBytes {
        get { lock (gate) return entries.Values.Where(e => !e.Uploaded).Sum(e => e.Bytes); }
    }

    public long OwnedBytes {
        get { lock (gate) return entries.Values.Where(e => e.Owned).Sum(e => e.Bytes); }
    }

    public SpoolEntry? Get(string hash) {
        lock (gate) return entries.TryGetValue(hash, out var e) ? e : null;
    }

    /// <summary>Keep a copy of <paramref name="fits"/> under the spool directory, named by its hash.</summary>
    public SpoolEntry Add(ImageCaptured described, byte[] fits) {
        var path = System.IO.Path.Combine(Directory, described.Hash + ".fits");

        lock (gate) {
            if (entries.TryGetValue(described.Hash, out var existing)) {
                return existing;
            }

            if (!File.Exists(path) || new FileInfo(path).Length != fits.LongLength) {
                var tmp = path + ".part";
                File.WriteAllBytes(tmp, fits);
                File.Move(tmp, path, overwrite: true);
            }

            return Insert(described, path, owned: true);
        }
    }

    /// <summary>Reference a file the host keeps itself (a plain FITS it wrote anyway). Never deleted by the spool.</summary>
    public SpoolEntry AddReference(ImageCaptured described, string path) {
        lock (gate) {
            if (entries.TryGetValue(described.Hash, out var existing)) {
                return existing;
            }

            return Insert(described, System.IO.Path.GetFullPath(path), owned: false);
        }
    }

    private SpoolEntry Insert(ImageCaptured described, string path, bool owned) {
        var entry = new SpoolEntry {
            Hash = described.Hash,
            Bytes = described.Bytes,
            Path = path,
            Owned = owned,
            CapturedAt = described.CapturedAt,
            AnnouncedAt = Timestamp.Now(),
            Kind = described.Kind,
            Camera = described.Camera,
            TaskId = described.TaskId,
            FileName = described.FileName,
        };
        entries[entry.Hash] = entry;
        Save();
        Trim();
        return entry;
    }

    public void MarkUploaded(string hash) {
        lock (gate) {
            if (entries.TryGetValue(hash, out var e) && !e.Uploaded) {
                e.Uploaded = true;
                e.UploadedAt = Timestamp.Now();
                Save();
                Trim();
            }
        }
    }

    /// <summary>The service holds the frame: forget it, deleting the file when the spool owns it and may.</summary>
    public SpoolEntry? Release(string hash) {
        lock (gate) {
            if (!entries.Remove(hash, out var e)) {
                return null;
            }

            if (e.Owned && DeleteOnRelease) {
                TryDelete(e.Path);
            }

            Save();
            return e;
        }
    }

    /// <summary>Over <see cref="MaxBytes"/>: drop owned files the service already received, oldest first. Unreceived frames are never dropped.</summary>
    public IReadOnlyList<SpoolEntry> Trim() {
        lock (gate) {
            var evicted = new List<SpoolEntry>();

            if (MaxBytes is not { } cap) {
                return evicted;
            }

            var owned = entries.Values.Where(e => e.Owned).Sum(e => e.Bytes);

            foreach (var e in entries.Values.Where(e => e.Owned && e.Uploaded).OrderBy(e => e.UploadedAt ?? e.AnnouncedAt).ToList()) {
                if (owned <= cap) {
                    break;
                }

                entries.Remove(e.Hash);
                TryDelete(e.Path);
                owned -= e.Bytes;
                evicted.Add(e);
            }

            if (evicted.Count > 0) {
                Save();
            }

            return evicted;
        }
    }

    /// <summary>Free space on the spool's disk, or null when it cannot be read.</summary>
    public long? FreeBytes() {
        try {
            return new DriveInfo(Directory).AvailableFreeSpace;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>The spooled bytes.</summary>
    public Stream Open(SpoolEntry entry) => new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void Load() {
        if (!File.Exists(IndexPath)) {
            return;
        }

        List<SpoolEntry>? saved;

        try {
            saved = JsonSerializer.Deserialize<List<SpoolEntry>>(File.ReadAllText(IndexPath), IndexJson);
        } catch (Exception) {
            saved = null;
        }

        foreach (var e in saved ?? new List<SpoolEntry>()) {
            // a file that went missing while we were away is not ours to announce
            if (Images.IsHash(e.Hash) && File.Exists(e.Path)) {
                entries[e.Hash] = e;
            }
        }
    }

    private void Save() {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries.Values.OrderBy(e => e.AnnouncedAt).ToList(), IndexJson));
        File.Move(tmp, IndexPath, overwrite: true);
        Changed?.Invoke();
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
