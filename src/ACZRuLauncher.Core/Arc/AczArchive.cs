using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ACZRuLauncher.Core.Arc
{
    /// <summary>Запись таблицы: смещение (от начала тела), размер распакованного, размер хранимого.
    /// stored != raw — запись сжата LZ4.</summary>
    public struct ArcEntry
    {
        public string Name;
        public uint Offset, Raw, Stored;
        public bool Compressed => Stored != Raw;
        public override string ToString() => $"{Name} off={Offset} raw={Raw} stored={Stored}";
    }

    /// <summary>
    /// Архив ACZ PC (.arc). Порт acz_arc.py (свой, по мотивам открытого aczarc.py Volcano Water; константы маски оттуда).
    ///
    /// Файл = 12 байт заголовка (magic, k1, k2) | тело под маской | 8 байт хвоста (k3, контрольная сумма под маской).
    /// Тело без маски: magic2, count, размер имён, версия | имена через \0 | таблица count × (off, raw, stored) | данные.
    /// Маска: XOR 32-битных слов с A + B × номер слова (номер от начала тела).
    /// Контрольная сумма = сумма rotr7 по всем словам тела, как они лежат на диске (под маской).
    /// </summary>
    public sealed class AczArchive : IDisposable
    {
        public const uint MagicOut = 0x454758C3;
        public const uint MagicIn = 0x414758E1;
        public const uint C1 = 0x5DF38C35;
        public const uint C2 = 0x4CE49F4D;
        public const int Hdr = 12;
        public const int Trail = 8;

        private readonly FileStream _f;
        public string Path { get; }
        public long Size { get; }
        public uint K1 { get; }
        public uint K2 { get; }
        public uint K3 { get; }
        public uint EncChecksum { get; }
        public uint A { get; }
        public uint B { get; }
        public int Count { get; }
        public int NamesSize { get; }
        /// <summary>Позиция таблицы от начала тела; в файле — Hdr + TablePos.</summary>
        public long TablePos { get; }
        public int TableLen => 12 * Count;
        public long TableFileOffset => Hdr + TablePos;
        public long BodyLength => Size - Hdr - Trail;
        public IReadOnlyList<ArcEntry> Entries => _entries;
        private readonly ArcEntry[] _entries;
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public AczArchive(string path)
        {
            Path = path;
            _f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            try
            {
                Size = _f.Length;
                if (Size < Hdr + Trail + 16) throw new InvalidDataException("файл слишком короткий для архива ACZ");
                var h = ReadRaw(0, Hdr);
                if (BitConverter.ToUInt32(h, 0) != MagicOut) throw new InvalidDataException("не архив ACZ (заголовок не тот)");
                K1 = BitConverter.ToUInt32(h, 4); K2 = BitConverter.ToUInt32(h, 8);
                var t = ReadRaw(Size - Trail, Trail);
                K3 = BitConverter.ToUInt32(t, 0); EncChecksum = BitConverter.ToUInt32(t, 4);
                A = K1 ^ K3 ^ C1;
                B = K2 ^ K3 ^ C2;
                var head = ReadBody(0, 16);
                uint magic2 = BitConverter.ToUInt32(head, 0);
                Count = checked((int)BitConverter.ToUInt32(head, 4));
                NamesSize = checked((int)BitConverter.ToUInt32(head, 8));
                uint version = BitConverter.ToUInt32(head, 12);
                if (magic2 != MagicIn || version != 1) throw new InvalidDataException("неподдерживаемый внутренний заголовок архива");
                if (Count <= 0 || NamesSize <= 0 || 16L + NamesSize + 12L * Count > BodyLength)
                    throw new InvalidDataException("таблица архива не помещается в файл");
                var names = ReadBody(16, NamesSize);
                TablePos = 16 + NamesSize;
                var table = ReadBody(TablePos, TableLen);
                _entries = new ArcEntry[Count];
                int p = 0;
                for (int i = 0; i < Count; i++)
                {
                    int z = Array.IndexOf(names, (byte)0, p);
                    if (z < 0) z = names.Length;
                    var name = Encoding.UTF8.GetString(names, p, z - p);
                    p = Math.Min(z + 1, names.Length);
                    _entries[i] = new ArcEntry
                    {
                        Name = name,
                        Offset = BitConverter.ToUInt32(table, 12 * i),
                        Raw = BitConverter.ToUInt32(table, 12 * i + 4),
                        Stored = BitConverter.ToUInt32(table, 12 * i + 8),
                    };
                    _index[name] = i;
                }
            }
            catch { _f.Dispose(); throw; }
        }

        public void Dispose() => _f.Dispose();

        public bool Contains(string name) => _index.ContainsKey(name);
        public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : -1;

        public ArcEntry Entry(string name)
        {
            var i = IndexOf(name);
            if (i < 0) throw new KeyNotFoundException("в архиве нет файла " + name);
            return _entries[i];
        }

        // ---------------------------------------------------------------- чтение
        public byte[] ReadRaw(long fileOffset, int n)
        {
            var buf = new byte[n];
            _f.Seek(fileOffset, SeekOrigin.Begin);
            int got = 0;
            while (got < n)
            {
                int r = _f.Read(buf, got, n - got);
                if (r <= 0) throw new EndOfStreamException("архив короче ожидаемого");
                got += r;
            }
            return buf;
        }

        /// <summary>Байты тела без маски.</summary>
        public byte[] ReadBody(long bodyPos, int n)
        {
            var b = ReadRaw(Hdr + bodyPos, n);
            Mask(b, 0, n, bodyPos, A, B);
            return b;
        }

        public byte[] ReadStored(ArcEntry e)
        {
            if ((long)e.Offset + e.Stored > BodyLength) throw new InvalidDataException($"{e.Name}: запись выходит за конец архива");
            return ReadBody(e.Offset, checked((int)e.Stored));
        }

        public byte[] Extract(string name) => Extract(Entry(name));

        public byte[] Extract(ArcEntry e)
        {
            var d = ReadStored(e);
            if (e.Compressed) d = Lz4Block.Decompress(d, checked((int)e.Raw));
            if (d.Length != e.Raw) throw new InvalidDataException($"{e.Name}: размер не совпадает");
            return d;
        }

        /// <summary>Сумма, записанная в хвосте (снятая маска).</summary>
        public uint ChecksumStored()
        {
            var b = BitConverter.GetBytes(EncChecksum);
            Mask(b, 0, 4, BodyLength, A, B);
            return BitConverter.ToUInt32(b, 0);
        }

        // ---------------------------------------------------------------- маска и сумма
        /// <summary>XOR с маской на месте: data[off..off+n) лежит в теле с позиции bodyPos.</summary>
        public static void Mask(byte[] data, int off, int n, long bodyPos, uint a, uint b)
        {
            int i = 0;
            long pos = bodyPos;
            // голова до границы слова
            while (i < n && (pos & 3) != 0)
            {
                uint m = unchecked(a + b * (uint)(pos >> 2));
                data[off + i] ^= (byte)(m >> (int)(8 * (pos & 3)));
                i++; pos++;
            }
            uint w = (uint)(pos >> 2);
            while (i + 4 <= n)
            {
                uint m = unchecked(a + b * w);
                data[off + i] ^= (byte)m;
                data[off + i + 1] ^= (byte)(m >> 8);
                data[off + i + 2] ^= (byte)(m >> 16);
                data[off + i + 3] ^= (byte)(m >> 24);
                i += 4; w++;
            }
            if (i < n)
            {
                uint m = unchecked(a + b * w);
                for (int k = 0; i < n; k++, i++) data[off + i] ^= (byte)(m >> (8 * k));
            }
        }

        /// <summary>Сумма rotr7 по словам (little-endian), len кратно 4.</summary>
        public static uint Rotr7Sum(byte[] d, int off, int len)
        {
            if ((len & 3) != 0) throw new ArgumentException("длина не кратна 4");
            uint s = 0;
            for (int i = off, end = off + len; i < end; i += 4)
            {
                uint x = (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));
                s = unchecked(s + ((x >> 7) | (x << 25)));
            }
            return s;
        }
    }
}
