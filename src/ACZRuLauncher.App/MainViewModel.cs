using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Pack;

namespace ACZRuLauncher
{
    public enum Phase { Checking, Install, Installing, Ready, Update, Error }

    /// <summary>Наш проект в карусели «Наши проекты».</summary>
    public sealed class ProjectCard
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Url { get; set; } = "";
        public string UrlLabel { get; set; } = "Релизы";
    }

    /// <summary>Состояние окна: Проверка → Установить → Установка → Играть / Обновить, и Ошибка.</summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly RuPack? _pack;
        private GameInstall? _game;
        private TranslationInstaller? _installer;
        private InstallStatus? _status;
        private int _refreshGen;
        private readonly string _exePath;
        private readonly string? _releasesUrl;
        private readonly UpdateChannel _channel;
        private ReleaseInfo? _latest;

        /// <summary>Окно просит закрыться (после подмены exe на новую версию).</summary>
        public event Action? RequestClose;

        public static readonly ProjectCard[] Projects =
        {
            new ProjectCard
            {
                Title = "ACE COMBAT ZERO",
                Subtitle = "Русский перевод: субтитры роликов и брифингов, радио, меню, Assault Records. Вы здесь.",
                Url = UpdateChannel.Current.ReleasesPageUrl,
            },
            new ProjectCard
            {
                Title = "PROJECT WINGMAN",
                Subtitle = "Полная русская локализация: озвучка, субтитры и интерфейс. Свой лаунчер.",
                Url = "https://github.com/RikudoSmpai/PWRuLauncher/releases/latest",
            },
        };

        public MainViewModel(string exePath, string? packOverride, string? releasesUrl = null, bool autoCheckUpdates = true, string? gameOverride = null)
        {
            _exePath = exePath;
            _releasesUrl = releasesUrl;
            _channel = UpdateChannel.Current;
            var exeDir = Path.GetDirectoryName(exePath);
            try { _pack = RuPack.TryOpen(exePath, packOverride); }
            catch (Exception ex) { _pack = null; _packError = "Пакет перевода повреждён: " + ex.Message; }

            Version = _channel.VersionLine(_pack != null ? "перевод " + _pack.Manifest.Version : _channel.IsDev ? "без пакета" : "dev");
            UpdateStatus = "Версия " + Updates.CurrentVersion + ". Обновления не проверялись.";
            if (autoCheckUpdates) _ = CheckUpdatesAsync(silent: true);

            var found = GameLocator.FindAll(exeDir).ToList();
            _game = gameOverride != null ? GameInstall.FromAnyPath(gameOverride) : found.FirstOrDefault();
            _otherCopies = string.Join("; ", found.Where(g => _game == null || !string.Equals(g.Root, _game.Root, StringComparison.OrdinalIgnoreCase))
                                               .Select(g => g.KindText + ": " + g.Root));
            _suppressPath = true;
            GamePath = _game?.Root ?? "";
            _suppressPath = false;
            PathValid = _game != null;
            PathHint = _game != null ? GameHint(_game) : "Автопоиск не нашёл игру. Укажите папку ACZero.";
            if (_game == null)
            {
                PathOpen = true;
                if (gameOverride == null) InstallLog.Write("автопоиск: игра не найдена (библиотеки Steam и папка лаунчера) — нужно указать папку ACZero");
            }
            _ = RefreshAsync();
        }

        private readonly string? _packError;
        private readonly string _otherCopies = "";
        private bool _suppressPath;

        // ------------------------------------------------------------------ свойства окна
        public string Version { get; }

        private Phase _phase;
        public Phase Phase { get => _phase; private set { if (Set(ref _phase, value)) RaisePhaseFlags(); } }
        public bool IsChecking => Phase == Phase.Checking;
        public bool IsInstall => Phase == Phase.Install;
        public bool IsInstalling => Phase == Phase.Installing;
        public bool IsReady => Phase == Phase.Ready;
        public bool IsUpdate => Phase == Phase.Update;
        public bool IsError => Phase == Phase.Error;
        /// <summary>Полоса прогресса вместо кнопки: проверка и установка.</summary>
        public bool IsBusy => IsChecking || IsInstalling;
        public bool PrimaryVisible => !IsBusy && !IsError;
        public bool PrimaryEnabled => IsInstall || IsReady || IsUpdate;
        private string _updateLabel = "ОБНОВИТЬ";
        public string PrimaryLabel => IsReady ? "ИГРАТЬ" : IsUpdate ? _updateLabel : "УСТАНОВИТЬ";
        public bool ShowPlayIcon => IsReady;
        public bool ShowInstallIcon => IsInstall || IsUpdate;
        /// <summary>«Удалить перевод» — когда в игре стоит наш перевод (в т.ч. старый или недоставленный).</summary>
        public bool RemoveVisible => IsReady || IsUpdate;
        /// <summary>«Проверить снова» — в ошибке, если игра найдена (например, после проверки целостности в Steam).</summary>
        public bool RetryVisible => IsError && _game != null && _pack != null;

        private string _hint = "";
        public string Hint { get => _hint; private set => Set(ref _hint, value); }

        /// <summary>Заметная строка про язык меню игры (починили UK при открытии / setting.ini ещё нет); "" — скрыта.</summary>
        private string _menuNote = "";
        public string MenuNote { get => _menuNote; private set { if (Set(ref _menuNote, value)) Raise(nameof(MenuNoteVisible)); } }
        public bool MenuNoteVisible => MenuNote.Length > 0 && !IsError;

        private bool _infoOpen;
        public bool InfoOpen { get => _infoOpen; set => Set(ref _infoOpen, value); }

        // «О программе», обновления, проекты
        private bool _aboutOpen;
        public bool AboutOpen { get => _aboutOpen; set => Set(ref _aboutOpen, value); }
        public string AppVersionText => _channel.AppVersionText(Updates.CurrentVersion);
        public string WindowTitle => _channel.WindowTitle("Ace Combat Zero — русский перевод");
        public bool IsDev => _channel.IsDev;
        public string RepoUrl => Updates.RepoUrl;
        public string ReleasesUrl => _channel.ReleasesPageUrl;
        private string _updateStatus = "";
        public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
        private string _updateActionLabel = "Проверить обновления";
        public string UpdateActionLabel { get => _updateActionLabel; private set => Set(ref _updateActionLabel, value); }
        private bool _updateBusy;
        public bool UpdateActionEnabled => !_updateBusy && !IsBusy;
        private bool _updateDot;
        public bool UpdateDot { get => _updateDot; private set => Set(ref _updateDot, value); }

        private int _projectIndex;
        public ProjectCard Project => Projects[_projectIndex];
        public string ProjectCounter => $"{_projectIndex + 1} / {Projects.Length}";
        public void NextProject(int step)
        {
            _projectIndex = (_projectIndex + step + Projects.Length) % Projects.Length;
            Raise(nameof(Project)); Raise(nameof(ProjectCounter));
        }

        private string _progressSource = "Файлы игры: a.arc, d.arc";
        public string ProgressSource { get => _progressSource; private set => Set(ref _progressSource, value); }
        private string _errorLabel = "ИГРА НЕ НАЙДЕНА";
        public string ErrorLabel { get => _errorLabel; private set => Set(ref _errorLabel, value); }
        private string _errorDetail = "";
        public string ErrorDetail { get => _errorDetail; private set => Set(ref _errorDetail, value); }

        private string _progressStatus = "";
        public string ProgressStatus { get => _progressStatus; private set => Set(ref _progressStatus, value); }
        private double _progressFraction;
        public double ProgressFraction
        {
            get => _progressFraction;
            private set { if (Set(ref _progressFraction, value)) { Raise(nameof(ProgressWidth)); Raise(nameof(ProgressPercent)); } }
        }
        public double ProgressWidth => Math.Round(368 * Math.Max(0, Math.Min(1, ProgressFraction)));
        public string ProgressPercent => ((int)Math.Round(100 * Math.Max(0, Math.Min(1, ProgressFraction)))).ToString();

        // путь
        private bool _pathOpen;
        public bool PathOpen { get => _pathOpen; set => Set(ref _pathOpen, value); }
        private string _gamePath = "";
        public string GamePath { get => _gamePath; set { if (Set(ref _gamePath, value) && !_suppressPath) OnPathEdited(value); } }
        private bool _pathValid;
        public bool PathValid { get => _pathValid; private set { if (Set(ref _pathValid, value)) Raise(nameof(PathInvalid)); } }
        public bool PathInvalid => !PathValid;
        private string _pathHint = "";
        public string PathHint { get => _pathHint; private set => Set(ref _pathHint, value); }
        public bool BrowseEnabled => !IsBusy;

        // ------------------------------------------------------------------ логика
        private string GameHint(GameInstall g)
        {
            var s = g.KindText + (g.Kind == GameKind.SteamAc8 ? " (не поддерживается)" : "") + ".";
            if (_otherCopies.Length > 0) s += " Найдено ещё: " + _otherCopies + ".";
            return s;
        }

        /// <summary>Пересчитать фазу из фактов на диске (sha256 архивов считается в фоне, 480 МБ — пара секунд).</summary>
        public async Task RefreshAsync(string? hintAfter = null)
        {
            int gen = ++_refreshGen;
            MenuNote = "";
            if (_game == null)
            {
                _installer = null; _status = null;
                ErrorLabel = "ИГРА НЕ НАЙДЕНА";
                ErrorDetail = "Укажите папку ACZero внизу окна (в ней лежат a.arc и d.arc). " +
                              "Steam: …\\steamapps\\common\\ACE COMBAT ZERO THE BELKAN WAR (Steam Version)\\ACZero.";
                Phase = Phase.Error;
                return;
            }
            if (_pack == null)
            {
                _installer = null; _status = null;
                ErrorLabel = "ПЕРЕВОД НЕ НАЙДЕН";
                ErrorDetail = _packError ?? "В этот exe не вшит пакет перевода. Скачайте ACZRuLauncher.exe со страницы релизов.";
                Phase = Phase.Error;
                return;
            }
            if (_game.Kind == GameKind.SteamAc8)
            {
                // 13.11.2026: в AC8 Zero запускается другим exe под EasyAntiCheat — файлы этой копии не трогаем,
                // пока это не проверено в игре (риск для учётки игрока). Ставим только в отдельную Zero.
                _installer = null; _status = null;
                ErrorLabel = "ZERO ИЗ ACE COMBAT 8 НЕ ПОДДЕРЖИВАЕТСЯ";
                ErrorDetail = "В составе ACE COMBAT 8 Zero работает под защитой EasyAntiCheat, её файлы мы не меняем. " +
                              "Перевод ставится в отдельную Zero из Steam (ACE COMBAT ZERO THE BELKAN WAR) — укажите её папку ACZero внизу окна.";
                Phase = Phase.Error;
                return;
            }
            _installer = new TranslationInstaller(_pack, _game.Root) { Module = ExeModule.ForGame(_game) };
            Phase = Phase.Checking;
            ProgressSource = "Файлы игры: a.arc, d.arc";
            ProgressStatus = "Проверяю файлы игры…"; ProgressFraction = 0;
            InstallStatus st;
            try
            {
                var p = new Progress<InstallProgress>(x => { if (gen == _refreshGen) { ProgressStatus = x.Status; ProgressFraction = x.Fraction; } });
                st = await _installer.InspectAsync(p);
            }
            catch (Exception ex)
            {
                if (gen != _refreshGen) return;
                ErrorLabel = "НЕ УДАЛОСЬ ПРОЧИТАТЬ ИГРУ"; ErrorDetail = ex.Message; Phase = Phase.Error;
                return;
            }
            if (gen != _refreshGen) return;     // пока считали, путь сменили
            _status = st;
            InstallLog.Write("состояние: " + st.Describe());
            // перевод стоит, а язык меню не UK (первый запуск игры создал setting.ini с пустым языком, или язык сбросили) — чиним
            var menu = MenuFix.None;
            try { var inst = _installer; menu = await Task.Run(() => inst.EnsureMenuRussian(st)); }
            catch (Exception ex) { InstallLog.Write("язык меню: починка не удалась: " + ex.Message); }
            if (gen != _refreshGen) return;
            switch (st.Overall)
            {
                case Overall.Missing:
                    ErrorLabel = "НЕТ ФАЙЛОВ ИГРЫ";
                    ErrorDetail = "В папке нет " + string.Join(" и ", st.Archives.Where(a => a.State == ArcState.Missing).Select(a => a.Name)) + ".";
                    Phase = Phase.Error; break;
                case Overall.Foreign:
                    ErrorLabel = "ФАЙЛЫ ИГРЫ ИЗМЕНЕНЫ";
                    ErrorDetail = TranslationInstaller.ForeignText(st.Archives.Where(a => a.State == ArcState.Foreign).Select(a => a.Name));
                    Phase = Phase.Error; break;
                case Overall.NotInstalled:
                    Phase = Phase.Install;
                    Hint = "Перевод ставится в a.arc и d.arc. Удалить можно здесь же, файлы вернутся к оригиналу.";
                    break;
                case Overall.Installed when st.Module == ModuleState.NotInstalled || st.Module == ModuleState.Outdated:
                    // архивы стоят, а модуль acz.exe новый или его ещё нет — «Обновить» доставит только его
                    _updateLabel = "ОБНОВИТЬ"; Raise(nameof(PrimaryLabel));
                    Phase = Phase.Update;
                    Hint = "Перевод стоит. Обновить — добавит русские надписи из acz.exe (метки целей).";
                    break;
                case Overall.Installed:
                    Phase = Phase.Ready;
                    Hint = "Перевод установлен." + (st.Module == ModuleState.Conflict
                        ? " Метки целей остаются английскими: рядом с игрой лежит чужой " + ExeModule.FileName + "." : "");
                    break;
                case Overall.NeedsUpdate:
                    _updateLabel = "ОБНОВИТЬ"; Raise(nameof(PrimaryLabel));
                    Phase = Phase.Update;
                    Hint = "Стоит прошлая версия перевода — обновить до " + _pack.Manifest.Version + ".";
                    break;
                default:
                    _updateLabel = "ИСПРАВИТЬ"; Raise(nameof(PrimaryLabel));
                    Phase = Phase.Update;
                    Hint = "Прошлая установка не завершилась. «Исправить» вернёт оригинал и поставит перевод заново.";
                    break;
            }
            if (hintAfter != null && Phase != Phase.Error) Hint = hintAfter;
            if (Phase != Phase.Error) MenuNote = MenuNoteText(menu);
        }

        private static string MenuNoteText(MenuFix m) => m switch
        {
            MenuFix.Fixed => TranslationInstaller.MenuFixedText,
            MenuFix.NoIni => TranslationInstaller.MenuNoIniText,
            MenuFix.GameRunning => TranslationInstaller.MenuGameRunningText,
            MenuFix.Failed => "Меню игры английское: включить русский (UK) не удалось. Закройте игру и снова откройте лаунчер.",
            _ => "",
        };

        public async Task PrimaryAsync()
        {
            if (IsReady)
            {
                if (TranslationInstaller.DefaultIsGameRunning()) { Hint = "Игра уже запущена."; return; }
                try { GameLauncher.Launch(_game!); Hint = _game!.Kind == GameKind.Other ? "Запускаю acz.exe…" : "Запускаю через Steam…"; }
                catch (Exception ex) { Hint = "Не удалось запустить игру: " + ex.Message; }
                return;
            }
            if (!(IsInstall || IsUpdate) || _installer == null) return;
            await RunAsync(install: true);
        }

        public async Task RemoveAsync()
        {
            if (!RemoveVisible || _installer == null) return;
            await RunAsync(install: false);
        }

        public Task RetryAsync() { ShaCache_ForgetGame(); return RefreshAsync(); }

        private void ShaCache_ForgetGame()
        {
            if (_game == null) return;
            foreach (var n in new[] { "a.arc", "d.arc" }) ShaCache.Forget(Path.Combine(_game.Root, n));
        }

        private async Task RunAsync(bool install)
        {
            if (_installer == null) return;
            var cts = new CancellationTokenSource();
            Phase = Phase.Installing;
            ProgressSource = install ? "Пакет перевода внутри лаунчера → a.arc, d.arc" : "Бэкап → a.arc, d.arc";
            ProgressFraction = 0; ProgressStatus = "Подготовка…";
            string? message = null;
            try
            {
                var p = new Progress<InstallProgress>(x => { ProgressStatus = x.Status; ProgressFraction = x.Fraction; });
                if (install) await _installer.InstallAsync(p, cts.Token);
                else await _installer.RemoveAsync(p, cts.Token);
                ProgressFraction = 1; ProgressStatus = "Готово";
                message = install ? "Перевод установлен. Можно играть." : "Перевод удалён, файлы игры — оригинальные.";
                if (install && _installer.ModuleMessage != null) message += " " + _installer.ModuleMessage;
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                message = ex is InstallException ? ex.Message : (install ? "Установка не удалась: " : "Удаление не удалось: ") + ex.Message;
                InstallLog.Write("ошибка в окне: " + ex);
            }
            await RefreshAsync(message);
            // setting.ini ещё нет — Refresh уже показал подсказку; на случай, если состояние не дошло до «установлено»
            if (install && Phase != Phase.Error && _installer.MenuMessage != null && MenuNote.Length == 0) MenuNote = _installer.MenuMessage;
            if (Phase == Phase.Error && message != null && ErrorDetail != message) ErrorDetail = message + "\n\n" + ErrorDetail;
        }

        private void OnPathEdited(string value)
        {
            var g = GameInstall.FromAnyPath(value);
            PathValid = g != null;
            if (g == null)
            {
                PathHint = string.IsNullOrWhiteSpace(value) ? "Укажите папку ACZero." : "Здесь нет a.arc и d.arc.";
                if (_game != null) { _game = null; _ = RefreshAsync(); }
                return;
            }
            PathHint = GameHint(g);
            if (_game == null || !string.Equals(_game.Root, g.Root, StringComparison.OrdinalIgnoreCase))
            {
                _game = g;
                _ = RefreshAsync();
            }
        }

        public void SetPathFromPicker(string folder) => GamePath = folder;

        /// <summary>Режим показа: --demo checking|install|installing|ready|update|repair|error|foreign|nopack|about|menufixed|menunoini. Диск не трогается.</summary>
        public void ApplyDemo(string demo)
        {
            // «…-path»: то же состояние с раскрытой папкой игры — тело окна ниже всего (так «О программе» ломался в PW 1.0.7)
            bool pathOpen = demo.EndsWith("-path", StringComparison.Ordinal);
            if (pathOpen) demo = demo.Substring(0, demo.Length - "-path".Length);
            _refreshGen++;
            _installer = null;
            _suppressPath = true;
            GamePath = @"D:\SteamLibrary\steamapps\common\ACE COMBAT ZERO THE BELKAN WAR (Steam Version)\ACZero";
            _suppressPath = false;
            PathValid = true; PathHint = "Steam: ACE COMBAT ZERO.";
            MenuNote = "";
            switch (demo)
            {
                case "menufixed":
                    Phase = Phase.Ready; Hint = "Перевод установлен."; MenuNote = TranslationInstaller.MenuFixedText; break;
                case "menunoini":
                    Phase = Phase.Ready; Hint = "Перевод установлен. Можно играть."; MenuNote = TranslationInstaller.MenuNoIniText; break;
                case "checking":
                    Phase = Phase.Checking; ProgressStatus = "Проверяю a.arc…"; ProgressFraction = 0.42; ProgressSource = "Файлы игры: a.arc, d.arc"; break;
                case "installing":
                    Phase = Phase.Installing; ProgressStatus = "Записываю a.arc…"; ProgressFraction = 0.67;
                    ProgressSource = "Пакет перевода внутри лаунчера → a.arc, d.arc"; break;
                case "ready":
                    Phase = Phase.Ready; Hint = "Перевод установлен."; break;
                case "update":
                    _updateLabel = "ОБНОВИТЬ"; Phase = Phase.Update; Raise(nameof(PrimaryLabel));
                    Hint = "Стоит прошлая версия перевода — обновить до 2026.11.20-1."; break;
                case "repair":
                    _updateLabel = "ИСПРАВИТЬ"; Phase = Phase.Update; Raise(nameof(PrimaryLabel));
                    Hint = "Прошлая установка не завершилась. «Исправить» вернёт оригинал и поставит перевод заново."; break;
                case "error":
                    ErrorLabel = "ИГРА НЕ НАЙДЕНА";
                    ErrorDetail = "Укажите папку ACZero внизу окна (в ней лежат a.arc и d.arc). Steam: …\\steamapps\\common\\ACE COMBAT ZERO THE BELKAN WAR (Steam Version)\\ACZero.";
                    _suppressPath = true; GamePath = ""; _suppressPath = false;
                    PathValid = false; PathOpen = true; PathHint = "Автопоиск не нашёл игру. Укажите папку ACZero.";
                    Phase = Phase.Error; break;
                case "foreign":
                    ErrorLabel = "ФАЙЛЫ ИГРЫ ИЗМЕНЕНЫ"; ErrorDetail = TranslationInstaller.ForeignText(new[] { "a.arc" });
                    Phase = Phase.Error; break;
                case "nopack":
                    ErrorLabel = "ПЕРЕВОД НЕ НАЙДЕН"; ErrorDetail = "В этот exe не вшит пакет перевода. Скачайте ACZRuLauncher.exe со страницы релизов.";
                    Phase = Phase.Error; break;
                case "info":
                    Phase = Phase.Install; InfoOpen = true;
                    Hint = "Перевод ставится в a.arc и d.arc. Удалить можно здесь же, файлы вернутся к оригиналу."; break;
                case "about":
                    Phase = Phase.Ready; AboutOpen = true; Hint = "Перевод установлен.";
                    UpdateStatus = "Доступна версия 1.1.0 (у вас " + Updates.CurrentVersion + ")."; UpdateActionLabel = "Скачать и установить"; UpdateDot = true;
                    break;
                default:
                    Phase = Phase.Install; Hint = "Перевод ставится в a.arc и d.arc. Удалить можно здесь же, файлы вернутся к оригиналу."; break;
            }
            if (pathOpen) PathOpen = true;
            Raise(nameof(RetryVisible));
        }

        // ------------------------------------------------------------------ обновления (release — GitHub, DEV — наш сервер)
        /// <summary>Проверка релизов acz-v*. silent — при старте: молча, только точка на «i».</summary>
        public async Task CheckUpdatesAsync(bool silent)
        {
            if (_updateBusy) return;
            _updateBusy = true; Raise(nameof(UpdateActionEnabled));
            if (!silent) UpdateStatus = "Проверяю обновления…";
            try
            {
                var feed = _channel.FeedFor(_releasesUrl);
                if (feed == null)
                {
                    if (!silent) UpdateStatus = "Лента обновлений DEV не задана при сборке.";
                    _latest = null;
                    return;
                }
                var latest = await Updates.FetchLatestAsync(feed, CancellationToken.None);
                _latest = latest;
                var cur = Updates.CurrentVersion;
                var v = latest?.Version;
                if (latest != null && v != null && v > cur && latest.Exe != null)
                {
                    UpdateStatus = $"Доступна версия {v} (у вас {cur}).";
                    UpdateActionLabel = "Скачать и установить";
                    UpdateDot = true;
                    AboutOpen = true;
                }
                else
                {
                    UpdateStatus = $"У вас последняя версия ({cur}).";
                    UpdateActionLabel = "Проверить обновления";
                    UpdateDot = false;
                }
            }
            catch (Exception ex)
            {
                if (!silent) UpdateStatus = "Не удалось проверить обновления: " + Short(ex);
                _latest = null;
            }
            finally { _updateBusy = false; Raise(nameof(UpdateActionEnabled)); }
        }

        public async Task UpdateActionAsync()
        {
            var latest = _latest;
            if (latest?.Exe == null || latest.Version == null || latest.Version <= Updates.CurrentVersion)
            {
                await CheckUpdatesAsync(silent: false);
                return;
            }
            if (IsBusy || _updateBusy) return;
            _updateBusy = true; Raise(nameof(UpdateActionEnabled));
            var prevPhase = Phase;
            var cts = new CancellationTokenSource();
            Phase = Phase.Installing;
            ProgressSource = _channel.ProgressSource;
            ProgressStatus = $"Скачиваю версию {latest.Version}…"; ProgressFraction = 0;
            try
            {
                var staged = Path.Combine(SelfUpdate.StagingDir, $"ACZRuLauncher-{latest.Version}.exe");
                var p = new Progress<double>(f => ProgressFraction = f);
                await Updates.DownloadAsync(latest.Exe.Url, staged, latest.Exe.Size, p, cts.Token);
                if (latest.Exe.Size > 0 && new FileInfo(staged).Length != latest.Exe.Size)
                    throw new IOException("размер скачанного файла не совпадает");
                if (latest.ExeSha == null) throw new IOException("в релизе нет ACZRuLauncher.exe.sha256");
                ProgressStatus = "Сверяю контрольную сумму…";
                var expected = Updates.ParseShaText(await Updates.FetchTextAsync(latest.ExeSha.Url, cts.Token));
                var actual = Updates.Sha256File(staged);
                if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("sha256 не совпадает с опубликованным");
                ProgressStatus = "Перезапуск…"; ProgressFraction = 1;
                await Task.Delay(300);
                SelfUpdate.SwapAndStart(_exePath, staged, "");
                RequestClose?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                UpdateStatus = "Обновление не удалось: " + Short(ex);
                AboutOpen = true;
            }
            finally
            {
                _updateBusy = false; Raise(nameof(UpdateActionEnabled));
            }
            Phase = prevPhase;
            await RefreshAsync();
        }

        private string Short(Exception ex)
        {
            var e = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
            if (e is System.Net.Http.HttpRequestException || e is System.Net.WebException) return _channel.NoConnectionText;
            if (e is TaskCanceledException) return "истекло время ожидания.";
            return e.Message;
        }

        // ------------------------------------------------------------------ INotifyPropertyChanged
        private void RaisePhaseFlags()
        {
            foreach (var n in new[] { nameof(IsChecking), nameof(IsInstall), nameof(IsInstalling), nameof(IsReady), nameof(IsUpdate), nameof(IsError),
                                      nameof(IsBusy), nameof(PrimaryVisible), nameof(PrimaryEnabled), nameof(PrimaryLabel),
                                      nameof(ShowPlayIcon), nameof(ShowInstallIcon), nameof(RemoveVisible), nameof(RetryVisible),
                                      nameof(BrowseEnabled), nameof(UpdateActionEnabled), nameof(MenuNoteVisible) })
                Raise(n);
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name!);
            return true;
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
