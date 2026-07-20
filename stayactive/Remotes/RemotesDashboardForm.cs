using System.Drawing;

namespace StayActive.Remotes;

internal sealed class RemotesDashboardForm : Form
{
    private readonly IRemoteFleetClient _fleetClient;
    private readonly IRemoteActionService _remoteActionService;
    private readonly IRemoteExitNodeController _exitNodeController;
    private readonly IRemoteAdminConsoleLauncher _adminConsoleLauncher;
    private readonly IRemoteHubAccessTokenProvider _remoteHubTokenProvider;
    private readonly Action _openAddDevice;
    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly Action<RemoteClientPreferences> _savePreferences;
    private readonly Label _connectionLabel;
    private readonly Label _internetRouteLabel;
    private readonly Label _fleetSummaryLabel;
    private readonly TextBox _searchTextBox;
    private readonly ListView _deviceList;
    private readonly Label _detailsTitleLabel;
    private readonly Label _detailsLabel;
    private readonly Button _routeButton;
    private readonly Button _viewScreenButton;
    private readonly Button _sendFileButton;
    private readonly Button _requestFileButton;
    private readonly Label _routeHelpLabel;
    private readonly Label _remoteHelpLabel;
    private readonly Label _footerLabel;
    private readonly Button _refreshButton;
    private readonly ToolTip _toolTip = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private RemoteMenuModel? _currentModel;
    private RemoteFleetSnapshot _currentSnapshot = RemoteFleetSnapshot.NotConfigured;
    private string? _selectedDeviceId;
    private string? _noticeText;
    private bool _renderingDeviceList;
    private bool _refreshInProgress;
    private bool _routeChangeInProgress;
    private bool _ownedResourcesDisposed;

    public RemotesDashboardForm(
        IRemoteFleetClient fleetClient,
        IRemoteActionService remoteActionService,
        IRemoteExitNodeController exitNodeController,
        IRemoteAdminConsoleLauncher adminConsoleLauncher,
        IRemoteHubAccessTokenProvider remoteHubTokenProvider,
        Func<RemoteClientPreferences> getPreferences,
        Action<RemoteClientPreferences> savePreferences,
        Action? openAddDevice = null)
    {
        _fleetClient = fleetClient;
        _remoteActionService = remoteActionService;
        _exitNodeController = exitNodeController;
        _adminConsoleLauncher = adminConsoleLauncher;
        _remoteHubTokenProvider = remoteHubTokenProvider;
        _openAddDevice = openAddDevice ?? (() => { });
        _getPreferences = getPreferences;
        _savePreferences = savePreferences;

        Text = "StayActive Remotes";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 620);
        Size = new Size(1180, 720);
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var header = new TableLayoutPanel
        {
            Name = "RemotesHeader",
            Dock = DockStyle.Top,
            Height = 108,
            Padding = new Padding(18, 12, 18, 10),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = SystemColors.ControlLightLight
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var headerStatus = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        var title = new Label
        {
            Name = "RemotesTitle",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            Text = "Remotes"
        };
        _connectionLabel = new Label
        {
            Name = "ConnectionStatusLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "Loading self-hosted VPN status..."
        };
        _internetRouteLabel = new Label
        {
            Name = "InternetRouteStatusLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Internet route: checking..."
        };
        headerStatus.Controls.Add(title, 0, 0);
        headerStatus.Controls.Add(_connectionLabel, 0, 1);
        headerStatus.Controls.Add(_internetRouteLabel, 0, 2);

        var headerActions = new FlowLayoutPanel
        {
            Name = "HeaderActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 11, 0, 0),
            Margin = Padding.Empty
        };
        var settingsButton = CreateHeaderButton("Settings...", "SettingsButton");
        settingsButton.Click += (_, _) => OpenSettings();
        var adminConsoleButton = CreateHeaderButton("Administration...", "AdminConsoleButton");
        adminConsoleButton.Click += (_, _) => OpenAdminConsole();
        var signInButton = CreateHeaderButton("RemoteHub sign in...", "SignInButton");
        signInButton.Click += async (_, _) => await SignInToRemoteHubAsync();
        var addDeviceButton = CreateHeaderButton("Add device...", "AddDeviceButton");
        addDeviceButton.Click += (_, _) => _openAddDevice();
        _refreshButton = new Button
        {
            Name = "RefreshButton",
            Text = "Refresh",
            AutoSize = true,
            Margin = new Padding(6, 3, 0, 3)
        };
        _refreshButton.Click += async (_, _) => await RefreshFleetAsync();
        _toolTip.SetToolTip(_refreshButton, "Refresh VPN presence and self-hosted remote-management metadata (F5).");
        headerActions.Controls.AddRange(new Control[]
        {
            settingsButton,
            adminConsoleButton,
            signInButton,
            addDeviceButton,
            _refreshButton
        });
        header.Controls.Add(headerStatus, 0, 0);
        header.Controls.Add(headerActions, 1, 0);

        var split = new SplitContainer
        {
            Name = "RemotesSplitContainer",
            Dock = DockStyle.Fill,
            Size = new Size(1140, 560),
            SplitterDistance = 455,
            Panel1MinSize = 340,
            Panel2MinSize = 430,
            FixedPanel = FixedPanel.Panel1
        };

        _deviceList = new ListView
        {
            Name = "DeviceList",
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            View = View.Details
        };
        _deviceList.Columns.Add("Computer", 125);
        _deviceList.Columns.Add("Owner / user", 95);
        _deviceList.Columns.Add("Location", 95);
        _deviceList.Columns.Add("Status", 65);
        _deviceList.Columns.Add("VPN", 65);
        _deviceList.SelectedIndexChanged += (_, _) => RenderDetails();
        _deviceList.SizeChanged += (_, _) => ResizeDeviceColumns();

        _searchTextBox = new TextBox
        {
            Name = "DeviceSearchTextBox",
            Dock = DockStyle.Fill,
            PlaceholderText = "Search computers, owner labels, or locations...",
            Margin = new Padding(0, 0, 0, 8)
        };
        _searchTextBox.TextChanged += (_, _) => RenderDeviceList();
        _fleetSummaryLabel = new Label
        {
            Name = "FleetSummaryLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "No computers loaded"
        };

        var devicePanel = new TableLayoutPanel
        {
            Name = "DevicePanel",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        devicePanel.Controls.Add(new Label
        {
            Name = "DeviceSearchLabel",
            Text = "Search",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Anchor = AnchorStyles.Left
        }, 0, 0);
        devicePanel.Controls.Add(_searchTextBox, 0, 1);
        devicePanel.Controls.Add(_fleetSummaryLabel, 0, 2);
        devicePanel.Controls.Add(_deviceList, 0, 3);
        split.Panel1.Padding = new Padding(12);
        split.Panel1.Controls.Add(devicePanel);

        var detailsPanel = new Panel
        {
            Name = "DetailsPanel",
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            AutoScroll = true
        };
        var detailsLayout = new TableLayoutPanel
        {
            Name = "DetailsLayout",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty
        };
        detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < detailsLayout.RowCount; row++)
        {
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        _detailsTitleLabel = new Label
        {
            Name = "SelectedDeviceTitleLabel",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
            Text = "Select a computer"
        };
        _detailsLabel = new Label
        {
            Name = "SelectedDeviceDetailsLabel",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
            Text = "Select a computer to see its VPN identity, owner label, location, address, last-seen time, and available controls."
        };

        var routeGroup = new GroupBox
        {
            Name = "InternetRoutingGroup",
            Text = "Internet routing (VPN exit)",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var routeLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        routeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var routeActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _routeButton = CreateActionButton("Use as internet exit", "RouteButton", 190);
        _routeHelpLabel = CreateHelpLabel("Select a computer to see whether it can be used as an internet exit.", "RouteHelpLabel");
        _routeButton.Click += async (_, _) => await RouteSelectedAsync();
        routeActions.Controls.Add(_routeButton);
        routeLayout.Controls.Add(routeActions, 0, 0);
        routeLayout.Controls.Add(_routeHelpLabel, 0, 1);
        routeGroup.Controls.Add(routeLayout);

        var remoteAccessGroup = new GroupBox
        {
            Name = "RemoteAccessGroup",
            Text = "Screen and files",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var remoteAccessLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        remoteAccessLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        remoteAccessLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var remoteActionFlow = new FlowLayoutPanel
        {
            Name = "RemoteActionButtons",
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _viewScreenButton = CreateActionButton("View screen", "ViewScreenButton", 110);
        _sendFileButton = CreateActionButton("Send file", "SendFileButton", 100);
        _requestFileButton = CreateActionButton("Request a file", "RequestFileButton", 115);
        _viewScreenButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.ViewScreen);
        _sendFileButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.SendFile);
        _requestFileButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.RequestFile);
        remoteActionFlow.Controls.AddRange(new Control[]
        {
            _viewScreenButton,
            _sendFileButton,
            _requestFileButton
        });
        _remoteHelpLabel = CreateHelpLabel(
            "Screen and file controls require your self-hosted MeshCentral server and an authorized device mapping.",
            "RemoteActionsHelpLabel");
        remoteAccessLayout.Controls.Add(remoteActionFlow, 0, 0);
        remoteAccessLayout.Controls.Add(_remoteHelpLabel, 0, 1);
        remoteAccessGroup.Controls.Add(remoteAccessLayout);

        var safetyLabel = new Label
        {
            Name = "RemoteSafetyLabel",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
            Text = "Self-hosted only: screen and file controls use MeshCentral, with no hosted fallback. " +
                   "Bluetooth adapter and passkey relaying are not available in this build."
        };
        detailsLayout.Controls.Add(_detailsTitleLabel, 0, 0);
        detailsLayout.Controls.Add(_detailsLabel, 0, 1);
        detailsLayout.Controls.Add(routeGroup, 0, 2);
        detailsLayout.Controls.Add(remoteAccessGroup, 0, 3);
        detailsLayout.Controls.Add(safetyLabel, 0, 4);
        detailsPanel.Controls.Add(detailsLayout);
        detailsPanel.SizeChanged += (_, _) =>
        {
            ResizeDetailText(detailsPanel.ClientSize.Width);
            safetyLabel.MaximumSize = new Size(Math.Max(260, detailsPanel.ClientSize.Width - 54), 0);
        };
        split.Panel2.Controls.Add(detailsPanel);

        _footerLabel = new Label
        {
            Name = "RemotesFooterLabel",
            Dock = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(18, 9, 18, 0),
            AutoEllipsis = true,
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = SystemColors.GrayText,
            Text = "Waiting for the first refresh."
        };

        Controls.Add(split);
        Controls.Add(_footerLabel);
        Controls.Add(header);
        KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.F5)
            {
                eventArgs.Handled = true;
                await RefreshFleetAsync();
            }
            else if (eventArgs.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
        RefreshFromSnapshot();
        Shown += async (_, _) => await RefreshFleetAsync();
    }

    public void RefreshFromSnapshot()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        _currentSnapshot = _fleetClient.GetCachedSnapshot();
        _currentModel = RemoteMenuModelBuilder.Build(_currentSnapshot);
        _connectionLabel.Text = _currentModel.ConnectionText;
        _internetRouteLabel.Text = _currentModel.InternetRouteText;
        var routeIsActive = _currentSnapshot.ActiveExitNodeId is not null || _currentSnapshot.HasUnmanagedActiveExitNode;
        _internetRouteLabel.ForeColor = routeIsActive ? Color.DarkGreen : SystemColors.ControlText;
        _toolTip.SetToolTip(_connectionLabel, _currentSnapshot.StatusMessage);
        RenderDeviceList();
        UpdateFooter();
    }

    private async Task RefreshFleetAsync()
    {
        if (_refreshInProgress || _routeChangeInProgress || IsDisposed || Disposing)
        {
            return;
        }

        var cancellationToken = _lifetimeCancellation.Token;
        _refreshInProgress = true;
        _refreshButton.Enabled = false;
        _refreshButton.Text = "Refreshing...";
        _connectionLabel.Text = "Refreshing the self-hosted VPN and remote inventory...";
        try
        {
            await _fleetClient.RefreshAsync(cancellationToken);
            _noticeText = null;
        }
        catch (OperationCanceledException)
        {
            _noticeText = "Refresh cancelled.";
        }
        catch
        {
            _noticeText = "Refresh failed. The last known device state remains visible.";
        }
        finally
        {
            _refreshInProgress = false;
            if (!IsDisposed && !Disposing)
            {
                _refreshButton.Enabled = true;
                _refreshButton.Text = "Refresh";
                RefreshFromSnapshot();
            }
        }
    }

    private void RenderDeviceList()
    {
        if (_currentModel is null)
        {
            return;
        }

        var selectedId = GetSelectedDevice()?.Device.Id ?? _selectedDeviceId;
        var filter = _searchTextBox.Text.Trim();
        var visibleDevices = _currentModel.Devices
            .Where(device => MatchesFilter(device.Device, filter))
            .ToArray();

        _renderingDeviceList = true;
        _deviceList.BeginUpdate();
        try
        {
            _deviceList.Items.Clear();
            ListViewItem? itemToSelect = null;
            foreach (var device in visibleDevices)
            {
                var item = new ListViewItem(device.Device.DeviceName)
                {
                    Name = "Device_" + device.Device.Id,
                    Tag = device
                };
                item.SubItems.Add(FormatOwnerLabel(device.Device.OwnerDisplayName));
                item.SubItems.Add(FormatLocation(device.Device.Location));
                item.SubItems.Add(device.Device.IsOnline ? "Online" : "Offline");
                item.SubItems.Add(device.IsActiveExitNode
                    ? "Active"
                    : device.Device.Capabilities.HasFlag(RemoteCapability.ExitNode)
                        ? device.Device.IsOnline && device.Device.IsVerified ? "Ready" : "Unavailable"
                        : "No");
                _deviceList.Items.Add(item);
                if (string.Equals(device.Device.Id, selectedId, StringComparison.Ordinal))
                {
                    itemToSelect = item;
                }
            }

            if (itemToSelect is null && _deviceList.Items.Count > 0)
            {
                itemToSelect = _deviceList.Items[0];
            }
            if (itemToSelect is not null)
            {
                itemToSelect.Selected = true;
                itemToSelect.Focused = true;
            }
        }
        finally
        {
            _deviceList.EndUpdate();
            _renderingDeviceList = false;
        }

        var onlineCount = visibleDevices.Count(device => device.Device.IsOnline);
        _fleetSummaryLabel.Text = visibleDevices.Length == _currentModel.Devices.Count
            ? $"{visibleDevices.Length} computer{(visibleDevices.Length == 1 ? string.Empty : "s")} - {onlineCount} online"
            : $"Showing {visibleDevices.Length} of {_currentModel.Devices.Count} computers - {onlineCount} online";
        ResizeDeviceColumns();
        RenderDetails();
    }

    private void RenderDetails()
    {
        if (_renderingDeviceList)
        {
            return;
        }

        var selected = GetSelectedDevice();

        if (selected is null)
        {
            _detailsTitleLabel.Text = _currentModel?.Devices.Count == 0
                ? "No remote computers found"
                : "Select a computer";
            _detailsLabel.Text = _currentModel?.Devices.Count == 0
                ? "No other StayActive computers are currently enrolled in this self-hosted VPN. Use Add device to enroll one."
                : "No computer matches the current search, or no computer is selected.";
            SetActionButtons(null);
            return;
        }

        _selectedDeviceId = selected.Device.Id;
        var device = selected.Device;
        _detailsTitleLabel.Text = selected.IsActiveExitNode
            ? $"{device.DeviceName} - active internet exit"
            : device.DeviceName;
        _detailsLabel.Text =
                             $"Status: {(device.IsOnline ? "Online" : "Offline")}\r\n" +
                             $"VPN identity: {(device.IsVerified ? "Enrolled and trusted by Headscale" : "Not verified")}\r\n" +
                             $"Owner / user label: {FormatOwnerLabel(device.OwnerDisplayName)}\r\n" +
                             $"Location: {FormatLocation(device.Location)}\r\n" +
                             $"VPN address: {FormatValue(device.TailnetIp, "Not reported")}\r\n" +
                             $"Last seen: {FormatLastSeen(device)}\r\n" +
                             $"Capabilities: {FormatCapabilities(device.Capabilities)}\r\n" +
                             $"Remote management: {(string.IsNullOrWhiteSpace(device.MeshCentralNodeId) ? "Not linked to MeshCentral" : "Linked to self-hosted MeshCentral")}\r\n\r\n" +
                             "Owner/user and location are opt-in labels. StayActive does not silently read the remote Windows account or infer physical location from its IP address.";
        SetActionButtons(selected);
    }

    private void OpenSettings()
    {
        using var form = new RemoteSettingsForm(_getPreferences(), _savePreferences);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            RefreshFromSnapshot();
        }
    }

    private void OpenAdminConsole()
    {
        var result = _adminConsoleLauncher.Open();
        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task SignInToRemoteHubAsync()
    {
        try
        {
            await _remoteHubTokenProvider.SignInAsync(_lifetimeCancellation.Token);
            await RefreshFleetAsync();
            if (!IsDisposed && !Disposing)
            {
                _noticeText = "RemoteHub sign-in completed.";
                UpdateFooter();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteHubOidcException exception)
        {
            MessageBox.Show(this, exception.Message, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            MessageBox.Show(this, "Could not sign in to the self-hosted RemoteHub.", "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Button CreateHeaderButton(string text, string name)
    {
        return new Button
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(6, 3, 0, 3)
        };
    }

    private static Button CreateActionButton(string text, string name, int minimumWidth)
    {
        return new Button
        {
            Name = name,
            AutoSize = true,
            MinimumSize = new Size(minimumWidth, 0),
            Enabled = false,
            Text = text,
            Margin = new Padding(0, 0, 8, 8)
        };
    }

    private static Label CreateHelpLabel(string text, string name)
    {
        return new Label
        {
            Name = name,
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = text,
            Margin = new Padding(0, 2, 0, 4)
        };
    }

    private void SetActionButtons(RemoteMenuDevice? selected)
    {
        var device = selected?.Device;
        var exitAvailability = selected is null
            ? new RemoteActionAvailability(false, "Select a computer first.")
            : selected.IsActiveExitNode
                ? _exitNodeController.GetClearAvailability()
                : _exitNodeController.GetAvailability(selected.Device);
        _routeButton.Text = selected?.IsActiveExitNode == true
            ? "Stop using as internet exit"
            : "Use as internet exit";
        _routeButton.Enabled = exitAvailability.IsAvailable && !_routeChangeInProgress;
        _routeButton.AccessibleDescription = exitAvailability.Reason;
        _toolTip.SetToolTip(_routeButton, exitAvailability.IsAvailable ? _routeButton.Text : exitAvailability.Reason);
        _routeHelpLabel.Text = selected is null
            ? "Select a computer to see whether it can be used as an internet exit."
            : selected.IsActiveExitNode
                ? selected.Device.IsOnline
                    ? $"Internet traffic is configured to leave through {selected.Device.DeviceName}, which is online. Local-network bypass is disabled."
                    : $"{selected.Device.DeviceName} remains configured as the internet exit but is offline. Internet access may be unavailable until it reconnects or you stop using it."
                : exitAvailability.IsAvailable
                    ? $"Use {selected.Device.DeviceName}'s public connection for websites and downloads. Local-network bypass will remain disabled."
                    : exitAvailability.Reason;

        var screenAvailability = SetActionButton(_viewScreenButton, device, RemoteWebAction.ViewScreen);
        var sendAvailability = SetActionButton(_sendFileButton, device, RemoteWebAction.SendFile);
        var requestAvailability = SetActionButton(_requestFileButton, device, RemoteWebAction.RequestFile);
        var unavailableReasons = new[] { screenAvailability, sendAvailability, requestAvailability }
            .Where(availability => !availability.IsAvailable)
            .Select(availability => availability.Reason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _remoteHelpLabel.Text = unavailableReasons.Length == 0
            ? "Ready. These actions open only your self-hosted MeshCentral server; its consent and permission policies still apply."
            : _currentSnapshot.ConnectionState == RemoteFleetConnectionState.Degraded
                ? "VPN control is available, but screen and file control are not set up: " + _currentSnapshot.StatusMessage
                : string.Join(Environment.NewLine, unavailableReasons);
    }

    private RemoteActionAvailability SetActionButton(Button button, RemoteDevice? device, RemoteWebAction action)
    {
        var availability = device is null
            ? new RemoteActionAvailability(false, "Select a computer first.")
            : _remoteActionService.GetAvailability(device, action);
        button.Enabled = availability.IsAvailable;
        button.AccessibleDescription = availability.Reason;
        _toolTip.SetToolTip(button, availability.IsAvailable ? button.Text : availability.Reason);
        return availability;
    }

    private void OpenSelectedAction(RemoteWebAction action)
    {
        var selected = GetSelectedDevice();
        if (selected is null)
        {
            return;
        }

        var result = _remoteActionService.Open(selected.Device, action);
        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RouteSelectedAsync()
    {
        if (_routeChangeInProgress || IsDisposed || Disposing)
        {
            return;
        }

        var selected = GetSelectedDevice();
        if (selected is null)
        {
            return;
        }

        var cancellationToken = _lifetimeCancellation.Token;
        Func<Task<RemoteActionResult>> applyRouteChange;
        if (selected.IsActiveExitNode)
        {
            var confirmation = MessageBox.Show(
                this,
                "Stop routing this computer's internet traffic through the selected remote computer and restore direct routing?",
                "Turn off internet routing",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            applyRouteChange = () => _exitNodeController.ClearExitNodeAsync(cancellationToken);
        }
        else
        {
            var confirmation = MessageBox.Show(
                this,
                $"Route all internet traffic through {selected.Device.DeviceName}?\n\n" +
                $"Non-overlay traffic from this computer will leave through {selected.Device.DeviceName}'s public network connection. " +
                "Its owner can see connection metadata and any traffic that is not end-to-end encrypted by the destination. " +
                "Access to this computer's local network will remain disabled while this route is active.",
                "Route internet traffic",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            applyRouteChange = () => _exitNodeController.UseExitNodeAsync(
                    selected.Device,
                    allowLocalNetworkAccess: false,
                    cancellationToken);
        }

        _routeChangeInProgress = true;
        _routeButton.Enabled = false;
        _refreshButton.Enabled = false;
        _routeHelpLabel.Text = "Applying the internet-routing change...";
        try
        {
            var result = await applyRouteChange();
            _noticeText = result.Message;
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Message, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                try
                {
                    await _fleetClient.RefreshAsync(cancellationToken);
                }
                catch
                {
                    _noticeText += " Device status could not be refreshed yet; use Refresh to retry.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            _noticeText = "The internet-routing change was cancelled.";
        }
        catch
        {
            _noticeText = "The internet-routing change could not be applied. Your last known route remains visible.";
            MessageBox.Show(this, _noticeText, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _routeChangeInProgress = false;
            if (!IsDisposed && !Disposing)
            {
                _refreshButton.Enabled = !_refreshInProgress;
                RefreshFromSnapshot();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_ownedResourcesDisposed)
        {
            _ownedResourcesDisposed = true;
            _lifetimeCancellation.Cancel();
            _toolTip.Dispose();
            _lifetimeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    private RemoteMenuDevice? GetSelectedDevice()
    {
        return _deviceList.SelectedItems.Count == 1
            ? _deviceList.SelectedItems[0].Tag as RemoteMenuDevice
            : null;
    }

    private static bool MatchesFilter(RemoteDevice device, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var searchable = string.Join(
            " ",
            device.DeviceName,
            device.OwnerDisplayName,
            device.Location,
            device.IsOnline ? "online" : "offline",
            device.TailnetIp,
            FormatCapabilities(device.Capabilities));
        return searchable.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOwnerLabel(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "Tagged Devices", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Headscale user", StringComparison.OrdinalIgnoreCase)
            ? "Not provided"
            : value;
    }

    private static string FormatLocation(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Not shared" : value;
    }

    private static string FormatValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FormatLastSeen(RemoteDevice device)
    {
        if (device.IsOnline)
        {
            return "Now";
        }

        return device.LastSeenAt is { Year: >= 2000 } lastSeen
            ? lastSeen.ToLocalTime().ToString("g")
            : "Not reported";
    }

    private void UpdateFooter()
    {
        var refreshedText = _currentSnapshot.RefreshedAt == DateTimeOffset.MinValue
            ? "Not refreshed yet"
            : "Last refreshed " + _currentSnapshot.RefreshedAt.ToLocalTime().ToString("g");
        var parts = new[] { _noticeText, _currentSnapshot.StatusMessage, refreshedText }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        _footerLabel.Text = string.Join("  |  ", parts);
        _toolTip.SetToolTip(_footerLabel, _footerLabel.Text);
    }

    private void ResizeDeviceColumns()
    {
        if (_deviceList.Columns.Count != 5)
        {
            return;
        }

        var width = _deviceList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (width < 250)
        {
            return;
        }

        var proportions = new[] { 28, 22, 24, 13 };
        var used = 0;
        for (var index = 0; index < proportions.Length; index++)
        {
            var columnWidth = width * proportions[index] / 100;
            _deviceList.Columns[index].Width = columnWidth;
            used += columnWidth;
        }
        _deviceList.Columns[4].Width = Math.Max(50, width - used);
    }

    private void ResizeDetailText(int panelWidth)
    {
        var textWidth = Math.Max(260, panelWidth - 54);
        _detailsLabel.MaximumSize = new Size(textWidth, 0);
        _routeHelpLabel.MaximumSize = new Size(textWidth - 24, 0);
        _remoteHelpLabel.MaximumSize = new Size(textWidth - 24, 0);
    }

    private static string FormatCapabilities(RemoteCapability capabilities)
    {
        return capabilities == RemoteCapability.None
            ? "No optional controls reported"
            : string.Join(", ", Enum.GetValues<RemoteCapability>()
                .Where(capability => capability != RemoteCapability.None && capabilities.HasFlag(capability))
                .Select(capability => capability switch
                {
                    RemoteCapability.ExitNode => "Internet exit",
                    RemoteCapability.ScreenView => "Screen control",
                    RemoteCapability.SendFile => "Send files",
                    RemoteCapability.RequestFile => "Request files",
                    RemoteCapability.LocalAuthenticatorApproval => "Local authenticator approval",
                    _ => capability.ToString()
                }));
    }
}

internal sealed class RemoteSettingsForm : Form
{
    private readonly TextBox _controlPlaneUrlTextBox;
    private readonly TextBox _remoteHubUrlTextBox;
    private readonly TextBox _remoteHubOidcIssuerUrlTextBox;
    private readonly TextBox _remoteHubOidcClientIdTextBox;
    private readonly TextBox _remoteEnrollmentUrlTextBox;
    private readonly TextBox _remoteEnrollmentOidcClientIdTextBox;
    private readonly TextBox _adminConsoleUrlTextBox;
    private readonly TextBox _meshCentralUrlTextBox;
    private readonly TextBox _deviceDisplayNameTextBox;
    private readonly TextBox _locationTextBox;
    private readonly Action<RemoteClientPreferences> _savePreferences;

    public RemoteSettingsForm(RemoteClientPreferences preferences, Action<RemoteClientPreferences> savePreferences)
    {
        _savePreferences = savePreferences;
        Text = "Self-hosted Remotes settings";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 12,
            Padding = new Padding(16),
            AutoSize = false
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _controlPlaneUrlTextBox = AddField(table, 0, "Headscale URL", preferences.ControlPlaneUrl, StayActiveRemoteDefaults.ControlPlaneUrl);
        _remoteHubUrlTextBox = AddField(table, 1, "RemoteHub URL", preferences.RemoteHubUrl, StayActiveRemoteDefaults.RemoteHubUrl);
        _remoteHubOidcIssuerUrlTextBox = AddField(table, 2, "OIDC issuer URL", preferences.RemoteHubOidcIssuerUrl, StayActiveRemoteDefaults.OidcIssuerUrl);
        _remoteHubOidcClientIdTextBox = AddField(table, 3, "Fleet OIDC client", preferences.RemoteHubOidcClientId, StayActiveRemoteDefaults.FleetOidcClientId);
        _remoteEnrollmentUrlTextBox = AddField(table, 4, "Enrollment broker URL", preferences.RemoteEnrollmentUrl, StayActiveRemoteDefaults.EnrollmentBrokerUrl);
        _remoteEnrollmentOidcClientIdTextBox = AddField(table, 5, "Enrollment OIDC client", preferences.RemoteEnrollmentOidcClientId, StayActiveRemoteDefaults.EnrollmentOidcClientId);
        _adminConsoleUrlTextBox = AddField(table, 6, "Admin GUI URL", preferences.AdminConsoleUrl, StayActiveRemoteDefaults.AdminConsoleUrl);
        _meshCentralUrlTextBox = AddField(table, 7, "MeshCentral URL", preferences.MeshCentralUrl, StayActiveRemoteDefaults.MeshCentralUrl);
        _deviceDisplayNameTextBox = AddField(table, 8, "This computer name", preferences.DeviceDisplayName, Environment.MachineName);
        _locationTextBox = AddField(table, 9, "Coarse location", preferences.Location, "Optional, for example: Austin office");

        var note = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "Only non-secret display and endpoint settings are stored here. Enrollment keys, certificates, and server API keys are never stored in this file."
        };
        table.Controls.Add(note, 0, 10);
        table.SetColumnSpan(note, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        var saveButton = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        saveButton.Click += (_, _) => Save();
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        table.Controls.Add(buttons, 0, 11);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void Save()
    {
        var preferences = new RemoteClientPreferences(
            _controlPlaneUrlTextBox.Text.Trim(),
            _remoteHubUrlTextBox.Text.Trim(),
            _adminConsoleUrlTextBox.Text.Trim(),
            _meshCentralUrlTextBox.Text.Trim(),
            _deviceDisplayNameTextBox.Text.Trim(),
            _locationTextBox.Text.Trim(),
            _remoteHubOidcIssuerUrlTextBox.Text.Trim(),
            _remoteHubOidcClientIdTextBox.Text.Trim(),
            _remoteEnrollmentUrlTextBox.Text.Trim(),
            _remoteEnrollmentOidcClientIdTextBox.Text.Trim());

        if ((!string.IsNullOrWhiteSpace(preferences.ControlPlaneUrl)
                && !RemoteClientPreferences.IsSelfHostedControlPlane(preferences.ControlPlaneUrl))
            || !IsSafeEndpoint(preferences.RemoteHubUrl)
            || !IsSafeEndpoint(preferences.AdminConsoleUrl)
            || !IsSafeEndpoint(preferences.MeshCentralUrl)
            || !IsSafeEndpoint(preferences.RemoteEnrollmentUrl)
            || !IsValidOidcConfiguration(preferences)
            || !IsValidEnrollmentOidcConfiguration(preferences))
        {
            MessageBox.Show(
                this,
                "Use absolute HTTPS URLs for self-hosted endpoints. Tailscale-hosted URLs are not allowed; provide each configured OIDC client with its required companion settings.",
                "Invalid remote settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _savePreferences(preferences);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static TextBox AddField(TableLayoutPanel table, int row, string label, string value, string placeholder)
    {
        var fieldLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = label
        };
        var textBox = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Text = value,
            PlaceholderText = placeholder
        };
        table.Controls.Add(fieldLabel, 0, row);
        table.Controls.Add(textBox, 1, row);
        return textBox;
    }

    private static bool IsSafeEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return RemoteClientPreferences.IsSelfHostedEndpoint(value);
    }

    private static bool IsValidOidcConfiguration(RemoteClientPreferences preferences)
    {
        var issuerEmpty = string.IsNullOrWhiteSpace(preferences.RemoteHubOidcIssuerUrl);
        var clientEmpty = string.IsNullOrWhiteSpace(preferences.RemoteHubOidcClientId);
        return issuerEmpty && clientEmpty
            || RemoteHubOidcConfiguration.TryCreate(
                preferences.RemoteHubOidcIssuerUrl,
                preferences.RemoteHubOidcClientId,
                out _);
    }

    private static bool IsValidEnrollmentOidcConfiguration(RemoteClientPreferences preferences)
    {
        var brokerEmpty = string.IsNullOrWhiteSpace(preferences.RemoteEnrollmentUrl);
        var clientEmpty = string.IsNullOrWhiteSpace(preferences.RemoteEnrollmentOidcClientId);
        return brokerEmpty && clientEmpty
            || !brokerEmpty
                && !clientEmpty
                && RemoteHubOidcConfiguration.TryCreateEnrollment(
                    preferences.RemoteHubOidcIssuerUrl,
                    preferences.RemoteEnrollmentOidcClientId,
                    out _);
    }
}
