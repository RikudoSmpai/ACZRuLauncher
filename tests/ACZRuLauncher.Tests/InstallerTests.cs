using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Arc;
using ACZRuLauncher.Core.Pack;
using Xunit;

namespace ACZRuLauncher.Tests
{
    /// <summary>Установка, проверка, откат на синтетической «игре»: a.arc + d.arc, пакет с bin и png.</summary>
    public class InstallerTests
    {
        private sealed class World
        {
            public string Game = "", Backup = "", PackDir = "";
            public byte[] OrigA = Array.Empty<byte>(), OrigD = Array.Empty<byte>();
            public List<Synthetic.PackEntry> Entries = new List<Synthetic.PackEntry>();
            public string A => Path.Combine(Game, "a.arc");
            public string D => Path.Combine(Game, "d.arc");

            public TranslationInstaller Installer(string? packDir = null)
            {
                var pack = RuPack.OpenPath(packDir ?? PackDir);
                return new TranslationInstaller(pack, Game, Backup) { IsGameRunning = () => false };
            }
        }

        private static readonly IProgress<InstallProgress> NoProgress = new Progress<InstallProgress>(_ => { });

        private static World Make(string version = "t-1", Func<PackFile, PackFile>? tamper = null)
        {
            var root = TestDir.New();
            var w = new World { Game = Path.Combine(root, "ACZero"), Backup = Path.Combine(root, "backup"), PackDir = Path.Combine(root, "pack") };
            Directory.CreateDirectory(w.Game);

            int pw = 40, ph = 20;
            var pngRaw = Synthetic.Noise(11, pw * ph * 3);
            var png = Synthetic.BuildPng(pw, ph, 2, pngRaw);
            var aFiles = new List<Synthetic.FileSpec>
            {
                new Synthetic.FileSpec { Name = "data/caption/uk/m01.bin", Raw = Synthetic.Text(21, 9000), Compress = true },
                new Synthetic.FileSpec { Name = "data/caption/us/m01.bin", Raw = Synthetic.Text(21, 9000), Compress = true },
                new Synthetic.FileSpec { Name = "data/radio/m01.bin", Raw = Synthetic.Text(22, 30000), Compress = true },
                new Synthetic.FileSpec { Name = "extratex/manual/000.png", Raw = png },
                new Synthetic.FileSpec { Name = "data/other.bin", Raw = Synthetic.Noise(23, 4096) },
            };
            var dFiles = new List<Synthetic.FileSpec>
            {
                new Synthetic.FileSpec { Name = "select.msa", Raw = Synthetic.Text(31, 20000), Compress = true },
                new Synthetic.FileSpec { Name = "pause/supply.fhm", Raw = Synthetic.Text(32, 5000), Compress = true },
            };
            w.OrigA = Synthetic.BuildArc(aFiles);
            w.OrigD = Synthetic.BuildArc(dFiles, 0x11111111, 0x22222222, 0x33333333);
            File.WriteAllBytes(w.A, w.OrigA);
            File.WriteAllBytes(w.D, w.OrigD);

            byte[] Edit(byte[] src, int seed, int growBy)
            {
                var o = src.ToList();
                o.RemoveRange(src.Length / 3, 200);
                o.InsertRange(src.Length / 3, Synthetic.Text(seed, 200 + growBy));
                return o.ToArray();
            }
            var outPng = (byte[])pngRaw.Clone();
            for (int i = 300; i < 900; i++) outPng[i] = 0xEE;
            w.Entries = new List<Synthetic.PackEntry>
            {
                new Synthetic.PackEntry { Arc = "a.arc", Path = "data/caption/uk/m01.bin", Src = aFiles[0].Raw, Out = Edit(aFiles[0].Raw, 40, 1500) },
                new Synthetic.PackEntry { Arc = "a.arc", Path = "data/radio/m01.bin", Src = aFiles[2].Raw, Out = Edit(aFiles[2].Raw, 41, -50) },
                new Synthetic.PackEntry { Arc = "a.arc", Path = "extratex/manual/000.png", Src = png, Out = outPng, Png = true },
                new Synthetic.PackEntry { Arc = "d.arc", Path = "select.msa", Src = dFiles[0].Raw, Out = Edit(dFiles[0].Raw, 42, 333) },
            };
            Synthetic.WritePack(w.PackDir, version, new Dictionary<string, byte[]> { ["a.arc"] = w.OrigA, ["d.arc"] = w.OrigD }, w.Entries, tamper);
            return w;
        }

        private static void AssertOriginal(World w)
        {
            Assert.Equal(Synthetic.Sha(w.OrigA), Hashing.Sha256File(w.A));
            Assert.Equal(Synthetic.Sha(w.OrigD), Hashing.Sha256File(w.D));
        }

        private static void AssertInstalledContent(World w)
        {
            foreach (var g in w.Entries.GroupBy(e => e.Arc))
            {
                var path = Path.Combine(w.Game, g.Key);
                using var arc = new AczArchive(path);
                Assert.Equal(Hashing.Scan(path).ChecksumCalc, arc.ChecksumStored());
                foreach (var e in g)
                {
                    var d = arc.Extract(e.Path);
                    Assert.Equal(e.Out, e.Png ? PngPixels.Decode(d).Raw : d);
                }
            }
            using (var a = new AczArchive(w.A))
            {
                Assert.Equal(Synthetic.Text(21, 9000), a.Extract("data/caption/us/m01.bin"));   // US не тронут
                Assert.Equal(Synthetic.Noise(23, 4096), a.Extract("data/other.bin"));
            }
        }

        [Fact]
        public void Install_Then_Remove_GivesOriginalSha()
        {
            var w = Make();
            var inst = w.Installer();
            Assert.Equal(Overall.NotInstalled, inst.Inspect().Overall);
            inst.Install(NoProgress, CancellationToken.None);
            Assert.Equal(Overall.Installed, inst.Inspect().Overall);
            Assert.True(new FileInfo(w.A).Length > w.OrigA.Length);
            AssertInstalledContent(w);

            inst.Install(NoProgress, CancellationToken.None);                 // повторно — ничего не делает
            Assert.Equal(Overall.Installed, inst.Inspect().Overall);

            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
            Assert.Equal(Overall.NotInstalled, inst.Inspect().Overall);
        }

        [Fact]
        public void Module_InstalledAfterArchives_RemovedWithThem_ForeignKept()
        {
            var w = Make();
            var man = Path.Combine(w.PackDir, "manifest.json");
            var json = File.ReadAllText(man);
            File.WriteAllText(man, json.Substring(0, json.Length - 1) + ",\"features\":[\"" + ExeModule.FontFeature + "\"]}");
            var dll = Path.Combine(w.Game, ExeModule.FileName);
            var mod = new ExeModule(System.Text.Encoding.ASCII.GetBytes("MZ synthetic module"), GameKind.SteamZero);

            var inst = w.Installer();
            inst.Module = mod;
            Assert.Equal(ModuleState.NotInstalled, inst.Inspect().Module);
            inst.Install(NoProgress, CancellationToken.None);
            Assert.Null(inst.ModuleMessage);
            Assert.Equal(mod.Dll, File.ReadAllBytes(dll));
            Assert.Equal(ModuleState.Installed, inst.Inspect().Module);
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
            Assert.False(File.Exists(dll));

            // чужой version.dll: перевод ставится, модуль — нет, файл не тронут ни установкой, ни удалением
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3 });
            inst.Install(NoProgress, CancellationToken.None);
            Assert.Equal(Overall.Installed, inst.Inspect().Overall);
            Assert.Contains(ExeModule.FileName, inst.ModuleMessage);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dll));
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dll));
        }

        [Fact]
        public void FailureDuringWrite_RollsBackBothArchives()
        {
            var w = Make();
            var inst = w.Installer();
            inst.AfterTableWritten = name => { if (name == "d.arc") throw new IOException("обрыв записи (тест)"); };
            var ex = Assert.Throws<InstallException>(() => inst.Install(NoProgress, CancellationToken.None));
            Assert.Contains("возвращены к оригиналу", ex.Message);
            AssertOriginal(w);                                                 // a.arc уже был записан — откатился тоже
        }

        [Fact]
        public void InterruptedInstall_TableOnly_IsDetectedAndRolledBack()
        {
            var w = Make();
            var inst = w.Installer();
            // «процесс убит» после записи таблицы: исключение-маркер не ловится нашим откатом, если мы сами имитируем обрыв
            inst.Install(NoProgress, CancellationToken.None);
            // вернуть хвост к исходному, оставив новую таблицу = состояние «записана только таблица»
            var trailer = w.OrigA.Skip(w.OrigA.Length - 8).ToArray();
            using (var f = new FileStream(w.A, FileMode.Open, FileAccess.ReadWrite))
            {
                f.SetLength(w.OrigA.Length);
                f.Seek(w.OrigA.Length - 8, SeekOrigin.Begin); f.Write(trailer, 0, 8);
            }
            var st = inst.Inspect();
            Assert.Equal(ArcState.Interrupted, st.Archives.Single(a => a.Name == "a.arc").State);
            Assert.Equal(Overall.NeedsRepair, st.Overall);
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
        }

        [Fact]
        public void InterruptedInstall_PartialTail_InstallRepairs()
        {
            var w = Make();
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            using (var f = new FileStream(w.A, FileMode.Open, FileAccess.ReadWrite)) f.SetLength(w.OrigA.Length + 100);   // хвост дописан не весь
            Assert.Equal(ArcState.Interrupted, inst.Inspect().Archives.Single(a => a.Name == "a.arc").State);
            inst.Install(NoProgress, CancellationToken.None);                 // установка сама откатывает и ставит заново
            Assert.Equal(Overall.Installed, inst.Inspect().Overall);
            AssertInstalledContent(w);
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
        }

        [Fact]
        public void ForeignArchive_IsNotTouched()
        {
            var w = Make();
            var bytes = (byte[])w.OrigA.Clone();
            bytes[bytes.Length / 2] ^= 0xFF;                                   // «другой мод»
            File.WriteAllBytes(w.A, bytes);
            var inst = w.Installer();
            Assert.Equal(Overall.Foreign, inst.Inspect().Overall);
            var ex = Assert.Throws<InstallException>(() => inst.Install(NoProgress, CancellationToken.None));
            Assert.Contains("Проверить целостность", ex.Message);
            Assert.Equal(Synthetic.Sha(bytes), Hashing.Sha256File(w.A));
            Assert.Equal(Synthetic.Sha(w.OrigD), Hashing.Sha256File(w.D));     // и второй архив не тронут
        }

        [Fact]
        public void GameRunning_Refuses()
        {
            var w = Make();
            var inst = w.Installer();
            inst.IsGameRunning = () => true;
            var ex = Assert.Throws<InstallException>(() => inst.Install(NoProgress, CancellationToken.None));
            Assert.Contains("Игра запущена", ex.Message);
            AssertOriginal(w);
        }

        [Fact]
        public void WrongSource_RefusesBeforeWriting()
        {
            var w = Make(tamper: f => { if (f.Path == "select.msa") f.SrcSha256 = new string('0', 64); return f; });
            var inst = w.Installer();
            Assert.ThrowsAny<Exception>(() => inst.Install(NoProgress, CancellationToken.None));
            AssertOriginal(w);
            Assert.False(Directory.Exists(w.Backup) && Directory.GetFiles(w.Backup, "*.installed.json").Any());
        }

        [Fact]
        public void NewPackVersion_IsUpdate_AndReinstalls()
        {
            var w = Make("t-1");
            w.Installer().Install(NoProgress, CancellationToken.None);
            var w2Pack = Path.Combine(Path.GetDirectoryName(w.PackDir)!, "pack2");
            var e2 = w.Entries.Select(e => new Synthetic.PackEntry { Arc = e.Arc, Path = e.Path, Src = e.Src, Png = e.Png, Out = e.Out.Concat(new byte[e.Png ? 0 : 7]).ToArray() }).ToList();
            Synthetic.WritePack(w2Pack, "t-2", new Dictionary<string, byte[]> { ["a.arc"] = w.OrigA, ["d.arc"] = w.OrigD }, e2);
            var inst2 = w.Installer(w2Pack);
            Assert.Equal(Overall.NeedsUpdate, inst2.Inspect().Overall);
            inst2.Install(NoProgress, CancellationToken.None);
            Assert.Equal(Overall.Installed, inst2.Inspect().Overall);
            w.Entries = e2;
            AssertInstalledContent(w);
            inst2.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
        }

        [Fact]
        public void SrcExtra_CopiesFromOtherFilesOfTheArchive()
        {
            var w = Make();
            // результат собирается из своего файла и участка соседнего (US) — как буквы из других файлов игры
            var src = Synthetic.Text(21, 9000);
            var us = Synthetic.Text(21, 9000);
            var radio = Synthetic.Text(22, 30000);
            var mine = System.Text.Encoding.ASCII.GetBytes("<<наши байты>>");
            var outBytes = src.Take(100).Concat(radio.Skip(500).Take(300)).Concat(mine).Concat(us.Skip(8000)).ToArray();
            var delta = new AcdDelta.Writer(outBytes.Length).Copy(0, 100).Copy(src.Length + us.Length + 500, 300).Add(mine)
                                                           .Copy(src.Length + 8000, 1000).ToArray();
            var e = w.Entries[0];
            e.Out = outBytes; e.Delta = delta;
            e.Extra = new List<(string, byte[])> { ("data/caption/us/m01.bin", us), ("data/radio/m01.bin", radio) };
            Directory.Delete(w.PackDir, true);
            Synthetic.WritePack(w.PackDir, "t-x", new Dictionary<string, byte[]> { ["a.arc"] = w.OrigA, ["d.arc"] = w.OrigD }, w.Entries);
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            AssertInstalledContent(w);
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);

            // повреждённая дельта (sha256 не тот) — отказ до записи
            var path = Path.Combine(w.PackDir, "deltas", "a", "data", "caption", "uk", "m01.bin.acd");
            var bad = File.ReadAllBytes(path); bad[bad.Length - 1] ^= 1; File.WriteAllBytes(path, bad);
            var ex = Assert.ThrowsAny<Exception>(() => w.Installer().Install(NoProgress, CancellationToken.None));
            Assert.Contains("дельта", ex.Message);
            AssertOriginal(w);
        }

        [Fact]
        public void MissingArchive_IsReported()
        {
            var w = Make();
            File.Delete(w.D);
            var inst = w.Installer();
            Assert.Equal(Overall.Missing, inst.Inspect().Overall);
            Assert.Throws<InstallException>(() => inst.Install(NoProgress, CancellationToken.None));
            Assert.Equal(Synthetic.Sha(w.OrigA), Hashing.Sha256File(w.A));
        }

        /// <summary>setting.ini как у Steam-версии Zero: CRLF, табы, комментарий со списком языков.</summary>
        private const string ZeroIni =
            "Resolution=1280x720\t;3840x2160 1920x1080 1280x720\r\n" +
            "ScreenMode=Windowed\t\t;Windowed Borderless\r\n" +
            "SystemLanguage=\t\t;JP US UK DE FR IT ES\r\n" +
            "SkipLauncher=False\t\t;True False\r\n";

        private static string WriteIni(World w, string text)
        {
            var p = MenuLanguage.IniPath(w.Game);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, System.Text.Encoding.ASCII.GetBytes(text));
            return p;
        }

        [Fact]
        public void Install_SetsMenuUk_Remove_RestoresIniByteForByte()
        {
            var w = Make();
            var ini = WriteIni(w, ZeroIni);
            var inst = w.Installer();
            var st0 = inst.Inspect();
            Assert.True(st0.MenuIniExists);
            Assert.Equal("", st0.MenuLanguageValue);
            Assert.False(st0.MenuRussian);

            inst.Install(NoProgress, CancellationToken.None);
            var st1 = inst.Inspect();
            Assert.True(st1.MenuRussian);
            Assert.Equal(ZeroIni.Replace("SystemLanguage=\t", "SystemLanguage=UK\t"), File.ReadAllText(ini));

            inst.Install(NoProgress, CancellationToken.None);                 // повторно — бэкап меню не перезаписан
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
            Assert.Equal(ZeroIni, File.ReadAllText(ini));
            Assert.False(File.Exists(Path.Combine(w.Backup, "setting.ini.backup.json")));
        }

        [Fact]
        public void Install_WithoutIni_KeepsGameFolderAsIs()
        {
            var w = Make();
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            var st = inst.Inspect();
            Assert.Equal(Overall.Installed, st.Overall);
            Assert.False(st.MenuIniExists);
            Assert.False(st.MenuRussian);
            Assert.False(Directory.Exists(Path.Combine(w.Game, "settings")));    // лаунчер файл не выдумывает
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
        }

        [Fact]
        public void Update_KeepsMenuBackupFromFirstInstall()
        {
            var w = Make("t-1");
            var ini = WriteIni(w, ZeroIni.Replace("SystemLanguage=\t", "SystemLanguage=US\t"));
            w.Installer().Install(NoProgress, CancellationToken.None);
            Assert.Equal("UK", MenuLanguage.Read(ini));
            var w2Pack = Path.Combine(Path.GetDirectoryName(w.PackDir)!, "pack2");
            var e2 = w.Entries.Select(e => new Synthetic.PackEntry { Arc = e.Arc, Path = e.Path, Src = e.Src, Png = e.Png, Out = e.Out.Concat(new byte[e.Png ? 0 : 7]).ToArray() }).ToList();
            Synthetic.WritePack(w2Pack, "t-2", new Dictionary<string, byte[]> { ["a.arc"] = w.OrigA, ["d.arc"] = w.OrigD }, e2);
            var inst2 = w.Installer(w2Pack);
            inst2.Install(NoProgress, CancellationToken.None);                // обновление: UK уже стоит, бэкап — прежний (US)
            Assert.Equal("UK", MenuLanguage.Read(ini));
            inst2.Remove(NoProgress, CancellationToken.None);
            Assert.Equal("US", MenuLanguage.Read(ini));
        }

        [Fact]
        public void Remove_LeavesMenuAlone_IfPlayerChangedIt()
        {
            var w = Make();
            var ini = WriteIni(w, ZeroIni);
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            MenuLanguage.Write(ini, "DE");                                    // игрок сам поставил немецкий
            inst.Remove(NoProgress, CancellationToken.None);
            Assert.Equal("DE", MenuLanguage.Read(ini));
            AssertOriginal(w);
        }

        private string MenuBackupFile(World w) => Path.Combine(w.Backup, "setting.ini.backup.json");

        /// <summary>Свежая установка: игру не запускали, setting.ini нет. Первый запуск создаёт его с пустым
        /// языком (или ключа нет / стоит US) — при открытии лаунчер включает UK, «Удалить» возвращает как было.</summary>
        [Theory]
        [InlineData("SystemLanguage=\t\t;JP US UK DE FR IT ES\r\n")]
        [InlineData("SystemLanguage=US\t\t;JP US UK DE FR IT ES\r\n")]
        [InlineData("")]
        public void Open_AfterFirstGameRun_SetsUk_RemoveRestores(string langLine)
        {
            var w = Make();
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            Assert.Equal(TranslationInstaller.MenuNoIniText, inst.MenuMessage);         // подсказка в окне после установки
            Assert.False(File.Exists(MenuBackupFile(w)));

            var text = "Resolution=1280x720\t;3840x2160 1920x1080 1280x720\r\n" + langLine + "SkipLauncher=False\t\t;True False\r\n";
            var ini = WriteIni(w, text);                                                // игра запущена один раз
            var st = inst.Inspect();
            Assert.False(st.MenuRussian);
            Assert.Equal(MenuFix.Fixed, inst.EnsureMenuRussian(st));
            Assert.True(st.MenuRussian);                                                // состояние обновлено
            Assert.Equal("UK", MenuLanguage.Read(ini));
            Assert.True(File.Exists(MenuBackupFile(w)));                                // бэкап создан сейчас
            Assert.Equal(MenuFix.None, inst.EnsureMenuRussian(inst.Inspect()));         // второй раз — делать нечего

            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
            Assert.Equal(text, File.ReadAllText(ini));                                  // байт в байт как создала игра
            Assert.False(File.Exists(MenuBackupFile(w)));
        }

        /// <summary>Язык сбросили после установки (лаунчер Bandai, игрок): UK снова, бэкап — прежний, с первой установки.</summary>
        [Fact]
        public void Open_LanguageReset_SetsUk_KeepsExistingBackup()
        {
            var w = Make();
            var ini = WriteIni(w, ZeroIni.Replace("SystemLanguage=\t", "SystemLanguage=DE\t"));
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            Assert.Null(inst.MenuMessage);
            var bk0 = File.ReadAllBytes(MenuBackupFile(w));
            MenuLanguage.Write(ini, "");                                                // сбросили на US
            Assert.Equal(MenuFix.Fixed, inst.EnsureMenuRussian(inst.Inspect()));
            Assert.Equal("UK", MenuLanguage.Read(ini));
            Assert.Equal(bk0, File.ReadAllBytes(MenuBackupFile(w)));                    // бэкап не тронут
            inst.Remove(NoProgress, CancellationToken.None);
            Assert.Equal("DE", MenuLanguage.Read(ini));                                 // то, что было до первой установки
        }

        [Fact]
        public void Open_NotInstalled_LeavesIniAlone()
        {
            var w = Make();
            var ini = WriteIni(w, ZeroIni);
            var inst = w.Installer();
            var st = inst.Inspect();
            Assert.Equal(Overall.NotInstalled, st.Overall);
            Assert.Equal(MenuFix.None, inst.EnsureMenuRussian(st));
            Assert.Equal(ZeroIni, File.ReadAllText(ini));
            Assert.False(Directory.Exists(w.Backup) && File.Exists(MenuBackupFile(w)));
        }

        [Fact]
        public void Open_InstalledWithoutIni_DoesNotCreateFile_Hints()
        {
            var w = Make();
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            var st = inst.Inspect();
            Assert.Equal(MenuFix.NoIni, inst.EnsureMenuRussian(st));
            Assert.False(Directory.Exists(Path.Combine(w.Game, "settings")));
            Assert.False(File.Exists(MenuBackupFile(w)));
        }

        [Fact]
        public void Open_GameRunning_DoesNotWrite()
        {
            var w = Make();
            var inst = w.Installer();
            inst.Install(NoProgress, CancellationToken.None);
            var ini = WriteIni(w, ZeroIni);
            inst.IsGameRunning = () => true;
            Assert.Equal(MenuFix.GameRunning, inst.EnsureMenuRussian(inst.Inspect()));
            Assert.Equal(ZeroIni, File.ReadAllText(ini));
        }

        [Fact]
        public void PackFromZip_Works()
        {
            var w = Make();
            var zip = w.PackDir + ".zip";
            System.IO.Compression.ZipFile.CreateFromDirectory(w.PackDir, zip);
            var inst = new TranslationInstaller(RuPack.OpenPath(zip), w.Game, w.Backup) { IsGameRunning = () => false };
            inst.Install(NoProgress, CancellationToken.None);
            AssertInstalledContent(w);
            inst.Remove(NoProgress, CancellationToken.None);
            AssertOriginal(w);
        }
    }
}
