using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Arc;
using ACZRuLauncher.Core.Pack;

namespace ACZRuLauncher
{
    /// <summary>Прогоны без окна: установщик на копии ACZero и контур самообновления.</summary>
    internal static class SelfTest
    {
        public enum Mode { Full, InstallOnly, RemoveOnly }

        private static string? Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1].Trim('"', '\'');
            return null;
        }

        /// <summary>
        /// --selftest &lt;ACZero&gt; [--pack p] [--backup-dir b] [--log f]: состояние → установить → проверить (контрольная сумма,
        /// каждый новый файл = out_sha256 / raw_out_sha256, остальные записи не тронуты) → удалить → sha256 = оригинал.
        /// Только на копиях: в настоящую игру этим ключом не ходить.
        /// </summary>
        public static int Run(string[] args, string gameRoot, Mode mode)
        {
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Arg(args, "--log") ?? Path.Combine(Path.GetDirectoryName(exe) ?? ".", "ACZRuLauncher.selftest.log");
            var sb = new StringBuilder();
            int failed = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
            var t0 = DateTime.Now;
            try
            {
                var game = GameInstall.FromAnyPath(gameRoot) ?? throw new Exception("нет a.arc/d.arc в " + gameRoot);
                sb.AppendLine($"game: {game.Root} ({game.KindText})");
                using var pack = RuPack.TryOpen(exe, Arg(args, "--pack")) ?? throw new Exception("нет пакета (--pack)");
                sb.AppendLine($"pack: {pack.Source} v{pack.Manifest.Version} id={pack.Id.Substring(0, 12)} " +
                              string.Join(" ", pack.Manifest.Archives.Select(k => $"{k.Key}:{k.Value.Files.Count}")));
                var inst = new TranslationInstaller(pack, game.Root, Arg(args, "--backup-dir")) { Module = ExeModule.ForGame(game) };
                sb.AppendLine("backup: " + inst.BackupDir);
                var moduleDll = Path.Combine(game.Root, ExeModule.FileName);
                string? ModuleSha() => File.Exists(moduleDll) ? Hashing.Sha256(File.ReadAllBytes(moduleDll)) : null;
                var moduleSha0 = ModuleSha();
                var features = pack.Manifest.Features ?? new System.Collections.Generic.List<string>();
                sb.AppendLine("module: " + (inst.Module == null ? "нет в этой сборке" : "sha256 " + inst.Module.Sha256) +
                              "; pack features: " + (features.Count == 0 ? "нет" : string.Join(",", features)) +
                              "; " + ExeModule.FileName + " рядом с игрой: " + (moduleSha0 ?? "нет"));
                var progress = new Progress<InstallProgress>(_ => { });
                var st0 = inst.Inspect();
                sb.AppendLine("before: " + st0.Overall + " — " + st0.Describe());
                var ini = MenuLanguage.IniPath(game.Root);
                string? IniSha() => File.Exists(ini) ? Hashing.Sha256(File.ReadAllBytes(ini)) : null;
                var iniSha0 = IniSha();
                sb.AppendLine("setting.ini: " + (iniSha0 ?? "нет"));

                if (mode != Mode.RemoveOnly)
                {
                    inst.Install(progress, CancellationToken.None);
                    var st1 = inst.Inspect();
                    sb.AppendLine("after install: " + st1.Overall + " — " + st1.Describe());
                    Check(st1.Overall == Overall.Installed, "installed");
                    foreach (var kv in pack.Manifest.Archives)
                    {
                        var path = Path.Combine(game.Root, kv.Key);
                        var scan = Hashing.Scan(path);
                        using var arc = new AczArchive(path);
                        Check(scan.ChecksumCalc == arc.ChecksumStored(), $"{kv.Key}: archive checksum ok");
                        int bad = 0;
                        foreach (var f in kv.Value.Files)
                        {
                            var d = arc.Extract(f.Path);
                            var ok = f.IsPng ? Hashing.Sha256(PngPixels.Decode(d).Raw) == f.RawOutSha256 : Hashing.Sha256(d) == f.OutSha256;
                            if (!ok) { bad++; sb.AppendLine("  bad: " + f.Path); }
                        }
                        Check(bad == 0, $"{kv.Key}: {kv.Value.Files.Count} new files read back = pack");
                        Check(arc.Entries.Count(e => e.Offset >= kv.Value.OrigSize - AczArchive.Hdr - AczArchive.Trail) == kv.Value.Files.Count,
                              $"{kv.Key}: only pack files point past the original end");
                    }
                    if (inst.Module != null)
                    {
                        sb.AppendLine("module state: " + st1.Module + (inst.ModuleMessage != null ? " — " + inst.ModuleMessage : ""));
                        bool want = game.Kind == GameKind.SteamZero && features.Contains(ExeModule.FontFeature) && ExeModule.FindAntiCheat(game.Root) == null;
                        bool foreign = moduleSha0 != null && moduleSha0 != inst.Module.Sha256 && st0.Module != ModuleState.Outdated;
                        if (want && !foreign) Check(st1.Module == ModuleState.Installed && ModuleSha() == inst.Module.Sha256, "module: version.dll next to acz.exe = ours");
                        else Check(ModuleSha() == moduleSha0, "module: not installed here, " + ExeModule.FileName + " untouched");
                    }
                    if (st1.MenuIniExists)
                        Check(st1.MenuRussian, $"menu language: SystemLanguage={st1.MenuLanguageValue ?? "(нет ключа)"} (ждали UK)");
                    else
                    {
                        // игру на этой копии ни разу не запускали: setting.ini создаёт сама игра, лаунчер его не выдумывает
                        sb.AppendLine("menu language: SKIP — нет settings\\setting.ini (игру не запускали), язык меню не проверить; " +
                                      "окно: " + (inst.MenuMessage ?? "(без подсказки)"));
                        Check(!File.Exists(ini), "menu language: setting.ini не создан лаунчером");
                        Check(inst.MenuMessage == TranslationInstaller.MenuNoIniText, "menu language: подсказка «запустите игру один раз» в окне");
                    }
                }
                if (mode != Mode.InstallOnly)
                {
                    inst.Remove(progress, CancellationToken.None);
                    foreach (var kv in pack.Manifest.Archives)
                    {
                        var path = Path.Combine(game.Root, kv.Key);
                        Check(new FileInfo(path).Length == kv.Value.OrigSize && Hashing.Sha256File(path) == kv.Value.OrigSha256, $"{kv.Key}: back to original sha256");
                    }
                    if (inst.Module != null)
                    {
                        bool foreign0 = moduleSha0 != null && moduleSha0 != inst.Module.Sha256 && st0.Module != ModuleState.Outdated;
                        if (foreign0) Check(ModuleSha() == moduleSha0, "module: foreign " + ExeModule.FileName + " untouched by removal");
                        else Check(ModuleSha() == null, "module: " + ExeModule.FileName + " removed");
                    }
                    var st2 = inst.Inspect();
                    Check(st2.Overall == Overall.NotInstalled, "inspect after removal: not installed");
                    sb.AppendLine("after removal: " + st2.Describe() + "; setting.ini: " + (IniSha() ?? "нет"));
                    if (mode == Mode.Full && iniSha0 != null)
                        Check(IniSha() == iniSha0, "setting.ini: byte for byte as before install");
                }
            }
            catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); failed++; }
            sb.AppendLine($"time {(DateTime.Now - t0).TotalSeconds:0.0}s");
            sb.AppendLine(failed == 0 ? "RESULT OK" : $"RESULT FAILED ({failed})");
            File.WriteAllText(logPath, sb.ToString());
            return failed == 0 ? 0 : 2;
        }

        private static string ChannelLine()
        {
            var ch = UpdateChannel.Current;
            return "channel " + (ch.IsDev ? "dev" : "release") + " feed " + (ch.FeedFor(null) ?? "(не задана)") + " page " + ch.ReleasesPageUrl;
        }

        /// <summary>--selftest-channel [--log f]: канал и лента этой сборки в лог (ACZRuLauncher.channel.log рядом с exe), без сети.</summary>
        public static int RunChannel(string[] args)
        {
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Arg(args, "--log") ?? Path.Combine(Path.GetDirectoryName(exe) ?? ".", "ACZRuLauncher.channel.log");
            File.WriteAllText(logPath, "version " + Updates.CurrentVersion + Environment.NewLine + ChannelLine() + Environment.NewLine);
            return 0;
        }

        /// <summary>Контур обновления без окна: latest.json → сравнение версий → exe + sha256 → (при --swap) подмена и запуск нового.</summary>
        public static int RunUpdate(string[] args, int urlIndex)
        {
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "ACZRuLauncher.update.log");
            var sb = new StringBuilder();
            int failed = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
            try
            {
                var url = args[urlIndex].Trim('"', '\'');
                bool swap = false; var passthrough = new StringBuilder();
                for (int i = urlIndex + 1; i < args.Length; i++)
                {
                    if (args[i] == "--swap") { swap = true; continue; }
                    if (swap) passthrough.Append(' ').Append(args[i].Contains(" ") ? "\"" + args[i] + "\"" : args[i]);
                }
                sb.AppendLine("current " + Updates.CurrentVersion + " exe " + exe);
                sb.AppendLine(ChannelLine());
                var latest = Updates.FetchLatestAsync(url, CancellationToken.None).GetAwaiter().GetResult();
                sb.AppendLine(latest == null ? "latest: no acz-v* release" : $"latest tag={latest.Tag} version={latest.Version} assets={latest.Assets.Count}");
                bool newer = latest?.Version != null && latest.Version > Updates.CurrentVersion;
                sb.AppendLine("update available: " + newer);
                if (latest == null || !newer || latest.Exe == null) { sb.AppendLine("RESULT NO-UPDATE"); File.WriteAllText(logPath, sb.ToString()); return failed == 0 ? 0 : 2; }

                var staged = Path.Combine(SelfUpdate.StagingDir, $"ACZRuLauncher-{latest.Version}.exe");
                Updates.DownloadAsync(latest.Exe.Url, staged, latest.Exe.Size, new Progress<double>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
                Check(File.Exists(staged) && new FileInfo(staged).Length == latest.Exe.Size, "downloaded size matches asset size");
                if (latest.ExeSha != null)
                {
                    var expected = Updates.ParseShaText(Updates.FetchTextAsync(latest.ExeSha.Url, CancellationToken.None).GetAwaiter().GetResult());
                    Check(expected != null && expected == Updates.Sha256File(staged), "sha256 matches published");
                }
                else sb.AppendLine("no sha256 asset (skipped)");
                if (swap && failed == 0)
                {
                    SelfUpdate.SwapAndStart(exe, staged, passthrough.ToString().Trim());
                    sb.AppendLine("swapped; new exe started with: " + passthrough.ToString().Trim());
                }
            }
            catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); failed++; }
            sb.AppendLine(failed == 0 ? "RESULT OK" : $"RESULT FAILED ({failed})");
            File.WriteAllText(logPath, sb.ToString());
            return failed == 0 ? 0 : 2;
        }

    }
}
