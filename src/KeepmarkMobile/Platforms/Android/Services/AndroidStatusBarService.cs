using Android.OS;
using Android.Views;
using KeepmarkMobile.Services;

namespace KeepmarkMobile.Platforms.Android.Services;

public sealed class AndroidStatusBarService : IStatusBarService
{
    public void ApplyTheme(string theme)
    {
        try
        {
            var activity = Platform.CurrentActivity;
            var window = activity?.Window;
            if (window is null)
            {
                return;
            }

            // The navy top bar now starts at viewport top (inset as padding),
            // so the strip behind the system icons is always navy: icons stay
            // white in both themes, status bar opaque navy.
            window.SetStatusBarColor(global::Android.Graphics.Color.ParseColor("#172552"));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                // 0 clears LightStatusBars: white icons on the navy strip.
                window.InsetsController?.SetSystemBarsAppearance(
                    0,
                    (int)WindowInsetsControllerAppearance.LightStatusBars);
            }
            else
            {
#pragma warning disable CA1422 // deprecated on 30+, required on 29
                window.DecorView.SystemUiVisibility &= ~(StatusBarVisibility)SystemUiFlags.LightStatusBar;
#pragma warning restore CA1422
            }
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
