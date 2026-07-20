using System.Threading;

namespace StayActive.Remotes;

internal sealed class RemotesMenuController : IDisposable
{
    private readonly IRemoteFleetClient _fleetClient;
    private readonly IRemoteActionService _remoteActionService;
    private readonly IRemoteExitNodeController _exitNodeController;
    private readonly IRemoteAdminConsoleLauncher _adminConsoleLauncher;
    private readonly IRemoteHubAccessTokenProvider _remoteHubTokenProvider;
    private readonly SynchronizationContext _uiContext;
    private readonly Action _openDashboard;
    private readonly Action _openAddDevice;
    private readonly Action<string> _showError;
    private readonly ToolStripMenuItem _menuItem;
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshInProgress;
    private bool _disposed;

    public RemotesMenuController(
        IRemoteFleetClient fleetClient,
        IRemoteActionService remoteActionService,
        IRemoteExitNodeController exitNodeController,
        IRemoteAdminConsoleLauncher adminConsoleLauncher,
        IRemoteHubAccessTokenProvider remoteHubTokenProvider,
        SynchronizationContext uiContext,
        Action openDashboard,
        Action openAddDevice,
        Action<string> showError)
    {
        _fleetClient = fleetClient;
        _remoteActionService = remoteActionService;
        _exitNodeController = exitNodeController;
        _adminConsoleLauncher = adminConsoleLauncher;
        _remoteHubTokenProvider = remoteHubTokenProvider;
        _uiContext = uiContext;
        _openDashboard = openDashboard;
        _openAddDevice = openAddDevice;
        _showError = showError;
        _menuItem = new ToolStripMenuItem("Remotes");
        _menuItem.DropDownOpening += (_, _) =>
        {
            Render();
            QueueRefresh();
        };
        Render();
    }

    public ToolStripMenuItem MenuItem => _menuItem;

    public void RefreshCachedUi()
    {
        if (!_disposed)
        {
            Render();
        }
    }

    public void QueueRefresh()
    {
        if (_disposed || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        Render();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await _fleetClient.RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = ex;
            }

            _uiContext.Post(_ =>
            {
                if (_disposed)
                {
                    return;
                }

                _refreshInProgress = false;
                Render();
                if (error is not null)
                {
                    _showError($"Could not refresh remotes: {error.Message}");
                }
            }, null);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _fleetClient.Dispose();
        _menuItem.Dispose();
    }

    private void Render()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _fleetClient.GetCachedSnapshot();
        var model = RemoteMenuModelBuilder.Build(snapshot);
        _menuItem.DropDownItems.Clear();

        _menuItem.DropDownItems.Add(CreateDisabledItem(model.ConnectionText));
        _menuItem.DropDownItems.Add(CreateDisabledItem(model.InternetRouteText));

        if (snapshot.ActiveExitNodeId is not null || snapshot.HasUnmanagedActiveExitNode)
        {
            AddClearExitNodeItem(_menuItem);
        }

        var refreshItem = new ToolStripMenuItem(_refreshInProgress ? "Refreshing…" : "Refresh now")
        {
            Enabled = model.CanRefresh && !_refreshInProgress
        };
        refreshItem.Click += (_, _) => QueueRefresh();
        _menuItem.DropDownItems.Add(refreshItem);

        var addDeviceItem = new ToolStripMenuItem("Add a device…");
        addDeviceItem.Click += (_, _) => _openAddDevice();
        _menuItem.DropDownItems.Add(addDeviceItem);
        var openDashboardItem = new ToolStripMenuItem("Open Remotes…");
        openDashboardItem.Click += (_, _) => _openDashboard();
        _menuItem.DropDownItems.Add(openDashboardItem);
        var signInItem = new ToolStripMenuItem("Sign in to RemoteHub…");
        signInItem.Click += async (_, _) => await SignInToRemoteHubAsync();
        _menuItem.DropDownItems.Add(signInItem);
        var signOutItem = new ToolStripMenuItem("Sign out of RemoteHub");
        signOutItem.Click += async (_, _) => await SignOutOfRemoteHubAsync();
        _menuItem.DropDownItems.Add(signOutItem);
        var adminConsoleItem = new ToolStripMenuItem("Open administration console…");
        adminConsoleItem.Click += (_, _) =>
        {
            var result = _adminConsoleLauncher.Open();
            if (!result.Succeeded)
            {
                _showError(result.Message);
            }
        };
        _menuItem.DropDownItems.Add(adminConsoleItem);
        _menuItem.DropDownItems.Add(new ToolStripSeparator());

        if (model.Devices.Count == 0)
        {
            _menuItem.DropDownItems.Add(CreateDisabledItem("No enrolled computers"));
        }
        else
        {
            foreach (var device in model.Devices)
            {
                _menuItem.DropDownItems.Add(CreateDeviceItem(device));
            }
        }

        _menuItem.DropDownItems.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Remote settings…");
        settingsItem.Click += (_, _) => _openDashboard();
        _menuItem.DropDownItems.Add(settingsItem);
    }

    private ToolStripMenuItem CreateDeviceItem(RemoteMenuDevice menuDevice)
    {
        var item = new ToolStripMenuItem(menuDevice.DisplayText)
        {
            Enabled = true
        };

        if (!menuDevice.IsActionable)
        {
            item.DropDownItems.Add(CreateDisabledItem(menuDevice.Device.IsVerified
                ? "This computer is offline or the control plane is unavailable"
                : "Verify this computer before enabling remote access"));
        }
        else
        {
            AddActionItem(item, menuDevice.Device, "View screen...", RemoteWebAction.ViewScreen);
            AddActionItem(item, menuDevice.Device, "Send files...", RemoteWebAction.SendFile);
            AddActionItem(item, menuDevice.Device, "Get files...", RemoteWebAction.RequestFile);

            if (menuDevice.Device.Capabilities.HasFlag(RemoteCapability.ExitNode))
            {
                AddExitNodeItem(item, menuDevice);
            }
        }

        item.DropDownItems.Add(new ToolStripSeparator());
        var detailsItem = new ToolStripMenuItem("Details…");
        detailsItem.Click += (_, _) => _openDashboard();
        item.DropDownItems.Add(detailsItem);
        return item;
    }

    private void AddActionItem(
        ToolStripMenuItem parent,
        RemoteDevice device,
        string text,
        RemoteWebAction action)
    {
        var availability = _remoteActionService.GetAvailability(device, action);
        var item = new ToolStripMenuItem(text)
        {
            Enabled = availability.IsAvailable,
            ToolTipText = availability.Reason
        };
        item.Click += (_, _) =>
        {
            var result = _remoteActionService.Open(device, action);
            if (!result.Succeeded)
            {
                _showError(result.Message);
            }
        };
        parent.DropDownItems.Add(item);

        if (!availability.IsAvailable)
        {
            parent.DropDownItems.Add(CreateDisabledItem(availability.Reason));
        }
    }

    private void AddExitNodeItem(ToolStripMenuItem parent, RemoteMenuDevice menuDevice)
    {
        var availability = menuDevice.IsActiveExitNode
            ? _exitNodeController.GetClearAvailability()
            : _exitNodeController.GetAvailability(menuDevice.Device);
        var text = menuDevice.IsActiveExitNode
            ? "VPN ON - turn off"
            : "Use this computer for Internet VPN";
        var item = new ToolStripMenuItem(text)
        {
            Enabled = availability.IsAvailable,
            Checked = menuDevice.IsActiveExitNode,
            ToolTipText = availability.IsAvailable
                ? menuDevice.IsActiveExitNode
                    ? "Restore this computer's normal internet connection."
                    : $"Websites and downloads will use {menuDevice.Device.DeviceName}'s internet connection."
                : availability.Reason
        };
        item.Click += async (_, _) =>
        {
            if (menuDevice.IsActiveExitNode)
            {
                await ClearExitNodeAsync();
                return;
            }

            await UseExitNodeAsync(menuDevice.Device);
        };
        parent.DropDownItems.Add(item);

        if (!availability.IsAvailable)
        {
            parent.DropDownItems.Add(CreateDisabledItem(availability.Reason));
        }
    }

    private void AddClearExitNodeItem(ToolStripMenuItem parent)
    {
        var availability = _exitNodeController.GetClearAvailability();
        var item = new ToolStripMenuItem("VPN ON - turn off")
        {
            Enabled = availability.IsAvailable,
            Checked = true,
            ToolTipText = availability.IsAvailable
                ? "Restore this computer's normal internet connection."
                : availability.Reason
        };
        item.Click += async (_, _) => await ClearExitNodeAsync();
        parent.DropDownItems.Add(item);

        if (!availability.IsAvailable)
        {
            parent.DropDownItems.Add(CreateDisabledItem(availability.Reason));
        }
    }

    private async Task UseExitNodeAsync(RemoteDevice device)
    {
        var result = await _exitNodeController.UseExitNodeAsync(
            device,
            allowLocalNetworkAccess: false,
            CancellationToken.None);
        if (!result.Succeeded)
        {
            _showError(result.Message);
        }

        QueueRefresh();
    }

    private async Task SignInToRemoteHubAsync()
    {
        try
        {
            await _remoteHubTokenProvider.SignInAsync(CancellationToken.None);
            QueueRefresh();
        }
        catch (OperationCanceledException)
        {
            // The browser sign-in was cancelled or timed out; preserve the
            // existing fail-closed action state without displaying a failure.
        }
        catch (RemoteHubOidcException exception)
        {
            _showError(exception.Message);
        }
        catch
        {
            _showError("Could not sign in to the self-hosted RemoteHub.");
        }
    }

    private async Task SignOutOfRemoteHubAsync()
    {
        var confirmation = MessageBox.Show(
            "Remove this Windows user's locally protected RemoteHub sign-in and disable RemoteHub actions until the next sign-in?",
            "Sign out of RemoteHub",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _remoteHubTokenProvider.SignOutAsync(CancellationToken.None);
            QueueRefresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _showError("Could not remove the local RemoteHub sign-in.");
        }
    }

    private async Task ClearExitNodeAsync()
    {
        var result = await _exitNodeController.ClearExitNodeAsync(CancellationToken.None);
        if (!result.Succeeded)
        {
            _showError(result.Message);
        }

        QueueRefresh();
    }

    private static ToolStripMenuItem CreateDisabledItem(string text)
    {
        return new ToolStripMenuItem(text)
        {
            Enabled = false
        };
    }
}
