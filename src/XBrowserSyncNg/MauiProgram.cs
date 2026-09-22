using XBrowserSyncNg.Core.I18n;
using XBrowserSyncNg.Services;

namespace XBrowserSyncNg;

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

        builder.Services.AddSingleton<IXbsLocalizer, EnglishLocalizer>();
        builder.Services.AddSingleton<XbsSettings>();
        builder.Services.AddSingleton<SecureCredentialStore>();
        builder.Services.AddSingleton<PinnedStore>();
        builder.Services.AddSingleton<AppLockState>();
        builder.Services.AddSingleton<XbsSession>();
#if ANDROID
        builder.Services.AddSingleton<IAppLockService, Platforms.Android.Services.AndroidAppLockService>();
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
