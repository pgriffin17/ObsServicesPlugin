using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Imaging;

/// <summary>
/// What the host knows about a frame it just took. Everything the service wants beyond the pixels; the
/// generic agent fills in the entity ids (camera, instrument, task, wheel) it is bound to when the host
/// leaves them null.
/// </summary>
public sealed class FrameInfo {
    public ImageKind Kind { get; set; } = ImageKind.Other;

    /// <summary>Camera entity id. The generic agent supplies its bound camera when null.</summary>
    public string? Camera { get; set; }

    /// <summary>Mount entity id of the instrument.</summary>
    public string? Instrument { get; set; }

    /// <summary>The task this frame belongs to, when known.</summary>
    public string? TaskId { get; set; }

    /// <summary>Start of the exposure, unix seconds.</summary>
    public double CapturedAt { get; set; }

    public double ExposureS { get; set; }

    /// <summary>Wheel entity id → filter name. The generic agent adds its bound wheel with <see cref="Filter"/> when empty.</summary>
    public Dictionary<string, string> Filters { get; set; } = new();

    /// <summary>Filter label as the host names it.</summary>
    public string? Filter { get; set; }

    public double? Gain { get; set; }

    public double? Offset { get; set; }

    public int? Binning { get; set; }

    public double? TemperatureC { get; set; }

    /// <summary>The host's own file name, for display.</summary>
    public string? FileName { get; set; }

    /// <summary>Numbers the host already measured (N.I.N.A.'s statistics and star detection); computed from the pixels when null and <see cref="IncludeStats"/>.</summary>
    public ImageStats? Stats { get; set; }

    public bool IncludeStats { get; set; } = true;

    public bool IncludePreview { get; set; } = true;
}

/// <summary>Builds the <c>image_captured</c> announcement for a frame: identity, header, statistics and preview.</summary>
public static class FrameDescriber {
    /// <summary>Describe a FITS file by decoding it fully. Any <c>BITPIX</c> the service accepts.</summary>
    public static ImageCaptured Describe(byte[] fits, FrameInfo info) {
        var image = Fits.Parse(fits);
        var message = Common(fits, image.Header, image.Width, image.Height, image.Bitpix, info);
        message.Hash = Fits.HashOfPixels(image.Pixels);

        if (info.IncludeStats) {
            message.Stats = info.Stats ?? Fits.Stats(image);
        }

        if (info.IncludePreview) {
            message.PreviewPng = Base64(Fits.PreviewWithinLimit(image));
        }

        return message;
    }

    /// <summary>
    /// Describe an unsigned 16-bit frame straight from memory, with <paramref name="fits"/> the file that will
    /// be uploaded (<c>BITPIX 16, BZERO 32768</c>, as N.I.N.A.'s FITS writer and <see cref="Fits.Write"/> produce
    /// it). Hash, statistics and preview come from the pixels without decoding the file again; only its
    /// header is read. Falls back to <see cref="Describe(byte[], FrameInfo)"/> when the file is encoded differently.
    /// </summary>
    public static ImageCaptured Describe(ReadOnlyMemory<ushort> pixels, int width, int height, byte[] fits, FrameInfo info) {
        var (header, _) = Fits.ParseHeader(fits);

        if (!IsPlainUInt16(header, width, height) || pixels.Length < (long)width * height) {
            return Describe(fits, info);
        }

        var span = pixels.Span[..(width * height)];
        var message = Common(fits, header, width, height, 16, info);
        message.Hash = Fits.HashOfUInt16(span);

        if (info.IncludeStats) {
            message.Stats = info.Stats ?? Fits.StatsOfUInt16(span);
        }

        if (info.IncludePreview) {
            message.PreviewPng = Base64(Fits.PreviewWithinLimit<ushort>(span, width, height));
        }

        return message;
    }

    private static bool IsPlainUInt16(JsonObject header, int width, int height) {
        static double? Num(JsonNode? n) => n is JsonValue v ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : null : null;
        var naxis = Num(header["NAXIS"]);
        return Num(header["BITPIX"]) == 16
            && (naxis == 2 || (naxis == 3 && Num(header["NAXIS3"]) == 1))
            && Num(header["NAXIS1"]) == width
            && Num(header["NAXIS2"]) == height
            && (Num(header["BZERO"]) ?? 0) == 32768
            && (Num(header["BSCALE"]) ?? 1) == 1;
    }

    private static ImageCaptured Common(byte[] fits, JsonObject header, int width, int height, int bitpix, FrameInfo info) => new() {
        Bytes = fits.LongLength,
        Kind = info.Kind,
        Camera = info.Camera ?? string.Empty,
        Instrument = info.Instrument,
        TaskId = info.TaskId,
        CapturedAt = info.CapturedAt,
        ExposureS = info.ExposureS,
        Filters = new Dictionary<string, string>(info.Filters),
        Filter = info.Filter,
        Gain = info.Gain,
        Offset = info.Offset,
        Binning = info.Binning,
        TemperatureC = info.TemperatureC,
        Width = width,
        Height = height,
        Bitpix = bitpix,
        FileSha256 = Fits.Hex(SHA256.HashData(fits)),
        FileName = info.FileName,
        Header = Trimmed(header),
    };

    /// <summary>The header within the service's cap: trailing keywords go first when it is too long.</summary>
    private static JsonObject Trimmed(JsonObject header) {
        var copy = (JsonObject)header.DeepClone();

        while (copy.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(copy).Length > Images.MaxHeaderBytes) {
            copy.Remove(copy.Last().Key);
        }

        return copy;
    }

    private static string? Base64(byte[]? png) => png == null ? null : Convert.ToBase64String(png);
}
