using Keepmark.Core.I18n;
using Keepmark.Core.Workflow;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace KeepmarkMobile.Services;

/// <summary>One native camera session; no images, credentials or results are persisted.</summary>
public sealed class QrScanner(AppLockState appLock) : IQrScanner
{
    private int active;

    public async Task<string?> ScanAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw Unavailable();
        ScannerPage? page = null;
        INavigation? navigation = null;
        Action? lockChanged = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (appLock.Locked || !appLock.Foreground || (!BarcodeScanning.IsSupported || Android.App.Application.Context.PackageManager?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureCameraAny) != true)) throw Unavailable();
            var permission = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>());
            cancellationToken.ThrowIfCancellationRequested();
            if (permission != PermissionStatus.Granted) throw new QrScanException(Str.ConnQrPermission);
            if (appLock.Locked || !appLock.Foreground) return null;
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                navigation = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation ?? throw Unavailable();
                page = new ScannerPage();
                lockChanged = () =>
                {
                    if (appLock.Locked || !appLock.Foreground)
                        MainThread.BeginInvokeOnMainThread(() => page.Finish(null));
                };
                appLock.Changed += lockChanged;
                using var registration = cancellationToken.Register(() => MainThread.BeginInvokeOnMainThread(() => page.Finish(null)));
                if (cancellationToken.IsCancellationRequested || appLock.Locked || !appLock.Foreground) return null;
                await navigation.PushModalAsync(page);
                var result = await page.Result;
                return cancellationToken.IsCancellationRequested || appLock.Locked || !appLock.Foreground ? null : result;
            });
        }
        finally
        {
            if (lockChanged is not null) appLock.Changed -= lockChanged;
            try
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (page is null) return;
                    try { page.Stop(); }
                    finally { if (navigation?.ModalStack.LastOrDefault() == page) await navigation.PopModalAsync(); }
                });
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }
    }

    private static QrScanException Unavailable() => new(Str.ConnQrUnavailable);

    private sealed class ScannerPage : ContentPage
    {
        private readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CameraBarcodeReaderView reader;
        private int completed;
        public Task<string?> Result => completion.Task;

        public ScannerPage()
        {
            Title = Loc.Get(Str.ConnQrScan);
            reader = new CameraBarcodeReaderView
            {
                Options = new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode, Multiple = false, AutoRotate = true, CharacterSet = "UTF-8" },
                IsDetecting = true
            };
            reader.BarcodesDetected += Detected;
            var cancel = new Button { Text = Loc.Get(Str.CommonCancel) };
            cancel.Clicked += (_, _) => Finish(null);
            var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) } };
            grid.Add(reader); grid.Add(cancel, 0, 1);
            Content = grid;
        }

        private void Detected(object? sender, BarcodeDetectionEventArgs args)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
            var value = args.Results.FirstOrDefault()?.Value;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { Stop(); }
                catch { completion.TrySetException(Unavailable()); return; }
                if (value is not null && (value.Length > ConnectQrPayload.MaxUtf8Bytes || System.Text.Encoding.UTF8.GetByteCount(value) > ConnectQrPayload.MaxUtf8Bytes))
                    completion.TrySetException(new QrScanException(Str.ConnQrInvalid));
                else completion.TrySetResult(value);
            });
        }
        public void Finish(string? result)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
            try { Stop(); }
            catch { completion.TrySetException(Unavailable()); return; }
            completion.TrySetResult(result);
        }
        private int stopped;
        public void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            reader.BarcodesDetected -= Detected;
            try { reader.IsDetecting = false; }
            finally { reader.Handler?.DisconnectHandler(); }
            // 0.10.4 DisconnectHandler disconnects/disposes CameraManager and
            // Android CameraManager unbinds all CameraX use cases.
        }
        protected override bool OnBackButtonPressed() { Finish(null); return true; }
        protected override void OnDisappearing() { Finish(null); base.OnDisappearing(); }
    }
}
