using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ACZRuLauncher.Core
{
    /// <summary>Ассет релиза GitHub (нам нужны ACZRuLauncher.exe и ACZRuLauncher.exe.sha256).</summary>
    [DataContract]
    public sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "browser_download_url")] public string Url = "";
        [DataMember(Name = "size")] public long Size;
    }

    /// <summary>Релиз из GET /repos/{owner}/{repo}/releases — только нужные поля, остальное игнорируется.</summary>
    [DataContract]
    public sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag = "";
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "html_url")] public string HtmlUrl = "";
        [DataMember(Name = "body")] public string Body = "";
        [DataMember(Name = "prerelease")] public bool Prerelease;
        [DataMember(Name = "draft")] public bool Draft;
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets = new List<ReleaseAsset>();

        public Version? Version => Updates.ParseVersion(Tag);
        public ReleaseAsset? Exe => Find(Updates.ExeAssetName);
        public ReleaseAsset? ExeSha => Find(Updates.ExeAssetName + ".sha256");

        private ReleaseAsset? Find(string name)
        {
            foreach (var a in Assets)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }

    /// <summary>
    /// Клиент релизов GitHub: проверка версии, скачивание, sha256. Сеть только здесь и только по запросу.
    /// Свой репозиторий ACZ (с 23.11 отдельно от лаунчера Project Wingman); теги acz-vX.Y.Z.
    /// Берём список релизов и оставляем только acz-v* — так надёжнее «latest» (черновики пакетов и прочее не мешают).
    /// </summary>
    public static class Updates
    {
        public const string Owner = "RikudoSmpai";
        public const string Repo = "ACZRuLauncher";
        public const string TagPrefix = "acz-v";
        public const string ExeAssetName = "ACZRuLauncher.exe";
        public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
        public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";
        public static string ReleasesApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50";

        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return Normalize(v);
            }
        }

        /// <summary>Версия из тега acz-vX.Y.Z; любой другой тег (в т.ч. vX.Y.Z у PW) — null.</summary>
        public static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag!.Trim();
            if (!t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            t = t.Substring(TagPrefix.Length);
            var cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) t = t.Substring(0, cut);
            return Version.TryParse(t, out var v) ? Normalize(v) : null;
        }

        public static Version Normalize(Version v) => new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

        private static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"ACZRuLauncher/{CurrentVersion}");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.Timeout = TimeSpan.FromMinutes(30);
            return c;
        }

        /// <summary>Самый новый релиз ACZ (тег acz-v*, не черновик, не пре-релиз, с exe). null — таких нет.
        /// apiUrl для тестов: файл/URL со списком релизов (массив) или с одним релизом (объект).</summary>
        public static async Task<ReleaseInfo?> FetchLatestAsync(string? apiUrl, CancellationToken ct)
        {
            using var client = MakeClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var json = await client.GetStringAsync(apiUrl ?? ReleasesApiUrl).ConfigureAwait(false);
            return PickLatest(ParseReleases(json));
        }

        public static List<ReleaseInfo> ParseReleases(string json)
        {
            var t = json.TrimStart();
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
            List<ReleaseInfo> list;
            if (t.StartsWith("["))
                list = (List<ReleaseInfo>?)new DataContractJsonSerializer(typeof(List<ReleaseInfo>)).ReadObject(ms) ?? new List<ReleaseInfo>();
            else
            {
                var one = (ReleaseInfo?)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(ms);
                list = one == null ? new List<ReleaseInfo>() : new List<ReleaseInfo> { one };
            }
            foreach (var r in list) if (r.Assets == null) r.Assets = new List<ReleaseAsset>();
            return list;
        }

        public static ReleaseInfo? PickLatest(IEnumerable<ReleaseInfo> releases)
        {
            ReleaseInfo? best = null;
            foreach (var r in releases)
            {
                if (r.Draft || r.Prerelease || r.Version == null || r.Exe == null) continue;
                if (best == null || r.Version > best.Version) best = r;
            }
            return best;
        }

        public static async Task<string> FetchTextAsync(string url, CancellationToken ct)
        {
            using var client = MakeClient();
            return await client.GetStringAsync(url).ConfigureAwait(false);
        }

        /// <summary>Скачивание в dest через .part с прогрессом 0..1 (по Content-Length или expectedSize).</summary>
        public static async Task DownloadAsync(string url, string dest, long expectedSize, IProgress<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            using (var client = MakeClient())
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? expectedSize;
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var buf = new byte[1 << 18];
                long done = 0; int n;
                var sw = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    done += n;
                    if (sw.ElapsedMilliseconds > 60) { progress.Report(total > 0 ? (double)done / total : 0); sw.Restart(); }
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            progress.Report(1);
        }

        public static string Sha256File(string path) => Hashing.Sha256File(path);

        /// <summary>Из текста вида "&lt;hex&gt;  ACZRuLauncher.exe" (формат sha256sum) достаёт хэш.</summary>
        public static string? ParseShaText(string text)
        {
            foreach (var tok in text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length == 64 && IsHex(tok)) return tok.ToLowerInvariant();
            return null;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    /// <summary>Подмена собственного exe: работающий файл нельзя перезаписать, но можно переименовать.</summary>
    public static class SelfUpdate
    {
        public const string OldSuffix = ".old";
        public const string UpdatedFromArg = "--updated-from";

        public static string StagingDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACZRuLauncher", "update");

        /// <summary>current → current.old, staged → current, запуск нового с --updated-from. Вызывающий после этого закрывается.</summary>
        public static void SwapAndStart(string currentExe, string stagedExe, string extraArgs)
        {
            var old = currentExe + OldSuffix;
            if (File.Exists(old)) File.Delete(old);
            File.Move(currentExe, old);
            try
            {
                File.Move(stagedExe, currentExe);
            }
            catch
            {
                File.Move(old, currentExe);   // откат: старый exe обратно на место
                throw;
            }
            var args = $"{UpdatedFromArg} \"{old}\"" + (string.IsNullOrEmpty(extraArgs) ? "" : " " + extraArgs);
            Process.Start(new ProcessStartInfo(currentExe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(currentExe) });
        }

        /// <summary>Новый exe после старта убирает старый файл (тот ещё секунду-другую может быть занят).</summary>
        public static void CleanupOld(string oldPath)
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (!File.Exists(oldPath)) return; File.Delete(oldPath); return; }
                catch { Thread.Sleep(250); }
            }
        }
    }
}
