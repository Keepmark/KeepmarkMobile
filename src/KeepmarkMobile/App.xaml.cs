namespace KeepmarkMobile;

public partial class App : Application
{
    private readonly Services.XbsSession _session;
    private readonly Services.XbsSettings _settings;
    private readonly Services.AppLockState _lock;

    public App(Services.XbsSession session, Services.XbsSettings settings, Services.AppLockState lockState)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        _lock = lockState;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Keepmark" };
        window.Activated += (_, _) => _session.SetForeground(true);
        window.Resumed += (_, _) => _session.SetForeground(true);
        window.Stopped += (_, _) => _session.SetForeground(false);
        window.Destroying += (_, _) => _session.SetForeground(false);
        return window;
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

        _lock.Initialize(_settings);
    }
}
