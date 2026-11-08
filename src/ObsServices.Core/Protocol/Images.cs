using System.Text.Json.Nodes;

namespace ObsServices.Core.Protocol;

/// <summary>
/// Camera frames (protocol v6). An agent that keeps a spool of captured frames declares
/// <see cref="Capability"/> in <c>hello</c>, announces every frame (<see cref="ImageCaptured"/>: identity,
/// metadata, a small preview, never pixels) and lists what it still holds (<see cref="ImageInventory"/>).
/// The service decides what moves where and when: it grants uploads (<see cref="ImageGrant"/>) and
/// releases frames it holds durably (<see cref="ImageRelease"/>). Back pressure is the service not
/// granting. The bytes travel over HTTP, out of band of the socket (<c>docs/IMAGES.md</c> in the service).
/// </summary>
public static class Images {
    /// <summary><c>hello</c> capability of an agent that spools frames and honours grants.</summary>
    public const string Capability = "images:spool";

    /// <summary>Largest body of one upload request in v6 (8 MiB); the grant repeats it.</summary>
    public const long MaxChunk = 8L << 20;

    /// <summary>Largest frame the service accepts (1 GiB).</summary>
    public const long MaxFrameBytes = 1L << 30;

    /// <summary>Largest announced header (its JSON form) the service keeps.</summary>
    public const int MaxHeaderBytes = 64 << 10;

    /// <summary>Largest announced preview (PNG bytes, before base64) the service keeps.</summary>
    public const int MaxPreviewBytes = 64 << 10;

    /// <summary>Longest side of an announced preview.</summary>
    public const int PreviewMaxSide = 512;

    /// <summary><c>true</c> for a well-formed pixel hash on the wire: 64 lowercase hex characters.</summary>
    public static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>What a frame is for. <c>other</c> covers anything the agent cannot classify.</summary>
public enum ImageKind {
    Bias,
    Dark,
    Flat,
    Light,
    Other,
}

public static class ImageKindExtensions {
    public static string AsString(this ImageKind kind) => kind switch {
        ImageKind.Bias => "bias",
        ImageKind.Dark => "dark",
        ImageKind.Flat => "flat",
        ImageKind.Light => "light",
        _ => "other",
    };

    /// <summary>N.I.N.A.'s and the common <c>IMAGETYP</c> spellings; anything unknown is <see cref="ImageKind.Other"/>.</summary>
    public static ImageKind ParseImageKind(string? text) => (text ?? string.Empty).Trim().ToUpperInvariant() switch {
        "BIAS" => ImageKind.Bias,
        "DARK" or "DARKFLAT" or "DARK FLAT" => ImageKind.Dark,
        "FLAT" => ImageKind.Flat,
        "LIGHT" or "OBJECT" => ImageKind.Light,
        _ => ImageKind.Other,
    };
}

/// <summary>Numbers the agent measured on the frame (all optional; the service recomputes what it needs).</summary>
public sealed class ImageStats {
    public double? Median { get; set; }

    /// <summary>Robust standard deviation: 1.4826 × the median absolute deviation.</summary>
    public double? Sigma { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    public double? Stars { get; set; }

    /// <summary>Half flux radius, pixels.</summary>
    public double? Hfr { get; set; }
}

/// <summary>A frame exists in the agent's spool: everything about it except the pixels.</summary>
public sealed class ImageCaptured : Up {
    internal override string Type => "image_captured";

    /// <summary>Pixel hash, 64 lowercase hex characters.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Size of the file in the spool (the encoding that will be uploaded).</summary>
    public long Bytes { get; set; }

    public ImageKind Kind { get; set; } = ImageKind.Other;

    /// <summary>Camera entity id.</summary>
    public string Camera { get; set; } = string.Empty;

    /// <summary>Mount entity id of the instrument.</summary>
    public string? Instrument { get; set; }

    public string? TaskId { get; set; }

    /// <summary>Start of the exposure, unix seconds.</summary>
    public double CapturedAt { get; set; }

    public double ExposureS { get; set; }

    /// <summary>Wheel entity id → filter name, one per wheel in the light path (may be empty).</summary>
    public Dictionary<string, string> Filters { get; set; } = new();

    /// <summary>Filter label as the agent names it (<c>Ha+ND3</c>).</summary>
    public string? Filter { get; set; }

    public double? Gain { get; set; }

    public double? Offset { get; set; }

    public int? Binning { get; set; }

    public double? TemperatureC { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Bitpix { get; set; }

    /// <summary>SHA-256 of the file's bytes (hex), when the agent computed it.</summary>
    public string? FileSha256 { get; set; }

    public string? FileName { get; set; }

    /// <summary>The FITS header as keyword → value (the service keeps at most <see cref="Images.MaxHeaderBytes"/>).</summary>
    public JsonObject? Header { get; set; }

    public ImageStats? Stats { get; set; }

    /// <summary>Base64 PNG, at most <see cref="Images.MaxPreviewBytes"/> decoded, longest side ≤ <see cref="Images.PreviewMaxSide"/>.</summary>
    public string? PreviewPng { get; set; }
}

/// <summary>One frame the spool holds.</summary>
public sealed class InventoryFrame {
    public string Hash { get; set; } = string.Empty;

    public long Bytes { get; set; }
}

/// <summary>What the spool holds right now; sent after <c>welcome</c>.</summary>
public sealed class ImageInventory : Up {
    internal override string Type => "image_inventory";

    public List<InventoryFrame> Frames { get; set; } = new();

    public long? FreeBytes { get; set; }
}

/// <summary>
/// Upload these frames. <see cref="UploadUrl"/> is the base the agent appends the hash to
/// (<c>{public_url}/agent/{site}/images/</c>); <see cref="Token"/> is the bearer for those requests.
/// </summary>
public sealed class ImageGrant : Down {
    public List<string> Hashes { get; set; } = new();

    public string UploadUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>Unix seconds.</summary>
    public double ExpiresAt { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Largest request body, bytes.</summary>
    public long MaxChunk { get; set; } = Images.MaxChunk;

    public long? RateBps { get; set; }
}

/// <summary>The service holds these frames durably; the spool may drop them.</summary>
public sealed class ImageRelease : Down {
    public List<string> Hashes { get; set; } = new();
}
