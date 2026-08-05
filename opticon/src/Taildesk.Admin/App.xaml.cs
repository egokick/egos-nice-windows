using System.Windows;

namespace Taildesk.Admin;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\Taildesk.Admin.SingleInstance";
    private const string ActivationEventName = "Local\\Taildesk.Admin.Activate";
    private CoordinatorServer? _coordinator;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    public bool ExitRequested { get; private set; }

    public AdminState State { get; } = new();
    public HeadscaleApiClient Headscale { get; private set; } = null!;
    public AgentClient Agents { get; } = new();
    public TransferManager Transfers { get; } = new();

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, SingleInstanceMutexName, out var created);
        if (!created)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowCommandCenter),
            null,
            Timeout.Infinite,
            false);

        try
        {
            await State.InitializeAsync();
            Headscale = new HeadscaleApiClient(State);
            var window = new MainWindow(new MainViewModel(State, Headscale, Agents, Transfers));
            MainWindow = window;
            window.Show();
            CreateTrayIcon();
            try
            {
                await RestartCoordinatorAsync();
            }
            catch (Exception exception)
            {
                MessageBox.Show($"Opticon opened, but the coordinator is not listening yet. Check Tailscale and then save Settings again.\n\n{exception.Message}",
                    "Coordinator unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Opticon could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public async Task RestartCoordinatorAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
            _coordinator = null;
        }
        if (State.Config.Mode == AdminMode.Primary && State.Config.SetupComplete)
        {
            _coordinator = new CoordinatorServer(State, Headscale);
            await _coordinator.StartAsync();
        }
    }

    public void ShowCommandCenter()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void ExitFromTray()
    {
        ExitRequested = true;
        Shutdown();
    }

    private void CreateTrayIcon()
    {
        var processPath = Environment.ProcessPath;
        _trayIconImage = processPath is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(processPath);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Opticon", null, (_, _) => Dispatcher.Invoke(ShowCommandCenter));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Opticon command center",
            Icon = _trayIconImage ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowCommandCenter);
    }

    private async void Application_Exit(object sender, ExitEventArgs e)
    {
        if (_coordinator is not null) await _coordinator.DisposeAsync();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconImage?.Dispose();
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _singleInstance?.Dispose();
    }
}
