using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace KeepmarkMobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Never draw under the status bar / notch / cutout: the whole notch
        // defense in CSS (`env(safe-area-inset-*)`) depends on the WebView
        // reporting real insets, which is not guaranteed on every WebView
        // version. Opting out of edge-to-edge makes the window avoid system
        // bars and the cutout by construction, so scrolled page content can
        // never slide under the notch in any orientation. The status strip
        // stays opaque navy via ApplyStatusBar, seamless with the app bar.
        // No-op on API < 35 (legacy fit-behavior is already the default).
        try
        {
            if (Window is not null)
            {
                global::AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(Window, true);
            }
        }
        catch
        {
            // Cosmetic only: CSS inset math is the fallback.
        }

        Platforms.Android.ShareIntake.Handle(Intent);

        // Status bar follows the stored theme (white icons on dark).
        ApplyStatusBar();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Platforms.Android.ShareIntake.Handle(intent);
    }

    protected override void OnPause()
    {
        base.OnPause();
        NotifyLock(s => s.OnPaused(GetSettings()));
    }

    protected override void OnResume()
    {
        base.OnResume();
        Platforms.Android.ShareIntake.Flush();
        NotifyLock(s => s.OnResumed(GetSettings()));
        ApplyStatusBar();
    }

    private static void ApplyStatusBar()
    {
        try
        {
            var services = IPlatformApplication.Current?.Services;
            var bar = services?.GetService(typeof(Services.IStatusBarService)) as Services.IStatusBarService;
            bar?.ApplyTheme(GetSettings().Theme);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static void NotifyLock(Action<Services.AppLockState> action)
    {
        try
        {
            var services = IPlatformApplication.Current?.Services;
            var state = services?.GetService(typeof(Services.AppLockState)) as Services.AppLockState;
            if (state is not null)
            {
                action(state);
            }
        }
        catch
        {
            // Never break the activity lifecycle for lock bookkeeping.
        }
    }

    private static Services.XbsSettings GetSettings()
    {
        var services = IPlatformApplication.Current?.Services;
        return (services?.GetService(typeof(Services.XbsSettings)) as Services.XbsSettings)
            ?? new Services.XbsSettings();
    }
}
