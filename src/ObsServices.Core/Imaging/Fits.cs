using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Imaging;

/// <summary>The bytes are not a 2-D FITS image this library decodes.</summary>
public sealed class FitsException : Exception {
    public FitsException(string message) : base(message) {
    }
}

/// <summary>A decoded primary HDU: the header as JSON-ready values and the pixels as the service hashes them.</summary>
public sealed class FitsImage {
    public FitsImage(JsonObject header, int width, int height, int bitpix, double bzero, double bscale, float[] pixels) {
        Header = header;
        Width = width;
        Height = height;
        Bitpix = bitpix;
        Bzero = bzero;
        Bscale = bscale;
        Pixels = pixels;
    }

    /// <summary>Keyword → value; commentary keywords are arrays of text, a keyword without a value is null.</summary>
    public JsonObject Header { get; }

    public int Width { get; }

    public int Height { get; }

    public int Bitpix { get; }

    public double Bzero { get; }

    public double Bscale { get; }

    /// <summary>Row-major (NAXIS1 fastest), <c>BZERO + BSCALE × raw</c> rounded to single once.</summary>
    public float[] Pixels { get; }

    public float At(int x, int y) => Pixels[y * Width + x];
}

/// <summary>One header card to write: a keyword, its value (bool, long/int, double, string, or null for a blank card) and a comment.</summary>
public readonly record struct FitsCard(string Key, object? Value, string? Comment = null);

/// <summary>
/// FITS primary HDUs the way the service identifies frames (<c>docs/IMAGES.md</c>): a header parser, the
/// pixel decoder, the <b>pixel hash</b> (SHA-256 over every pixel as little-endian <c>f32</c> after
/// <c>BZERO</c>/<c>BSCALE</c>, row-major; the header is never hashed), a writer for 16-bit camera frames,
/// statistics and a preview. The test vectors in the service's <c>tests/fixtures/pixel_hash/</c> pin the
/// hash down; <c>PixelHashTests</c> runs them.
/// </summary>
public static class Fits {
    public const int Block = 2880;
    public const int Card = 80;

    private static readonly string[] Commentary = { string.Empty, "COMMENT", "HISTORY", "CONTINUE" };

    // ---- header ---------------------------------------------------------------------------------------

    /// <summary>Keyword → value (commentary keywords → arrays of text; the first of a repeated keyword wins) and the data offset.</summary>
    public static (JsonObject Header, int DataOffset) ParseHeader(ReadOnlySpan<byte> data) {
        if (data.Length < Card || !data.StartsWith("SIMPLE  ="u8)) {
            throw new FitsException("not a FITS file");
        }

        var header = new JsonObject();
        var pos = 0;

        while (true) {
            if (data.Length - pos < Card) {
                throw new FitsException("truncated FITS header");
            }

            var raw = data.Slice(pos, Card);
            pos += Card;

            if (raw.StartsWith("END"u8) && raw[3..].TrimEnd((byte)' ').IsEmpty) {
                break;
            }

            foreach (var b in raw) {
                if (b > 127) {
                    throw new FitsException("non-ASCII header card");
                }
            }

            var text = Encoding.ASCII.GetString(raw);
            var key = text[..8].TrimEnd();

            if (Array.IndexOf(Commentary, key) >= 0 || text.Substring(8, 2) != "= ") {
                if (key.Length > 0) {
                    if (header[key] is not JsonArray list) {
                        list = new JsonArray();
                        header[key] = list;
                    }

                    list.Add(JsonValue.Create(text[8..].Trim()));
                }

                continue;
            }

            var value = ParseValue(text[10..], key);

            if (!header.ContainsKey(key)) {
                header[key] = value;
            }
        }

        if (header["SIMPLE"] is JsonValue simple && simple.TryGetValue<bool>(out var ok) && !ok) {
            throw new FitsException("SIMPLE = F");
        }

        return (header, (pos + Block - 1) / Block * Block);
    }

    /// <summary>The value of a <c>KEY = value / comment</c> card.</summary>
    private static JsonNode? ParseValue(string field, string key) {
        var t = field.TrimStart();

        if (t.StartsWith('\'')) {
            var sb = new StringBuilder();
            var i = 1;

            while (true) {
                if (i >= t.Length) {
                    throw new FitsException($"unterminated string in card {key}");
                }

                var c = t[i];

                if (c == '\'') {
                    if (i + 1 < t.Length && t[i + 1] == '\'') {
                        sb.Append('\'');
                        i += 2;
                        continue;
                    }

                    return JsonValue.Create(sb.ToString().TrimEnd());
                }

                sb.Append(c);
                i++;
            }
        }

        var slash = t.IndexOf('/');
        var token = (slash >= 0 ? t[..slash] : t).Trim();

        if (token.Length == 0) {
            return null;
        }

        if (token == "T") {
            return JsonValue.Create(true);
        }

        if (token == "F") {
            return JsonValue.Create(false);
        }

        if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) {
            return JsonValue.Create(l);
        }

        if (double.TryParse(token.Replace('D', 'E').Replace('d', 'E'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) {
            return JsonValue.Create(d);
        }

        throw new FitsException($"bad card: {key} = {token}");
    }

    private static long IntKey(JsonObject header, string key) {
        if (header[key] is JsonValue v) {
            if (v.TryGetValue<long>(out var l)) {
                return l;
            }

            if (v.TryGetValue<double>(out var d) && d == Math.Floor(d)) {
                return (long)d;
            }
        }

        throw new FitsException($"{key} missing or not an integer");
    }

    private static double? NumberKey(JsonObject header, string key) {
        if (header[key] is JsonValue v) {
            if (v.TryGetValue<double>(out var d)) {
                return d;
            }

            if (v.TryGetValue<long>(out var l)) {
                return l;
            }
        }

        return null;
    }

    // ---- pixels ---------------------------------------------------------------------------------------

    /// <summary>
    /// Decode a primary HDU. Refuses anything that is not a 2-D image (<c>NAXIS 3</c> with <c>NAXIS3 = 1</c>
    /// passes), more than <paramref name="maxPixels"/> pixels, or data shorter than the header says.
    /// </summary>
    public static FitsImage Parse(ReadOnlySpan<byte> data, long maxPixels = 200_000_000) {
        var (header, offset) = ParseHeader(data);
        var bitpix = (int)IntKey(header, "BITPIX");

        if (bitpix is not (8 or 16 or 32 or 64 or -32 or -64)) {
            throw new FitsException($"unsupported BITPIX {bitpix}");
        }

        var naxis = IntKey(header, "NAXIS");

        if (!(naxis == 2 || (naxis == 3 && IntKey(header, "NAXIS3") == 1))) {
            throw new FitsException($"unsupported NAXIS {naxis}");
        }

        var width = IntKey(header, "NAXIS1");
        var height = IntKey(header, "NAXIS2");

        if (width < 1 || height < 1) {
            throw new FitsException($"empty image {width}x{height}");
        }

        var count = width * height;

        if (count > maxPixels) {
            throw new FitsException($"image too large: {count} pixels, at most {maxPixels}");
        }

        var size = Math.Abs(bitpix) / 8;

        if ((long)data.Length - offset < count * size) {
            throw new FitsException("truncated FITS data");
        }

        var bzero = NumberKey(header, "BZERO") ?? 0.0;
        var bscale = NumberKey(header, "BSCALE") ?? 1.0;
        var buf = data.Slice(offset, (int)(count * size));
        var pixels = new float[count];

        // Always through the arithmetic, even when it is the identity: it turns -0.0 into +0.0, and the
        // hash is defined over its result. The cast rounds each double to the nearest single once and
        // takes out-of-range values to ±inf, as the service's Rust `as f32` does.
        switch (bitpix) {
            case 8:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * buf[i]);
                }

                break;
            case 16:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * BinaryPrimitives.ReadInt16BigEndian(buf.Slice(i * 2, 2)));
                }

                break;
            case 32:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * BinaryPrimitives.ReadInt32BigEndian(buf.Slice(i * 4, 4)));
                }

                break;
            case 64:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * (double)BinaryPrimitives.ReadInt64BigEndian(buf.Slice(i * 8, 8)));
                }

                break;
            case -32:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * (double)BinaryPrimitives.ReadSingleBigEndian(buf.Slice(i * 4, 4)));
                }

                break;
            default:
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (float)(bzero + bscale * BinaryPrimitives.ReadDoubleBigEndian(buf.Slice(i * 8, 8)));
                }

                break;
        }

        return new FitsImage(header, (int)width, (int)height, bitpix, bzero, bscale, pixels);
    }

    // ---- the pixel hash -------------------------------------------------------------------------------

    /// <summary>The pixel hash (64 lowercase hex characters) of a FITS file's bytes.</summary>
    public static string PixelHash(ReadOnlySpan<byte> fits) => HashOfPixels(Parse(fits).Pixels);

    /// <summary>The pixel hash of decoded pixels.</summary>
    public static string HashOfPixels(ReadOnlySpan<float> pixels) {
        if (BitConverter.IsLittleEndian) {
            return Hex(SHA256.HashData(MemoryMarshal.AsBytes(pixels)));
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> le = stackalloc byte[4];

        foreach (var p in pixels) {
            BinaryPrimitives.WriteSingleLittleEndian(le, p);
            sha.AppendData(le);
        }

        return Hex(sha.GetHashAndReset());
    }

    /// <summary>
    /// The pixel hash of an unsigned 16-bit camera frame straight from memory, without a FITS round trip.
    /// Equal to <see cref="PixelHash"/> of the same pixels written as <c>BITPIX 16, BZERO 32768, BSCALE 1</c>
    /// (what <see cref="Write"/> and N.I.N.A.'s FITS writer produce): every value is exact in single precision.
    /// </summary>
    public static string HashOfUInt16(ReadOnlySpan<ushort> pixels) {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new float[16384];

        for (var start = 0; start < pixels.Length; start += block.Length) {
            var n = Math.Min(block.Length, pixels.Length - start);

            for (var i = 0; i < n; i++) {
                block[i] = pixels[start + i];
            }

            var bytes = MemoryMarshal.AsBytes(block.AsSpan(0, n));

            if (BitConverter.IsLittleEndian) {
                sha.AppendData(bytes);
            } else {
                var le = new byte[bytes.Length];

                for (var i = 0; i < n; i++) {
                    BinaryPrimitives.WriteSingleLittleEndian(le.AsSpan(i * 4, 4), block[i]);
                }

                sha.AppendData(le);
            }
        }

        return Hex(sha.GetHashAndReset());
    }

    /// <summary>The 44-character padded base64 form Fits_Utils writes in headers (<c>BIASHASH</c> and friends).</summary>
    public static string HashToBase64(string text) => Convert.ToBase64String(Convert.FromHexString(HashFromText(text)));

    /// <summary>Either text form (64 hex characters in any case, or 44 of padded base64) → canonical lowercase hex.</summary>
    public static string HashFromText(string text) {
        var t = text.Trim();

        if (t.Length == 64) {
            try {
                return Hex(Convert.FromHexString(t));
            } catch (FormatException) {
            }
        } else if (t.Length == 44) {
            try {
                var raw = Convert.FromBase64String(t);

                if (raw.Length == 32) {
                    return Hex(raw);
                }
            } catch (FormatException) {
            }
        }

        throw new ArgumentException($"not a pixel hash: `{text}`");
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // ---- writing --------------------------------------------------------------------------------------

    private const int MaxStringValue = 68;

    /// <summary>
    /// FITS headers are printable ASCII: typographic characters a target name may carry (minus, degree,
    /// prime, dashes) become their ASCII readings, anything else <c>?</c>, and the text is cut to
    /// <paramref name="maxLength"/> so a card keeps its closing quote.
    /// </summary>
    public static string AsciiText(string s, int maxLength = MaxStringValue) {
        var sb = new StringBuilder(s.Length);

        foreach (var c in s) {
            if (c is >= ' ' and <= '~') {
                sb.Append(c);
            } else {
                sb.Append(c switch {
                    '−' or '‐' or '‑' or '‒' or '–' or '—' => "-",
                    '°' => "d",
                    '′' or '‘' or '’' => "'",
                    '″' or '“' or '”' => "\"",
                    ' ' or ' ' or ' ' => " ",
                    '×' => "x",
                    'μ' or 'µ' => "u",
                    _ => "?",
                });
            }
        }

        return sb.Length > maxLength ? sb.ToString(0, maxLength) : sb.ToString();
    }

    private static string FloatText(double f) {
        if (double.IsFinite(f) && f == Math.Floor(f) && Math.Abs(f) < 1e15) {
            return f.ToString("0.0", CultureInfo.InvariantCulture);
        }

        return f.ToString("R", CultureInfo.InvariantCulture).Replace('e', 'E');
    }

    /// <summary>One 80-character card.</summary>
    public static byte[] FormatCard(FitsCard card) {
        var key8 = (card.Key.Length > 8 ? card.Key[..8] : card.Key).PadRight(8);
        string text;

        if ((card.Key is "" or "COMMENT" or "HISTORY") && card.Value is string commentary) {
            text = key8 + AsciiText(commentary, 72);
        } else if (card.Value == null) {
            text = key8;
        } else {
            var field = card.Value switch {
                bool b => (b ? "T" : "F").PadLeft(20),
                int i => i.ToString(CultureInfo.InvariantCulture).PadLeft(20),
                long l => l.ToString(CultureInfo.InvariantCulture).PadLeft(20),
                double d => FloatText(d).PadLeft(20),
                float f => FloatText(f).PadLeft(20),
                string s => ("'" + AsciiText(s).Replace("'", "''").PadRight(8) + "'").PadRight(20),
                _ => throw new ArgumentException($"card {card.Key}: unsupported value {card.Value.GetType().Name}"),
            };
            text = key8 + "= " + field;

            if (!string.IsNullOrEmpty(card.Comment)) {
                text += " / " + AsciiText(card.Comment, 80);
            }
        }

        if (text.Length > Card) {
            text = text[..Card];
        }

        return Encoding.ASCII.GetBytes(text.PadRight(Card));
    }

    /// <summary>
    /// A primary HDU of unsigned 16-bit pixels as cameras write them (<c>BITPIX 16</c>, <c>BZERO 32768</c>).
    /// <paramref name="cards"/> follow the mandatory keywords. Pixels beyond <paramref name="pixels"/> are 0.
    /// </summary>
    public static byte[] Write(ReadOnlySpan<ushort> pixels, int width, int height, IEnumerable<FitsCard>? cards = null) {
        using var ms = new MemoryStream();
        ms.Write(FormatCard(new FitsCard("SIMPLE", true, "conforms to FITS standard")));
        ms.Write(FormatCard(new FitsCard("BITPIX", 16, "bits per pixel")));
        ms.Write(FormatCard(new FitsCard("NAXIS", 2)));
        ms.Write(FormatCard(new FitsCard("NAXIS1", width)));
        ms.Write(FormatCard(new FitsCard("NAXIS2", height)));
        ms.Write(FormatCard(new FitsCard("BZERO", 32768.0, "offset data range")));
        ms.Write(FormatCard(new FitsCard("BSCALE", 1.0, "scale data range")));

        foreach (var card in cards ?? Array.Empty<FitsCard>()) {
            ms.Write(FormatCard(card));
        }

        ms.Write(FormatCard(new FitsCard("END", null)));
        Pad(ms, (byte)' ');

        var count = (long)width * height;
        var data = new byte[count * 2];

        for (long i = 0; i < count; i++) {
            var v = i < pixels.Length ? pixels[(int)i] : 0;
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan((int)(i * 2), 2), (short)(v - 32768));
        }

        ms.Write(data);
        Pad(ms, 0);
        return ms.ToArray();
    }

    private static void Pad(MemoryStream ms, byte fill) {
        var rem = (int)(ms.Length % Block);

        if (rem != 0) {
            var pad = new byte[Block - rem];
            Array.Fill(pad, fill);
            ms.Write(pad);
        }
    }

    // ---- statistics -----------------------------------------------------------------------------------

    /// <summary><c>median</c>, <c>sigma</c> (1.4826 × MAD), <c>min</c> and <c>max</c> of the finite pixels; NaN when none is.</summary>
    public static ImageStats Stats(FitsImage image) => Stats(image.Pixels);

    public static ImageStats Stats(ReadOnlySpan<float> pixels) {
        var finite = new List<double>(pixels.Length);

        foreach (var p in pixels) {
            if (float.IsFinite(p)) {
                finite.Add(p);
            }
        }

        if (finite.Count == 0) {
            return new ImageStats { Median = double.NaN, Sigma = double.NaN, Min = double.NaN, Max = double.NaN };
        }

        var sorted = finite.ToArray();
        Array.Sort(sorted);
        var median = MedianOfSorted(sorted);
        var deviations = new double[sorted.Length];

        for (var i = 0; i < sorted.Length; i++) {
            deviations[i] = Math.Abs(sorted[i] - median);
        }

        Array.Sort(deviations);
        return new ImageStats { Median = median, Sigma = 1.4826 * MedianOfSorted(deviations), Min = sorted[0], Max = sorted[^1] };
    }

    /// <summary>The same numbers for an unsigned 16-bit frame, from a histogram: exact and fast on a full-size camera frame.</summary>
    public static ImageStats StatsOfUInt16(ReadOnlySpan<ushort> pixels) {
        if (pixels.Length == 0) {
            return new ImageStats { Median = double.NaN, Sigma = double.NaN, Min = double.NaN, Max = double.NaN };
        }

        var hist = new long[65536];
        int min = 65535, max = 0;

        foreach (var p in pixels) {
            hist[p]++;

            if (p < min) {
                min = p;
            }

            if (p > max) {
                max = p;
            }
        }

        var median = MedianOfHistogram(hist, pixels.Length, 1.0);
        // deviations in half units, so an x.5 median stays exact
        var twiceMedian = (long)Math.Round(median * 2);
        var dev = new long[131072];

        for (var v = min; v <= max; v++) {
            if (hist[v] != 0) {
                dev[Math.Abs(2L * v - twiceMedian)] += hist[v];
            }
        }

        return new ImageStats { Median = median, Sigma = 1.4826 * MedianOfHistogram(dev, pixels.Length, 0.5), Min = min, Max = max };
    }

    private static double MedianOfHistogram(long[] hist, long n, double unit) {
        // the two middle ranks (equal for odd n)
        var lo = (n - 1) / 2;
        var hi = n / 2;
        double? a = null, b = null;
        long seen = 0;

        for (var v = 0; v < hist.Length && b == null; v++) {
            seen += hist[v];

            if (a == null && seen > lo) {
                a = v * unit;
            }

            if (seen > hi) {
                b = v * unit;
            }
        }

        return ((a ?? 0) + (b ?? 0)) / 2.0;
    }

    private static double MedianOfSorted(double[] s) {
        var n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    // ---- previews -------------------------------------------------------------------------------------

    /// <summary>
    /// An 8-bit grey PNG, longest side at most <paramref name="maxSide"/>: box-downsampled, stretched between
    /// the 0.5 % and 99.8 % percentiles through asinh (like the service's own preview; pixel equality with it
    /// is not promised).
    /// </summary>
    public static byte[] PreviewPng(FitsImage image, int maxSide = 256) => PreviewPng<float>(image.Pixels, image.Width, image.Height, maxSide);

    public static byte[] PreviewPng<T>(ReadOnlySpan<T> pixels, int width, int height, int maxSide = 256) where T : struct, INumber<T> {
        if (maxSide < 1) {
            throw new ArgumentOutOfRangeException(nameof(maxSide));
        }

        if (pixels.Length < (long)width * height) {
            throw new ArgumentException("fewer pixels than width × height");
        }

        var k = Math.Max(1, (Math.Max(width, height) + maxSide - 1) / maxSide);
        var ow = (width + k - 1) / k;
        var oh = (height + k - 1) / k;
        var small = new double[ow * oh];
        var finite = new List<double>(small.Length);

        for (var by = 0; by < oh; by++) {
            for (var bx = 0; bx < ow; bx++) {
                double total = 0;
                var n = 0;

                for (var y = by * k; y < Math.Min(height, (by + 1) * k); y++) {
                    var row = y * width;

                    for (var x = bx * k; x < Math.Min(width, (bx + 1) * k); x++) {
                        var v = pixels[row + x];

                        if (T.IsFinite(v)) {
                            total += double.CreateChecked(v);
                            n++;
                        }
                    }
                }

                var mean = n > 0 ? total / n : double.NaN;
                small[by * ow + bx] = mean;

                if (n > 0) {
                    finite.Add(mean);
                }
            }
        }

        double black = 0, white = 0;

        if (finite.Count > 0) {
            finite.Sort();
            black = Percentile(finite, 0.005);
            white = Percentile(finite, 0.998);
        }

        var span = white - black;
        const double beta = 20.0;
        var norm = Math.Asinh(beta);
        var rows = new byte[oh * (ow + 1)];
        var o = 0;

        for (var y = 0; y < oh; y++) {
            rows[o++] = 0; // filter: none

            for (var x = 0; x < ow; x++) {
                var v = small[y * ow + x];

                if (!double.IsFinite(v)) {
                    rows[o++] = 0;
                    continue;
                }

                var t = span > 0 ? Math.Clamp((v - black) / span, 0.0, 1.0) : 0.5;
                rows[o++] = (byte)Math.Clamp(Math.Round(Math.Asinh(beta * t) / norm * 255.0), 0, 255);
            }
        }

        return Png.Grey8(ow, oh, rows);
    }

    /// <summary>A preview that fits the service's cap: 256 px a side, smaller when a noisy frame compresses badly; null when even 64 px does not fit.</summary>
    public static byte[]? PreviewWithinLimit(FitsImage image) => PreviewWithinLimit<float>(image.Pixels, image.Width, image.Height);

    public static byte[]? PreviewWithinLimit<T>(ReadOnlySpan<T> pixels, int width, int height) where T : struct, INumber<T> {
        foreach (var side in new[] { 256, 192, 128, 64 }) {
            var png = PreviewPng(pixels, width, height, side);

            if (png.Length <= Images.MaxPreviewBytes) {
                return png;
            }
        }

        return null;
    }

    private static double Percentile(List<double> sorted, double fraction) =>
        sorted[(int)Math.Round((sorted.Count - 1) * Math.Clamp(fraction, 0.0, 1.0), MidpointRounding.ToEven)];
}
