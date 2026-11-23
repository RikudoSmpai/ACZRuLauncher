using System.IO;
using System.Linq;
using System.Text;
using ACZRuLauncher.Core;
using Xunit;

namespace ACZRuLauncher.Tests
{
    /// <summary>SystemLanguage в settings\setting.ini: правится только значение, остальное — байт в байт.</summary>
    public class MenuLanguageTests
    {
        private static string Ini(string text)
        {
            var p = Path.Combine(TestDir.New(), "setting.ini");
            File.WriteAllBytes(p, Encoding.ASCII.GetBytes(text));
            return p;
        }

        private static string Text(string p) => Encoding.ASCII.GetString(File.ReadAllBytes(p));

        [Fact]
        public void Write_ChangesOnlyTheValue()
        {
            var p = Ini("A=1\r\nSystemLanguage=\t\t;JP US UK DE FR IT ES\r\nB=2\r\n");
            Assert.Equal("", MenuLanguage.Read(p));
            Assert.True(MenuLanguage.Write(p, "UK"));
            Assert.Equal("A=1\r\nSystemLanguage=UK\t\t;JP US UK DE FR IT ES\r\nB=2\r\n", Text(p));
            Assert.Equal("UK", MenuLanguage.Read(p));
            Assert.True(MenuLanguage.Write(p, ""));
            Assert.Equal("A=1\r\nSystemLanguage=\t\t;JP US UK DE FR IT ES\r\nB=2\r\n", Text(p));
        }

        [Fact]
        public void Read_ToleratesSpacesAndCase_ValueEndsAtTabOrComment()
        {
            Assert.Equal("US", MenuLanguage.Read(Ini("  systemlanguage = US;comment\n")));
            Assert.Equal("UK", MenuLanguage.Read(Ini("SystemLanguage=UK")));
            Assert.Null(MenuLanguage.Read(Ini("SystemLanguageX=UK\nOther=1\n")));
            Assert.Null(MenuLanguage.Read(Path.Combine(TestDir.New(), "nope.ini")));
            Assert.True(MenuLanguage.IsRussian("uk"));
            Assert.False(MenuLanguage.IsRussian(null));
        }

        [Fact]
        public void MissingKey_IsAppended_AndRemovedBack()
        {
            var orig = "Resolution=1280x720\r\nMsaa=x1";                  // без перевода строки в конце
            var p = Ini(orig);
            Assert.Null(MenuLanguage.Read(p));
            MenuLanguage.Write(p, "UK");
            Assert.Equal(orig + "\r\nSystemLanguage=UK\r\n", Text(p));
            Assert.True(MenuLanguage.RemoveKey(p));
            Assert.Equal(orig + "\r\n", Text(p));                          // дописанный перевод строки остаётся

            var lf = Ini("A=1\n");
            MenuLanguage.Write(lf, "UK");
            Assert.Equal("A=1\nSystemLanguage=UK\n", Text(lf));
            MenuLanguage.RemoveKey(lf);
            Assert.Equal("A=1\n", Text(lf));
        }

        [Fact]
        public void NoFile_NothingCreated()
        {
            var p = Path.Combine(TestDir.New(), "settings", "setting.ini");
            Assert.False(MenuLanguage.Write(p, "UK"));
            Assert.False(MenuLanguage.RemoveKey(p));
            Assert.False(File.Exists(p));
        }

        [Fact]
        public void NonAsciiBytes_SurviveRoundTrip()
        {
            var p = Path.Combine(TestDir.New(), "setting.ini");
            // BOM, байт вне ASCII и CRLF не портятся
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'X', (byte)'=', 0xC0, 0x0D, 0x0A }
                        .Concat(Encoding.ASCII.GetBytes("SystemLanguage=US\r\n")).ToArray();
            File.WriteAllBytes(p, bytes);
            MenuLanguage.Write(p, "UK");
            Assert.Equal("UK", MenuLanguage.Read(p));
            MenuLanguage.Write(p, "US");
            Assert.Equal(bytes, File.ReadAllBytes(p));
        }
    }
}
