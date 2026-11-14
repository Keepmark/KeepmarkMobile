using Keepmark.Core.Workflow;

namespace KeepmarkMobile.Services;

/// <summary>
/// A shared sync link that reached the app from outside (a tapped
/// <c>keepmark://sync</c> link or text shared to Keepmark). It is held in
/// memory only, for a few minutes (cold start + unlock), and consumed once by
/// the connect panel, which turns it into the same reviewed candidate as a QR
/// scan. Nothing is applied without the user confirming the review.
/// </summary>
public sealed class SyncLinkInbox
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Latest instance (set by DI ctor), for the Android intake.</summary>
    public static SyncLinkInbox? Current { get; private set; }

    private readonly object _gate = new();
    private string? _text;
    private long _at;

    public event Action? Arrived;

    public SyncLinkInbox() => Current = this;

    /// <summary>Takes the text when it looks like a sync link (valid or not:
    /// such text must never become a bookmark). False = not a link.</summary>
    public bool Publish(string? text)
    {
        if (!SyncShareLink.LooksLikeLink(text) || text!.Length > SyncShareLink.MaxLength * 4) return SyncShareLink.LooksLikeLink(text);
        lock (_gate)
        {
            _text = text;
            _at = Environment.TickCount64;
        }

        Arrived?.Invoke();
        return true;
    }

    public bool HasPending
    {
        get { lock (_gate) return Fresh(); }
    }

    public string? Consume()
    {
        lock (_gate)
        {
            var text = Fresh() ? _text : null;
            _text = null;
            _at = 0;
            return text;
        }
    }

    private bool Fresh() => _text is not null && Environment.TickCount64 - _at < (long)Lifetime.TotalMilliseconds;
}
