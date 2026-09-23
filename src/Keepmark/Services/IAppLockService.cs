namespace Keepmark.Services;

// Platform biometric / device-credential prompt.
// requireDeviceCredential=true forces the device PIN/pattern/password
// (used for security-setting changes); false honors the user's unlock mode.

public interface IAppLockService
{
    Task<bool> IsAvailableAsync();
    Task<bool> AuthenticateAsync(string title, bool requireDeviceCredential);
}
