using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ACZRuLauncher.Core
{
    public enum ModuleState
    {
        /// <summary>В этой сборке лаунчера модуля нет (не собран).</summary>
        NotBuilt,
        /// <summary>Не отдельная Zero из Steam (Zero из AC8, ручная папка) — модуль не ставится.</summary>
        Unsupported,
        /// <summary>Рядом с игрой античит — модуль не ставится.</summary>
        AntiCheat,
        /// <summary>В пакете перевода нет кириллицы в шрифте HUD (h8) — русские метки нечем рисовать.</summary>
        NoFont,
        /// <summary>version.dll рядом с игрой чужой (ReShade, другой мод) — не трогаем.</summary>
        Conflict,
        NotInstalled,
        /// <summary>Стоит наш модуль этой сборки лаунчера.</summary>
        Installed,
        /// <summary>Стоит наш модуль из другой сборки лаунчера (есть в нашем списке) — обновится при установке.</summary>
        Outdated,
    }

    /// <summary>Что лаунчер положил в папку игры: имя и sha256 (список в папке бэкапа, не в папке игры).</summary>
    [DataContract]
    internal sealed class ModuleRecord
    {
        [DataMember(Name = "file")] public string File = "";
        [DataMember(Name = "sha256")] public List<string> Sha256 = new List<string>();
        [DataMember(Name = "time")] public string Time = "";
    }

    /// <summary>
    /// Модуль перевода текста из acz.exe (module\ в репозитории): прокси version.dll, кладётся рядом с acz.exe.
    /// Он меняет в памяти игры только строки секции .rdata (экран итогов, метки целей); exe на диске не меняется.
    /// Ставится только в отдельную Zero из Steam, только без античита рядом и только если в пакете есть кириллица
    /// в шрифте HUD (фича <see cref="FontFeature"/>). Чужой version.dll не перезаписывается, удаляется только наш
    /// (sha256 = нашему или записанному в списке при установке).
    /// </summary>
    public sealed class ExeModule
    {
        public const string FileName = "version.dll";
        /// <summary>Фича пакета: в huddataN.fhm/000.png есть кириллица в таблице h8 (коды a..z).</summary>
        public const string FontFeature = "hud-font-h8-cyr";
        public const string ResourceName = "ACZRuLauncher.Core.module.version.dll";

        private static readonly string[] AntiCheatPatterns =
            { "EasyAntiCheat*", "start_protected_game*", "BattlEye*", "BEService*", "vgk*", "nProtect*", "GameGuard*", "xigncode*", "EOSBootstrapper*" };

        public byte[] Dll { get; }
        public string Sha256 { get; }
        public GameKind Kind { get; }

        public ExeModule(byte[] dll, GameKind kind)
        {
            Dll = dll;
            Sha256 = Hashing.Sha256(dll);
            Kind = kind;
        }

        /// <summary>Модуль, вшитый в лаунчер при сборке (module\build\version.dll); null — в этой сборке его нет.</summary>
        public static byte[]? Embedded()
        {
            using var s = typeof(ExeModule).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) return null;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Для окна: модуль под эту игру или null, если его нет в сборке.</summary>
        public static ExeModule? ForGame(GameInstall game)
        {
            var dll = Embedded();
            return dll == null ? null : new ExeModule(dll, game.Kind);
        }

        /// <summary>Найденный рядом с игрой (или папкой выше — так лежит Zero в AC8) античит; null — нет.</summary>
        public static string? FindAntiCheat(string gameDir)
        {
            var dirs = new List<string> { gameDir };
            var up = Path.GetDirectoryName(Path.GetFullPath(gameDir).TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(up)) dirs.Add(up!);
            foreach (var d in dirs)
                foreach (var p in AntiCheatPatterns)
                {
                    try
                    {
                        var hit = Directory.EnumerateFileSystemEntries(d, p).FirstOrDefault();
                        if (hit != null) return Path.GetFileName(hit);
                    }
                    catch { }
                }
            return null;
        }

        private static string RecordPath(string backupDir) => Path.Combine(backupDir, "module.json");
        private static string Target(string gameDir) => Path.Combine(gameDir, FileName);

        public ModuleState Inspect(string gameDir, string backupDir, IList<string>? packFeatures)
        {
            var t = Target(gameDir);
            var rec = LoadRecord(backupDir);
            if (File.Exists(t))
            {
                var sha = Hashing.Sha256(File.ReadAllBytes(t));
                if (sha == Sha256) return ModuleState.Installed;
                if (rec != null && rec.Sha256.Contains(sha)) return ModuleState.Outdated;
            }
            if (Kind != GameKind.SteamZero) return ModuleState.Unsupported;
            if (FindAntiCheat(gameDir) != null) return ModuleState.AntiCheat;
            if (packFeatures == null || !packFeatures.Contains(FontFeature)) return ModuleState.NoFont;
            return File.Exists(t) ? ModuleState.Conflict : ModuleState.NotInstalled;
        }

        /// <summary>Поставить или обновить. Возвращает состояние после; текст для игрока — в <paramref name="message"/>
        /// (null — сказать нечего). Ошибки записи бросаются: перевод при этом уже стоит, окно покажет текст.</summary>
        public ModuleState Install(string gameDir, string backupDir, IList<string>? packFeatures, Action<string> log, out string? message)
        {
            message = null;
            var st = Inspect(gameDir, backupDir, packFeatures);
            var t = Target(gameDir);
            switch (st)
            {
                case ModuleState.Installed:
                    log("модуль: уже стоит (sha256 " + Sha256.Substring(0, 12) + ")");
                    SaveRecord(backupDir, Sha256);
                    return st;
                case ModuleState.Unsupported:
                    log("модуль: не отдельная Zero из Steam — не ставится");
                    return st;
                case ModuleState.AntiCheat:
                    log("модуль: рядом с игрой античит (" + FindAntiCheat(gameDir) + ") — не ставится");
                    message = "Русские надписи из acz.exe не поставлены: рядом с игрой найден античит. Остальной перевод установлен.";
                    return st;
                case ModuleState.NoFont:
                    log("модуль: в пакете нет кириллицы в шрифте HUD (" + FontFeature + ") — не ставится");
                    return st;
                case ModuleState.Conflict:
                    log("модуль: рядом с acz.exe уже есть чужой " + FileName + " — не трогаю");
                    message = $"Русские надписи из acz.exe (метки целей) не поставлены: рядом с игрой уже лежит {FileName} " +
                              "другой программы (например, ReShade или другого мода). Мы его не трогаем. Остальной перевод установлен.";
                    return st;
                case ModuleState.Outdated:
                case ModuleState.NotInstalled:
                    break;
                default:
                    return st;
            }
            if (st == ModuleState.Outdated && (Kind != GameKind.SteamZero || FindAntiCheat(gameDir) != null ||
                                               packFeatures == null || !packFeatures.Contains(FontFeature)))
            {
                // старый наш модуль там, где новый ставить нельзя — убрать
                File.Delete(t);
                log("модуль: старая версия удалена, новая сюда не ставится");
                RemoveRecord(backupDir);
                return Inspect(gameDir, backupDir, packFeatures);
            }
            Directory.CreateDirectory(backupDir);
            SaveRecord(backupDir, Sha256);                     // в список — до записи: оборванную запись тоже узнаем как свою
            var tmp = t + ".acz-tmp";
            File.WriteAllBytes(tmp, Dll);
            if (Hashing.Sha256(File.ReadAllBytes(tmp)) != Sha256) { File.Delete(tmp); throw new IOException("модуль записан с ошибкой"); }
            if (File.Exists(t)) File.Delete(t);                 // только наш (Outdated): чужой сюда не доходит
            File.Move(tmp, t);
            log($"модуль: {FileName} {(st == ModuleState.Outdated ? "обновлён" : "поставлен")}, sha256 {Sha256}");
            return ModuleState.Installed;
        }

        /// <summary>Удалить наш модуль: только если sha256 = нашему или записанному при установке. Чужой файл не трогаем.</summary>
        public bool Remove(string gameDir, string backupDir, Action<string> log)
        {
            var t = Target(gameDir);
            var rec = LoadRecord(backupDir);
            TryDelete(t + ".acz-tmp");
            if (!File.Exists(t)) { RemoveRecord(backupDir); return true; }
            var sha = Hashing.Sha256(File.ReadAllBytes(t));
            if (sha == Sha256 || (rec != null && rec.Sha256.Contains(sha)))
            {
                File.Delete(t);
                RemoveRecord(backupDir);
                log("модуль: " + FileName + " удалён");
                return true;
            }
            log("модуль: " + FileName + " рядом с игрой не наш (sha256 " + sha.Substring(0, 12) + ") — не трогаю");
            RemoveRecord(backupDir);
            return false;
        }

        private static ModuleRecord? LoadRecord(string backupDir)
        {
            try
            {
                var p = RecordPath(backupDir);
                if (!File.Exists(p)) return null;
                using var f = File.OpenRead(p);
                return new DataContractJsonSerializer(typeof(ModuleRecord)).ReadObject(f) as ModuleRecord;
            }
            catch { return null; }
        }

        private static void SaveRecord(string backupDir, string sha)
        {
            Directory.CreateDirectory(backupDir);
            var rec = LoadRecord(backupDir) ?? new ModuleRecord { File = FileName };
            if (!rec.Sha256.Contains(sha)) rec.Sha256.Add(sha);
            rec.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var p = RecordPath(backupDir);
            using var f = new FileStream(p, FileMode.Create, FileAccess.Write);
            new DataContractJsonSerializer(typeof(ModuleRecord)).WriteObject(f, rec);
        }

        private static void RemoveRecord(string backupDir) => TryDelete(RecordPath(backupDir));

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}
