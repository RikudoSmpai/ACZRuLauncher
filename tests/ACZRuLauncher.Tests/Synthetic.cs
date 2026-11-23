using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Arc;
using ACZRuLauncher.Core.Pack;

namespace ACZRuLauncher.Tests
{
    /// <summary>Синтетика для тестов: архив в формате ACZ, PNG, пакет acz-ru-pack/1. Ни байта данных игры.</summary>
    internal static class Synthetic
    {
        public sealed class FileSpec
        {
            public string Name = "";
            public byte[] Raw = Array.Empty<byte>();
            public bool Compress;
        }

        /// <summary>Архив ACZ: заголовок, тело под маской (заголовок, имена, таблица, данные), хвост с суммой.</summary>
        public static byte[] BuildArc(IList<FileSpec> files, uint k1 = 0x13572468, uint k2 = 0x9ABCDEF1, uint k3 = 0x0F1E2D3C)
        {
            var names = new MemoryStream();
            foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); names.Write(b, 0, b.Length); names.WriteByte(0); }
            if (names.Length % 2 == 0) names.WriteByte(0);                 // нечётная длина: таблица не на границе слова, как в a.arc
            int nsz = (int)names.Length;
            long tablePos = 16 + nsz;
            long dataPos = tablePos + 12L * files.Count;
            var data = new MemoryStream();
            var table = new MemoryStream();
            foreach (var f in files)
            {
                var stored = f.Compress ? Lz4Block.Compress(f.Raw) : f.Raw;
                if (f.Compress && stored.Length >= f.Raw.Length) throw new InvalidOperationException("тестовые данные не сжимаются");
                var off = (uint)(dataPos + data.Length);
                table.Write(BitConverter.GetBytes(off), 0, 4);
                table.Write(BitConverter.GetBytes((uint)f.Raw.Length), 0, 4);
                table.Write(BitConverter.GetBytes((uint)stored.Length), 0, 4);
                data.Write(stored, 0, stored.Length);
                data.WriteByte(0x5A);                                      // зазор: записи не обязаны идти вплотную
            }
            var body = new MemoryStream();
            body.Write(BitConverter.GetBytes(AczArchive.MagicIn), 0, 4);
            body.Write(BitConverter.GetBytes((uint)files.Count), 0, 4);
            body.Write(BitConverter.GetBytes((uint)nsz), 0, 4);
            body.Write(BitConverter.GetBytes(1u), 0, 4);
            names.WriteTo(body); table.WriteTo(body); data.WriteTo(body);
            while (body.Length % 4 != 0) body.WriteByte(0);
            var b2 = body.ToArray();
            uint a = k1 ^ k3 ^ AczArchive.C1, bb = k2 ^ k3 ^ AczArchive.C2;
            AczArchive.Mask(b2, 0, b2.Length, 0, a, bb);
            uint sum = AczArchive.Rotr7Sum(b2, 0, b2.Length);
            var ck = BitConverter.GetBytes(sum);
            AczArchive.Mask(ck, 0, 4, b2.Length, a, bb);
            var o = new MemoryStream();
            o.Write(BitConverter.GetBytes(AczArchive.MagicOut), 0, 4);
            o.Write(BitConverter.GetBytes(k1), 0, 4);
            o.Write(BitConverter.GetBytes(k2), 0, 4);
            o.Write(b2, 0, b2.Length);
            o.Write(BitConverter.GetBytes(k3), 0, 4);
            o.Write(ck, 0, 4);
            return o.ToArray();
        }

        /// <summary>Текстоподобные данные: хорошо жмутся LZ4.</summary>
        public static byte[] Text(int seed, int len)
        {
            var r = new Random(seed);
            string[] words = { "alpha ", "bravo ", "charlie ", "delta ", "echo ", "foxtrot ", "golf ", "hotel ", "\0\0\0\0", "india " };
            var sb = new StringBuilder();
            while (sb.Length < len) sb.Append(words[r.Next(words.Length)]);
            return Encoding.ASCII.GetBytes(sb.ToString(0, len));
        }

        public static byte[] Noise(int seed, int len)
        {
            var b = new byte[len];
            new Random(seed).NextBytes(b);
            return b;
        }

        // ---------------------------------------------------------------- PNG
        /// <summary>PNG с фильтрами строк по кругу (None/Sub/Up/Average/Paeth), чтобы проверить их снятие.</summary>
        public static byte[] BuildPng(int w, int h, int colorType, byte[] raw, byte[]? plte = null, byte[]? trns = null)
        {
            int ch = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new ArgumentException() };
            int stride = w * ch, bpp = ch;
            var filtered = new byte[(stride + 1) * h];
            for (int y = 0; y < h; y++)
            {
                int f = y % 5;
                filtered[y * (stride + 1)] = (byte)f;
                for (int x = 0; x < stride; x++)
                {
                    int cur = raw[y * stride + x];
                    int a = x >= bpp ? raw[y * stride + x - bpp] : 0;
                    int b = y > 0 ? raw[(y - 1) * stride + x] : 0;
                    int c = x >= bpp && y > 0 ? raw[(y - 1) * stride + x - bpp] : 0;
                    int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                    filtered[y * (stride + 1) + 1 + x] = (byte)(cur - pred);
                }
            }
            var o = new MemoryStream();
            o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = (byte)colorType;
            Chunk(o, "IHDR", ihdr);
            Chunk(o, "tEXt", Encoding.ASCII.GetBytes("Comment\0synthetic"));   // лишний блок: при сборке выпадает
            if (plte != null) Chunk(o, "PLTE", plte);
            if (trns != null) Chunk(o, "tRNS", trns);
            var z = PngPixels.ZlibDeflate(filtered);
            int half = z.Length / 2;                                              // два IDAT подряд
            Chunk(o, "IDAT", z.Take(half).ToArray());
            Chunk(o, "IDAT", z.Skip(half).ToArray());
            Chunk(o, "IEND", Array.Empty<byte>());
            return o.ToArray();
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static void BE(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

        private static void Chunk(Stream o, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, (uint)data.Length); o.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type); o.Write(t, 0, 4); o.Write(data, 0, data.Length);
            var crcIn = t.Concat(data).ToArray();
            uint c = 0xFFFFFFFF;
            foreach (var x in crcIn) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
            var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFF); o.Write(crc, 0, 4);
        }

        // ---------------------------------------------------------------- пакет
        public static string Sha(byte[] b) => Hashing.Sha256(b);

        /// <summary>Дельта «как у сборки пакета»: общее начало и конец — COPY из исходника, середина — ADD.</summary>
        public static byte[] MakeDelta(byte[] src, byte[] dst)
        {
            int pre = 0;
            while (pre < src.Length && pre < dst.Length && src[pre] == dst[pre]) pre++;
            int suf = 0;
            while (suf < src.Length - pre && suf < dst.Length - pre && src[src.Length - 1 - suf] == dst[dst.Length - 1 - suf]) suf++;
            var w = new AcdDelta.Writer(dst.Length);
            if (pre > 0) w.Copy(0, pre);
            var mid = dst.Skip(pre).Take(dst.Length - pre - suf).ToArray();
            if (mid.Length > 0) w.Add(mid);
            if (suf > 0) w.Copy(src.Length - suf, suf);
            return w.ToArray();
        }

        public sealed class PackEntry
        {
            public string Arc = "a.arc";
            public string Path = "";
            public byte[] Src = Array.Empty<byte>();
            public byte[] Out = Array.Empty<byte>();
            /// <summary>Для png: исходная картинка = Src, пиксели результата = Out.</summary>
            public bool Png;
            /// <summary>src_extra: доп. файлы того же архива (путь, содержимое); дельта тогда задаётся явно.</summary>
            public List<(string Path, byte[] Data)>? Extra;
            public byte[]? Delta;
        }

        /// <summary>Пакет-папка: manifest.json + deltas/&lt;a|d&gt;/&lt;path&gt;.acd.</summary>
        public static void WritePack(string dir, string version, IDictionary<string, byte[]> origArchives, IEnumerable<PackEntry> entries,
                                     Func<PackFile, PackFile>? tamper = null)
        {
            Directory.CreateDirectory(dir);
            var man = new PackManifest { Format = PackManifest.FormatId, Version = version, Slot = "uk" };
            foreach (var kv in origArchives)
                man.Archives[kv.Key] = new PackArchive { OrigSize = kv.Value.Length, OrigSha256 = Sha(kv.Value) };
            foreach (var e in entries)
            {
                var tag = e.Arc.Substring(0, 1);
                var rel = $"deltas/{tag}/{e.Path}.acd";
                var full = System.IO.Path.Combine(dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                var f = new PackFile { Path = e.Path, Delta = rel, SrcSha256 = Sha(e.Src), SrcSize = e.Src.Length };
                if (e.Png)
                {
                    var p = PngPixels.Decode(e.Src);
                    f.Kind = "png";
                    f.Png = new PngInfo { Width = p.Width, Height = p.Height, ColorType = p.ColorType, BitDepth = p.BitDepth };
                    f.SrcSize = null;
                    f.RawSrcSha256 = Sha(p.Raw);
                    f.RawOutSha256 = Sha(e.Out);
                    File.WriteAllBytes(full, MakeDelta(p.Raw, e.Out));
                }
                else
                {
                    f.Kind = "bin";
                    f.OutSha256 = Sha(e.Out); f.OutSize = e.Out.Length;
                    var delta = e.Delta ?? MakeDelta(e.Src, e.Out);
                    File.WriteAllBytes(full, delta);
                    f.DeltaSize = delta.Length; f.DeltaSha256 = Sha(delta);
                    if (e.Extra != null)
                        f.SrcExtra = e.Extra.Select(x => new PackExtra { Path = x.Path, Size = x.Data.Length, Sha256 = Sha(x.Data) }).ToList();
                }
                man.Archives[e.Arc].Files.Add(tamper != null ? tamper(f) : f);
            }
            var ser = new DataContractJsonSerializer(typeof(PackManifest), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using var fs = File.Create(System.IO.Path.Combine(dir, "manifest.json"));
            ser.WriteObject(fs, man);
        }
    }
}
