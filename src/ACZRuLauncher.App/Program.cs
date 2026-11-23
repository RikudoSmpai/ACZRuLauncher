using System;
using Avalonia;

namespace ACZRuLauncher
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // --updated-from <старый exe>: мы — свежескачанная версия, убираем старый файл
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == ACZRuLauncher.Core.SelfUpdate.UpdatedFromArg) ACZRuLauncher.Core.SelfUpdate.CleanupOld(args[i + 1].Trim('"', '\''));
            // --selftest <ACZero> [--pack <zip|папка>] [--backup-dir <папка>]: установить → проверить → удалить → проверить, без окна
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest") return SelfTest.Run(args, args[i + 1].Trim('"', '\''), SelfTest.Mode.Full);
            // --selftest-install / --selftest-remove <ACZero>: только поставить / только снять (ручная сверка с Python)
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest-install") return SelfTest.Run(args, args[i + 1].Trim('"', '\''), SelfTest.Mode.InstallOnly);
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest-remove") return SelfTest.Run(args, args[i + 1].Trim('"', '\''), SelfTest.Mode.RemoveOnly);
            // --selftest-channel [--log f]: канал (release/dev) и лента обновлений этой сборки в лог
            if (Array.IndexOf(args, "--selftest-channel") >= 0) return SelfTest.RunChannel(args);
            // --selftest-update <url latest.json> [--swap [аргументы новому exe]]: проверка/скачивание/подмена без окна
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest-update") return SelfTest.RunUpdate(args, i + 1);
            try
            {
                NativeLoader.Ensure();
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // окно без консоли: падение на старте иначе было бы немым
                try
                {
                    var log = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".", "ACZRuLauncher.log");
                    System.IO.File.AppendAllText(log, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
                }
                catch { }
                return 1;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
