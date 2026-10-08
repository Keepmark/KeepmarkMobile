using AndroidX.Biometric;
using AndroidX.Core.Content;
using FragmentActivity = AndroidX.Fragment.App.FragmentActivity;

namespace KeepmarkMobile.Platforms.Android.Services;

public sealed class AndroidAppLockService : global::KeepmarkMobile.Services.IAppLockService
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

            int authenticators;
            var mode = Preferences.Get("app_lock", "Off");
            if (requireDeviceCredential || mode == "PinOnly")
            {
                // PIN/pattern/password only — no biometric shortcut.
                // NOTE: with DEVICE_CREDENTIAL allowed, a custom negative button
                // is forbidden and makes the builder throw — the system provides
                // its own cancel/credential UI.
                authenticators = BiometricManager.Authenticators.DeviceCredential;
            }
            else
            {
                // Biometric with device-credential fallback.
                authenticators = BiometricManager.Authenticators.BiometricStrong
                    | BiometricManager.Authenticators.DeviceCredential;
            }

            var builder = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(title)
                .SetAllowedAuthenticators(authenticators);
            // No custom negative button: DEVICE_CREDENTIAL is always allowed
            // and forbids it (the system provides its own cancel UI).

            var info = builder.Build();
            activity.RunOnUiThread(() =>
            {
                try
                {
                    prompt.Authenticate(info);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Biometric authenticate failed: {ex}");
                    tcs.TrySetResult(false);
                }
            });
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

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
        {
            System.Diagnostics.Debug.WriteLine($"Biometric error {errorCode}: {errString}");
            tcs.TrySetResult(false);
        }

        public override void OnAuthenticationFailed() { }
    }
}
