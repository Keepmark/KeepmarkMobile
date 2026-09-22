namespace XBrowserSyncNg;

public partial class App : Application
{
    private readonly Services.XbsSession _session;

    public App(Services.XbsSession session)
    {
        InitializeComponent();
        _session = session;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage()) { Title = "XBrowserSyncNg" };
    }

    protected override async void OnStart()
    {
        base.OnStart();
        try
        {
            await _session.InitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Session init failed: {ex}");
        }
    }
}
