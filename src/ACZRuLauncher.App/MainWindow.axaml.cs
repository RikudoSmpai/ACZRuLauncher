using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ACZRuLauncher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            var args = Environment.GetCommandLineArgs();
            string? pack = null, demo = null, releasesUrl = null, shot = null, game = null;
            bool autoCheck = true;
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[i + 1].Trim('"', '\'') : "";
                switch (args[i])
                {
                    case "--pack": pack = Next(); break;
                    case "--demo": demo = Next(); break;
                    case "--releases-url": releasesUrl = Next(); break;
                    case "--shot": shot = Next(); break;
                    case "--game": game = Next(); break;
                    case "--no-update-check": autoCheck = false; break;
                }
            }
            if ((demo != null || shot != null) && releasesUrl == null) autoCheck = false;   // показ/скриншот — без сети, если не задан свой источник
            _vm = new MainViewModel(args[0], pack, releasesUrl, autoCheck, game);
            if (demo != null) _vm.ApplyDemo(demo);
            _vm.RequestClose += () => Avalonia.Threading.Dispatcher.UIThread.Post(Close);
            DataContext = _vm;

            // --shot <png>: окно рендерит себя в файл и закрывается (проверка вида без захвата экрана)
            if (shot != null)
            {
                // за краем экрана и без фокуса: снимок не мешает тому, кто работает за компьютером
                ShowActivated = false;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new Avalonia.PixelPoint(-20000, -20000);
                Opened += async (_, _) =>
                {
                    await System.Threading.Tasks.Task.Delay(releasesUrl != null ? 2000 : 600);
                    try
                    {
                        var size = new Avalonia.PixelSize((int)Width, (int)Height);
                        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                        rtb.Render(this);
                        rtb.Save(shot);
                    }
                    catch (Exception ex) { System.IO.File.WriteAllText(shot + ".err.txt", ex.ToString()); }
                    Close();
                };
            }
        }

        // окно без системной рамки: тянем за шапку
        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private async void Primary_Click(object? sender, RoutedEventArgs e) => await _vm.PrimaryAsync();

        private async void Remove_Click(object? sender, RoutedEventArgs e) => await _vm.RemoveAsync();
        private async void Retry_Click(object? sender, RoutedEventArgs e) => await _vm.RetryAsync();
        private void Info_Click(object? sender, RoutedEventArgs e) { _vm.AboutOpen = false; _vm.InfoOpen = !_vm.InfoOpen; }
        private void InfoClose_Click(object? sender, RoutedEventArgs e) => _vm.InfoOpen = false;
        private void ProjectPrev_Click(object? sender, RoutedEventArgs e) => _vm.NextProject(-1);
        private void ProjectNext_Click(object? sender, RoutedEventArgs e) => _vm.NextProject(1);
        private void ProjectOpen_Click(object? sender, RoutedEventArgs e) => OpenUrl(_vm.Project.Url);
        private void Path_Click(object? sender, RoutedEventArgs e) => _vm.PathOpen = !_vm.PathOpen;

        private void About_Click(object? sender, RoutedEventArgs e) { _vm.InfoOpen = false; _vm.AboutOpen = !_vm.AboutOpen; }
        private void AboutClose_Click(object? sender, RoutedEventArgs e) => _vm.AboutOpen = false;
        private void Repo_Click(object? sender, RoutedEventArgs e) => OpenUrl(_vm.RepoUrl);
        private async void UpdateAction_Click(object? sender, RoutedEventArgs e) => await _vm.UpdateActionAsync();

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* браузера нет — молчим */ }
        }

        private async void Browse_Click(object? sender, RoutedEventArgs e)
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Папка ACZero (в ней a.arc и d.arc)",
                AllowMultiple = false,
            });
            var folder = picked.FirstOrDefault();
            if (folder == null) return;
            var path = folder.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _vm.SetPathFromPicker(path!);
        }
    }
}
