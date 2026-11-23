using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ACZRuLauncher.Core.Pack
{
    [DataContract]
    public sealed class PngInfo
    {
        [DataMember(Name = "width")] public int Width;
        [DataMember(Name = "height")] public int Height;
        [DataMember(Name = "color_type")] public int ColorType;
        [DataMember(Name = "bit_depth")] public int BitDepth;
    }

    /// <summary>Доп. файл исходника (Уточнения к acz-ru-pack/1): источник COPY = файл + эти файлы того же архива подряд.</summary>
    [DataContract]
    public sealed class PackExtra
    {
        [DataMember(Name = "path")] public string Path = "";
        [DataMember(Name = "size")] public long? Size;
        [DataMember(Name = "sha256")] public string Sha256 = "";
    }

    [DataContract]
    public sealed class PackFile
    {
        [DataMember(Name = "path")] public string Path = "";
        [DataMember(Name = "kind")] public string Kind = "bin";
        [DataMember(Name = "src_size")] public long? SrcSize;
        [DataMember(Name = "src_sha256")] public string SrcSha256 = "";
        [DataMember(Name = "out_size")] public long? OutSize;
        [DataMember(Name = "out_sha256")] public string? OutSha256;
        [DataMember(Name = "png")] public PngInfo? Png;
        [DataMember(Name = "raw_src_sha256")] public string? RawSrcSha256;
        [DataMember(Name = "raw_out_sha256")] public string? RawOutSha256;
        [DataMember(Name = "raw_size")] public long? RawSize;
        [DataMember(Name = "delta")] public string Delta = "";
        [DataMember(Name = "delta_size")] public long? DeltaSize;
        [DataMember(Name = "delta_sha256")] public string? DeltaSha256;
        [DataMember(Name = "src_extra")] public List<PackExtra>? SrcExtra;

        public bool IsPng => string.Equals(Kind, "png", StringComparison.OrdinalIgnoreCase);
    }

    [DataContract]
    public sealed class PackArchive
    {
        [DataMember(Name = "orig_size")] public long OrigSize;
        [DataMember(Name = "orig_sha256")] public string OrigSha256 = "";
        [DataMember(Name = "files")] public List<PackFile> Files = new List<PackFile>();
    }

    [DataContract]
    public sealed class PackManifest
    {
        public const string FormatId = "acz-ru-pack/1";
        [DataMember(Name = "format")] public string Format = "";
        [DataMember(Name = "version")] public string Version = "";
        [DataMember(Name = "slot")] public string? Slot;
        [DataMember(Name = "archives")] public Dictionary<string, PackArchive> Archives = new Dictionary<string, PackArchive>();
        /// <summary>Что умеет пакет сверх файлов (необязательное поле). <c>hud-font-h8-cyr</c> — в шрифте HUD (таблица h8)
        /// есть кириллица, без этого модуль acz.exe не ставится (<see cref="ExeModule"/>).</summary>
        [DataMember(Name = "features", EmitDefaultValue = false)] public List<string>? Features;

        public static PackManifest Parse(byte[] json)
        {
            var ser = new DataContractJsonSerializer(typeof(PackManifest), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            PackManifest m;
            using (var ms = new MemoryStream(json)) m = (PackManifest?)ser.ReadObject(ms) ?? throw new InvalidDataException("пустой manifest.json");
            m.Validate();
            return m;
        }

        public void Validate()
        {
            if (Format != FormatId) throw new InvalidDataException($"пакет не того формата: «{Format}», ждали {FormatId}");
            if (Archives == null || Archives.Count == 0) throw new InvalidDataException("в пакете нет архивов");
            foreach (var kv in Archives)
            {
                if (!RuPack.ArchiveNames.Contains(kv.Key)) throw new InvalidDataException("пакет: неизвестный архив " + kv.Key);
                var a = kv.Value ?? throw new InvalidDataException("пакет: пустая запись " + kv.Key);
                if (a.Files == null) a.Files = new List<PackFile>();
                if (!IsSha(a.OrigSha256) || a.OrigSize <= 0) throw new InvalidDataException($"пакет: у {kv.Key} нет orig_sha256/orig_size");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in a.Files)
                {
                    if (string.IsNullOrEmpty(f.Path) || !seen.Add(f.Path)) throw new InvalidDataException($"пакет: пустой или повторный путь в {kv.Key}");
                    if (!IsSha(f.SrcSha256)) throw new InvalidDataException($"пакет: {f.Path}: нет src_sha256");
                    if (string.IsNullOrEmpty(f.Delta) || f.Delta.Contains("..")) throw new InvalidDataException($"пакет: {f.Path}: плохой путь дельты");
                    if (f.DeltaSha256 != null && !IsSha(f.DeltaSha256)) throw new InvalidDataException($"пакет: {f.Path}: плохой delta_sha256");
                    if (f.SrcExtra != null)
                    {
                        if (f.IsPng && f.SrcExtra.Count > 0) throw new InvalidDataException($"пакет: {f.Path}: src_extra у png не поддерживается");
                        foreach (var x in f.SrcExtra)
                            if (x == null || string.IsNullOrEmpty(x.Path) || !IsSha(x.Sha256)) throw new InvalidDataException($"пакет: {f.Path}: плохой src_extra");
                    }
                    if (f.IsPng)
                    {
                        if (f.Png == null || !IsSha(f.RawSrcSha256) || !IsSha(f.RawOutSha256))
                            throw new InvalidDataException($"пакет: {f.Path}: у png нет png/raw_src_sha256/raw_out_sha256");
                    }
                    else if (!string.Equals(f.Kind, "bin", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"пакет: {f.Path}: неизвестный kind «{f.Kind}»");
                    else if (!IsSha(f.OutSha256)) throw new InvalidDataException($"пакет: {f.Path}: нет out_sha256");
                }
            }
        }

        internal static bool IsSha(string? s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    /// <summary>
    /// Пакет перевода acz-ru-pack/1: manifest.json + deltas/a/…, deltas/d/….
    /// В релизе — zip, дописанный в хвост exe (8 байт длины + ACZRPAY1). В разработке — zip или папка по --pack.
    /// </summary>
    public sealed class RuPack : IDisposable
    {
        public const string TrailerMagic = "ACZRPAY1";
        public static readonly HashSet<string> ArchiveNames = new HashSet<string>(StringComparer.Ordinal) { "a.arc", "d.arc" };

        private readonly ZipArchive? _zip;
        private readonly Stream? _stream;
        private readonly string? _dir;
        public PackManifest Manifest { get; }
        /// <summary>sha256 manifest.json — отличает сборки пакета с одинаковой версией.</summary>
        public string Id { get; }
        public string Source { get; }

        private RuPack(Stream zipStream, string source)
        {
            _stream = zipStream; Source = source;
            _zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
            var bytes = ReadAll("manifest.json");
            Manifest = PackManifest.Parse(bytes);
            Id = Hashing.Sha256(bytes);
        }

        private RuPack(string dir)
        {
            _dir = dir; Source = dir;
            var bytes = ReadAll("manifest.json");
            Manifest = PackManifest.Parse(bytes);
            Id = Hashing.Sha256(bytes);
        }

        public static RuPack OpenPath(string path)
        {
            if (Directory.Exists(path)) return new RuPack(path);
            return new RuPack(File.OpenRead(path), path);
        }

        public static RuPack OpenStream(Stream zip, string source) => new RuPack(zip, source);

        /// <summary>Пакет из --pack, иначе из хвоста exe, иначе acz-ru-pack.zip рядом с exe. null — пакета нет.</summary>
        public static RuPack? TryOpen(string exePath, string? overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath)) return OpenPath(overridePath!);
            var fromExe = OpenAppended(exePath);
            if (fromExe != null) return fromExe;
            var side = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath) ?? ".", "acz-ru-pack.zip");
            return File.Exists(side) ? OpenPath(side) : null;
        }

        private static RuPack? OpenAppended(string exePath)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < 16) { fs.Dispose(); return null; }
                fs.Seek(-16, SeekOrigin.End);
                var t = new byte[16];
                if (fs.Read(t, 0, 16) != 16 || Encoding.ASCII.GetString(t, 8, 8) != TrailerMagic) { fs.Dispose(); return null; }
                long len = BitConverter.ToInt64(t, 0), start = fs.Length - 16 - len;
                if (len <= 0 || start < 0) { fs.Dispose(); return null; }
                return new RuPack(new SubStream(fs, start, len), exePath);
            }
            catch (InvalidDataException) { fs?.Dispose(); throw; }   // пакет есть, но битый — пусть окно скажет
            catch { fs?.Dispose(); return null; }
        }

        public byte[] ReadAll(string name)
        {
            if (_dir != null)
            {
                var p = System.IO.Path.Combine(_dir, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(p)) throw new FileNotFoundException("в пакете нет " + name);
                return File.ReadAllBytes(p);
            }
            var e = _zip!.GetEntry(name) ?? throw new FileNotFoundException("в пакете нет " + name);
            using var s = e.Open();
            var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public void Dispose() { _zip?.Dispose(); _stream?.Dispose(); }
    }

    /// <summary>Окно чтения внутри большого файла: ZipArchive видит только хвост exe.</summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start, _length;
        private long _pos;

        public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = Math.Max(0, Math.Min(value, _length)); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remain = _length - _pos;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            _inner.Seek(_start + _pos, SeekOrigin.Begin);
            var n = _inner.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
