using AndroidX.Biometric;
using AndroidX.Core.Content;
using FragmentActivity = AndroidX.Fragment.App.FragmentActivity;

namespace XBrowserSyncNg.Platforms.Android.Services;

public sealed class AndroidAppLockService : global::XBrowserSyncNg.Services.IAppLockService
{
    public Task<bool> IsAvailableAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            return Task.FromResult(false);
        }

        var mgr = BiometricManager.From(activity);
        var ok = mgr!.CanAuthenticate(BiometricManager.Authenticators.BiometricStrong)
            == BiometricManager.BiometricSuccess;
        var credOk = mgr.CanAuthenticate(BiometricManager.Authenticators.DeviceCredential)
            == BiometricManager.BiometricSuccess;
        return Task.FromResult(ok || credOk);
    }

    public Task<bool> AuthenticateAsync(string title, bool requireDeviceCredential)
    {
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            if (Platform.CurrentActivity is not FragmentActivity activity)
            {
                tcs.SetResult(false);
                return tcs.Task;
            }

            var executor = ContextCompat.GetMainExecutor(activity)!;
            var prompt = new BiometricPrompt(activity, executor, new AuthCallback(tcs));
            var builder = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(title)
                .SetConfirmationRequired(true);

            if (requireDeviceCredential)
            {
                // PIN/pattern/password only — no biometric shortcut, no negative button allowed.
                builder.SetAllowedAuthenticators(BiometricManager.Authenticators.DeviceCredential);
            }
            else
            {
                var mode = Preferences.Get("app_lock", "Off");
                builder.SetAllowedAuthenticators(mode == "BiometricOnly"
                    ? BiometricManager.Authenticators.BiometricStrong
                    : BiometricManager.Authenticators.BiometricStrong | BiometricManager.Authenticators.DeviceCredential);
                builder.SetNegativeButtonText("Cancel");
            }

            prompt.Authenticate(builder.Build());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Biometric prompt failed: {ex}");
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }

    private sealed class AuthCallback(TaskCompletionSource<bool> tcs) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) =>
            tcs.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) =>
            tcs.TrySetResult(false);

        public override void OnAuthenticationFailed() { }
    }
}
