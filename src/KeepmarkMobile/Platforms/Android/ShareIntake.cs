using Android.Content;

namespace KeepmarkMobile.Platforms.Android;

// Single choke point for incoming shares, callable from any activity.
// Cold-start safe: unpublishable shares wait in limbo for the next resume.

internal static class ShareIntake
{
    private static (string? Text, string? Subject)? _limboShare;

    public static void Handle(Intent? intent)
    {
        try
        {
            string? text;
            if (intent?.Action == Intent.ActionView &&
                string.Equals(intent.Data?.Scheme, Keepmark.Core.Workflow.SyncShareLink.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                // A tapped keepmark://sync link: same route as shared text.
                text = intent.DataString;
            }
            else if (intent?.Action == Intent.ActionSend &&
                     string.Equals(intent.Type, "text/plain", StringComparison.OrdinalIgnoreCase))
            {
                text = intent.GetStringExtra(Intent.ExtraText);
            }
            else
            {
                return;
            }

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
        // A sync link carries a key: it goes to the connect flow, never to
        // the add-bookmark sheet (it would be saved and synced as a bookmark).
        if (Keepmark.Core.Workflow.SyncShareLink.LooksLikeLink(text))
        {
            var inbox = global::KeepmarkMobile.Services.SyncLinkInbox.Current
                ?? IPlatformApplication.Current?.Services?.GetService(typeof(global::KeepmarkMobile.Services.SyncLinkInbox)) as global::KeepmarkMobile.Services.SyncLinkInbox;
            if (inbox is null) return false;
            inbox.Publish(text);
            return true;
        }

        var share = global::KeepmarkMobile.Services.SharedImportService.Current;
        if (share is null)
        {
            var services = IPlatformApplication.Current?.Services;
            share = services?.GetService(typeof(global::KeepmarkMobile.Services.SharedImportService)) as global::KeepmarkMobile.Services.SharedImportService;
        }

        if (share is null)
        {
            return false;
        }

        share.Publish(text, subject);
        return true;
    }
}
