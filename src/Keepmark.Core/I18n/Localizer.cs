using System.Globalization;

namespace Keepmark.Core.I18n;

// UI strings in en/it/fr/es/de. The active language is CurrentUICulture
// (set at app start from the Language setting). Log strings stay English.
// Every key lives in Str.g.cs and in the five Catalog.*.cs files.

public interface IXbsLocalizer
{
    string Get(string key, params object?[] args);
}

public static class Langs
{
    public static readonly string[] Supported = ["en", "it", "fr", "es", "de"];

    /// <summary>An explicit supported setting wins; otherwise the base language
    /// of <paramref name="systemCulture"/> if supported, else "en".</summary>
    public static string Resolve(string? setting, CultureInfo systemCulture)
    {
        if (setting is not null && Supported.Contains(setting))
        {
            return setting;
        }

        var two = systemCulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Supported.Contains(two) ? two : "en";
    }

    /// <summary>Language of the current UI culture, "en" when unsupported.</summary>
    public static string Current
    {
        get
        {
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Supported.Contains(two) ? two : "en";
        }
    }
}

/// <summary>Static access for code without DI (workflow texts, lock labels…).</summary>
public static class Loc
{
    public static string Get(string key, params object?[] args)
    {
        var template = Catalogs.Lookup(Langs.Current, key) ?? Catalogs.Lookup("en", key) ?? key;
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    /// <summary>Picks <c>baseKey_one</c> / <c>baseKey_other</c>; the count is {0},
    /// extra arguments follow as {1}, {2}… French also uses "one" for 0.</summary>
    public static string Plural(string baseKey, long count, params object?[] args)
    {
        var one = count == 1 || (Langs.Current == "fr" && count == 0);
        var all = new object?[args.Length + 1];
        all[0] = count;
        args.CopyTo(all, 1);
        return Get(baseKey + (one ? "_one" : "_other"), all);
    }
}

public sealed class XbsLocalizer : IXbsLocalizer
{
    public string Get(string key, params object?[] args) => Loc.Get(key, args);
}

public static partial class Str
{
}

internal static partial class Catalogs
{
    private static Dictionary<string, Dictionary<string, string>>? _all;

    private static Dictionary<string, Dictionary<string, string>> All => _all ??= new()
    {
        ["en"] = En,
        ["it"] = It,
        ["fr"] = Fr,
        ["es"] = Es,
        ["de"] = De,
    };

    internal static IReadOnlyDictionary<string, Dictionary<string, string>> Tables => All;

    internal static string? Lookup(string lang, string key) =>
        All.TryGetValue(lang, out var table) && table.TryGetValue(key, out var s) ? s : null;
}
