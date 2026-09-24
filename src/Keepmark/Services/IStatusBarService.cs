namespace Keepmark.Services;

/// <summary>Applies the effective theme to the OS chrome (status bar
/// background + system icon color). Web content follows via data-bs-theme.</summary>
public interface IStatusBarService
{
    /// <param name="theme">System, Light or Dark (the stored preference).</param>
    void ApplyTheme(string theme);
}
