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
            if (intent?.Action != Intent.ActionSend ||
                !string.Equals(intent.Type, "text/plain", StringComparison.OrdinalIgnoreCase))
            {
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
        return true;
    }
}
