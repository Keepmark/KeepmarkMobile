using System.Globalization;
using Keepmark.Core.I18n;

namespace KeepmarkMobile.Services;

/// <summary>Applies the Language setting to CurrentCulture/CurrentUICulture.
/// The Android system culture is captured once, before any override.</summary>
public sealed class LanguageService
{
    private readonly XbsSettings _settings;

    public LanguageService(XbsSettings settings) => _settings = settings;

    public CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    public string ActiveLang { get; private set; } = "en";

    public void Apply()
    {
        ActiveLang = Langs.Resolve(_settings.Language, SystemCulture);
        // Keep the system region (date/number formats) when the language matches it.
        var culture = SystemCulture.TwoLetterISOLanguageName.Equals(ActiveLang, StringComparison.OrdinalIgnoreCase)
            ? SystemCulture
            : CultureInfo.GetCultureInfo(ActiveLang);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
