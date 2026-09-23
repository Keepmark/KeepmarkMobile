using System.Text.RegularExpressions;

namespace XBrowserSyncNg.Services;

// Shared text (URL) received from other apps via Android ACTION_SEND.
// Consumed once by the Explore page, which opens the add sheet prefilled.

public sealed partial class SharedImportService
{
    /// <summary>Latest instance (set by DI ctor). Lets the Activity publish
    /// without a service locator, which is unreliable during cold start.</summary>
    public static SharedImportService? Current { get; private set; }

    private readonly object _gate = new();
    private PendingShare? _pending;

    public event Action? Arrived;

    public SharedImportService()
    {
        Current = this;
    }

    public void Publish(string? text, string? subject)
    {
        var url = ExtractUrl(text);
        if (url is null)
        {
            return;
        }

        lock (_gate)
        {
            // EXTRA_SUBJECT usually carries the page title: handy, editable, never forced.
            _pending = new PendingShare(url, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim());
        }

        Arrived?.Invoke();
    }

    public PendingShare? Consume()
    {
        lock (_gate)
        {
            var p = _pending;
            _pending = null;
            return p;
        }
    }

    public PendingShare? Peek()
    {
        lock (_gate)
        {
            return _pending;
        }
    }

    private static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = UrlRegex().Match(text.Trim());
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

public sealed record PendingShare(string Url, string? Title);
