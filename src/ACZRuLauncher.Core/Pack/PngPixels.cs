using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ACZRuLauncher.Core.Pack
{
    /// <summary>
    /// PNG как сырые пиксели (acz-ru-pack/1, kind "png"): строки подряд без байта фильтра.
    /// Decode: IDAT (zlib) → снять фильтры None/Sub/Up/Average/Paeth. Encode: IHDR как у исходной,
    /// PLTE и tRNS из исходной, IDAT = zlib(каждая строка с фильтром 0), IEND.
    /// Байты PNG у нас и у сборки пакета могут отличаться — сверка идёт по пикселям.
    /// </summary>
    public sealed class PngPixels
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int BitDepth { get; private set; }
        public int ColorType { get; private set; }
        public int Interlace { get; private set; }
        public byte[] Ihdr { get; private set; } = Array.Empty<byte>();
        public byte[]? Plte { get; private set; }
        public byte[]? Trns { get; private set; }
        /// <summary>Строки подряд, без байта фильтра.</summary>
        public byte[] Raw { get; private set; } = Array.Empty<byte>();

        public int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("PNG: неизвестный тип цвета " + ColorType) };
        public int Stride => checked((Width * Channels * BitDepth + 7) / 8);
        /// <summary>Байт на пиксель для фильтров (не меньше 1).</summary>
        public int FilterBpp => Math.Max(1, Channels * BitDepth / 8);

        public static PngPixels Decode(byte[] png)
        {
            if (png.Length < 8) throw new InvalidDataException("PNG: слишком короткий файл");
            for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) throw new InvalidDataException("PNG: нет сигнатуры");
            var p = new PngPixels();
            var idat = new MemoryStream();
            int pos = 8; bool end = false;
            while (pos + 12 <= png.Length && !end)
            {
                int len = (int)BE32(png, pos);
                if (len < 0 || pos + 12L + len > png.Length) throw new InvalidDataException("PNG: оборванный блок");
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                var body = new byte[len];
                Buffer.BlockCopy(png, pos + 8, body, 0, len);
                switch (type)
                {
                    case "IHDR":
                        if (len != 13) throw new InvalidDataException("PNG: IHDR не той длины");
                        p.Ihdr = body;
                        p.Width = (int)BE32(body, 0); p.Height = (int)BE32(body, 4);
                        p.BitDepth = body[8]; p.ColorType = body[9]; p.Interlace = body[12];
                        if (body[10] != 0 || body[11] != 0) throw new InvalidDataException("PNG: неизвестное сжатие или фильтр");
                        break;
                    case "PLTE": p.Plte = body; break;
                    case "tRNS": p.Trns = body; break;
                    case "IDAT": idat.Write(body, 0, len); break;
                    case "IEND": end = true; break;
                }
                pos += 12 + len;
            }
            if (p.Ihdr.Length == 0) throw new InvalidDataException("PNG: нет IHDR");
            if (p.Interlace != 0) throw new InvalidDataException("PNG: чересстрочные картинки не поддерживаются");
            if (p.Width <= 0 || p.Height <= 0) throw new InvalidDataException("PNG: пустая картинка");
            var filtered = ZlibInflate(idat.ToArray());
            int stride = p.Stride, h = p.Height, bpp = p.FilterBpp;
            if (filtered.Length < (long)(stride + 1) * h) throw new InvalidDataException("PNG: данных меньше, чем пикселей");
            var raw = new byte[(long)stride * h];
            for (int y = 0; y < h; y++)
            {
                int f = filtered[y * (stride + 1)];
                int src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? raw[dst + x - bpp] : 0;
                    int b = y > 0 ? raw[prev + x] : 0;
                    int c = x >= bpp && y > 0 ? raw[prev + x - bpp] : 0;
                    int v = filtered[src + x];
                    switch (f)
                    {
                        case 0: break;
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4: v += Paeth(a, b, c); break;
                        default: throw new InvalidDataException("PNG: неизвестный фильтр строки " + f);
                    }
                    raw[dst + x] = (byte)v;
                }
            }
            p.Raw = raw;
            return p;
        }

        /// <summary>Собрать PNG с теми же IHDR/PLTE/tRNS и новыми пикселями.</summary>
        public byte[] Encode(byte[] raw)
        {
            int stride = Stride;
            if (raw.Length != (long)stride * Height) throw new InvalidDataException($"PNG: пикселей {raw.Length} байт, ждали {(long)stride * Height}");
            var filtered = new byte[(long)(stride + 1) * Height];
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(raw, y * stride, filtered, y * (stride + 1) + 1, stride);   // фильтр 0
            var z = ZlibDeflate(filtered);
            var o = new MemoryStream(z.Length + 1024);
            o.Write(Signature, 0, 8);
            WriteChunk(o, "IHDR", Ihdr, 0, Ihdr.Length);
            if (Plte != null) WriteChunk(o, "PLTE", Plte, 0, Plte.Length);
            if (Trns != null) WriteChunk(o, "tRNS", Trns, 0, Trns.Length);
            const int part = 1 << 16;
            for (int i = 0; i < z.Length; i += part) WriteChunk(o, "IDAT", z, i, Math.Min(part, z.Length - i));
            WriteChunk(o, "IEND", Array.Empty<byte>(), 0, 0);
            return o.ToArray();
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static uint BE32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

        private static void WriteBE32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static void WriteChunk(Stream o, string type, byte[] data, int off, int len)
        {
            var t = Encoding.ASCII.GetBytes(type);
            WriteBE32(o, (uint)len);
            o.Write(t, 0, 4);
            o.Write(data, off, len);
            uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, t, 0, 4), data, off, len) ^ 0xFFFFFFFF;
            WriteBE32(o, crc);
        }

        // ---------------------------------------------------------------- zlib поверх DeflateStream
        public static byte[] ZlibInflate(byte[] z)
        {
            if (z.Length < 6) throw new InvalidDataException("zlib: слишком коротко");
            if ((z[0] & 0x0F) != 8 || ((z[0] << 8) | z[1]) % 31 != 0) throw new InvalidDataException("zlib: неверный заголовок");
            if ((z[1] & 0x20) != 0) throw new InvalidDataException("zlib: словарь не поддерживается");
            using var ds = new DeflateStream(new MemoryStream(z, 2, z.Length - 2), CompressionMode.Decompress);
            var o = new MemoryStream();
            ds.CopyTo(o);
            return o.ToArray();
        }

        public static byte[] ZlibDeflate(byte[] data)
        {
            var o = new MemoryStream();
            o.WriteByte(0x78); o.WriteByte(0x9C);
            using (var ds = new DeflateStream(o, CompressionLevel.Optimal, leaveOpen: true)) ds.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length;)
            {
                int n = Math.Min(3800, data.Length - i);
                for (int k = 0; k < n; k++) { a += data[i + k]; b += a; }
                a %= 65521; b %= 65521; i += n;
            }
            WriteBE32(o, (b << 16) | a);
            return o.ToArray();
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] T = Make();
        private static uint[] Make()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
        public static uint Update(uint crc, byte[] b, int off, int len)
        {
            for (int i = off; i < off + len; i++) crc = T[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
