using Keepmark.Core.I18n;
using KeepmarkMobile.Services;

namespace KeepmarkMobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton<IXbsLocalizer, XbsLocalizer>();
        builder.Services.AddSingleton<XbsSettings>();
        builder.Services.AddSingleton<LanguageService>();
        builder.Services.AddSingleton<SecureCredentialStore>();
        builder.Services.AddSingleton<PinnedStore>();
        builder.Services.AddSingleton<ToastState>();
        builder.Services.AddSingleton<SharedImportService>();
        builder.Services.AddSingleton<AppLockState>();
        builder.Services.AddSingleton<XbsSession>();
#if ANDROID
        builder.Services.AddSingleton<IAppLockService, Platforms.Android.Services.AndroidAppLockService>();
        builder.Services.AddSingleton<IStatusBarService, Platforms.Android.Services.AndroidStatusBarService>();
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        var app = builder.Build();
        // Before the first page renders: every text reads the active culture.
        app.Services.GetRequiredService<LanguageService>().Apply();
        return app;
    }
}
