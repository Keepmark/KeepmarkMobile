using Android.Content;

namespace XBrowserSyncNg.Platforms.Android;

// Single choke point for incoming shares, callable from any activity.
// Cold-start safe: unpublishable shares wait in limbo for the next resume.

internal static class ShareIntake
{
    private static (string? Text, string? Subject)? _limboShare;

    public static void Handle(Intent? intent)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine(
                $"[share] intent: action={intent?.Action} type={intent?.Type} hasText={intent?.HasExtra(Intent.ExtraText)}");

            if (intent?.Action != Intent.ActionSend ||
                !string.Equals(intent.Type, "text/plain", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine("[share] intent ignored (not SEND/text)");
                return;
            }

            var text = intent.GetStringExtra(Intent.ExtraText);
            var subject = intent.GetStringExtra(Intent.ExtraSubject);
            if (!Publish(text, subject))
            {
                _limboShare = (text, subject);
            }
        }
        catch
        {
            // Never break startup for a share intent.
        }
    }

    public static void Flush()
    {
        var pending = _limboShare;
        if (pending is null)
        {
            return;
        }

        try
        {
            if (Publish(pending.Value.Text, pending.Value.Subject))
            {
                _limboShare = null;
            }
        }
        catch
        {
        }
    }

    private static bool Publish(string? text, string? subject)
    {
        var share = global::XBrowserSyncNg.Services.SharedImportService.Current;
        if (share is null)
        {
            var services = IPlatformApplication.Current?.Services;
            share = services?.GetService(typeof(global::XBrowserSyncNg.Services.SharedImportService)) as global::XBrowserSyncNg.Services.SharedImportService;
        }

        if (share is null)
        {
            return false;
        }

        share.Publish(text, subject);

        try
        {
            var act = Platform.CurrentActivity;
            var pending = share.Peek();
            act?.RunOnUiThread(() => global::Android.Widget.Toast.MakeText(act,
                $"share ok: {(pending is null ? "no-url" : pending.Url)}", global::Android.Widget.ToastLength.Short)?.Show());
        }
        catch
        {
        }

        try
        {
            var services = IPlatformApplication.Current?.Services;
            var toast = services?.GetService(typeof(global::XBrowserSyncNg.Services.ToastState)) as global::XBrowserSyncNg.Services.ToastState;
            toast?.ShowAsync("Link received — complete the bookmark");
        }
        catch
        {
        }

        return true;
    }
}
