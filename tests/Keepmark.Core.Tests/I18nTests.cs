using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Keepmark.Core.I18n;

namespace Keepmark.Core.Tests;

public class I18nTests
{
    private static readonly string[] Others = ["it", "fr", "es", "de", "ja", "pt", "ru", "zh"];

    private static IEnumerable<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);

    [Fact]
    public void EveryLanguageHasTheSameKeysAsEnglish()
    {
        var en = Catalogs.Tables["en"];
        foreach (var lang in Others)
        {
            var table = Catalogs.Tables[lang];
            Assert.Equal(en.Keys.OrderBy(x => x, StringComparer.Ordinal), table.Keys.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TranslationsAreNotEmptyAndKeepPlaceholders()
    {
        var en = Catalogs.Tables["en"];
        foreach (var lang in Others)
        {
            foreach (var (key, english) in en)
            {
                var text = Catalogs.Tables[lang][key];
                Assert.False(string.IsNullOrWhiteSpace(text), $"{lang}:{key} is empty");
                Assert.True(Placeholders(english).SequenceEqual(Placeholders(text)), $"{lang}:{key} placeholders differ");
            }
        }
    }

    [Fact]
    public void EveryStrConstantExistsInEnglish()
    {
        var en = Catalogs.Tables["en"];
        var fields = typeof(Str).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));
        foreach (var f in fields)
        {
            var key = (string)f.GetRawConstantValue()!;
            // Plural bases (Loc.Plural) exist as key_one / key_other.
            Assert.True(en.ContainsKey(key) || en.ContainsKey(key + "_one"), f.Name);
        }
    }

    [Fact]
    public void EveryOneFormHasAnOtherForm()
    {
        foreach (var key in Catalogs.Tables["en"].Keys.Where(k => k.EndsWith("_one", StringComparison.Ordinal)))
        {
            Assert.True(Catalogs.Tables["en"].ContainsKey(key[..^4] + "_other"), key);
        }
    }

    [Theory]
    [InlineData("system", "it-CH", "it")]
    [InlineData("system", "pt-BR", "pt")]
    [InlineData("system", "ja-JP", "ja")]
    [InlineData("system", "ru-RU", "ru")]
    [InlineData("system", "zh-TW", "zh")]
    [InlineData("system", "ko-KR", "en")]
    [InlineData("fr", "it-IT", "fr")]
    [InlineData("xx", "de-DE", "de")]
    public void ResolvePicksExplicitThenSystemThenEnglish(string setting, string system, string expected) =>
        Assert.Equal(expected, Langs.Resolve(setting, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void LocFollowsTheCurrentUiCulture()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it-IT");
            Assert.Equal("Lingua", Loc.Get(Str.SettingsLanguage));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.Equal("Idioma", Loc.Get(Str.SettingsLanguage));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            Assert.Equal("Language", Loc.Get(Str.SettingsLanguage));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
