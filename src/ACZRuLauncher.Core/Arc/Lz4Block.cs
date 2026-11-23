using System;
using System.IO;

namespace ACZRuLauncher.Core.Arc
{
    /// <summary>
    /// LZ4 block format (без кадра и без размера в начале) — так сжаты записи архивов ACZ
    /// (в Python: lz4.block.compress(store_size=False) / decompress(uncompressed_size=raw)).
    /// Компрессор свой, жадный, по спецификации LZ4 Block Format: результат другой, чем у liblz4,
    /// но читается любым декомпрессором LZ4. Поэтому после установки проверяется содержимое, а не байты.
    /// </summary>
    public static class Lz4Block
    {
        private const int MinMatch = 4;
        private const int LastLiterals = 5;   // последние 5 байт — всегда литералы
        private const int MfLimit = 12;       // матч не начинается ближе 12 байт к концу
        private const int MaxOffset = 65535;
        private const int HashLog = 16;

        public static byte[] Decompress(byte[] src, int srcOffset, int srcLength, int rawSize)
        {
            var dst = new byte[rawSize];
            int ip = srcOffset, iend = srcOffset + srcLength, op = 0;
            while (ip < iend)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15)
                {
                    int b;
                    do { if (ip >= iend) throw Bad("литералы"); b = src[ip++]; lit += b; } while (b == 255);
                }
                if (lit > 0)
                {
                    if (ip + lit > iend || op + lit > rawSize) throw Bad("длина литералов");
                    Buffer.BlockCopy(src, ip, dst, op, lit);
                    ip += lit; op += lit;
                }
                if (ip >= iend) break;                // последняя последовательность — только литералы
                if (ip + 2 > iend) throw Bad("смещение");
                int off = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (off == 0 || off > op) throw Bad("смещение вне данных");
                int len = token & 15;
                if (len == 15)
                {
                    int b;
                    do { if (ip >= iend) throw Bad("длина матча"); b = src[ip++]; len += b; } while (b == 255);
                }
                len += MinMatch;
                if (op + len > rawSize) throw Bad("матч за концом");
                int from = op - off;
                if (off >= len) { Buffer.BlockCopy(dst, from, dst, op, len); op += len; }
                else for (int i = 0; i < len; i++) dst[op++] = dst[from + i];   // перекрытие: побайтно
            }
            if (op != rawSize) throw Bad($"распаковано {op} из {rawSize}");
            return dst;
        }

        public static byte[] Decompress(byte[] src, int rawSize) => Decompress(src, 0, src.Length, rawSize);

        private static InvalidDataException Bad(string what) => new InvalidDataException("LZ4: повреждённые данные (" + what + ")");

        public static byte[] Compress(byte[] src)
        {
            int n = src.Length;
            var outp = new MemoryStream(n / 2 + 64);
            if (n < MfLimit + 1)
            {
                WriteSequence(outp, src, 0, n, 0, 0);
                return outp.ToArray();
            }
            var table = new int[1 << HashLog];
            for (int i = 0; i < table.Length; i++) table[i] = -1;
            int anchor = 0, ip = 0;
            int matchLimit = n - LastLiterals;       // матч кончается не дальше
            int searchLimit = n - MfLimit;           // и начинается не позже
            while (ip <= searchLimit)
            {
                uint seq = Read32(src, ip);
                int h = Hash(seq);
                int cand = table[h];
                table[h] = ip;
                if (cand < 0 || ip - cand > MaxOffset || Read32(src, cand) != seq) { ip++; continue; }
                // расширяем назад и вперёд
                int s = ip, c = cand;
                while (s > anchor && c > 0 && src[s - 1] == src[c - 1]) { s--; c--; }
                int len = MinMatch + (ip - s);
                int e = ip + MinMatch, ce = cand + MinMatch;
                while (e < matchLimit && src[e] == src[ce]) { e++; ce++; len++; }
                WriteSequence(outp, src, anchor, s - anchor, s - c, len);
                ip = s + len;
                anchor = ip;
                if (ip - 2 >= 0 && ip - 2 <= searchLimit) table[Hash(Read32(src, ip - 2))] = ip - 2;
            }
            WriteSequence(outp, src, anchor, n - anchor, 0, 0);
            return outp.ToArray();
        }

        /// <summary>Последовательность: токен, литералы, (смещение + длина матча, если matchLen &gt; 0).</summary>
        private static void WriteSequence(Stream o, byte[] src, int litStart, int litLen, int offset, int matchLen)
        {
            int ml = matchLen > 0 ? matchLen - MinMatch : 0;
            int token = (Math.Min(litLen, 15) << 4) | (matchLen > 0 ? Math.Min(ml, 15) : 0);
            o.WriteByte((byte)token);
            if (litLen >= 15) WriteLen(o, litLen - 15);
            o.Write(src, litStart, litLen);
            if (matchLen == 0) return;
            o.WriteByte((byte)offset); o.WriteByte((byte)(offset >> 8));
            if (ml >= 15) WriteLen(o, ml - 15);
        }

        private static void WriteLen(Stream o, int rest)
        {
            while (rest >= 255) { o.WriteByte(255); rest -= 255; }
            o.WriteByte((byte)rest);
        }

        private static uint Read32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        private static int Hash(uint v) => (int)((v * 2654435761u) >> (32 - HashLog));
    }
}
