namespace KeepmarkMobile.Services;

public interface IQrScanner
{
    Task<string?> ScanAsync(CancellationToken cancellationToken);
}

public sealed class QrScanException(string messageKey) : Exception("QR scanner unavailable")
{
    public string MessageKey { get; } = messageKey;
}
