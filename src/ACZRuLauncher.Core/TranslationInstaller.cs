using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ACZRuLauncher.Core.Arc;
using ACZRuLauncher.Core.Pack;

namespace ACZRuLauncher.Core
{
    public readonly struct InstallProgress
    {
        public InstallProgress(string status, double fraction) { Status = status; Fraction = fraction; }
        public string Status { get; }
        public double Fraction { get; }
    }

    /// <summary>Состояние одного архива (как в arcinstall.py: ORIGINAL / PATCHED / INTERRUPTED / UNKNOWN / MISSING).</summary>
    public enum ArcState
    {
        Missing,
        /// <summary>sha256 = оригинал из пакета.</summary>
        Original,
        /// <summary>Стоит наш перевод именно из этого пакета.</summary>
        Installed,
        /// <summary>Стоит наш перевод из другой сборки пакета (лаунчер обновился) — переставить.</summary>
        InstalledOther,
        /// <summary>Ни то ни другое, но откат областей из бэкапа даёт ровно оригинал (проверено без записи).</summary>
        Interrupted,
        /// <summary>Другая версия игры, другой мод или повреждение — не трогаем.</summary>
        Foreign,
    }

    public sealed class ArchiveStatus
    {
        public string Name = "";
        public string Path = "";
        public ArcState State;
        public string? Sha256;
        public long Size;
        public override string ToString() => $"{Name}: {State} ({Size} б, {Sha256})";
    }

    /// <summary>Итог починки языка меню (EnsureMenuRussian): None — делать нечего (перевод не наш или уже UK).</summary>
    public enum MenuFix { None, Fixed, NoIni, GameRunning, Failed }

    public enum Overall { NotInstalled, Installed, NeedsUpdate, NeedsRepair, Foreign, Missing }

    public sealed class InstallStatus
    {
        public List<ArchiveStatus> Archives = new List<ArchiveStatus>();
        /// <summary>Есть ли settings\setting.ini (его создаёт игра) и что в SystemLanguage (null — ключа нет).</summary>
        public bool MenuIniExists;
        public string? MenuLanguageValue;
        /// <summary>Модуль acz.exe (version.dll); null — в лаунчере его нет.</summary>
        public ModuleState? Module;
        /// <summary>Меню игры русское: SystemLanguage=UK (русский стоит на месте UK).</summary>
        public bool MenuRussian => MenuLanguage.IsRussian(MenuLanguageValue);
        public Overall Overall
        {
            get
            {
                if (Archives.Any(a => a.State == ArcState.Missing)) return Overall.Missing;
                if (Archives.Any(a => a.State == ArcState.Foreign)) return Overall.Foreign;
                if (Archives.All(a => a.State == ArcState.Original)) return Overall.NotInstalled;
                if (Archives.All(a => a.State == ArcState.Installed)) return Overall.Installed;
                if (Archives.Any(a => a.State == ArcState.Interrupted)) return Overall.NeedsRepair;
                if (Archives.All(a => a.State == ArcState.Installed || a.State == ArcState.InstalledOther)) return Overall.NeedsUpdate;
                return Overall.NeedsRepair;                          // часть стоит, часть нет
            }
        }
        public string Describe() => string.Join("; ", Archives.Select(a => a.ToString())) +
                                    "; меню: " + (!MenuIniExists ? "нет settings\\setting.ini" :
                                                  MenuLanguageValue == null ? "нет SystemLanguage" : "SystemLanguage=" + MenuLanguageValue) +
                                    "; модуль: " + (Module?.ToString() ?? "нет в сборке");
    }

    /// <summary>Что было в SystemLanguage до первой установки (settings\setting.ini) — вернуть при удалении.</summary>
    [DataContract]
    internal sealed class MenuBackup
    {
        [DataMember(Name = "had_key")] public bool HadKey;
        [DataMember(Name = "value")] public string Value = "";
        [DataMember(Name = "file_sha256")] public string FileSha256 = "";
        [DataMember(Name = "time")] public string Time = "";
    }

    [DataContract]
    internal sealed class BackupInfo
    {
        [DataMember(Name = "target")] public string Target = "";
        [DataMember(Name = "orig_size")] public long OrigSize;
        [DataMember(Name = "orig_sha256")] public string OrigSha256 = "";
        [DataMember(Name = "table_file_off")] public long TableFileOff;
        [DataMember(Name = "table_len")] public int TableLen;
        [DataMember(Name = "table_sha256")] public string TableSha256 = "";
        [DataMember(Name = "trailer_hex")] public string TrailerHex = "";
    }

    [DataContract]
    internal sealed class InstalledInfo
    {
        [DataMember(Name = "pack_id")] public string PackId = "";
        [DataMember(Name = "pack_version")] public string PackVersion = "";
        [DataMember(Name = "patched_size")] public long PatchedSize;
        [DataMember(Name = "patched_sha256")] public string PatchedSha256 = "";
        [DataMember(Name = "launcher")] public string Launcher = "";
        [DataMember(Name = "time")] public string Time = "";
    }

    /// <summary>
    /// Установка перевода в a.arc / d.arc. Логика — как apply_all.py / restore_all.py (arcinstall.py):
    /// сначала бэкап областей всех архивов, потом запись; проверка после каждой записи; если хоть что-то
    /// не так — все архивы пакета возвращаются к оригиналу («оба или ни одного»).
    /// Бэкап областей (таблица, 8 байт хвоста, исходная длина) лежит в %LOCALAPPDATA%\ACZRuLauncher\backup\…,
    /// не в папке игры: лаунчер пишет только в сами архивы.
    /// </summary>
    public sealed class TranslationInstaller
    {
        public const string GameProcessName = "acz";

        private readonly RuPack _pack;
        public string GameDir { get; }
        public string BackupDir { get; }
        /// <summary>Проверка «игра запущена» (в тестах подменяется).</summary>
        public Func<bool> IsGameRunning { get; set; } = DefaultIsGameRunning;
        /// <summary>Модуль перевода строк acz.exe (version.dll рядом с игрой); null — не ставить.</summary>
        public ExeModule? Module { get; set; }
        /// <summary>Что сказать игроку про модуль после установки (чужой version.dll, античит); null — нечего.</summary>
        public string? ModuleMessage { get; private set; }
        /// <summary>Только для тестов: вызывается после записи таблицы, до записи хвоста (имитация обрыва).</summary>
        internal Action<string>? AfterTableWritten { get; set; }

        public TranslationInstaller(RuPack pack, string gameDir, string? backupDir = null)
        {
            _pack = pack;
            GameDir = gameDir;
            BackupDir = backupDir ?? DefaultBackupDir(gameDir);
        }

        public static string DefaultBackupDir(string gameDir)
        {
            var full = System.IO.Path.GetFullPath(gameDir).TrimEnd('\\', '/').ToLowerInvariant();
            var id = Hashing.Sha256(Encoding.UTF8.GetBytes(full)).Substring(0, 16);
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACZRuLauncher", "backup", id);
        }

        public static bool DefaultIsGameRunning()
        {
            try { return Process.GetProcessesByName(GameProcessName).Length > 0; } catch { return false; }
        }

        private IEnumerable<KeyValuePair<string, PackArchive>> PackArchives =>
            _pack.Manifest.Archives.OrderBy(k => k.Key, StringComparer.Ordinal);

        private string Target(string name) => System.IO.Path.Combine(GameDir, name);
        private string Bpath(string name, string what) => System.IO.Path.Combine(BackupDir, name + "." + what);

        // ================================================================ состояние
        public Task<InstallStatus> InspectAsync(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
            => Task.Run(() => Inspect(progress, ct), ct);

        public InstallStatus Inspect(IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        {
            var st = new InstallStatus();
            var list = PackArchives.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = list[i].Key;
                int k = i;
                var p = progress == null ? null : new Progress<double>(f => progress.Report(new InstallProgress($"Проверяю {name}…", (k + f) / list.Count)));
                st.Archives.Add(InspectOne(name, list[i].Value, p));
            }
            try
            {
                st.MenuIniExists = File.Exists(MenuIni);
                st.MenuLanguageValue = MenuLanguage.Read(MenuIni);
            }
            catch (Exception ex) { Log("язык меню: не прочитать settings\\setting.ini: " + ex.Message); }
            try { if (Module != null) st.Module = Module.Inspect(GameDir, BackupDir, _pack.Manifest.Features); }
            catch (Exception ex) { Log("модуль: не проверить: " + ex.Message); }
            return st;
        }

        private ArchiveStatus InspectOne(string name, PackArchive spec, IProgress<double>? progress)
        {
            var path = Target(name);
            var s = new ArchiveStatus { Name = name, Path = path };
            if (!File.Exists(path)) { s.State = ArcState.Missing; return s; }
            var scan = ShaCache.Get(path, progress);
            s.Sha256 = scan.Sha256; s.Size = scan.Size;
            if (scan.Size == spec.OrigSize && scan.Sha256 == spec.OrigSha256) { s.State = ArcState.Original; return s; }
            var bk = LoadBackup(name, spec);
            var inst = Load<InstalledInfo>(Bpath(name, "installed.json"));
            if (bk != null && inst != null && inst.PatchedSha256 == scan.Sha256 && inst.PatchedSize == scan.Size)
            {
                s.State = inst.PackId == _pack.Id ? ArcState.Installed : ArcState.InstalledOther;
                return s;
            }
            if (bk != null && ShaIfRolledBack(path, bk) == spec.OrigSha256) { s.State = ArcState.Interrupted; return s; }
            s.State = ArcState.Foreign;
            return s;
        }

        /// <summary>Бэкап областей годен: файлы есть, sha таблицы и длина хвоста совпадают с записанными, оригинал тот же.</summary>
        private BackupInfo? LoadBackup(string name, PackArchive spec)
        {
            var bk = Load<BackupInfo>(Bpath(name, "backup.json"));
            if (bk == null || bk.OrigSha256 != spec.OrigSha256 || bk.OrigSize != spec.OrigSize) return null;
            try
            {
                var table = File.ReadAllBytes(Bpath(name, "table.bin"));
                var trailer = File.ReadAllBytes(Bpath(name, "trailer.bin"));
                if (table.Length != bk.TableLen || Hashing.Sha256(table) != bk.TableSha256) return null;
                if (Hashing.Hex(trailer) != bk.TrailerHex || trailer.Length != AczArchive.Trail) return null;
                return bk;
            }
            catch { return null; }
        }

        /// <summary>sha256, который был бы у файла после отката областей, — без записи (sha_if_rolled_back из apply_a.py).
        /// Равен оригиналу — откат гарантированно вернёт оригинал, в том числе после оборванной установки.</summary>
        private string? ShaIfRolledBack(string path, BackupInfo bk)
        {
            byte[] table, trailer;
            try
            {
                table = File.ReadAllBytes(Bpath(System.IO.Path.GetFileName(path), "table.bin"));
                trailer = File.ReadAllBytes(Bpath(System.IO.Path.GetFileName(path), "trailer.bin"));
            }
            catch { return null; }
            long t0 = bk.TableFileOff, t1 = bk.TableFileOff + bk.TableLen, end = bk.OrigSize - AczArchive.Trail;
            if (table.Length != bk.TableLen) return null;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (f.Length < end) return null;
            using var h = Hashing.NewSha256();
            var buf = new byte[1 << 22];
            bool Feed(long n)
            {
                while (n > 0)
                {
                    int r = f.Read(buf, 0, (int)Math.Min(buf.Length, n));
                    if (r <= 0) return false;
                    h.TransformBlock(buf, 0, r, null, 0); n -= r;
                }
                return true;
            }
            if (!Feed(t0)) return null;
            h.TransformBlock(table, 0, table.Length, null, 0);
            f.Seek(t1, SeekOrigin.Begin);
            if (!Feed(end - t1)) return null;
            h.TransformFinalBlock(trailer, 0, trailer.Length);
            return Hashing.Hex(h.Hash);
        }

        // ================================================================ установка
        public Task InstallAsync(IProgress<InstallProgress> progress, CancellationToken ct) => Task.Run(() => Install(progress, ct), ct);

        public void Install(IProgress<InstallProgress> progress, CancellationToken ct)
        {
            EnsureGameClosed();
            Log($"установка: пакет {_pack.Manifest.Version} ({_pack.Id.Substring(0, 12)}), игра {GameDir}");
            progress.Report(new InstallProgress("Проверяю файлы игры…", 0));
            var st = Inspect(Scale(progress, 0, 0.25), ct);
            Log("состояние: " + st.Describe());
            ThrowIfNotInstallable(st);
            if (st.Overall == Overall.Installed)
            {
                Log("уже установлено");
                InstallMenu();
                InstallModule();
                progress.Report(new InstallProgress("Готово", 1));
                return;
            }

            // 1. всё, что не оригинал (наш прошлый перевод, оборванная установка), — сначала к оригиналу
            foreach (var a in st.Archives.Where(a => a.State != ArcState.Original))
            {
                progress.Report(new InstallProgress($"Возвращаю оригинал {a.Name}…", 0.25));
                RestoreOne(a.Name, Spec(a.Name));
            }
            ct.ThrowIfCancellationRequested();

            // 2. новые файлы — в памяти, до первого байта записи; каждый сверяется с пакетом
            var patches = new List<(string Name, ArcPatch Patch, PackArchive Spec)>();
            var list = PackArchives.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var name = list[i].Key;
                var p = Prepare(name, list[i].Value, Scale(progress, 0.25 + 0.35 * i / list.Count, 0.25 + 0.35 * (i + 1) / list.Count), ct);
                patches.Add((name, p, list[i].Value));
            }

            // 3. место на диске
            long need = patches.Sum(p => (long)p.Patch.TailNew.Length) + (16L << 20);
            long free = DiskFree(GameDir);
            if (free >= 0 && free < need)
                throw new IOException($"Мало места на диске с игрой: свободно {free >> 20} МБ, нужно {need >> 20} МБ.");

            // 4. права на запись и бэкап областей всех архивов — до первой записи.
            //    «Только чтение» у файлов раздачи снимаем сами; если запись всё равно запрещена — понятная ошибка
            //    здесь, пока архивы не тронуты (24.11: иначе ложное «вернуть оригинал не удалось» про целые файлы).
            EnsureGameClosed();
            foreach (var p in patches) EnsureWritable(p.Name);
            foreach (var p in patches) WriteBackup(p.Name, p.Spec, p.Patch);
            Log("бэкап областей: " + BackupDir);

            // 5. запись и проверка; при любой ошибке — к оригиналу те архивы, до которых дошла запись
            var touched = new List<string>();
            try
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (name, patch, spec) = patches[i];
                    var sub = Scale(progress, 0.6 + 0.4 * i / patches.Count, 0.6 + 0.4 * (i + 1) / patches.Count);
                    sub.Report(new InstallProgress($"Записываю {name}…", 0));
                    touched.Add(name);
                    WritePatch(name, patch);
                    var sha = Verify(name, patch, spec, sub);
                    Save(Bpath(name, "installed.json"), new InstalledInfo
                    {
                        PackId = _pack.Id, PackVersion = _pack.Manifest.Version, PatchedSize = patch.PatchedSize, PatchedSha256 = sha,
                        Launcher = Updates.CurrentVersion.ToString(), Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                    Log($"{name}: установлено, {patch.Replaced.Count} файлов, {patch.OrigSize} → {patch.PatchedSize} б, sha256 {sha}");
                }
            }
            catch (Exception ex)
            {
                Log($"ОШИБКА установки: {ex.Message}; откат");
                var failed = new List<string>();
                foreach (var p in patches)
                {
                    if (!touched.Contains(p.Name)) continue;                         // запись до него не дошла
                    if (IsOriginal(p.Name, p.Spec)) { Log($"{p.Name}: не изменён — откат не нужен"); continue; }
                    try { RestoreOne(p.Name, p.Spec); }
                    catch (Exception rex) { failed.Add(p.Name); Log($"{p.Name}: откат не удался: {rex.Message}"); }
                }
                var why = ex is OperationCanceledException ? "Установка отменена" : "Установка не удалась: " + Friendly(ex);
                if (failed.Count == 0) throw new InstallException(why + ". Файлы игры возвращены к оригиналу.", ex);
                throw new InstallException(why + $". Вернуть оригинал {string.Join(", ", failed)} не удалось — " +
                                           "Steam → Свойства игры → Установленные файлы → «Проверить целостность файлов».", ex);
            }
            // 6. русское меню и модуль acz.exe — только когда оба архива стоят и проверены
            InstallMenu();
            InstallModule();
            progress.Report(new InstallProgress("Готово", 1));
        }

        // ================================================================ язык меню (settings\setting.ini)
        private string MenuIni => MenuLanguage.IniPath(GameDir);
        private string MenuBackupPath => System.IO.Path.Combine(BackupDir, "setting.ini.backup.json");

        /// <summary>Что сказать игроку про язык меню после установки (setting.ini ещё нет); null — нечего.</summary>
        public string? MenuMessage { get; private set; }

        /// <summary>Перевод стоит, а settings\setting.ini ещё нет (игру ни разу не запускали) — текст для окна.</summary>
        public const string MenuNoIniText = "Чтобы меню было русским: запустите игру один раз, закройте и снова откройте лаунчер.";
        /// <summary>Лаунчер при открытии нашёл язык меню не UK и включил UK — текст для окна.</summary>
        public const string MenuFixedText = "Язык меню игры был английским — включил русский (UK).";
        /// <summary>То же, но игра запущена: писать setting.ini под ней нельзя (игра перепишет его при выходе).</summary>
        public const string MenuGameRunningText = "Меню игры сейчас английское. Закройте игру и снова откройте лаунчер — он включит русский (UK).";

        /// <summary>
        /// Починка при открытии: перевод наш (Installed / NeedsUpdate), а SystemLanguage не UK — первый запуск игры
        /// создал setting.ini с пустым языком (= US), или язык сбросил лаунчер Bandai / сам игрок. Ставит UK тем же
        /// путём, что установка; бэкап прежнего значения создаётся, только если его ещё нет. Перевод не наш — None,
        /// файл не трогается. Нет setting.ini — NoIni, файл не создаётся. <paramref name="st"/> обновляется.
        /// </summary>
        public MenuFix EnsureMenuRussian(InstallStatus st)
        {
            if (st.Overall != Overall.Installed && st.Overall != Overall.NeedsUpdate) return MenuFix.None;
            if (!st.MenuIniExists) return MenuFix.NoIni;
            if (st.MenuRussian) return MenuFix.None;
            if (IsGameRunning()) { Log("язык меню: не UK, но игра запущена — не трогаю"); return MenuFix.GameRunning; }
            Log("язык меню при открытии: SystemLanguage " + (st.MenuLanguageValue == null ? "(нет ключа)" : "'" + st.MenuLanguageValue + "'") +
                ", перевод стоит — включаю UK");
            var r = SetMenuRussian();
            try { st.MenuIniExists = File.Exists(MenuIni); st.MenuLanguageValue = MenuLanguage.Read(MenuIni); } catch { }
            return r == MenuFix.Fixed && st.MenuRussian ? MenuFix.Fixed : r == MenuFix.Fixed ? MenuFix.Failed : r;
        }

        /// <summary>Русское меню: SystemLanguage=UK. Прежнее значение — в бэкап, если его там ещё нет (при обновлении
        /// и переустановке бэкап хранит то, что было до первой установки). Ошибка здесь установку не отменяет:
        /// архивы уже стоят, а меню видно в состоянии (MenuRussian).</summary>
        private MenuFix SetMenuRussian()
        {
            try
            {
                if (!File.Exists(MenuIni)) { Log("язык меню: нет settings\\setting.ini — меню английское, пока игру не запустят один раз"); return MenuFix.NoIni; }
                var cur = MenuLanguage.Read(MenuIni);
                if (Load<MenuBackup>(MenuBackupPath) == null)
                {
                    Directory.CreateDirectory(BackupDir);
                    Save(MenuBackupPath, new MenuBackup
                    {
                        HadKey = cur != null, Value = cur ?? "", FileSha256 = Hashing.Sha256(File.ReadAllBytes(MenuIni)),
                        Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                    Log($"язык меню: бэкап прежнего значения — {(cur == null ? "без ключа" : "'" + cur + "'")}");
                }
                if (MenuLanguage.IsRussian(cur)) { Log("язык меню: SystemLanguage уже UK"); return MenuFix.None; }
                MenuLanguage.Write(MenuIni, MenuLanguage.Russian);
                Log($"язык меню: SystemLanguage {(cur == null ? "(нет ключа)" : "'" + cur + "'")} → UK");
                return MenuFix.Fixed;
            }
            catch (Exception ex) { Log("язык меню: поставить UK не удалось: " + ex.Message); return MenuFix.Failed; }
        }

        /// <summary>Шаг установки: UK в setting.ini; нет файла — подсказка в MenuMessage.</summary>
        private void InstallMenu()
        {
            MenuMessage = SetMenuRussian() == MenuFix.NoIni ? MenuNoIniText : null;
        }

        /// <summary>Вернуть SystemLanguage, если его ставил лаунчер и игрок его потом не менял. Бэкап после этого удаляется.</summary>
        private void RestoreMenu()
        {
            var bk = Load<MenuBackup>(MenuBackupPath);
            if (bk == null) return;
            try
            {
                var cur = MenuLanguage.Read(MenuIni);
                if (!File.Exists(MenuIni)) Log("язык меню: settings\\setting.ini пропал — возвращать некуда");
                else if (!MenuLanguage.IsRussian(cur)) Log($"язык меню: сейчас '{cur}', не UK лаунчера — не трогаю");
                else if (bk.HadKey && MenuLanguage.IsRussian(bk.Value)) Log("язык меню: UK стоял и до установки — оставляю");
                else
                {
                    if (bk.HadKey) MenuLanguage.Write(MenuIni, bk.Value); else MenuLanguage.RemoveKey(MenuIni);
                    var same = Hashing.Sha256(File.ReadAllBytes(MenuIni)) == bk.FileSha256;
                    Log($"язык меню: возвращено {(bk.HadKey ? "SystemLanguage='" + bk.Value + "'" : "без ключа")}" +
                        (same ? ", файл байт в байт как до установки" : " (остальные строки файла с тех пор менялись)"));
                }
                TryDelete(MenuBackupPath);
            }
            catch (Exception ex) { Log("язык меню: вернуть не удалось: " + ex.Message); }
        }

        /// <summary>Модуль acz.exe: ставится после архивов. Не встал (чужой version.dll, античит, ошибка записи) —
        /// перевод всё равно стоит; игроку — понятный текст в ModuleMessage.</summary>
        private void InstallModule()
        {
            ModuleMessage = null;
            if (Module == null) return;
            try
            {
                EnsureGameClosed();
                Module.Install(GameDir, BackupDir, _pack.Manifest.Features, Log, out var msg);
                ModuleMessage = msg;
            }
            catch (Exception ex)
            {
                Log("модуль: поставить не удалось: " + ex.Message);
                ModuleMessage = "Русские надписи из acz.exe не поставлены: " + ex.Message + " Остальной перевод установлен.";
            }
        }

        private void RemoveModule()
        {
            if (Module == null) return;
            try { Module.Remove(GameDir, BackupDir, Log); }
            catch (Exception ex) { Log("модуль: удалить не удалось: " + ex.Message); throw new InstallException("Не удалось удалить " + ExeModule.FileName + ": " + ex.Message, ex); }
        }

        private PackArchive Spec(string name) => _pack.Manifest.Archives[name];

        private static void ThrowIfNotInstallable(InstallStatus st)
        {
            var miss = st.Archives.Where(a => a.State == ArcState.Missing).Select(a => a.Name).ToList();
            if (miss.Count > 0) throw new InstallException($"В папке игры нет {string.Join(" и ", miss)}.");
            var foreign = st.Archives.Where(a => a.State == ArcState.Foreign).Select(a => a.Name).ToList();
            if (foreign.Count > 0) throw new InstallException(ForeignText(foreign));
        }

        public static string ForeignText(IEnumerable<string> names) =>
            $"{string.Join(" и ", names)} не совпадает с оригиналом Steam-версии: другая версия игры, другой мод или файл повреждён. " +
            "Лаунчер его не трогает. Верните оригинал: Steam → Свойства игры → Установленные файлы → «Проверить целостность файлов», затем повторите.";

        /// <summary>Новые файлы одного архива: исходник из архива игрока + дельта из пакета; каждая проверка — по sha256.</summary>
        private ArcPatch Prepare(string name, PackArchive spec, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            using var arc = new AczArchive(Target(name));
            var repl = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < spec.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = spec.Files[i];
                if (i % 8 == 0) progress.Report(new InstallProgress($"Готовлю перевод ({name})…", (double)i / Math.Max(1, spec.Files.Count)));
                if (!arc.Contains(f.Path)) throw new InvalidDataException($"{name}: в архиве нет {f.Path}");
                repl[f.Path] = BuildFile(arc, f, _pack);
            }
            progress.Report(new InstallProgress($"Готовлю перевод ({name})…", 1));
            return ArcPatch.Build(arc, repl);
        }

        internal static byte[] BuildFile(AczArchive arc, PackFile f, RuPack pack)
        {
            var src = arc.Extract(f.Path);
            if (f.SrcSize.HasValue && src.Length != f.SrcSize.Value) throw new InvalidDataException($"{f.Path}: исходный файл другого размера");
            if (Hashing.Sha256(src) != f.SrcSha256) throw new InvalidDataException($"{f.Path}: исходный файл не совпадает с ожидаемым");
            var delta = pack.ReadAll(f.Delta);
            if (f.DeltaSize.HasValue && delta.Length != f.DeltaSize.Value) throw new InvalidDataException($"{f.Path}: дельта в пакете повреждена (размер)");
            if (f.DeltaSha256 != null && Hashing.Sha256(delta) != f.DeltaSha256) throw new InvalidDataException($"{f.Path}: дельта в пакете повреждена (sha256)");
            if (!f.IsPng)
            {
                var basis = src;
                if (f.SrcExtra != null && f.SrcExtra.Count > 0)
                {
                    // источник COPY = файл игрока + доп. файлы того же архива подряд (Уточнения к acz-ru-pack/1)
                    var ms = new MemoryStream();
                    ms.Write(src, 0, src.Length);
                    foreach (var x in f.SrcExtra)
                    {
                        if (!arc.Contains(x.Path)) throw new InvalidDataException($"{f.Path}: в архиве нет доп. исходника {x.Path}");
                        var d = arc.Extract(x.Path);
                        if ((x.Size.HasValue && d.Length != x.Size.Value) || Hashing.Sha256(d) != x.Sha256)
                            throw new InvalidDataException($"{f.Path}: доп. исходник {x.Path} не совпадает с ожидаемым");
                        ms.Write(d, 0, d.Length);
                    }
                    basis = ms.ToArray();
                }
                var o = AcdDelta.Apply(basis, delta);
                if (f.OutSize.HasValue && o.Length != f.OutSize.Value) throw new InvalidDataException($"{f.Path}: результат другого размера");
                if (Hashing.Sha256(o) != f.OutSha256) throw new InvalidDataException($"{f.Path}: результат не совпадает с пакетом (sha256)");
                return o;
            }
            var png = PngPixels.Decode(src);
            var pi = f.Png!;
            if (png.Width != pi.Width || png.Height != pi.Height || png.ColorType != pi.ColorType || png.BitDepth != pi.BitDepth)
                throw new InvalidDataException($"{f.Path}: картинка другого формата");
            if (Hashing.Sha256(png.Raw) != f.RawSrcSha256) throw new InvalidDataException($"{f.Path}: пиксели исходной картинки не совпадают");
            var raw = AcdDelta.Apply(png.Raw, delta);
            if (f.RawSize.HasValue && raw.Length != f.RawSize.Value) throw new InvalidDataException($"{f.Path}: пикселей результата не столько, сколько в пакете");
            if (Hashing.Sha256(raw) != f.RawOutSha256) throw new InvalidDataException($"{f.Path}: пиксели результата не совпадают с пакетом");
            return png.Encode(raw);
        }

        private void WriteBackup(string name, PackArchive spec, ArcPatch p)
        {
            Directory.CreateDirectory(BackupDir);
            byte[] table, trailer;
            using (var f = new FileStream(Target(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                table = ReadAt(f, p.TableFileOffset, p.TableOld.Length);
                trailer = ReadAt(f, spec.OrigSize - AczArchive.Trail, AczArchive.Trail);
            }
            if (!table.SequenceEqual(p.TableOld) || !trailer.SequenceEqual(p.TrailerOld))
                throw new InstallException($"{name} изменился во время подготовки — повторите.");
            File.WriteAllBytes(Bpath(name, "table.bin"), table);
            File.WriteAllBytes(Bpath(name, "trailer.bin"), trailer);
            var bk = new BackupInfo
            {
                Target = Target(name), OrigSize = spec.OrigSize, OrigSha256 = spec.OrigSha256, TableFileOff = p.TableFileOffset,
                TableLen = table.Length, TableSha256 = Hashing.Sha256(table), TrailerHex = Hashing.Hex(trailer),
            };
            Save(Bpath(name, "backup.json"), bk);
            if (LoadBackup(name, spec) == null) throw new InstallException("Не удалось записать бэкап в " + BackupDir);
            TryDelete(Bpath(name, "installed.json"));
        }

        private void WritePatch(string name, ArcPatch p)
        {
            using var f = OpenForWrite(Target(name));
            f.Seek(p.TableFileOffset, SeekOrigin.Begin);
            f.Write(p.TableNew, 0, p.TableNew.Length);
            f.Flush(true);
            AfterTableWritten?.Invoke(name);
            f.Seek(p.TailFileOffset, SeekOrigin.Begin);
            f.Write(p.TailNew, 0, p.TailNew.Length);
            f.SetLength(p.PatchedSize);
            f.Flush(true);
        }

        /// <summary>Проверка после записи: размер, контрольная сумма архива (пересчёт по всему телу = записанной),
        /// таблица = ожидаемой, каждый новый файл читается обратно и совпадает с out_sha256 / raw_out_sha256.</summary>
        private string Verify(string name, ArcPatch p, PackArchive spec, IProgress<InstallProgress> progress)
        {
            var path = Target(name);
            progress.Report(new InstallProgress($"Проверяю {name}…", 0.1));
            var scan = Hashing.Scan(path, new Progress<double>(f => progress.Report(new InstallProgress($"Проверяю {name}…", 0.1 + 0.7 * f))));
            if (scan.Size != p.PatchedSize) throw new InvalidDataException($"{name}: размер {scan.Size}, ждали {p.PatchedSize}");
            using (var arc = new AczArchive(path))
            {
                if (scan.ChecksumCalc != arc.ChecksumStored() || arc.ChecksumStored() != p.Checksum)
                    throw new InvalidDataException($"{name}: контрольная сумма архива не сходится");
                if (arc.Count != p.NewEntries.Length) throw new InvalidDataException($"{name}: число записей изменилось");
                for (int i = 0; i < arc.Count; i++)
                {
                    var e = arc.Entries[i]; var want = p.NewEntries[i];
                    if (e.Name != want.Name || e.Offset != want.Offset || e.Raw != want.Raw || e.Stored != want.Stored)
                        throw new InvalidDataException($"{name}: запись {e.Name} не та");
                }
                foreach (var f in spec.Files)
                {
                    var d = arc.Extract(f.Path);
                    var ok = f.IsPng ? Hashing.Sha256(PngPixels.Decode(d).Raw) == f.RawOutSha256 : Hashing.Sha256(d) == f.OutSha256;
                    if (!ok) throw new InvalidDataException($"{name}: {f.Path} прочитан обратно не таким");
                }
            }
            ShaCache.Put(path, scan);
            progress.Report(new InstallProgress($"Проверяю {name}…", 1));
            return scan.Sha256;
        }

        // ================================================================ удаление
        public Task RemoveAsync(IProgress<InstallProgress> progress, CancellationToken ct) => Task.Run(() => Remove(progress, ct), ct);

        /// <summary>Вернуть оригинал всех архивов пакета. Чужой архив не трогаем.</summary>
        public void Remove(IProgress<InstallProgress> progress, CancellationToken ct)
        {
            EnsureGameClosed();
            Log("удаление перевода: " + GameDir);
            RemoveModule();
            var st = Inspect(Scale(progress, 0, 0.5), ct);
            Log("состояние: " + st.Describe());
            var foreign = st.Archives.Where(a => a.State == ArcState.Foreign).Select(a => a.Name).ToList();
            var todo = st.Archives.Where(a => a.State == ArcState.Installed || a.State == ArcState.InstalledOther || a.State == ArcState.Interrupted).ToList();
            for (int i = 0; i < todo.Count; i++)
            {
                progress.Report(new InstallProgress($"Возвращаю оригинал {todo[i].Name}…", 0.5 + 0.5 * i / todo.Count));
                RestoreOne(todo[i].Name, Spec(todo[i].Name));
            }
            RestoreMenu();
            if (foreign.Count > 0) throw new InstallException(ForeignText(foreign));
            progress.Report(new InstallProgress("Готово", 1));
        }

        /// <summary>Откат областей одного архива с проверкой «без записи» и после записи. Бросает, если оригинал не получился.</summary>
        private void RestoreOne(string name, PackArchive spec)
        {
            var path = Target(name);
            var bk = LoadBackup(name, spec) ?? throw new InstallException(
                $"Нет бэкапа для {name} — вернуть оригинал может только Steam: Свойства игры → Установленные файлы → «Проверить целостность файлов».");
            if (ShaIfRolledBack(path, bk) != spec.OrigSha256)
                throw new InstallException($"{name}: откат не даст оригинал (файл изменён не нами). " +
                                           "Steam → Свойства игры → Установленные файлы → «Проверить целостность файлов».");
            EnsureGameClosed();
            ClearReadOnly(path);
            var table = File.ReadAllBytes(Bpath(name, "table.bin"));
            var trailer = File.ReadAllBytes(Bpath(name, "trailer.bin"));
            using (var f = OpenForWrite(path))
            {
                f.Seek(bk.TableFileOff, SeekOrigin.Begin);
                f.Write(table, 0, table.Length);
                f.SetLength(bk.OrigSize);
                f.Seek(bk.OrigSize - AczArchive.Trail, SeekOrigin.Begin);
                f.Write(trailer, 0, trailer.Length);
                f.Flush(true);
            }
            ShaCache.Forget(path);
            var sha = ShaCache.Get(path, null).Sha256;
            if (sha != spec.OrigSha256) throw new InstallException($"{name}: после отката sha256 {sha} не равен оригиналу.");
            TryDelete(Bpath(name, "installed.json"));
            Log($"{name}: оригинал возвращён, sha256 {sha}");
        }

        // ================================================================ утилиты
        private void EnsureGameClosed()
        {
            if (!IsGameRunning()) return;
            Log("отказ: игра запущена");
            throw new InstallException("Игра запущена — закройте Ace Combat Zero и повторите.");
        }

        /// <summary>Перед записью: снять «только чтение» (у файлов раздачи торрента он бывает) и проверить, что архив
        /// открывается на запись. Бросает понятный InstallException — до бэкапа, архивы не тронуты.</summary>
        private void EnsureWritable(string name)
        {
            var path = Target(name);
            ClearReadOnly(path);
            using (OpenForWrite(path)) { }
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.ReadOnly) == 0) return;
                File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                Log($"{System.IO.Path.GetFileName(path)}: снят атрибут «только чтение»");
            }
            catch (Exception ex) { Log($"{System.IO.Path.GetFileName(path)}: снять «только чтение» не удалось: {ex.Message}"); }
        }

        /// <summary>Архив по-прежнему оригинал: размер и sha256 (кэш по размеру и времени — после неудачной записи дёшево).</summary>
        private bool IsOriginal(string name, PackArchive spec)
        {
            var path = Target(name);
            try { return new FileInfo(path).Length == spec.OrigSize && ShaCache.Get(path, null).Sha256 == spec.OrigSha256; }
            catch { return false; }
        }

        private static FileStream OpenForWrite(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16); }
            catch (UnauthorizedAccessException ex)
            {
                throw new InstallException($"Нет прав на запись в {System.IO.Path.GetFileName(path)}. Снимите «только чтение» или запустите лаунчер от имени администратора. ({ex.Message})", ex);
            }
            catch (IOException ex)
            {
                throw new InstallException($"{System.IO.Path.GetFileName(path)} занят другой программой (игра запущена?). Закройте её и повторите. ({ex.Message})", ex);
            }
        }

        private static string Friendly(Exception ex) => ex is InstallException ? ex.Message : ex.Message;

        private static byte[] ReadAt(Stream f, long off, int n)
        {
            var b = new byte[n];
            f.Seek(off, SeekOrigin.Begin);
            int got = 0;
            while (got < n) { int r = f.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
            return b;
        }

        private static long DiskFree(string dir)
        {
            try { return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir))!).AvailableFreeSpace; } catch { return -1; }
        }

        private static IProgress<InstallProgress> Scale(IProgress<InstallProgress> p, double from, double to)
            => new SyncProgress<InstallProgress>(x => p.Report(new InstallProgress(x.Status, from + (to - from) * x.Fraction)));

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static T? Load<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var f = File.OpenRead(path);
                return new DataContractJsonSerializer(typeof(T)).ReadObject(f) as T;
            }
            catch { return null; }
        }

        private static void Save<T>(string path, T obj)
        {
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(f, obj);
                f.Flush(true);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void Log(string s) => InstallLog.Write(s);
    }

    /// <summary>Ошибка с понятным текстом для окна.</summary>
    public sealed class InstallException : Exception
    {
        public InstallException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>IProgress без SynchronizationContext — пересчёт долей в том же потоке.</summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) { _a = a; }
        public void Report(T value) => _a(value);
    }

    /// <summary>sha256 архивов по (путь, длина, время записи): a.arc — 480 МБ, второй раз не считаем.</summary>
    public static class ShaCache
    {
        private static readonly Dictionary<string, (long Len, DateTime Mtime, Hashing.ScanResult Scan)> Map =
            new Dictionary<string, (long, DateTime, Hashing.ScanResult)>(StringComparer.OrdinalIgnoreCase);

        public static Hashing.ScanResult Get(string path, IProgress<double>? progress)
        {
            var fi = new FileInfo(path);
            lock (Map)
                if (Map.TryGetValue(path, out var c) && c.Len == fi.Length && c.Mtime == fi.LastWriteTimeUtc) return c.Scan;
            var scan = Hashing.Scan(path, progress);
            Put(path, scan);
            return scan;
        }

        public static void Put(string path, Hashing.ScanResult scan)
        {
            var fi = new FileInfo(path);
            lock (Map) Map[path] = (fi.Length, fi.LastWriteTimeUtc, scan);
        }

        public static void Forget(string path) { lock (Map) Map.Remove(path); }
    }

    /// <summary>Лог установки: %LOCALAPPDATA%\ACZRuLauncher\install.log, при 2 МБ уходит в install.prev.log.</summary>
    public static class InstallLog
    {
        public static string Path =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACZRuLauncher", "install.log");

        private static readonly object Gate = new object();

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    var p = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                    if (File.Exists(p) && new FileInfo(p).Length > 2 * 1024 * 1024)
                    {
                        var prev = System.IO.Path.ChangeExtension(p, ".prev.log");
                        try { if (File.Exists(prev)) File.Delete(prev); File.Move(p, prev); } catch { }
                    }
                    File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
                }
            }
            catch { /* лог не должен ломать установку */ }
        }
    }
}
