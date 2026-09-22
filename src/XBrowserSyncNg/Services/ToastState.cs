namespace XBrowserSyncNg.Services;

// Global lightweight toast (sync feedback, errors). Rendered by MainLayout.

public sealed class ToastState
{
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public event Action? Changed;

    public async Task ShowAsync(string message, bool isError = false, int ms = 3500)
    {
        Message = message;
        IsError = isError;
        Changed?.Invoke();
        try
        {
            await Task.Delay(ms).ConfigureAwait(false);
        }
        catch
        {
        }

        if (Message == message)
        {
            Message = null;
            Changed?.Invoke();
        }
    }
}
