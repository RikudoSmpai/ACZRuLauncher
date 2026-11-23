using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ACZRuLauncher.Core
{
    public enum GameKind
    {
        /// <summary>Отдельная Steam-версия ACE COMBAT ZERO: THE BELKAN WAR (appid 5370600).</summary>
        SteamZero,
        /// <summary>Zero в составе ACE COMBAT 8 (appid 2288340, папка ACE COMBAT 8\ACZero).</summary>
        SteamAc8,
        /// <summary>Любая другая папка с a.arc и d.arc (указана руками).</summary>
        Other,
    }

    /// <summary>Папка ACZero: в ней a.arc, d.arc, acz.exe. Строится только из подтверждённой папки.</summary>
    public sealed class GameInstall
    {
        public const string ZeroAppId = "5370600";
        public const string Ac8AppId = "2288340";
        public const string ZeroInstallDir = "ACE COMBAT ZERO THE BELKAN WAR (Steam Version)";
        public const string Ac8InstallDir = "ACE COMBAT 8";
        public const string SubDir = "ACZero";
        public const string ExeName = "acz.exe";

        /// <summary>Папка ACZero.</summary>
        public string Root { get; }
        public GameKind Kind { get; }
        public string Exe => Path.Combine(Root, ExeName);

        private GameInstall(string root)
        {
            Root = root;
            Kind = DetectKind(root);
        }

        private static GameKind DetectKind(string root)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? "");
            bool steam = root.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0;
            if (steam && string.Equals(parent, ZeroInstallDir, StringComparison.OrdinalIgnoreCase)) return GameKind.SteamZero;
            if (steam && string.Equals(parent, Ac8InstallDir, StringComparison.OrdinalIgnoreCase)) return GameKind.SteamAc8;
            return GameKind.Other;
        }

        public string KindText => Kind switch
        {
            GameKind.SteamZero => "Steam: ACE COMBAT ZERO",
            GameKind.SteamAc8 => "Steam: Zero в составе ACE COMBAT 8",
            _ => "папка указана вручную",
        };

        public static bool IsGameDir(string dir)
            => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "a.arc")) && File.Exists(Path.Combine(dir, "d.arc"));

        /// <summary>Принимает папку ACZero, папку игры над ней, любой файл внутри — находит ACZero.</summary>
        public static GameInstall? FromAnyPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string? dir;
            try
            {
                dir = path!.Trim().Trim('"');
                if (File.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (dir == null || !Directory.Exists(dir)) return null;
                dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
                if (dir.EndsWith(":")) dir += "\\";
            }
            catch { return null; }

            if (IsGameDir(Path.Combine(dir, SubDir))) return new GameInstall(Path.Combine(dir, SubDir));
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (IsGameDir(dir)) return new GameInstall(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    /// <summary>Автопоиск: соседство с лаунчером, библиотеки Steam (libraryfolders.vdf + appmanifest), типовые папки.</summary>
    public static class GameLocator
    {
        public static GameInstall? AutoDetect(string? launcherDir = null)
        {
            foreach (var g in FindAll(launcherDir)) return g;
            return null;
        }

        /// <summary>Все найденные копии без повторов: сначала отдельная Zero, потом Zero из AC8.</summary>
        public static IEnumerable<GameInstall> FindAll(string? launcherDir)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Candidates(launcherDir))
            {
                GameInstall? g;
                try { g = GameInstall.FromAnyPath(c); } catch { g = null; }
                if (g != null && seen.Add(g.Root)) yield return g;
            }
        }

        public static IEnumerable<string> Candidates(string? launcherDir)
        {
            // 1. лаунчер положили в папку игры
            if (!string.IsNullOrEmpty(launcherDir)) yield return launcherDir!;

            var libs = new List<string>(SteamLibraries());
            // 2. отдельная Zero: installdir из appmanifest, иначе имя папки по умолчанию
            foreach (var lib in libs)
                yield return Path.Combine(lib, "steamapps", "common", InstallDir(lib, GameInstall.ZeroAppId) ?? GameInstall.ZeroInstallDir, GameInstall.SubDir);
            // 3. Zero в составе AC8
            foreach (var lib in libs)
                yield return Path.Combine(lib, "steamapps", "common", InstallDir(lib, GameInstall.Ac8AppId) ?? GameInstall.Ac8InstallDir, GameInstall.SubDir);

            // 4. типовые папки библиотек на всех дисках (Steam не в реестре)
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { yield break; }
            foreach (var d in drives)
            {
                bool ready;
                try { ready = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ready = false; }
                if (!ready) continue;
                foreach (var lib in new[] { "SteamLibrary", @"Steam", @"Program Files (x86)\Steam", @"Games\Steam" })
                {
                    var root = Path.Combine(d.RootDirectory.FullName, lib, "steamapps", "common");
                    yield return Path.Combine(root, GameInstall.ZeroInstallDir, GameInstall.SubDir);
                    yield return Path.Combine(root, GameInstall.Ac8InstallDir, GameInstall.SubDir);
                }
            }
        }

        /// <summary>installdir из steamapps\appmanifest_&lt;appid&gt;.acf (null — нет такого).</summary>
        public static string? InstallDir(string library, string appId)
        {
            try
            {
                var acf = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
                if (!File.Exists(acf)) return null;
                foreach (var raw in File.ReadAllLines(acf))
                {
                    var v = VdfValue(raw, "installdir");
                    if (v != null) return v;
                }
            }
            catch { }
            return null;
        }

        public static IEnumerable<string> SteamLibraries()
        {
            var steam = ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam!.Replace('/', '\\');
            yield return steam;
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            string[] lines;
            try { lines = File.Exists(vdf) ? File.ReadAllLines(vdf) : new string[0]; } catch { yield break; }
            foreach (var raw in lines)
            {
                var v = VdfValue(raw, "path");
                if (v != null && !string.Equals(v.TrimEnd('\\'), steam.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) yield return v;
            }
        }

        /// <summary>Строка вида  "key"  "value"  → value (с раскрытием \\).</summary>
        internal static string? VdfValue(string raw, string key)
        {
            var line = raw.Trim();
            var head = "\"" + key + "\"";
            if (!line.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return null;
            var q = line.IndexOf('"', head.Length);
            if (q < 0) return null;
            var end = line.IndexOf('"', q + 1);
            if (end < 0) return null;
            return line.Substring(q + 1, end - q - 1).Replace(@"\\", @"\");
        }

        private static string? ReadRegistry(RegistryHive hive, string sub, string value)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(sub);
                return k?.GetValue(value) as string;
            }
            catch { return null; }
        }
    }

    public static class GameLauncher
    {
        /// <summary>Steam-копии — через Steam (оверлей, достижения, защита как обычно); другие — acz.exe напрямую.</summary>
        public static void Launch(GameInstall game)
        {
            string? appId = game.Kind switch { GameKind.SteamZero => GameInstall.ZeroAppId, GameKind.SteamAc8 => GameInstall.Ac8AppId, _ => null };
            if (appId != null)
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/" + appId) { UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo(game.Exe) { WorkingDirectory = game.Root, UseShellExecute = true });
        }
    }
}
