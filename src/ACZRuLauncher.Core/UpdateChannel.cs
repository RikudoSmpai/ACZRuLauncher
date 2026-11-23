using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ACZRuLauncher.Core
{
    /// <summary>
    /// Канал обновлений, заданный при сборке: release (GitHub, теги acz-v*) или dev (лента latest.json на нашем сервере).
    /// Канал и адрес ленты приходят только из метаданных сборки ACZRuLauncher.Core
    /// (<c>-p:AczChannel=dev -p:AczDevFeedUrl=https://…/latest.json</c>), в исходниках адреса нет.
    /// </summary>
    public sealed class UpdateChannel
    {
        public const string ChannelKey = "AczChannel";
        public const string FeedKey = "AczDevFeedUrl";

        public bool IsDev { get; }
        /// <summary>DEV: адрес latest.json; null — лента не задана (сборка без адреса). Release — всегда null.</summary>
        public string? DevFeedUrl { get; }

        private UpdateChannel(bool isDev, string? devFeedUrl) { IsDev = isDev; DevFeedUrl = devFeedUrl; }

        public static readonly UpdateChannel Release = new UpdateChannel(false, null);

        /// <summary>Канал этой сборки (метаданные ACZRuLauncher.Core).</summary>
        public static UpdateChannel Current { get; } = FromAssembly(typeof(UpdateChannel).Assembly);

        public static UpdateChannel FromAssembly(Assembly asm) =>
            FromMetadata(asm.GetCustomAttributes<AssemblyMetadataAttribute>().Select(a => new KeyValuePair<string, string?>(a.Key, a.Value)));

        public static UpdateChannel FromMetadata(IEnumerable<KeyValuePair<string, string?>> metadata)
        {
            string? channel = null, feed = null;
            foreach (var kv in metadata)
            {
                if (kv.Key == ChannelKey) channel = kv.Value;
                else if (kv.Key == FeedKey) feed = kv.Value;
            }
            return FromValues(channel, feed);
        }

        /// <summary>dev (без учёта регистра) → DEV; всё прочее, включая пусто, → release. Лента DEV — только абсолютный http(s).</summary>
        public static UpdateChannel FromValues(string? channel, string? feedUrl)
        {
            if (!string.Equals(channel?.Trim(), "dev", StringComparison.OrdinalIgnoreCase)) return Release;
            var f = feedUrl?.Trim();
            if (string.IsNullOrEmpty(f) || !Uri.TryCreate(f, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                f = null;
            return new UpdateChannel(true, f);
        }

        // ------------------------------------------------------------------ что видит пользователь

        /// <summary>Откуда берём список релизов: --releases-url перекрывает всё; DEV — лента; release — GitHub API.
        /// null — DEV без ленты (проверять нечего).</summary>
        public string? FeedFor(string? overrideUrl) =>
            !string.IsNullOrEmpty(overrideUrl) ? overrideUrl : IsDev ? DevFeedUrl : Updates.ReleasesApiUrl;

        /// <summary>«Все версии»: DEV — папка ленты (адрес без имени файла, со слэшем), release — страница релизов GitHub.</summary>
        public string ReleasesPageUrl => IsDev ? (FeedFolder(DevFeedUrl) ?? "") : Updates.ReleasesPageUrl;

        /// <summary>Имя источника в текстах: «нет связи с …», источник прогресса.</summary>
        public string SourceName => IsDev ? "сервер обновлений" : "GitHub";
        /// <summary>Подпись над полосой прогресса при скачивании новой версии.</summary>
        public string ProgressSource => IsDev ? "Сервер обновлений (DEV)" : "GitHub";
        public string NoConnectionText => "нет связи с " + (IsDev ? "сервером обновлений" : "GitHub") + ".";

        public string WindowTitle(string baseTitle) => IsDev ? baseTitle + " — DEV" : baseTitle;
        /// <summary>Строка версии в углу окна: «перевод X» → «DEV · перевод X».</summary>
        public string VersionLine(string line) => IsDev ? "DEV · " + line : line;
        public string AppVersionText(Version v) => "Версия " + v + (IsDev ? " · DEV" : "");

        /// <summary>https://host/a/b/latest.json → https://host/a/b/ (без query и fragment).</summary>
        public static string? FeedFolder(string? feedUrl)
        {
            if (string.IsNullOrEmpty(feedUrl) || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var u)) return null;
            var path = u.AbsolutePath;
            var slash = path.LastIndexOf('/');
            path = slash >= 0 ? path.Substring(0, slash + 1) : "/";
            return u.GetLeftPart(UriPartial.Authority) + path;
        }
    }
}
