using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Keepmark;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
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
