# ACZ: как устроено, как собрать, как выпустить

## Стек

- C# / .NET Framework 4.8 (стоит на любом Windows 10/11), Avalonia 11, Fody.Costura — один exe, как у лаунчера PW.
- Пакет перевода `acz-ru-pack/1` дописан в хвост exe zip-ом (трейлер: 8 байт длины + `ACZRPAY1`) и читается
  по смещению, без распаковки на диск.
- Сборка SDK любой версии от .NET 8 (проверено на 9.0.310); целевая платформа — net48.

## Структура

```
src/ACZRuLauncher.Core
  Arc/AczArchive.cs     чтение архива ACZ: заголовок, маска, таблица, LZ4, контрольная сумма
  Arc/Lz4Block.cs       LZ4 block: распаковка и свой компрессор
  Arc/ArcPatch.cs       патч дописыванием: новые файлы в конец, новая таблица, сумма, хвост
  Pack/RuPack.cs        пакет acz-ru-pack/1: manifest.json + deltas/ (zip, папка или хвост exe)
  Pack/AcdDelta.cs      дельта ACD1 (COPY/ADD)
  Pack/PngPixels.cs     PNG ↔ сырые пиксели (снятие фильтров, сборка с фильтром 0)
  TranslationInstaller.cs  состояние архивов, бэкап, установка, проверка, откат
  GameInstall.cs        поиск игры (Steam, Zero из AC8, ручной путь), запуск
  ExeModule.cs          модуль acz.exe (version.dll рядом с игрой): поставить, обновить, удалить только свой
  Updates.cs            самообновление: только релизы acz-v*
src/ACZRuLauncher.App   окно (Avalonia), SelfTest (прогоны без окна)
tests/ACZRuLauncher.Tests  xUnit на синтетических архивах — ни байта данных игры
tools/fetch_pack.py     скачать пакет по pack/manifest.json и сверить sha256 (CI)
tools/pack.py           вшить пакет в exe
pack/manifest.json      какой пакет вшивается в релиз (ссылка + sha256 + размер)
module/                 модуль перевода строк acz.exe (C, x64): исходник, таблица patches.json, сборка, тесты
```

## Архив ACZ (a.arc, d.arc)

Файл = 12 байт заголовка (`magic, k1, k2`) | тело под маской | 8 байт хвоста (`k3`, контрольная сумма под маской).
Тело без маски: `magic2, count, размер имён, версия` | имена через `\0` | таблица `count × (off, raw, stored)` | данные.
Маска — XOR 32-битных слов с `A + B × номер слова`, `A = k1^k3^0x5DF38C35`, `B = k2^k3^0x4CE49F4D`.
Контрольная сумма — сумма `rotr7` по всем словам тела, как они лежат на диске. `stored != raw` — запись сжата LZ4 block.
Порт `acz_arc.py`; константы маски — из открытого `aczarc.py` (Volcano Water).

## Установка (как `apply_all.py` / `restore_all.py`)

1. **Состояние** каждого архива из пакета (`TranslationInstaller.Inspect`):
   - `Original` — sha256 и размер = `orig_sha256`/`orig_size` пакета;
   - `Installed` — sha256 = записанному после нашей установки этого же пакета (`<архив>.installed.json`);
   - `InstalledOther` — то же, но от другой сборки пакета → «Обновить»;
   - `Interrupted` — ни то ни другое, но откат областей из бэкапа даёт ровно оригинал, проверено **без записи**
     (`sha_if_rolled_back`) → «Исправить» / «Удалить»;
   - `Foreign` — всё прочее (другая версия игры, другой мод): не трогаем, текст про «Проверить целостность»;
   - `Missing` — нет файла.
   sha256 a.arc (480 МБ) считается SHA256Cng за ~1 с и кэшируется по (путь, длина, время записи).
2. **Отказы до записи:** запущен `acz.exe`; архив не оригинал; нет места; в пакете не тот исходник.
3. **Подготовка в памяти:** для каждого файла пакета исходник читается из архива игрока, сверяется `src_sha256`
   (+ `src_extra`: доп. файлы того же архива, приклеенные к исходнику для COPY), применяется дельта, результат
   сверяется с `out_sha256`. PNG: пиксели исходника (`raw_src_sha256`) → дельта → `raw_out_sha256` → PNG
   (IHDR как у исходной, PLTE/tRNS из исходной, IDAT = zlib строк с фильтром 0). Сжатая в оригинале запись
   сжимается LZ4, несжатая остаётся несжатой.
4. **Бэкап областей** всех архивов до первой записи: `%LOCALAPPDATA%\ACZRuLauncher\backup\<хэш пути>\`
   `a.arc.table.bin`, `a.arc.trailer.bin`, `a.arc.backup.json` (смещение и sha таблицы, исходные длина и sha256).
5. **Запись:** таблица на месте, новые файлы и новый хвост с позиции старого хвоста (файл растёт), `Flush(true)`.
6. **Проверка:** размер; контрольная сумма пересчитана по всему телу = записанной; таблица = ожидаемой
   (остальные записи не тронуты); каждый новый файл прочитан обратно = `out_sha256` / `raw_out_sha256`.
7. Любая ошибка после бэкапа → **оба** архива к оригиналу (откат областей, затем проверка sha256).

8. **Модуль acz.exe** (`ExeModule`, после архивов и языка меню) — см. ниже. Не встал — перевод всё равно стоит,
   окно говорит почему.

**Язык меню** (`SystemLanguage=UK` в `ACZero\settings\setting.ini`, `MenuLanguage`). При установке лаунчер ставит UK.
Если `setting.ini` ещё нет (игру не запускали), лаунчер его не создаёт: формат файла пишет сама игра. В этом случае окно
подсказывает «запустите игру один раз, закройте и снова откройте лаунчер». **При каждом открытии**
(`EnsureMenuRussian`): перевод наш (Installed / NeedsUpdate), `setting.ini` есть, а язык не UK (первый запуск создал
файл с пустым `SystemLanguage=` = US, или язык сбросил лаунчер Bandai / сам игрок) → лаунчер ставит UK, пишет это в журнал
и показывает в окне строку «Язык меню игры был английским — включил русский (UK)». Бэкап прежнего значения
(`setting.ini.backup.json`) создаётся, только если его ещё нет, поэтому «Удалить перевод» вернёт то, что было до
первой установки, или то, что игра создала сама. Пока игра запущена, файл не трогаем: игра перепишет его при выходе.

Удаление: сначала модуль (только свой), затем таблица и хвост из бэкапа, обрезка до исходной длины,
проверка sha256 = оригинал.
Без бэкапа (переустановили Windows) — только Steam «Проверить целостность файлов»; окно так и говорит.

Сумма архива после установки у нас другая, чем у сборки Python (LZ4 разные), но детерминирована:
одинаковый пакет даёт одинаковый sha256.

## Модуль acz.exe (`module/`, `ExeModule.cs`)

Часть строк лежит в `acz.exe`, в секции `.rdata`: метки над целями, экран итогов. Exe не меняем. Вместо
этого рядом с ним кладётся прокси `version.dll`. После полного старта игры он меняет эти строки **в памяти**:
сверяет один sha256 всей таблицы строк (всех групп), затем пишет только байты самих исходных строк
`.rdata` (`[0, L+1)`). Как устроен и как собрать —
[`module/README.md`](../module/README.md).

Лаунчер ставит модуль, только если выполнены все условия:
- игра — отдельная Zero из Steam (`GameKind.SteamZero`);
- рядом с игрой и папкой выше нет античита (`EasyAntiCheat*`, `start_protected_game*`, `BattlEye*` …);
- в `manifest.json` пакета есть `"features": ["hud-font-h8-cyr"]`. Это значит, что в пакете шрифт HUD (h8) с
  кириллицей (сборка шрифта real2). Без него русские метки нечем рисовать;
- рядом с `acz.exe` нет **чужого** `version.dll`. Чужой не перезаписывается: модуль не ставится, перевод
  ставится, игроку — понятный текст.

Список своего: `%LOCALAPPDATA%\ACZRuLauncher\backup\<хэш пути>\module.json`, в нём имя файла и все sha256,
которые ставил лаунчер. По нему узнаётся старая версия модуля (`Outdated` → «Обновить»). Удаление стирает
`version.dll`, только если его sha256 — наш или есть в списке. Состояние модуля видно в `install.log`
(`; модуль: …`) и в `--selftest*`.

Модуль вшивается в лаунчер при сборке из `module\build\version.dll` (`ACZRuLauncher.Core.csproj`,
`EmbeddedResource`). Если файла нет, лаунчер собирается без модуля и его не ставит. В CI модуль собирается и
проверяется перед тестами ядра.

## Поиск игры

1. Папка, где лежит лаунчер.
2. Библиотеки Steam (`SteamPath` из реестра → `steamapps\libraryfolders.vdf`), в каждой — `installdir` из
   `appmanifest_5370600.acf` (отдельная Zero, по умолчанию `ACE COMBAT ZERO THE BELKAN WAR (Steam Version)`) + `\ACZero`.
3. Zero в составе **ACE COMBAT 8** (appid 2288340): `…\common\ACE COMBAT 8\ACZero`. Архивы там на 13.11.2026
   побайтно те же (sha256 a.arc `82c05ed0…`, d.arc `c28e41ac…`), поэтому пакет к ним подходит. Но `acz.exe` там другой
   (4 037 304 байт против 4 046 008), а в AC8 есть EasyAntiCheat — в игре это не проверено. «Играть» для этой копии
   открывает AC8 через Steam.
4. Типовые папки библиотек на всех дисках.
Ручной выбор: любая папка, где есть `a.arc` и `d.arc` (или папка над `ACZero`).

## Сборка у себя

```
module\build.cmd                       модуль acz.exe → module\build\version.dll (до сборки лаунчера)
module\build\patchtest.exe "<путь>\acz.exe"   таблица на образе exe без запуска игры (путь не обязателен)
module\build\proxytest.exe module\build\version.dll
dotnet test tests/ACZRuLauncher.Tests
dotnet publish src/ACZRuLauncher.App -c Release -o dist/raw -p:Version=1.0.0
python tools/pack.py --pack acz-ru-pack.zip --exe dist/raw/ACZRuLauncher.exe --exe-out dist/ACZRuLauncher.exe
```

## Ключи

```
--pack <zip|папка>          пакет не из exe (иначе: хвост exe, затем acz-ru-pack.zip рядом)
--game <папка>              папка игры вместо автопоиска
--demo checking|install|info|installing|ready|update|repair|error|foreign|nopack|about
                            показать состояние окна, диск не трогается
--shot out.png              окно рендерит себя в файл и закрывается
--no-update-check           не ходить на GitHub при старте
--releases-url <url>        другой источник списка релизов (имитация GitHub)
--selftest <ACZero> [--pack p] [--backup-dir b] [--log f]
                            без окна: установить → проверить → удалить → sha256 = оригинал. ТОЛЬКО НА КОПИЯХ.
--selftest-install / --selftest-remove <ACZero> ...   только поставить / только снять
                            (модуль acz.exe тоже: стоит ли наш version.dll / удалён ли; чужой — не тронут)
--selftest-update <url> [--swap ...]                  контур самообновления без окна
--selftest-channel [--log f]                          канал (release/dev) и лента этой сборки в лог, без сети
```

## Выпуск

1. Новый пакет: загрузить zip (ассетом черновика релиза этого репозитория или куда угодно по https),
   в `pack/manifest.json` вписать `url` (или `asset_id`), `sha256`, `size`, `version`. Пока там заглушка —
   CI честно падает на шаге «Пакет перевода».
2. `git tag acz-v1.2.3 && git push origin acz-v1.2.3`.
3. `release-acz.yml`: тесты → `dotnet publish` → `fetch_pack.py` (sha256) → `pack.py` → релиз с
   `ACZRuLauncher.exe` и `ACZRuLauncher.exe.sha256`.

Свой репозиторий (с 23.11, слово Димы): `RikudoSmpai/ACZRuLauncher`, отдельно от лаунчера PW — жалоба на одно не задевает
другое. Лаунчер берёт `GET /releases?per_page=50` и оставляет только `acz-v*` (не черновик, не пре-релиз, с exe);
черновики с пакетами перевода ему не мешают.

## Канал DEV

Сборка для тестировщиков: обновляется не с GitHub, а с ленты на нашем сервере.

```
module\build.cmd
dotnet test tests/ACZRuLauncher.Tests
dotnet publish src/ACZRuLauncher.App -c Release -o <папка> -p:Version=2026.1116.1 ^
    -p:AczChannel=dev -p:AczDevFeedUrl=https://<сервер>/<закрытая папка>/latest.json
python tools/pack.py ...                 пакет вшивается как обычно
```

- `AczChannel` — `release` (по умолчанию, поведение не меняется) или `dev` (константа `DEV_CHANNEL` в Core).
- Канал и адрес ленты попадают в метаданные `ACZRuLauncher.Core` (`AssemblyMetadata` `AczChannel`, `AczDevFeedUrl`),
  читает их `UpdateChannel`. **Адрес ленты закрытый: передаётся только в команде сборки, в репозиторий и в CI его не писать.**
- `dev` без `AczDevFeedUrl` (или не `https://…`) — сборка падает с ошибкой: DEV-exe, который не умеет обновляться,
  легко раздать по ошибке и не заметить.
- Версии DEV — `ГГГГ.ММДД.N` (2026.1116.1); теги `acz-v2026.1116.1`. Любой DEV новее релизов 1.x — сравнение то же.
- Лента — один объект релиза в формате GitHub API:

```json
{ "tag_name": "acz-v2026.1116.1", "name": "ACZ DEV 2026.11.16-1", "draft": false, "prerelease": false,
  "assets": [
    { "name": "ACZRuLauncher.exe",        "browser_download_url": "https://…/2026.1116.1/ACZRuLauncher.exe", "size": 29852388 },
    { "name": "ACZRuLauncher.exe.sha256", "browser_download_url": "https://…/2026.1116.1/ACZRuLauncher.exe.sha256" } ] }
```

  `size` должен совпадать с файлом (иначе обновление отклоняется), `.sha256` — в формате `sha256sum`.
- В окне: «DEV · перевод …» в углу, «Версия … · DEV» в «О программе», «— DEV» в заголовке окна; «Релизы» ведёт
  в папку ленты (адрес без `latest.json`), тексты «нет связи с сервером обновлений», источник прогресса — сервер.
- `--releases-url` по-прежнему перекрывает ленту; `--selftest-channel [--log f]` пишет канал, ленту и версию в
  `ACZRuLauncher.channel.log` рядом с exe (без сети).

## Ручная сверка с Python (вне репозитория)

`C:\DS_Work\acz-launcher-data\launcher_check\`: копии `a.arc`/`d.arc`, `check_with_python.py`
(aczarc.py Volcano Water + lz4 + Pillow), `compare_with_python_build.py` (против `out_rel1` сборки Python).
