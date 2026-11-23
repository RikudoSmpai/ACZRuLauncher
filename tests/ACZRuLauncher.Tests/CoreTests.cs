using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Arc;
using ACZRuLauncher.Core.Pack;
using Xunit;

namespace ACZRuLauncher.Tests
{
    public class Lz4Tests
    {
        [Fact]
        public void Decompress_HandMadeBlock()
        {
            // литералы "abcd" + матч (смещение 4, длина 8) + последние литералы "xyzzy"
            var block = new byte[] { 0x44, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 4, 0, 0x50, (byte)'x', (byte)'y', (byte)'z', (byte)'z', (byte)'y' };
            Assert.Equal("abcdabcdabcdxyzzy", Encoding.ASCII.GetString(Lz4Block.Decompress(block, 17)));
        }

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(5)] [InlineData(12)] [InlineData(13)] [InlineData(16)] [InlineData(100)]
        [InlineData(70000)] [InlineData(300000)]
        public void RoundTrip(int len)
        {
            foreach (var data in new[] { Synthetic.Text(len, len), Synthetic.Noise(len, len), new byte[len] })
            {
                var c = Lz4Block.Compress(data);
                Assert.Equal(data, Lz4Block.Decompress(c, data.Length));
            }
        }

        [Fact]
        public void Compress_ActuallyCompresses()
        {
            var data = Synthetic.Text(1, 100000);
            Assert.True(Lz4Block.Compress(data).Length < data.Length / 2);
        }

        [Fact]
        public void Decompress_RejectsGarbage()
        {
            Assert.ThrowsAny<InvalidDataException>(() => Lz4Block.Decompress(new byte[] { 0x0F, 1, 2 }, 10));
            Assert.ThrowsAny<InvalidDataException>(() => Lz4Block.Decompress(new byte[] { 0x10, 1, 9, 0 }, 10));   // смещение за данными
        }
    }

    public class ArchiveTests
    {
        internal static List<Synthetic.FileSpec> Files() => new List<Synthetic.FileSpec>
        {
            new Synthetic.FileSpec { Name = "data/caption/x1.bin", Raw = Synthetic.Text(1, 5000), Compress = true },
            new Synthetic.FileSpec { Name = "data/raw/y.bin", Raw = Synthetic.Noise(2, 777) },
            new Synthetic.FileSpec { Name = "data/caption/x2.bin", Raw = Synthetic.Text(3, 12345), Compress = true },
            new Synthetic.FileSpec { Name = "z.bin", Raw = Synthetic.Text(4, 3) },
        };

        [Fact]
        public void Reads_SyntheticArchive()
        {
            var dir = TestDir.New();
            var path = Path.Combine(dir, "a.arc");
            var files = Files();
            File.WriteAllBytes(path, Synthetic.BuildArc(files));
            using var arc = new AczArchive(path);
            Assert.Equal(files.Count, arc.Count);
            Assert.NotEqual(0, arc.TableFileOffset % 4);
            foreach (var f in files) Assert.Equal(f.Raw, arc.Extract(f.Name));
            Assert.Equal(Hashing.Scan(path).ChecksumCalc, arc.ChecksumStored());
        }

        [Fact]
        public void Patch_WrittenCopy_ReadsBackAndChecksumOk()
        {
            var dir = TestDir.New();
            var path = Path.Combine(dir, "a.arc");
            var files = Files();
            File.WriteAllBytes(path, Synthetic.BuildArc(files));
            var repl = new Dictionary<string, byte[]> { ["data/caption/x2.bin"] = Synthetic.Text(9, 20000), ["data/raw/y.bin"] = Synthetic.Noise(8, 100) };
            ArcPatch p;
            using (var arc = new AczArchive(path)) p = ArcPatch.Build(arc, repl);
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Seek(p.TableFileOffset, SeekOrigin.Begin); f.Write(p.TableNew, 0, p.TableNew.Length);
                f.Seek(p.TailFileOffset, SeekOrigin.Begin); f.Write(p.TailNew, 0, p.TailNew.Length);
            }
            using (var arc = new AczArchive(path))
            {
                Assert.Equal(Hashing.Scan(path).ChecksumCalc, arc.ChecksumStored());
                Assert.Equal(p.Checksum, arc.ChecksumStored());
                Assert.Equal(repl["data/caption/x2.bin"], arc.Extract("data/caption/x2.bin"));
                Assert.True(arc.Entry("data/caption/x2.bin").Compressed);       // сжатая осталась сжатой
                Assert.False(arc.Entry("data/raw/y.bin").Compressed);           // несжатая — несжатой
                Assert.Equal(repl["data/raw/y.bin"], arc.Extract("data/raw/y.bin"));
                Assert.Equal(files[0].Raw, arc.Extract(files[0].Name));
                Assert.Equal(files[3].Raw, arc.Extract(files[3].Name));
            }
        }
    }

    public class DeltaTests
    {
        [Fact]
        public void CopyAndAdd()
        {
            var src = Encoding.ASCII.GetBytes("HELLO WORLD");
            var d = new AcdDelta.Writer(14).Copy(6, 5).Add(Encoding.ASCII.GetBytes(", ")).Copy(0, 5).Add(new byte[] { (byte)'!', (byte)'!' }).ToArray();
            Assert.Equal("WORLD, HELLO!!", Encoding.ASCII.GetString(AcdDelta.Apply(src, d)));
        }

        [Fact]
        public void LongLengths_Uleb()
        {
            var src = Synthetic.Noise(1, 300000);
            var add = Synthetic.Noise(2, 200);
            var d = new AcdDelta.Writer(300200).Copy(0, 150000).Add(add).Copy(150000, 150000).ToArray();
            var o = AcdDelta.Apply(src, d);
            Assert.Equal(src.Take(150000).Concat(add).Concat(src.Skip(150000)).ToArray(), o);
        }

        [Fact]
        public void Rejects_Bad()
        {
            var src = new byte[10];
            Assert.Throws<InvalidDataException>(() => AcdDelta.Apply(src, Encoding.ASCII.GetBytes("ACD0........")));
            Assert.Throws<InvalidDataException>(() => AcdDelta.Apply(src, new AcdDelta.Writer(5).Copy(8, 5).ToArray()));   // за исходником
            Assert.Throws<InvalidDataException>(() => AcdDelta.Apply(src, new AcdDelta.Writer(5).Copy(0, 4).ToArray()));   // короче out_size
            Assert.Throws<InvalidDataException>(() => AcdDelta.Apply(src, new AcdDelta.Writer(3).Copy(0, 4).ToArray()));   // длиннее
        }
    }

    public class PngTests
    {
        [Theory]
        [InlineData(2)] [InlineData(6)] [InlineData(0)] [InlineData(4)]
        public void Decode_UnfiltersAllFilters(int colorType)
        {
            int w = 37, h = 23, ch = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
            var raw = Synthetic.Noise(colorType, w * h * ch);
            for (int i = 0; i < raw.Length / 2; i++) raw[i] = (byte)(i % 7);   // и гладкая часть
            var png = Synthetic.BuildPng(w, h, colorType, raw);
            var p = PngPixels.Decode(png);
            Assert.Equal(w, p.Width); Assert.Equal(h, p.Height);
            Assert.Equal(raw, p.Raw);
        }

        [Fact]
        public void Palette_DeltaOnPixels_EncodeKeepsPlteAndTrns()
        {
            int w = 16, h = 9;
            var raw = Enumerable.Range(0, w * h).Select(i => (byte)(i % 4)).ToArray();
            var plte = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 };
            var trns = new byte[] { 0, 128, 255, 255 };
            var src = Synthetic.BuildPng(w, h, 3, raw, plte, trns);
            var outRaw = (byte[])raw.Clone();
            for (int x = 3; x < 9; x++) outRaw[4 * w + x] = 3;   // «наша надпись»
            var delta = Synthetic.MakeDelta(raw, outRaw);
            var p = PngPixels.Decode(src);
            var res = p.Encode(AcdDelta.Apply(p.Raw, delta));
            var q = PngPixels.Decode(res);
            Assert.Equal(outRaw, q.Raw);
            Assert.Equal(plte, q.Plte);
            Assert.Equal(trns, q.Trns);
            Assert.Equal(p.Ihdr, q.Ihdr);
        }

        [Fact]
        public void Zlib_RoundTrip()
        {
            var d = Synthetic.Text(5, 200000);
            Assert.Equal(d, PngPixels.ZlibInflate(PngPixels.ZlibDeflate(d)));
        }
    }

    public class UpdatesTests
    {
        [Fact]
        public void PicksOnlyAczTags()
        {
            var json = @"[
              {""tag_name"":""v9.9.9"",""name"":""PW"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""ACZRuLauncher.exe"",""browser_download_url"":""x"",""size"":1}]},
              {""tag_name"":""acz-v1.2.0"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""ACZRuLauncher.exe"",""browser_download_url"":""u120"",""size"":1}]},
              {""tag_name"":""acz-v1.10.0"",""prerelease"":true,""draft"":false,""assets"":[{""name"":""ACZRuLauncher.exe"",""browser_download_url"":""pre"",""size"":1}]},
              {""tag_name"":""acz-v1.3.0"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""PWRuLauncher.exe"",""browser_download_url"":""noexe"",""size"":1}]},
              {""tag_name"":""acz-v1.2.5"",""prerelease"":false,""draft"":false,""assets"":[{""name"":""ACZRuLauncher.exe"",""browser_download_url"":""u125"",""size"":1}]}
            ]";
            var best = Updates.PickLatest(Updates.ParseReleases(json));
            Assert.NotNull(best);
            Assert.Equal("acz-v1.2.5", best!.Tag);
            Assert.Equal(new Version(1, 2, 5), best.Version);
        }

        [Fact]
        public void PwTagIsNotAVersion()
        {
            Assert.Null(Updates.ParseVersion("v1.0.4"));
            Assert.Null(Updates.ParseVersion("mods-20260928-0135"));
            Assert.Equal(new Version(2, 0, 1), Updates.ParseVersion("acz-v2.0.1"));
        }
    }

    public class GameLocateTests
    {
        [Fact]
        public void FindsAczeroFromParentOrFile()
        {
            var root = Path.Combine(TestDir.New(), "steamapps", "common", GameInstall.ZeroInstallDir);
            var acz = Path.Combine(root, "ACZero");
            Directory.CreateDirectory(acz);
            File.WriteAllBytes(Path.Combine(acz, "a.arc"), new byte[1]);
            File.WriteAllBytes(Path.Combine(acz, "d.arc"), new byte[1]);
            Assert.Equal(acz, GameInstall.FromAnyPath(root)!.Root);
            Assert.Equal(acz, GameInstall.FromAnyPath(acz)!.Root);
            Assert.Equal(acz, GameInstall.FromAnyPath(Path.Combine(acz, "a.arc"))!.Root);
            Assert.Equal(GameKind.SteamZero, GameInstall.FromAnyPath(root)!.Kind);
            Assert.Null(GameInstall.FromAnyPath(Path.GetDirectoryName(Path.GetDirectoryName(root))));
        }

        [Fact]
        public void Ac8Kind()
        {
            var acz = Path.Combine(TestDir.New(), "steamapps", "common", "ACE COMBAT 8", "ACZero");
            Directory.CreateDirectory(acz);
            File.WriteAllBytes(Path.Combine(acz, "a.arc"), new byte[1]);
            File.WriteAllBytes(Path.Combine(acz, "d.arc"), new byte[1]);
            Assert.Equal(GameKind.SteamAc8, GameInstall.FromAnyPath(acz)!.Kind);
        }

        [Fact]
        public void VdfValue()
        {
            Assert.Equal(@"D:\SteamLibrary", GameLocator.VdfValue("\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"", "path"));
            Assert.Equal("ACE COMBAT 8", GameLocator.VdfValue("\t\"installdir\"\t\t\"ACE COMBAT 8\"", "installdir"));
            Assert.Null(GameLocator.VdfValue("\t\"label\"\t\"\"", "path"));
        }
    }

    internal static class TestDir
    {
        private static int _n;
        /// <summary>Папка внутри вывода сборки (bin/…/work): не %TEMP%, и не в git.</summary>
        public static string New()
        {
            var d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work",
                                 $"{DateTime.Now:HHmmss}-{System.Diagnostics.Process.GetCurrentProcess().Id}-{System.Threading.Interlocked.Increment(ref _n)}");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
