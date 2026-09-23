using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace XBrowserSyncNg;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public class MainActivity : MauiAppCompatActivity
{
    // Share intents arriving before MAUI services exist (cold start) wait here
    // and are flushed on the next resumed state, when resolution is safe.
    private static (string? Text, string? Subject)? _limboShare;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleShareIntent(Intent);

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
        HandleShareIntent(intent);
    }

    private static void HandleShareIntent(Intent? intent)
    {
        try
        {
            if (intent?.Action != Intent.ActionSend || intent.Type != "text/plain")
            {
                return;
            }

            var text = intent.GetStringExtra(Intent.ExtraText);
            var subject = intent.GetStringExtra(Intent.ExtraSubject);
            if (!PublishShare(text, subject))
            {
                _limboShare = (text, subject);
            }
        }
        catch
        {
            // Never break startup for a share intent.
        }
    }

    private static void FlushLimboShare()
    {
        var pending = _limboShare;
        if (pending is null)
        {
            return;
        }

        try
        {
            if (PublishShare(pending.Value.Text, pending.Value.Subject))
            {
                _limboShare = null;
            }
        }
        catch
        {
        }
    }

    private static bool PublishShare(string? text, string? subject)
    {
        var services = IPlatformApplication.Current?.Services;
        var share = services?.GetService(typeof(Services.SharedImportService)) as Services.SharedImportService;
        if (share is null)
        {
            return false;
        }

        share.Publish(text, subject);
        return true;
    }

    protected override void OnPause()
    {
        base.OnPause();
        NotifyLock(s => s.OnPaused(GetSettings()));
    }

    protected override void OnResume()
    {
        base.OnResume();
        FlushLimboShare();
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
