using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ObsServices.Core.Imaging;

/// <summary>Just enough PNG to write a preview: 8-bit greyscale, no interlace, one IDAT. No imaging library, so it runs anywhere the core does.</summary>
public static class Png {
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary><paramref name="filteredRows"/> is <c>height</c> rows of a filter byte (0) followed by <c>width</c> grey bytes.</summary>
    public static byte[] Grey8(int width, int height, byte[] filteredRows) {
        if (filteredRows.Length != height * (width + 1)) {
            throw new ArgumentException("rows must be height × (width + 1) bytes");
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 0; // greyscale
        Chunk(ms, "IHDR", ihdr);

        using (var z = new MemoryStream()) {
            using (var deflate = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) {
                deflate.Write(filteredRows);
            }

            Chunk(ms, "IDAT", z.ToArray());
        }

        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>Width and height of a PNG, or null when the bytes are not one.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> png) {
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) {
            return null;
        }

        return (BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
    }

    private static void Chunk(Stream s, string type, byte[] body) {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(body);
        var crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(body, crc) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc) {
        foreach (var b in data) {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable() {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++) {
            var c = n;

            for (var k = 0; k < 8; k++) {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
