using System;
using System.IO;
using System.Text;

namespace ACZRuLauncher.Core.Pack
{
    /// <summary>
    /// Дельта ACD1 (acz-ru-pack/1, little-endian):
    /// "ACD1", u64 out_size, затем операции до конца файла:
    ///   0x01 COPY uleb128 src_offset, uleb128 length — участок исходного файла игрока;
    ///   0x02 ADD  uleb128 length, затем length байт   — наши байты.
    /// </summary>
    public static class AcdDelta
    {
        public const byte OpCopy = 0x01;
        public const byte OpAdd = 0x02;
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACD1");

        public static byte[] Apply(byte[] src, byte[] delta)
        {
            if (delta.Length < 12 || delta[0] != Magic[0] || delta[1] != Magic[1] || delta[2] != Magic[2] || delta[3] != Magic[3])
                throw new InvalidDataException("дельта: нет заголовка ACD1");
            ulong outSize = BitConverter.ToUInt64(delta, 4);
            if (outSize > int.MaxValue) throw new InvalidDataException("дельта: слишком большой результат");
            var dst = new byte[(int)outSize];
            int ip = 12, op = 0;
            while (ip < delta.Length)
            {
                byte code = delta[ip++];
                if (code == OpCopy)
                {
                    long off = Uleb(delta, ref ip), len = Uleb(delta, ref ip);
                    if (off + len > src.Length) throw new InvalidDataException("дельта: COPY за пределами исходного файла");
                    if (op + len > dst.Length) throw new InvalidDataException("дельта: результат длиннее out_size");
                    Buffer.BlockCopy(src, (int)off, dst, op, (int)len);
                    op += (int)len;
                }
                else if (code == OpAdd)
                {
                    long len = Uleb(delta, ref ip);
                    if (ip + len > delta.Length) throw new InvalidDataException("дельта: ADD за концом файла дельты");
                    if (op + len > dst.Length) throw new InvalidDataException("дельта: результат длиннее out_size");
                    Buffer.BlockCopy(delta, ip, dst, op, (int)len);
                    ip += (int)len; op += (int)len;
                }
                else throw new InvalidDataException($"дельта: неизвестная операция 0x{code:X2}");
            }
            if (op != dst.Length) throw new InvalidDataException($"дельта: получилось {op} байт вместо {dst.Length}");
            return dst;
        }

        private static long Uleb(byte[] b, ref int ip)
        {
            long v = 0; int shift = 0;
            while (true)
            {
                if (ip >= b.Length) throw new InvalidDataException("дельта: оборвано число");
                byte x = b[ip++];
                v |= (long)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) return v;
                shift += 7;
                if (shift > 56) throw new InvalidDataException("дельта: слишком длинное число");
            }
        }

        // ---------------------------------------------------------------- запись (тесты и инструменты)
        public sealed class Writer
        {
            private readonly MemoryStream _ms = new MemoryStream();
            private readonly long _outSize;
            public Writer(long outSize)
            {
                _outSize = outSize;
                _ms.Write(Magic, 0, 4);
                _ms.Write(BitConverter.GetBytes((ulong)outSize), 0, 8);
            }
            public Writer Copy(long srcOffset, long length) { _ms.WriteByte(OpCopy); WriteUleb(srcOffset); WriteUleb(length); return this; }
            public Writer Add(byte[] data) { _ms.WriteByte(OpAdd); WriteUleb(data.Length); _ms.Write(data, 0, data.Length); return this; }
            public byte[] ToArray() => _ms.ToArray();
            private void WriteUleb(long v)
            {
                do
                {
                    byte b = (byte)(v & 0x7F); v >>= 7;
                    _ms.WriteByte(v != 0 ? (byte)(b | 0x80) : b);
                } while (v != 0);
            }
        }
    }
}
