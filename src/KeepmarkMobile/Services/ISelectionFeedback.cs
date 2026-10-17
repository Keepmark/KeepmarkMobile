namespace KeepmarkMobile.Services;

/// <summary>Confirms an accepted long press using the device's system feedback policy.</summary>
public interface ISelectionFeedback
{
    void ConfirmLongPress();
}
