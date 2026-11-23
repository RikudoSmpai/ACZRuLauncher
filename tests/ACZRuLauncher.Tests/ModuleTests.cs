using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using ACZRuLauncher.Core;
using ACZRuLauncher.Core.Pack;
using Xunit;

namespace ACZRuLauncher.Tests
{
    /// <summary>Модуль acz.exe (version.dll): ставится только в отдельную Zero, без античита, с кириллицей в шрифте;
    /// чужой version.dll не перезаписывается и не удаляется. Байты модуля — синтетические.</summary>
    public class ModuleTests
    {
        private static readonly byte[] Ours = Encoding.ASCII.GetBytes("MZ fake module v2");
        private static readonly byte[] OursOld = Encoding.ASCII.GetBytes("MZ fake module v1");
        private static readonly byte[] Foreign = Encoding.ASCII.GetBytes("MZ ReShade or another mod");
        private static readonly List<string> WithFont = new List<string> { ExeModule.FontFeature };
        private static readonly List<string> Log = new List<string>();

        private static (string Game, string Backup) Dirs()
        {
            var root = TestDir.New();
            var game = Path.Combine(root, "ACE COMBAT ZERO THE BELKAN WAR (Steam Version)", "ACZero");
            Directory.CreateDirectory(game);
            return (game, Path.Combine(root, "backup"));
        }

        private static string Dll(string game) => Path.Combine(game, ExeModule.FileName);

        [Fact]
        public void Install_Remove_OnlyOurFile()
        {
            var (game, bk) = Dirs();
            var m = new ExeModule(Ours, GameKind.SteamZero);
            Assert.Equal(ModuleState.NotInstalled, m.Inspect(game, bk, WithFont));
            Assert.Equal(ModuleState.Installed, m.Install(game, bk, WithFont, Log.Add, out var msg));
            Assert.Null(msg);
            Assert.Equal(Ours, File.ReadAllBytes(Dll(game)));
            Assert.False(File.Exists(Dll(game) + ".acz-tmp"));
            Assert.Equal(ModuleState.Installed, m.Inspect(game, bk, WithFont));
            Assert.True(m.Remove(game, bk, Log.Add));
            Assert.False(File.Exists(Dll(game)));
            Assert.Equal(ModuleState.NotInstalled, m.Inspect(game, bk, WithFont));
        }

        [Fact]
        public void ForeignVersionDll_IsNeverOverwrittenOrDeleted()
        {
            var (game, bk) = Dirs();
            File.WriteAllBytes(Dll(game), Foreign);
            var m = new ExeModule(Ours, GameKind.SteamZero);
            Assert.Equal(ModuleState.Conflict, m.Inspect(game, bk, WithFont));
            Assert.Equal(ModuleState.Conflict, m.Install(game, bk, WithFont, Log.Add, out var msg));
            Assert.Contains("другой программы", msg);
            Assert.Equal(Foreign, File.ReadAllBytes(Dll(game)));
            Assert.False(m.Remove(game, bk, Log.Add));
            Assert.Equal(Foreign, File.ReadAllBytes(Dll(game)));
        }

        [Fact]
        public void UserReplacedOurFile_RemovalKeepsIt()
        {
            var (game, bk) = Dirs();
            var m = new ExeModule(Ours, GameKind.SteamZero);
            m.Install(game, bk, WithFont, Log.Add, out _);
            File.WriteAllBytes(Dll(game), Foreign);                 // игрок поставил свой version.dll поверх
            Assert.False(m.Remove(game, bk, Log.Add));
            Assert.Equal(Foreign, File.ReadAllBytes(Dll(game)));
        }

        [Fact]
        public void OlderOwnModule_IsUpdated_AndOldShaStillRemovable()
        {
            var (game, bk) = Dirs();
            new ExeModule(OursOld, GameKind.SteamZero).Install(game, bk, WithFont, Log.Add, out _);
            var m = new ExeModule(Ours, GameKind.SteamZero);
            Assert.Equal(ModuleState.Outdated, m.Inspect(game, bk, WithFont));
            Assert.Equal(ModuleState.Installed, m.Install(game, bk, WithFont, Log.Add, out _));
            Assert.Equal(Ours, File.ReadAllBytes(Dll(game)));

            // старый модуль, которого новый лаунчер не собирал, тоже удаляется по списку
            File.WriteAllBytes(Dll(game), OursOld);
            Assert.True(m.Remove(game, bk, Log.Add));
            Assert.False(File.Exists(Dll(game)));
        }

        [Fact]
        public void NotInstalled_ForAc8_Other_AntiCheat_OrPackWithoutFont()
        {
            foreach (var kind in new[] { GameKind.SteamAc8, GameKind.Other })
            {
                var (game, bk) = Dirs();
                var m = new ExeModule(Ours, kind);
                Assert.Equal(ModuleState.Unsupported, m.Install(game, bk, WithFont, Log.Add, out _));
                Assert.False(File.Exists(Dll(game)));
            }
            {
                var (game, bk) = Dirs();
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(game)!, "EasyAntiCheat"));   // как в AC8: папкой выше
                var m = new ExeModule(Ours, GameKind.SteamZero);
                Assert.Equal(ModuleState.AntiCheat, m.Install(game, bk, WithFont, Log.Add, out var msg));
                Assert.Contains("античит", msg);
                Assert.False(File.Exists(Dll(game)));
            }
            {
                var (game, bk) = Dirs();
                File.WriteAllBytes(Path.Combine(game, "start_protected_game.exe"), new byte[] { 1 });
                Assert.Equal(ModuleState.AntiCheat, new ExeModule(Ours, GameKind.SteamZero).Install(game, bk, WithFont, Log.Add, out _));
                Assert.False(File.Exists(Dll(game)));
            }
            foreach (var feats in new List<string>?[] { null, new List<string>(), new List<string> { "other" } })
            {
                var (game, bk) = Dirs();
                Assert.Equal(ModuleState.NoFont, new ExeModule(Ours, GameKind.SteamZero).Install(game, bk, feats, Log.Add, out _));
                Assert.False(File.Exists(Dll(game)));
            }
        }

        [Fact]
        public void OwnOldModule_WhereNoLongerAllowed_IsRemovedOnInstall()
        {
            var (game, bk) = Dirs();
            new ExeModule(OursOld, GameKind.SteamZero).Install(game, bk, WithFont, Log.Add, out _);
            var m = new ExeModule(Ours, GameKind.SteamZero);
            Assert.Equal(ModuleState.NoFont, m.Install(game, bk, new List<string>(), Log.Add, out _));   // новый пакет без шрифта
            Assert.False(File.Exists(Dll(game)));
        }

        [Fact]
        public void PackManifest_Features_RoundTrip()
        {
            var json = Encoding.UTF8.GetBytes("{\"format\":\"acz-ru-pack/1\",\"version\":\"x\",\"features\":[\"hud-font-h8-cyr\"]," +
                                              "\"archives\":{\"a.arc\":{\"orig_size\":1,\"orig_sha256\":\"" + new string('0', 64) + "\",\"files\":[]}}}");
            Assert.Equal(new[] { ExeModule.FontFeature }, PackManifest.Parse(json).Features);
            var old = Encoding.UTF8.GetBytes("{\"format\":\"acz-ru-pack/1\",\"version\":\"x\"," +
                                             "\"archives\":{\"a.arc\":{\"orig_size\":1,\"orig_sha256\":\"" + new string('0', 64) + "\",\"files\":[]}}}");
            Assert.Null(PackManifest.Parse(old).Features);
        }

        [Fact]
        public void EmbeddedModule_IfBuilt_IsAVersionDllProxy()
        {
            var dll = ExeModule.Embedded();
            if (dll == null) return;                                   // лаунчер собран без module\build — модуля нет
            Assert.Equal((byte)'M', dll[0]);
            Assert.Equal((byte)'Z', dll[1]);
            var text = Encoding.ASCII.GetString(dll);
            foreach (var name in new[] { "GetFileVersionInfoSizeW", "GetFileVersionInfoW", "VerQueryValueW", "VerLanguageNameW" })
                Assert.Contains(name, text);
            Assert.DoesNotContain("steam_api", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
