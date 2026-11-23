using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ACZRuLauncher.Core.Arc
{
    /// <summary>
    /// Патч дописыванием (порт build_append_patch из acz_arc.py). Меняются ровно два места исходного архива:
    /// таблица записей (переписывается на месте, той же длины) и всё с позиции старого 8-байтного хвоста:
    /// туда пишутся новые файлы подряд, затем новый хвост — файл растёт. Старые данные заменённых файлов
    /// остаются на месте, на них просто никто не ссылается. Поэтому для отката хватает старой таблицы,
    /// старого хвоста и исходной длины.
    /// </summary>
    public sealed class ArcPatch
    {
        public long TableFileOffset { get; private set; }
        public byte[] TableNew { get; private set; } = Array.Empty<byte>();
        public byte[] TableOld { get; private set; } = Array.Empty<byte>();
        public long TailFileOffset { get; private set; }
        public byte[] TailNew { get; private set; } = Array.Empty<byte>();
        public byte[] TrailerOld { get; private set; } = Array.Empty<byte>();
        public long OrigSize { get; private set; }
        public long PatchedSize => TailFileOffset + TailNew.Length;
        public uint Checksum { get; private set; }
        public ArcEntry[] NewEntries { get; private set; } = Array.Empty<ArcEntry>();
        public ArcEntry[] OldEntries { get; private set; } = Array.Empty<ArcEntry>();
        /// <summary>Имена заменённых файлов.</summary>
        public HashSet<string> Replaced { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// replacements: имя записи → новое содержимое (распакованное). Запись, сжатая в оригинале, сжимается LZ4;
        /// несжатая остаётся несжатой. Если LZ4 не дал выигрыша, пишем без сжатия (stored == raw — так игра
        /// и отличает несжатую запись).
        /// </summary>
        public static ArcPatch Build(AczArchive arc, IReadOnlyDictionary<string, byte[]> replacements)
        {
            if ((arc.BodyLength & 3) != 0) throw new InvalidDataException("длина тела архива не кратна 4 — формат не тот");
            var p = new ArcPatch { OrigSize = arc.Size };
            long tailPos = arc.BodyLength;                    // от начала тела: здесь был хвост
            if (tailPos + 0L > uint.MaxValue) throw new InvalidDataException("архив слишком большой");
            var entries = arc.Entries.ToArray();
            p.OldEntries = arc.Entries.ToArray();
            var data = new MemoryStream();
            foreach (var name in replacements.Keys.OrderBy(arc.IndexOf))
            {
                int i = arc.IndexOf(name);
                if (i < 0) throw new InvalidDataException("в архиве нет файла " + name);
                var raw = replacements[name];
                byte[] stored = raw;
                if (entries[i].Compressed)
                {
                    var c = Lz4Block.Compress(raw);
                    if (c.Length < raw.Length) stored = c;
                }
                long off = tailPos + data.Length;
                if (off + stored.Length > uint.MaxValue) throw new InvalidDataException("архив вырастет больше 4 ГБ");
                entries[i].Offset = (uint)off;
                entries[i].Raw = (uint)raw.Length;
                entries[i].Stored = (uint)stored.Length;
                data.Write(stored, 0, stored.Length);
                p.Replaced.Add(name);
            }
            while ((data.Length & 3) != 0) data.WriteByte(0);          // тело остаётся кратным слову
            var encData = data.ToArray();
            AczArchive.Mask(encData, 0, encData.Length, tailPos, arc.A, arc.B);

            var tablePlain = new byte[arc.TableLen];
            for (int i = 0; i < entries.Length; i++)
            {
                Put(tablePlain, 12 * i, entries[i].Offset);
                Put(tablePlain, 12 * i + 4, entries[i].Raw);
                Put(tablePlain, 12 * i + 8, entries[i].Stored);
            }
            var encTable = (byte[])tablePlain.Clone();
            AczArchive.Mask(encTable, 0, encTable.Length, arc.TablePos, arc.A, arc.B);

            // сумма пересчитывается по разнице: слово-выровненный кусок вокруг таблицы + новые данные
            long tp = arc.TableFileOffset;
            long a0 = tp - ((tp - AczArchive.Hdr) % 4);
            long a1 = tp + arc.TableLen; a1 += (4 - ((a1 - AczArchive.Hdr) % 4)) % 4;
            var oldSpan = arc.ReadRaw(a0, checked((int)(a1 - a0)));
            var newSpan = (byte[])oldSpan.Clone();
            Buffer.BlockCopy(encTable, 0, newSpan, (int)(tp - a0), encTable.Length);
            uint s = arc.ChecksumStored();
            s = unchecked(s - AczArchive.Rotr7Sum(oldSpan, 0, oldSpan.Length) + AczArchive.Rotr7Sum(newSpan, 0, newSpan.Length)
                          + AczArchive.Rotr7Sum(encData, 0, encData.Length));
            long newBody = arc.BodyLength + encData.Length;
            var ck = BitConverter.GetBytes(s);
            AczArchive.Mask(ck, 0, 4, newBody, arc.A, arc.B);

            var tail = new byte[encData.Length + AczArchive.Trail];
            Buffer.BlockCopy(encData, 0, tail, 0, encData.Length);
            Put(tail, encData.Length, arc.K3);
            Buffer.BlockCopy(ck, 0, tail, encData.Length + 4, 4);

            p.TableFileOffset = tp;
            p.TableNew = encTable;
            p.TableOld = arc.ReadRaw(tp, arc.TableLen);
            p.TailFileOffset = arc.Size - AczArchive.Trail;
            p.TailNew = tail;
            p.TrailerOld = arc.ReadRaw(arc.Size - AczArchive.Trail, AczArchive.Trail);
            p.Checksum = s;
            p.NewEntries = entries;
            return p;
        }

        private static void Put(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
    }
}
