using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DalamudMCP.Tools;

internal static class ScreenshotPixels
{
    public static (int Width, int Height) Fit(int width, int height, int cap)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 67108864)
            throw new ArgumentException("capture source dimensions are invalid or exceed the 64-megapixel limit");
        if (cap < 256 || cap > 3840) throw new ArgumentOutOfRangeException(nameof(cap));
        var scale = Math.Min(1.0, (double)cap / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    // Both WIC/GDI and GPU readbacks can contain row padding. Never interpret pitch as width.
    public static byte[] ToRgba(byte[] source, int width, int height, int pitch, bool bgra,
        int outputWidth, int outputHeight)
    {
        if (width < 1 || height < 1 || outputWidth < 1 || outputHeight < 1 ||
            outputWidth > width || outputHeight > height || (long)outputWidth * outputHeight > 14745600 ||
            pitch < (long)width * 4 || (long)pitch * (height - 1) + (long)width * 4 > source.Length)
            throw new ArgumentException("capture pixel dimensions, pitch or buffer length are invalid");
        var result = new byte[checked(outputWidth * outputHeight * 4)];
        if (width == outputWidth && height == outputHeight)
        {
            for (var y = 0; y < height; y++)
            {
                var row = result.AsSpan(y * width * 4, width * 4);
                source.AsSpan(y * pitch, width * 4).CopyTo(row);
                for (var x = 0; x < row.Length; x += 4)
                {
                    if (bgra) (row[x], row[x + 2]) = (row[x + 2], row[x]);
                    row[x + 3] = 255;
                }
            }
            return result;
        }
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
        {
            // Bilinear sampling preserves text edges better than the former nearest-neighbour path.
            var sx = Math.Clamp((x + 0.5) * width / outputWidth - 0.5, 0, width - 1);
            var sy = Math.Clamp((y + 0.5) * height / outputHeight - 0.5, 0, height - 1);
            var x0 = (int)sx;
            var y0 = (int)sy;
            var x1 = Math.Min(x0 + 1, width - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var fx = sx - x0;
            var fy = sy - y0;
            var dest = (y * outputWidth + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var channel = bgra ? 2 - c : c;
                var top = source[y0 * pitch + x0 * 4 + channel] * (1 - fx) + source[y0 * pitch + x1 * 4 + channel] * fx;
                var bottom = source[y1 * pitch + x0 * 4 + channel] * (1 - fx) + source[y1 * pitch + x1 * 4 + channel] * fx;
                result[dest + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
            }
            result[dest + 3] = 255;
        }
        return result;
    }

    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height * 4 != rgba.Length)
            throw new ArgumentException("PNG dimensions do not match the RGBA buffer");
        var stride = checked(width * 4);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            row[0] = 1; // PNG Sub filter: improves compression of UI surfaces without a second full image copy.
            for (var y = 0; y < height; y++)
            {
                var offset = y * stride;
                for (var x = 0; x < stride; x++)
                    row[x + 1] = unchecked((byte)(rgba[offset + x] - (x < 4 ? 0 : rgba[offset + x - 4])));
                zlib.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteUInt(header, 0, (uint)width);
        WriteUInt(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteUInt(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream stream, string type, byte[] bytes)
    {
        var header = new byte[8];
        WriteUInt(header, 0, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        stream.Write(header);
        stream.Write(bytes);
        uint crc = 0xFFFFFFFF;
        for (var i = 4; i < 8; i++) crc = Step(crc, header[i]);
        foreach (var b in bytes) crc = Step(crc, b);
        var checksum = new byte[4];
        WriteUInt(checksum, 0, crc ^ 0xFFFFFFFF);
        stream.Write(checksum);
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
