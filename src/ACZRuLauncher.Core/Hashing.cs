using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ACZRuLauncher.Core.Arc;

namespace ACZRuLauncher.Core
{
    public static class Hashing
    {
        /// <summary>SHA256Cng: на .NET Framework в разы быстрее SHA256Managed (архив a.arc — 480 МБ).</summary>
        public static HashAlgorithm NewSha256()
        {
            try { return new SHA256Cng(); } catch { return SHA256.Create(); }
        }

        public static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Sha256(byte[] data)
        {
            using var h = NewSha256();
            return Hex(h.ComputeHash(data));
        }

        public static string Sha256File(string path, IProgress<double>? progress = null) => Scan(path, progress).Sha256;

        public readonly struct ScanResult
        {
            public ScanResult(string sha, long size, uint? calc) { Sha256 = sha; Size = size; ChecksumCalc = calc; }
            public string Sha256 { get; }
            public long Size { get; }
            /// <summary>Сумма rotr7 по телу архива (null — длина тела не кратна 4, это не архив ACZ).</summary>
            public uint? ChecksumCalc { get; }
        }

        /// <summary>Один проход по файлу: sha256 и контрольная сумма тела архива ACZ.</summary>
        public static ScanResult Scan(string path, IProgress<double>? progress = null)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            long size = f.Length;
            long bodyEnd = size - AczArchive.Trail;
            bool aligned = size >= AczArchive.Hdr + AczArchive.Trail && ((size - AczArchive.Hdr - AczArchive.Trail) & 3) == 0;
            using var h = NewSha256();
            var buf = new byte[1 << 22];
            long pos = 0; uint sum = 0;
            int r;
            while ((r = ReadFull(f, buf)) > 0)
            {
                h.TransformBlock(buf, 0, r, null, 0);
                if (aligned)
                {
                    long lo = Math.Max(pos, AczArchive.Hdr), hi = Math.Min(pos + r, bodyEnd);
                    if (hi > lo) sum = unchecked(sum + AczArchive.Rotr7Sum(buf, (int)(lo - pos), (int)(hi - lo)));
                }
                pos += r;
                progress?.Report(size > 0 ? (double)pos / size : 1);
            }
            h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new ScanResult(Hex(h.Hash), size, aligned ? sum : (uint?)null);
        }

        /// <summary>Читает буфер целиком (кроме конца файла): границы кусков остаются кратны 4 для суммы по словам.</summary>
        private static int ReadFull(Stream s, byte[] buf)
        {
            int got = 0, r;
            while (got < buf.Length && (r = s.Read(buf, got, buf.Length - got)) > 0) got += r;
            return got;
        }
    }
}
