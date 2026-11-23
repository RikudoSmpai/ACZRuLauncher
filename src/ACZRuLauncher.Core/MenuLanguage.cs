using System;
using System.IO;
using System.Text;

namespace ACZRuLauncher.Core
{
    /// <summary>
    /// Язык меню Zero — строка <c>SystemLanguage=</c> в <c>ACZero\settings\setting.ini</c> (варианты в комментарии
    /// файла: JP US UK DE FR IT ES; пусто — английский US). В самой игре его не выбрать: в «Language settings» только
    /// озвучка и субтитры. Пакет ставит русский на место UK, поэтому русское меню — это <c>SystemLanguage=UK</c>
    /// (проверено в игре 13.11.2026: главное меню, меню вылета, брифинг, титул «[ НАЖМИТЕ ЛЮБУЮ КНОПКУ ]»; окно и
    /// логотип при этом европейские — «ACE COMBAT THE BELKAN WAR»). Без него меню английское, русские только
    /// субтитры (пункт RUSSIAN в списке).
    /// Меняется только значение после «=»: табы, комментарий и переводы строк остаются байт в байт.
    /// </summary>
    public static class MenuLanguage
    {
        public const string Key = "SystemLanguage";
        public const string Russian = "UK";

        /// <summary>Байт = символ (Latin-1): чтение и запись без потерь, какой бы ни была кодировка файла.</summary>
        private static readonly Encoding Bytes = Encoding.GetEncoding(28591);

        public static string IniPath(string gameDir) => Path.Combine(gameDir, "settings", "setting.ini");

        public static bool IsRussian(string? value) => string.Equals(value, Russian, StringComparison.OrdinalIgnoreCase);

        /// <summary>Значение ключа; "" — ключ есть, значение пустое; null — нет файла или нет ключа.</summary>
        public static string? Read(string iniPath)
        {
            if (!File.Exists(iniPath)) return null;
            var s = Bytes.GetString(File.ReadAllBytes(iniPath));
            return Find(s, out _, out _, out var v0, out var v1) ? s.Substring(v0, v1 - v0) : null;
        }

        /// <summary>Поставить значение. Нет ключа — дописать строку в конец (перевод строки как в файле).
        /// Нет файла — false: файл создаёт сама игра, лаунчер его не выдумывает.</summary>
        public static bool Write(string iniPath, string value)
        {
            if (!File.Exists(iniPath)) return false;
            var s = Bytes.GetString(File.ReadAllBytes(iniPath));
            string res;
            if (Find(s, out _, out _, out var v0, out var v1))
                res = s.Substring(0, v0) + value + s.Substring(v1);
            else
            {
                var nl = s.Contains("\r\n") ? "\r\n" : "\n";
                res = s + (s.Length == 0 || s.EndsWith("\n") ? "" : nl) + Key + "=" + value + nl;
            }
            if (res != s) WriteAtomic(iniPath, Bytes.GetBytes(res));
            return true;
        }

        /// <summary>Убрать строку ключа целиком, с её переводом строки (если строку дописал лаунчер).</summary>
        public static bool RemoveKey(string iniPath)
        {
            if (!File.Exists(iniPath)) return false;
            var s = Bytes.GetString(File.ReadAllBytes(iniPath));
            if (!Find(s, out var l0, out var l1, out _, out _)) return false;
            WriteAtomic(iniPath, Bytes.GetBytes(s.Substring(0, l0) + s.Substring(l1)));
            return true;
        }

        /// <summary>Первая строка ключа: [l0, l1) — строка с её «\n», [v0, v1) — значение: после «=» и пробелов,
        /// до таба, пробела, «;», «\r» или конца строки.</summary>
        private static bool Find(string s, out int l0, out int l1, out int v0, out int v1)
        {
            int pos = 0;
            while (pos < s.Length)
            {
                int nl = s.IndexOf('\n', pos);
                int eol = nl < 0 ? s.Length : nl;
                int i = pos;
                while (i < eol && (s[i] == ' ' || s[i] == '\t')) i++;
                if (eol - i >= Key.Length && string.Compare(s, i, Key, 0, Key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int j = i + Key.Length;
                    while (j < eol && (s[j] == ' ' || s[j] == '\t')) j++;
                    if (j < eol && s[j] == '=')
                    {
                        l0 = pos; l1 = nl < 0 ? s.Length : nl + 1;
                        v0 = j + 1;
                        while (v0 < eol && s[v0] == ' ') v0++;
                        v1 = v0;
                        while (v1 < eol && s[v1] != '\t' && s[v1] != ' ' && s[v1] != ';' && s[v1] != '\r') v1++;
                        return true;
                    }
                }
                if (nl < 0) break;
                pos = nl + 1;
            }
            l0 = l1 = v0 = v1 = -1;
            return false;
        }

        /// <summary>Файл в несколько сотен байт: пишется целиком, без временных файлов в папке игры.</summary>
        private static void WriteAtomic(string path, byte[] data) => File.WriteAllBytes(path, data);
    }
}
