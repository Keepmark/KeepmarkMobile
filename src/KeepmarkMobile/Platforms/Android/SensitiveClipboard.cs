using Android.Content;
using Android.OS;

namespace KeepmarkMobile.Platforms.Android;

/// <summary>Copies a secret (a shared sync link) flagged as sensitive, so
/// Android 13+ hides it from the clipboard preview and keyboard suggestions.</summary>
internal static class SensitiveClipboard
{
    public static void SetText(string label, string text)
    {
        var context = global::Android.App.Application.Context;
        if (context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard)
            throw new InvalidOperationException("Clipboard unavailable");
        var clip = ClipData.NewPlainText(label, text);
        var extras = new PersistableBundle();
        // ClipDescription.EXTRA_IS_SENSITIVE (API 33); older releases ignore it.
        extras.PutBoolean("android.content.extra.IS_SENSITIVE", true);
        clip.Description!.Extras = extras;
        clipboard.PrimaryClip = clip;
    }
}
