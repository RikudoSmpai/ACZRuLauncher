using System;
using System.Collections.Generic;
using ACZRuLauncher.Core;
using Xunit;

namespace ACZRuLauncher.Tests
{
    public class ChannelTests
    {
        private const string Feed = "https://example.invalid/acz-dev/latest.json";

        private static string DevRelease(string tag) => $@"{{
            ""tag_name"":""{tag}"",""name"":""ACZ DEV {tag}"",""draft"":false,""prerelease"":false,
            ""assets"":[
              {{""name"":""ACZRuLauncher.exe"",""browser_download_url"":""https://example.invalid/acz-dev/{tag}/ACZRuLauncher.exe"",""size"":123}},
              {{""name"":""ACZRuLauncher.exe.sha256"",""browser_download_url"":""https://example.invalid/acz-dev/{tag}/ACZRuLauncher.exe.sha256"",""size"":85}}
            ]}}";

        private static IEnumerable<KeyValuePair<string, string?>> Meta(params (string k, string? v)[] kv)
        {
            foreach (var (k, v) in kv) yield return new KeyValuePair<string, string?>(k, v);
        }

        [Fact]
        public void DevFeedSingleObjectParses()
        {
            var list = Updates.ParseReleases(DevRelease("acz-v2026.1116.1"));
            Assert.Single(list);
            var r = Updates.PickLatest(list);
            Assert.NotNull(r);
            Assert.Equal(new Version(2026, 1116, 1), r!.Version);
            Assert.Equal("https://example.invalid/acz-dev/acz-v2026.1116.1/ACZRuLauncher.exe", r.Exe!.Url);
            Assert.Equal(123, r.Exe.Size);
            Assert.Equal("https://example.invalid/acz-dev/acz-v2026.1116.1/ACZRuLauncher.exe.sha256", r.ExeSha!.Url);
        }

        [Fact]
        public void DevVersionsCompare()
        {
            var json = "[" + DevRelease("acz-v2026.1116.1") + "," + DevRelease("acz-v2026.1116.2") + "," + DevRelease("acz-v1.0.0") + "]";
            var best = Updates.PickLatest(Updates.ParseReleases(json));
            Assert.Equal("acz-v2026.1116.2", best!.Tag);
            Assert.True(Updates.ParseVersion("acz-v2026.1116.2") > Updates.ParseVersion("acz-v2026.1116.1"));
            Assert.True(Updates.ParseVersion("acz-v2026.1116.1") > new Version(2026, 1116, 0));   // сборка -p:Version=2026.1116.0 увидит .1
            Assert.True(Updates.ParseVersion("acz-v2026.1117.1") > Updates.ParseVersion("acz-v2026.1116.9"));
            Assert.True(Updates.ParseVersion("acz-v1.0.0") < Updates.ParseVersion("acz-v2026.1116.1"));
        }

        [Fact]
        public void ChannelFromMetadata()
        {
            var dev = UpdateChannel.FromMetadata(Meta(("AczChannel", "dev"), ("AczDevFeedUrl", Feed), ("Other", "x")));
            Assert.True(dev.IsDev);
            Assert.Equal(Feed, dev.DevFeedUrl);

            var devNoFeed = UpdateChannel.FromMetadata(Meta(("AczChannel", "dev")));
            Assert.True(devNoFeed.IsDev);
            Assert.Null(devNoFeed.DevFeedUrl);
            Assert.Null(devNoFeed.FeedFor(null));                       // проверять нечего — лаунчер так и пишет
            Assert.Null(UpdateChannel.FromValues("dev", "not a url").DevFeedUrl);
            Assert.Null(UpdateChannel.FromValues("dev", "file:///c:/x/latest.json").DevFeedUrl);

            Assert.False(UpdateChannel.FromMetadata(Meta(("AczChannel", "release"), ("AczDevFeedUrl", Feed))).IsDev);
            Assert.False(UpdateChannel.FromMetadata(Meta()).IsDev);
            Assert.Null(UpdateChannel.FromMetadata(Meta(("AczChannel", "release"), ("AczDevFeedUrl", Feed))).DevFeedUrl);
        }

        [Fact]
        public void TestBuildIsRelease()
        {
            // тесты собирают Core без -p:AczChannel → канал release, метаданные есть
            var ch = UpdateChannel.FromAssembly(typeof(Updates).Assembly);
            Assert.False(ch.IsDev);
            Assert.False(UpdateChannel.Current.IsDev);
        }

        [Fact]
        public void ReleaseTextsUnchanged()
        {
            var ch = UpdateChannel.Release;
            Assert.Equal(Updates.ReleasesApiUrl, ch.FeedFor(null));
            Assert.Equal("file:///t.json", ch.FeedFor("file:///t.json"));
            Assert.Equal(Updates.ReleasesPageUrl, ch.ReleasesPageUrl);
            Assert.Equal("GitHub", ch.ProgressSource);
            Assert.Equal("нет связи с GitHub.", ch.NoConnectionText);
            Assert.Equal("Ace Combat Zero — русский перевод", ch.WindowTitle("Ace Combat Zero — русский перевод"));
            Assert.Equal("перевод 2026.11.16-1", ch.VersionLine("перевод 2026.11.16-1"));
            Assert.Equal("Версия 1.0.0", ch.AppVersionText(new Version(1, 0, 0)));
        }

        [Fact]
        public void DevTexts()
        {
            var ch = UpdateChannel.FromValues("dev", Feed);
            Assert.Equal(Feed, ch.FeedFor(null));
            Assert.Equal("http://127.0.0.1:1/x.json", ch.FeedFor("http://127.0.0.1:1/x.json"));   // --releases-url перекрывает
            Assert.Equal("https://example.invalid/acz-dev/", ch.ReleasesPageUrl);
            Assert.DoesNotContain("GitHub", ch.ProgressSource);
            Assert.Equal("нет связи с сервером обновлений.", ch.NoConnectionText);
            Assert.Equal("Ace Combat Zero — русский перевод — DEV", ch.WindowTitle("Ace Combat Zero — русский перевод"));
            Assert.Equal("DEV · перевод 2026.11.16-1", ch.VersionLine("перевод 2026.11.16-1"));
            Assert.Equal("Версия 2026.1116.0 · DEV", ch.AppVersionText(new Version(2026, 1116, 0)));
        }

        [Fact]
        public void FeedFolder()
        {
            Assert.Equal("https://example.invalid/acz-dev/", UpdateChannel.FeedFolder(Feed));
            Assert.Equal("https://h.example/a/b/", UpdateChannel.FeedFolder("https://h.example/a/b/latest.json?x=1#y"));
            Assert.Equal("https://h.example/", UpdateChannel.FeedFolder("https://h.example/latest.json"));
            Assert.Equal("https://h.example:8443/d/", UpdateChannel.FeedFolder("https://h.example:8443/d/latest.json"));
            Assert.Null(UpdateChannel.FeedFolder(null));
            Assert.Equal("", UpdateChannel.FromValues("dev", null).ReleasesPageUrl);
        }
    }
}
