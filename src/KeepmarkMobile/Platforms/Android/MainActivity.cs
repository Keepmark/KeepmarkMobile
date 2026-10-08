using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

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

        ApplySafeAreaPadding();

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

    /// <summary>
    /// targetSdk 35+ enforces edge-to-edge (SetDecorFitsSystemWindows is
    /// ignored), and the WebView does not reliably report env(safe-area-*):
    /// in landscape the side navigation bar covered the drawer, and with
    /// adjustPan the keyboard shoved the whole page under the notch. Pad the
    /// content root by system bars + cutout + IME and consume the insets, so
    /// the WebView always sits inside the safe area and the keyboard resizes
    /// it instead of panning. The strips behind the bars show the navy root.
    /// </summary>
    private void ApplySafeAreaPadding()
    {
        try
        {
            Window?.SetSoftInputMode(SoftInput.AdjustResize);
            var root = FindViewById(global::Android.Resource.Id.Content);
            if (root is null)
            {
                return;
            }

            root.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#172552"));
            ViewCompat.SetOnApplyWindowInsetsListener(root, new SafeAreaInsetsListener());
            ViewCompat.RequestApplyInsets(root);
        }
        catch
        {
            // Fall back to the CSS env() inset math.
        }
    }

    private sealed class SafeAreaInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(
                WindowInsetsCompat.Type.SystemBars()
                | WindowInsetsCompat.Type.DisplayCutout()
                | WindowInsetsCompat.Type.Ime());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed!;
        }
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
