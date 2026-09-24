using Android.Content.Res;
using Android.OS;
using Android.Views;
using Keepmark.Services;

namespace Keepmark.Platforms.Android.Services;

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

            bool dark = ResolveDark(theme, activity!.Resources?.Configuration);
            // Navy-deep behind the spacer in dark (white icons), light gray in light.
            window.SetStatusBarColor(global::Android.Graphics.Color.ParseColor(dark ? "#0E1430" : "#F7F7F7"));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                window.InsetsController?.SetSystemBarsAppearance(
                    dark ? 0 : (int)WindowInsetsControllerAppearance.LightStatusBars,
                    (int)WindowInsetsControllerAppearance.LightStatusBars);
            }
            else
            {
#pragma warning disable CA1422 // deprecated on 30+, required on 29
                var flags = window.DecorView.SystemUiVisibility;
                if (dark)
                {
                    flags &= ~(StatusBarVisibility)SystemUiFlags.LightStatusBar;
                }
                else
                {
                    flags |= (StatusBarVisibility)SystemUiFlags.LightStatusBar;
                }

                window.DecorView.SystemUiVisibility = flags;
#pragma warning restore CA1422
            }
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static bool ResolveDark(string theme, Configuration? config)
    {
        if (theme == "Dark")
        {
            return true;
        }

        if (theme == "Light")
        {
            return false;
        }

        var night = config?.UiMode & UiMode.NightMask;
        return night == UiMode.NightYes;
    }
}
