namespace KeepmarkMobile.Services;

public sealed class SelectionFeedback : ISelectionFeedback
{
    public void ConfirmLongPress()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Default flags preserve Android's haptic-feedback settings. Unsupported
            // devices simply return false; visual selection remains the confirmation.
            Platform.CurrentActivity?.Window?.DecorView?.PerformHapticFeedback(
                Android.Views.FeedbackConstants.LongPress);
        });
    }
}
