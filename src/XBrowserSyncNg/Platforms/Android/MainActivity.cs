using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace XBrowserSyncNg;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Platforms.Android.ShareIntake.Handle(Intent);

        // Light status bar background (our spacer): system icons must be dark to stay visible.
        try
        {
            Window?.SetStatusBarColor(Android.Graphics.Color.ParseColor("#F7F7F7"));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window?.InsetsController?.SetSystemBarsAppearance(
                    (int)WindowInsetsControllerAppearance.LightStatusBars,
                    (int)WindowInsetsControllerAppearance.LightStatusBars);
            }
            else
            {
#pragma warning disable CA1422 // deprecated on 30+, required on 29
                Window.DecorView.SystemUiVisibility |= (StatusBarVisibility)SystemUiFlags.LightStatusBar;
#pragma warning restore CA1422
            }
        }
        catch
        {
            // Cosmetic only: never break startup.
        }
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
