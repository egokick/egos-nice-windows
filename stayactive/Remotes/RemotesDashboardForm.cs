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
    private readonly Action<string, RemoteDeviceDisplayOverride> _saveDeviceDisplayOverride;
    private readonly Label _connectionLabel;
    private readonly Label _internetRouteLabel;
    private readonly Label _fleetSummaryLabel;
    private readonly TextBox _searchTextBox;
    private readonly ListView _deviceList;
    private readonly Label _detailsTitleLabel;
    private readonly Label _detailsLabel;
    private readonly CheckBox _routeToggle;
    private readonly Button _turnOffVpnButton;
    private readonly Button _viewScreenButton;
    private readonly Button _sendFileButton;
    private readonly Button _requestFileButton;
    private readonly Label _routeHelpLabel;
    private readonly Label _remoteHelpLabel;
    private readonly TextBox _computerNameTextBox;
    private readonly TextBox _ownerLabelTextBox;
    private readonly TextBox _locationTextBox;
    private readonly TextBox _vpnAddressTextBox;
    private readonly Button _saveLabelsButton;
    private readonly Button _resetLabelsButton;
    private readonly Label _footerLabel;
    private readonly Button _refreshButton;
    private readonly ToolTip _toolTip = new();
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<string, RemoteDeviceDisplayOverride> _labelDrafts = new(StringComparer.Ordinal);
    private RemoteMenuModel? _currentModel;
    private RemoteFleetSnapshot _currentSnapshot = RemoteFleetSnapshot.NotConfigured;
    private string? _selectedDeviceId;
    private string? _noticeText;
    private string? _routeErrorText;
    private bool _renderingDeviceList;
    private bool _refreshInProgress;
    private bool _routeChangeInProgress;
    private bool _routeChangeTurningOff;
    private string? _routeChangeTargetName;
    private bool _updatingLabelFields;
    private bool _labelFieldsDirty;
    private bool _ownedResourcesDisposed;

    public RemotesDashboardForm(
        IRemoteFleetClient fleetClient,
        IRemoteActionService remoteActionService,
        IRemoteExitNodeController exitNodeController,
        IRemoteAdminConsoleLauncher adminConsoleLauncher,
        IRemoteHubAccessTokenProvider remoteHubTokenProvider,
        Func<RemoteClientPreferences> getPreferences,
        Action<RemoteClientPreferences> savePreferences,
        Action? openAddDevice = null,
        Action<string, RemoteDeviceDisplayOverride>? saveDeviceDisplayOverride = null)
    {
        _fleetClient = fleetClient;
        _remoteActionService = remoteActionService;
        _exitNodeController = exitNodeController;
        _adminConsoleLauncher = adminConsoleLauncher;
        _remoteHubTokenProvider = remoteHubTokenProvider;
        _openAddDevice = openAddDevice ?? (() => { });
        _getPreferences = getPreferences;
        _savePreferences = savePreferences;
        _saveDeviceDisplayOverride = saveDeviceDisplayOverride ?? ((_, _) => { });

        Text = "StayActive Remotes";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 540);
        Size = new Size(1220, 780);
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var header = new TableLayoutPanel
        {
            Name = "RemotesHeader",
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(20, 12, 20, 10),
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
            RowCount = 2,
            Margin = Padding.Empty
        };
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var title = new Label
        {
            Name = "RemotesTitle",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            Text = "Remotes"
        };
        _connectionLabel = new Label
        {
            Name = "ConnectionStatusLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = "Checking your private network..."
        };
        headerStatus.Controls.Add(title, 0, 0);
        headerStatus.Controls.Add(_connectionLabel, 0, 1);

        var headerActions = new FlowLayoutPanel
        {
            Name = "HeaderActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 14, 0, 0),
            Margin = Padding.Empty
        };
        var signInMenuItem = _moreMenu.Items.Add("Remote access sign in...");
        signInMenuItem.Name = "SignInMenuItem";
        signInMenuItem.Click += async (_, _) => await SignInToRemoteHubAsync();
        var adminMenuItem = _moreMenu.Items.Add("Administration...");
        adminMenuItem.Name = "AdminConsoleMenuItem";
        adminMenuItem.Click += (_, _) => OpenAdminConsole();
        _moreMenu.Items.Add(new ToolStripSeparator());
        var settingsMenuItem = _moreMenu.Items.Add("Settings...");
        settingsMenuItem.Name = "SettingsMenuItem";
        settingsMenuItem.Click += (_, _) => OpenSettings();
        var moreButton = CreateHeaderButton("More  ▾", "MoreButton");
        moreButton.ContextMenuStrip = _moreMenu;
        moreButton.Click += (_, _) => _moreMenu.Show(moreButton, new Point(0, moreButton.Height));
        var addDeviceButton = CreateHeaderButton("+ Add computer", "AddDeviceButton");
        addDeviceButton.Click += (_, _) => _openAddDevice();
        _refreshButton = new Button
        {
            Name = "RefreshButton",
            Text = "Refresh",
            AutoSize = true,
            MinimumSize = new Size(88, 36),
            Margin = new Padding(6, 3, 0, 3)
        };
        _refreshButton.Click += async (_, _) => await RefreshFleetAsync();
        _toolTip.SetToolTip(_refreshButton, "Refresh VPN presence and self-hosted remote-management metadata (F5).");
        headerActions.Controls.AddRange(new Control[]
        {
            moreButton,
            _refreshButton,
            addDeviceButton
        });
        header.Controls.Add(headerStatus, 0, 0);
        header.Controls.Add(headerActions, 1, 0);

        var split = new SplitContainer
        {
            Name = "RemotesSplitContainer",
            Dock = DockStyle.Fill,
            Size = new Size(1180, 650),
            SplitterDistance = 420,
            Panel1MinSize = 290,
            Panel2MinSize = 460,
            FixedPanel = FixedPanel.Panel1
        };

        _deviceList = new ListView
        {
            Name = "DeviceList",
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = false,
            View = View.Tile,
            TileSize = new Size(360, 78),
            BorderStyle = BorderStyle.FixedSingle,
            Activation = ItemActivation.OneClick
        };
        _deviceList.Columns.Add("Computer");
        _deviceList.Columns.Add("Labels");
        _deviceList.Columns.Add("Status");
        _deviceList.SelectedIndexChanged += (_, _) => RenderDetails();
        _deviceList.SizeChanged += (_, _) => ResizeDeviceTiles();

        _searchTextBox = new TextBox
        {
            Name = "DeviceSearchTextBox",
            Dock = DockStyle.Fill,
            PlaceholderText = "Search computers...",
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
            Padding = new Padding(12, 10, 12, 12)
        };
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        devicePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        devicePanel.Controls.Add(new Label
        {
            Name = "ComputersHeadingLabel",
            Text = "Your computers",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            Anchor = AnchorStyles.Left
        }, 0, 0);
        devicePanel.Controls.Add(_searchTextBox, 0, 1);
        devicePanel.Controls.Add(_fleetSummaryLabel, 0, 2);
        devicePanel.Controls.Add(_deviceList, 0, 3);
        split.Panel1.Padding = new Padding(8);
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
            RowCount = 6,
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
            AutoSize = false,
            AutoEllipsis = true,
            Height = 42,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12),
            Text = "Select a computer"
        };
        _detailsLabel = new Label
        {
            Name = "SelectedDeviceDetailsLabel",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 2),
            Text = "Select a computer to see its connection details."
        };

        var routeGroup = new GroupBox
        {
            Name = "InternetRoutingGroup",
            Text = "Internet VPN",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12)
        };
        var routeLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2
        };
        routeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        routeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _internetRouteLabel = new Label
        {
            Name = "InternetRouteStatusLabel",
            AutoSize = false,
            AutoEllipsis = true,
            Height = 42,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Margin = new Padding(0, 3, 12, 4),
            Text = "Checking VPN status..."
        };
        _routeToggle = new CheckBox
        {
            Name = "VpnToggle",
            Appearance = Appearance.Button,
            AutoCheck = false,
            CheckAlign = ContentAlignment.MiddleCenter,
            TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Font = new Font(Font, FontStyle.Bold),
            MinimumSize = new Size(146, 54),
            AutoSize = false,
            Size = new Size(146, 54),
            Enabled = false,
            AccessibleRole = AccessibleRole.CheckButton,
            TabStop = true,
            Text = "VPN OFF",
            Margin = new Padding(12, 0, 0, 0)
        };
        _routeToggle.FlatAppearance.CheckedBackColor = Color.FromArgb(46, 125, 50);
        _routeToggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 226, 232);
        _turnOffVpnButton = new Button
        {
            Name = "TurnOffVpnButton",
            Text = "Turn VPN off",
            AutoSize = false,
            Size = new Size(146, 34),
            Visible = false,
            AccessibleName = "Turn Internet VPN off",
            Margin = new Padding(12, 6, 0, 0)
        };
        _routeHelpLabel = CreateHelpLabel(
            "Select a computer to use its internet connection.",
            "RouteHelpLabel");
        _routeToggle.Click += async (_, _) => await RouteSelectedAsync();
        _turnOffVpnButton.Click += async (_, _) => await RouteSelectedAsync(forceClear: true);
        routeLayout.Controls.Add(_internetRouteLabel, 0, 0);
        routeLayout.SetColumnSpan(_internetRouteLabel, 2);
        routeLayout.Controls.Add(_routeHelpLabel, 0, 1);
        var routeControls = new FlowLayoutPanel
        {
            Name = "VpnControls",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        routeControls.Controls.Add(_routeToggle);
        routeControls.Controls.Add(_turnOffVpnButton);
        routeLayout.Controls.Add(routeControls, 1, 1);
        routeGroup.Controls.Add(routeLayout);

        var aboutGroup = new GroupBox
        {
            Name = "AboutComputerGroup",
            Text = "About this computer",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12)
        };
        var aboutLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7,
            Margin = Padding.Empty
        };
        aboutLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        aboutLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < aboutLayout.RowCount; row++)
        {
            aboutLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        _computerNameTextBox = CreateDetailsTextBox("ComputerNameTextBox", readOnly: true);
        _ownerLabelTextBox = CreateDetailsTextBox("OwnerLabelTextBox", readOnly: false);
        _ownerLabelTextBox.PlaceholderText = "Optional";
        _ownerLabelTextBox.MaxLength = RemoteDeviceDisplayOverride.MaxLabelLength;
        _locationTextBox = CreateDetailsTextBox("LocationTextBox", readOnly: false);
        _locationTextBox.PlaceholderText = "Optional";
        _locationTextBox.MaxLength = RemoteDeviceDisplayOverride.MaxLabelLength;
        _vpnAddressTextBox = CreateDetailsTextBox("VpnAddressTextBox", readOnly: true);
        _ownerLabelTextBox.TextChanged += (_, _) => MarkLabelsDirty();
        _locationTextBox.TextChanged += (_, _) => MarkLabelsDirty();
        AddDetailsRow(aboutLayout, 0, "PC name", _computerNameTextBox);
        AddDetailsRow(aboutLayout, 1, "User label", _ownerLabelTextBox);
        AddDetailsRow(aboutLayout, 2, "Location", _locationTextBox);
        AddDetailsRow(aboutLayout, 3, "VPN address", _vpnAddressTextBox);
        var labelButtons = new FlowLayoutPanel
        {
            Name = "LabelEditButtons",
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        _saveLabelsButton = CreateActionButton("Save labels", "SaveLabelsButton", 110);
        _saveLabelsButton.Enabled = false;
        _saveLabelsButton.Click += (_, _) => SaveSelectedLabels();
        _resetLabelsButton = CreateActionButton("Reset labels", "ResetLabelsButton", 110);
        _resetLabelsButton.Enabled = false;
        _resetLabelsButton.Click += (_, _) => ResetSelectedLabels();
        labelButtons.Controls.AddRange(new Control[] { _saveLabelsButton, _resetLabelsButton });
        aboutLayout.Controls.Add(labelButtons, 1, 4);
        var labelHelp = CreateHelpLabel(
            "User and location are optional labels saved only on this PC. Computer name and VPN address come from your private network and are read-only.",
            "LabelHelpLabel");
        aboutLayout.Controls.Add(labelHelp, 0, 5);
        aboutLayout.SetColumnSpan(labelHelp, 2);
        aboutLayout.Controls.Add(_detailsLabel, 0, 6);
        aboutLayout.SetColumnSpan(_detailsLabel, 2);
        aboutGroup.Controls.Add(aboutLayout);

        var remoteAccessGroup = new GroupBox
        {
            Name = "RemoteAccessGroup",
            Text = "Remote access",
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
        _viewScreenButton = CreateActionButton("View screen", "ViewScreenButton", 112);
        _sendFileButton = CreateActionButton("Send files", "SendFileButton", 106);
        _requestFileButton = CreateActionButton("Get files", "RequestFileButton", 106);
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
            "Screen and file tools use your self-hosted RemoteHub and MeshCentral services.",
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
            Text = "Remote tools use only your self-hosted services. Bluetooth passkey relay is not available yet."
        };
        detailsLayout.Controls.Add(_detailsTitleLabel, 0, 0);
        detailsLayout.Controls.Add(routeGroup, 0, 1);
        detailsLayout.Controls.Add(aboutGroup, 0, 2);
        detailsLayout.Controls.Add(remoteAccessGroup, 0, 3);
        detailsLayout.Controls.Add(safetyLabel, 0, 4);
        detailsLayout.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Fill }, 0, 5);
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
        FormClosing += RemotesDashboardForm_FormClosing;
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
        var onlineCount = _currentSnapshot.Devices.Count(device => device.IsOnline);
        _connectionLabel.Text = _currentSnapshot.ConnectionState switch
        {
            RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded =>
                $"Private network connected  •  {onlineCount} computer{(onlineCount == 1 ? string.Empty : "s")} online",
            RemoteFleetConnectionState.Connecting => "Private network connecting...",
            RemoteFleetConnectionState.NotConfigured => "Private network needs setup",
            _ => "Private network disconnected"
        };
        _connectionLabel.ForeColor = _currentSnapshot.ConnectionState switch
        {
            RemoteFleetConnectionState.Connected or RemoteFleetConnectionState.Degraded => Color.DarkGreen,
            RemoteFleetConnectionState.Connecting => Color.DarkGoldenrod,
            _ => Color.Firebrick
        };
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
        _connectionLabel.Text = "Checking your private network...";
        try
        {
            await _fleetClient.RefreshAsync(cancellationToken);
            _noticeText = null;
            _routeErrorText = null;
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
                item.SubItems.Add(
                    $"User: {FormatOwnerLabel(device.Device.OwnerDisplayName)}  •  Location: {FormatLocation(device.Device.Location)}");
                item.SubItems.Add(
                    $"{(device.Device.IsOnline ? "Online" : "Offline")}  •  " +
                    (device.IsActiveExitNode
                    ? "VPN in use"
                    : device.Device.Capabilities.HasFlag(RemoteCapability.ExitNode)
                        ? device.Device.IsOnline && device.Device.IsVerified ? "VPN ready" : "VPN unavailable"
                        : "No VPN routing"));
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
        ResizeDeviceTiles();
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
            _toolTip.SetToolTip(_detailsTitleLabel, _detailsTitleLabel.Text);
            _detailsLabel.Text = _currentModel?.Devices.Count == 0
                ? "No other StayActive computers are enrolled yet. Choose + Add computer to connect one."
                : "No computer matches the current search, or no computer is selected.";
            PopulateDeviceFields(null, force: true);
            SetActionButtons(null);
            return;
        }

        var selectionChanged = !string.Equals(_selectedDeviceId, selected.Device.Id, StringComparison.Ordinal);
        _selectedDeviceId = selected.Device.Id;
        var device = selected.Device;
        _detailsTitleLabel.Text = $"{device.DeviceName}  •  {(device.IsOnline ? "Online" : "Offline")}";
        _toolTip.SetToolTip(_detailsTitleLabel, _detailsTitleLabel.Text);
        _detailsLabel.Text =
                             $"Last seen: {FormatLastSeen(device)}  •  " +
                             $"Identity: {(device.IsVerified ? "Verified" : "Not verified")}  •  " +
                             $"Remote access: {(string.IsNullOrWhiteSpace(device.MeshCentralNodeId) ? "Not set up" : "Linked")}\r\n" +
                             $"Available tools: {FormatCapabilities(device.Capabilities)}";
        PopulateDeviceFields(device, force: selectionChanged || !_labelFieldsDirty);
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
            MinimumSize = new Size(96, 36),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(6, 3, 0, 3)
        };
    }

    private static Button CreateActionButton(string text, string name, int minimumWidth)
    {
        return new Button
        {
            Name = name,
            AutoSize = true,
            MinimumSize = new Size(minimumWidth, 36),
            Enabled = false,
            Text = text,
            Margin = new Padding(0, 0, 8, 8)
        };
    }

    private static TextBox CreateDetailsTextBox(string name, bool readOnly)
    {
        return new TextBox
        {
            Name = name,
            Dock = DockStyle.Fill,
            ReadOnly = readOnly,
            BackColor = readOnly ? SystemColors.Control : SystemColors.Window,
            ForeColor = SystemColors.ControlText,
            Margin = new Padding(0, 2, 0, 5),
            AccessibleRole = AccessibleRole.Text,
            AccessibleDescription = readOnly
                ? "Read-only value from your private network. You can select and copy it."
                : "Editable label saved only on this computer."
        };
    }

    private static void AddDetailsRow(TableLayoutPanel layout, int row, string labelText, Control field)
    {
        var label = new Label
        {
            Name = field.Name + "Label",
            AutoSize = true,
            Text = labelText,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 10, 5)
        };
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(field, 1, row);
    }

    private void MarkLabelsDirty()
    {
        if (_updatingLabelFields)
        {
            return;
        }

        _labelFieldsDirty = true;
        if (!string.IsNullOrWhiteSpace(_selectedDeviceId))
        {
            _labelDrafts[_selectedDeviceId] = new RemoteDeviceDisplayOverride(
                _ownerLabelTextBox.Text,
                _locationTextBox.Text);
        }
        _saveLabelsButton.Enabled = GetSelectedDevice() is not null;
    }

    private void PopulateDeviceFields(RemoteDevice? device, bool force)
    {
        var hasDevice = device is not null;
        _computerNameTextBox.Enabled = hasDevice;
        _ownerLabelTextBox.Enabled = hasDevice;
        _locationTextBox.Enabled = hasDevice;
        _vpnAddressTextBox.Enabled = hasDevice;
        _resetLabelsButton.Enabled = hasDevice;
        if (!force)
        {
            return;
        }

        _updatingLabelFields = true;
        try
        {
            RemoteDeviceDisplayOverride? draft = null;
            var hasDraft = device is not null
                && _labelDrafts.TryGetValue(device.Id, out draft);
            _computerNameTextBox.Text = device?.DeviceName ?? string.Empty;
            _ownerLabelTextBox.Text = device is null
                ? string.Empty
                : hasDraft
                    ? draft!.OwnerOrUserLabel ?? string.Empty
                    : FormatEditableOwnerLabel(device.OwnerDisplayName);
            _locationTextBox.Text = device is null
                ? string.Empty
                : hasDraft
                    ? draft!.Location ?? string.Empty
                    : device.Location?.Trim() ?? string.Empty;
            _vpnAddressTextBox.Text = device is null ? string.Empty : FormatValue(device.TailnetIp, "Not reported");
            _labelFieldsDirty = hasDraft;
            _saveLabelsButton.Enabled = hasDraft;
        }
        finally
        {
            _updatingLabelFields = false;
        }
    }

    private void SaveSelectedLabels()
    {
        var selected = GetSelectedDevice();
        if (selected is null)
        {
            return;
        }

        try
        {
            _saveDeviceDisplayOverride(
                selected.Device.Id,
                new RemoteDeviceDisplayOverride(_ownerLabelTextBox.Text, _locationTextBox.Text));
        }
        catch
        {
            _noticeText = "Labels could not be saved. Your edits are still here so you can try again.";
            UpdateFooter();
            return;
        }
        _labelDrafts.Remove(selected.Device.Id);
        _labelFieldsDirty = false;
        _noticeText = "Labels saved on this computer.";
        if (!string.IsNullOrWhiteSpace(_searchTextBox.Text))
        {
            _searchTextBox.Clear();
        }
        RefreshFromSnapshot();
    }

    private void ResetSelectedLabels()
    {
        var selected = GetSelectedDevice();
        if (selected is null)
        {
            return;
        }

        try
        {
            _saveDeviceDisplayOverride(selected.Device.Id, new RemoteDeviceDisplayOverride());
        }
        catch
        {
            _noticeText = "Labels could not be reset. Try again.";
            UpdateFooter();
            return;
        }
        _labelDrafts.Remove(selected.Device.Id);
        _labelFieldsDirty = false;
        _noticeText = "Local labels reset.";
        if (!string.IsNullOrWhiteSpace(_searchTextBox.Text))
        {
            _searchTextBox.Clear();
        }
        RefreshFromSnapshot();
    }

    private void RemotesDashboardForm_FormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_labelDrafts.Count == 0
            || eventArgs.CloseReason is CloseReason.WindowsShutDown
                or CloseReason.ApplicationExitCall
                or CloseReason.TaskManagerClosing)
        {
            return;
        }

        var decision = MessageBox.Show(
            this,
            "You have unsaved user or location labels. Save them before closing Remotes?",
            "Unsaved labels",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (decision == DialogResult.Cancel)
        {
            eventArgs.Cancel = true;
            return;
        }

        if (decision == DialogResult.No)
        {
            _labelDrafts.Clear();
            return;
        }

        try
        {
            foreach (var draft in _labelDrafts.ToArray())
            {
                _saveDeviceDisplayOverride(draft.Key, draft.Value);
            }
            _labelDrafts.Clear();
            _labelFieldsDirty = false;
        }
        catch
        {
            eventArgs.Cancel = true;
            _noticeText = "Labels could not be saved. The Remotes window will stay open so your edits are not lost.";
            UpdateFooter();
        }
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
        var activeDevice = _currentModel?.Devices.FirstOrDefault(candidate => candidate.IsActiveExitNode)?.Device;
        var hasUnknownActiveVpn = _currentSnapshot.HasUnmanagedActiveExitNode
            || (_currentSnapshot.ActiveExitNodeId is not null && activeDevice is null);
        var hasActiveVpn = activeDevice is not null || hasUnknownActiveVpn;
        var selectedIsActive = selected?.IsActiveExitNode == true;
        var clearAvailability = hasActiveVpn
            ? _exitNodeController.GetClearAvailability()
            : new RemoteActionAvailability(false, "VPN is already off.");
        var exitAvailability = hasUnknownActiveVpn
            ? clearAvailability
            : selected is null
            ? new RemoteActionAvailability(false, "Select a computer first.")
            : selectedIsActive
                ? clearAvailability
                : _exitNodeController.GetAvailability(selected.Device);

        if (_routeChangeInProgress)
        {
            _internetRouteLabel.Text = "CHANGING VPN...";
            _internetRouteLabel.ForeColor = Color.DarkSlateBlue;
            _routeToggle.Text = _routeChangeTurningOff ? "DISCONNECTING..." : "CONNECTING...";
            _routeHelpLabel.Text = _routeChangeTurningOff
                ? "Restoring this computer's normal internet connection."
                : $"Connecting through {_routeChangeTargetName ?? "the selected computer"}.";
        }
        else if (activeDevice is { IsOnline: true })
        {
            _internetRouteLabel.Text = $"VPN ON through {activeDevice.DeviceName}";
            _internetRouteLabel.ForeColor = Color.DarkGreen;
            _routeToggle.Text = selectedIsActive ? "VPN ON" : hasActiveVpn ? "SWITCH VPN" : "VPN OFF";
            _routeHelpLabel.Text = selectedIsActive
                ? $"Websites and downloads are using {activeDevice.DeviceName}'s internet connection. Select VPN ON to turn it off."
                : selected is null
                    ? $"Websites and downloads are using {activeDevice.DeviceName}'s internet connection. Select Turn VPN off to restore direct access."
                    : exitAvailability.IsAvailable
                        ? $"Currently using {activeDevice.DeviceName}. Select SWITCH VPN to use {selected.Device.DeviceName} instead, or turn VPN off."
                        : $"Currently using {activeDevice.DeviceName}. {FriendlyExitReason(selected.Device, exitAvailability.Reason)} You can still turn VPN off.";
        }
        else if (activeDevice is not null)
        {
            _internetRouteLabel.Text = "VPN NEEDS ATTENTION";
            _internetRouteLabel.ForeColor = Color.DarkGoldenrod;
            _routeToggle.Text = selectedIsActive ? "VPN ON" : "SWITCH VPN";
            _routeHelpLabel.Text = selectedIsActive
                ? $"{activeDevice.DeviceName} is still selected for VPN but is offline. Turn VPN off to restore direct internet access."
                : $"{activeDevice.DeviceName} is selected for VPN but is offline. Turn VPN off to restore direct internet access.";
        }
        else if (hasUnknownActiveVpn)
        {
            _internetRouteLabel.Text = "VPN ON through another exit";
            _internetRouteLabel.ForeColor = Color.DarkGreen;
            _routeToggle.Text = "VPN ON";
            _routeHelpLabel.Text = "Another VPN exit is active. Select VPN ON to turn it off and restore direct internet access.";
        }
        else
        {
            _internetRouteLabel.Text = "VPN OFF";
            _internetRouteLabel.ForeColor = SystemColors.ControlText;
            _routeToggle.Text = "VPN OFF";
            _routeHelpLabel.Text = selected is null
                ? "Your websites and downloads are using this computer's normal internet connection. Select a computer to turn VPN on."
                : exitAvailability.IsAvailable
                    ? $"Your internet connection is direct. Turn VPN on to use {selected.Device.DeviceName}'s internet connection."
                    : $"Your internet connection is direct. {FriendlyExitReason(selected.Device, exitAvailability.Reason)}";
        }

        _routeToggle.Checked = selectedIsActive || hasUnknownActiveVpn;
        _routeToggle.Enabled = exitAvailability.IsAvailable && !_routeChangeInProgress;
        _turnOffVpnButton.Visible = activeDevice is not null && !selectedIsActive && !_routeChangeInProgress;
        _turnOffVpnButton.Enabled = clearAvailability.IsAvailable && !_routeChangeInProgress;
        _turnOffVpnButton.AccessibleDescription = _turnOffVpnButton.Enabled
            ? "Restore this computer's normal internet connection."
            : clearAvailability.Reason;
        _toolTip.SetToolTip(_turnOffVpnButton, _turnOffVpnButton.AccessibleDescription);
        _routeToggle.AccessibleName = hasUnknownActiveVpn
            ? "Internet VPN through another exit"
            : selected is null
            ? "Internet VPN"
            : $"Use {selected.Device.DeviceName} for internet VPN";
        _routeToggle.AccessibleDescription = _routeHelpLabel.Text;
        var toggleBackColor = _routeChangeInProgress
            ? Color.LightSteelBlue
            : hasUnknownActiveVpn
                ? Color.FromArgb(46, 125, 50)
                : selectedIsActive
                    ? selected?.Device.IsOnline == true ? Color.FromArgb(46, 125, 50) : Color.DarkGoldenrod
                : Color.FromArgb(235, 238, 242);
        _routeToggle.BackColor = toggleBackColor;
        _routeToggle.FlatAppearance.CheckedBackColor = toggleBackColor;
        _routeToggle.FlatAppearance.MouseOverBackColor = ControlPaint.Light(toggleBackColor, 0.08F);
        var useWhiteToggleText = !_routeChangeInProgress
            && (hasUnknownActiveVpn || selected is { IsActiveExitNode: true, Device: { IsOnline: true } });
        _routeToggle.ForeColor = useWhiteToggleText ? Color.White : SystemColors.ControlText;
        if (!string.IsNullOrWhiteSpace(_routeErrorText))
        {
            _routeHelpLabel.Text += Environment.NewLine + "Couldn't change VPN: " + _routeErrorText;
            _routeToggle.AccessibleDescription = _routeHelpLabel.Text;
        }
        _toolTip.SetToolTip(_internetRouteLabel, _internetRouteLabel.Text);
        _toolTip.SetToolTip(_routeToggle, _routeHelpLabel.Text);

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
            ? "Ready. These actions open only your self-hosted remote-access service."
            : _currentSnapshot.ConnectionState == RemoteFleetConnectionState.Degraded
                ? "Screen and file tools need RemoteHub and MeshCentral setup. Open More > Remote access sign in."
                : device is { IsOnline: false }
                    ? "This computer is offline. Screen and file tools will be available after it reconnects."
                    : device is { IsVerified: false }
                        ? "Remote access is disabled until this computer is verified."
                        : "Screen and file tools are not set up for this computer yet.";
    }

    private static string FriendlyExitReason(RemoteDevice device, string fallback)
    {
        if (!device.IsOnline)
        {
            return "Can't connect: this computer is offline.";
        }

        if (!device.IsVerified)
        {
            return "Can't connect: this computer has not been verified.";
        }

        if (!device.Capabilities.HasFlag(RemoteCapability.ExitNode))
        {
            return "VPN routing is not enabled on this computer.";
        }

        return string.IsNullOrWhiteSpace(fallback) ? "VPN is unavailable for this computer." : fallback;
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

    private async Task RouteSelectedAsync(bool forceClear = false)
    {
        if (_routeChangeInProgress || IsDisposed || Disposing)
        {
            return;
        }

        var selected = GetSelectedDevice();
        var activeDeviceIsKnown = _currentModel?.Devices.Any(device => device.IsActiveExitNode) == true;
        var hasUnknownActiveVpn = _currentSnapshot.HasUnmanagedActiveExitNode
            || (_currentSnapshot.ActiveExitNodeId is not null && !activeDeviceIsKnown);
        if (selected is null && !hasUnknownActiveVpn && !forceClear)
        {
            return;
        }

        var cancellationToken = _lifetimeCancellation.Token;
        Func<Task<RemoteActionResult>> applyRouteChange;
        var turningOff = forceClear || hasUnknownActiveVpn || selected?.IsActiveExitNode == true;
        if (turningOff)
        {
            var availability = _exitNodeController.GetClearAvailability();
            if (!availability.IsAvailable)
            {
                _routeErrorText = availability.Reason;
                SetActionButtons(selected);
                return;
            }
            applyRouteChange = () => _exitNodeController.ClearExitNodeAsync(cancellationToken);
        }
        else
        {
            var selectedDevice = selected!.Device;
            var availability = _exitNodeController.GetAvailability(selectedDevice);
            if (!availability.IsAvailable)
            {
                _routeErrorText = FriendlyExitReason(selectedDevice, availability.Reason);
                SetActionButtons(selected);
                return;
            }
            applyRouteChange = () => _exitNodeController.UseExitNodeAsync(
                    selectedDevice,
                    allowLocalNetworkAccess: false,
                    cancellationToken);
        }

        _routeErrorText = null;
        _routeChangeInProgress = true;
        _routeChangeTurningOff = turningOff;
        _routeChangeTargetName = selected?.Device.DeviceName;
        _refreshButton.Enabled = false;
        SetActionButtons(selected);
        try
        {
            var result = await applyRouteChange();
            if (!result.Succeeded)
            {
                _noticeText = "VPN change failed.";
                _routeErrorText = result.Message;
            }
            else
            {
                try
                {
                    await _fleetClient.RefreshAsync(cancellationToken);
                    var refreshedSnapshot = _fleetClient.GetCachedSnapshot();
                    var noActiveExit = refreshedSnapshot.ActiveExitNodeId is null
                        && !refreshedSnapshot.HasUnmanagedActiveExitNode;
                    var selectedExitConfirmed = selected is not null
                        && string.Equals(
                            refreshedSnapshot.ActiveExitNodeId,
                            selected.Device.Id,
                            StringComparison.Ordinal);
                    _noticeText = turningOff && noActiveExit
                        ? "VPN turned off."
                        : !turningOff && selectedExitConfirmed
                            ? $"VPN connected through {selected!.Device.DeviceName}."
                            : "VPN request completed, but the new state is not confirmed yet. Select Refresh to check again.";
                }
                catch
                {
                    _noticeText = "VPN request completed, but the new state could not be checked. Select Refresh to try again.";
                    _routeErrorText = "The command was accepted, but its new status could not be confirmed. Select Refresh to check again.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            _noticeText = "The internet-routing change was cancelled.";
            _routeErrorText = "The change was cancelled.";
        }
        catch
        {
            _noticeText = "The internet-routing change could not be applied. Your last known route remains visible.";
            _routeErrorText = "The change could not be applied. Your last known VPN state is still shown.";
        }
        finally
        {
            _routeChangeInProgress = false;
            _routeChangeTargetName = null;
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
            _moreMenu.Dispose();
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
            ? "Not set"
            : value;
    }

    private static string FormatEditableOwnerLabel(string? value)
    {
        return string.Equals(FormatOwnerLabel(value), "Not set", StringComparison.Ordinal)
            ? string.Empty
            : value?.Trim() ?? string.Empty;
    }

    private static string FormatLocation(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Not set" : value;
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
            ? "Not checked yet"
            : "Last checked " + _currentSnapshot.RefreshedAt.ToLocalTime().ToString("g");
        _footerLabel.Text = string.IsNullOrWhiteSpace(_noticeText)
            ? refreshedText
            : _noticeText + "  •  " + refreshedText;
        _toolTip.SetToolTip(
            _footerLabel,
            string.Join(Environment.NewLine, new[] { _noticeText, _currentSnapshot.StatusMessage, refreshedText }
                .Where(part => !string.IsNullOrWhiteSpace(part))));
    }

    private void ResizeDeviceTiles()
    {
        if (_deviceList.ClientSize.Width < 220)
        {
            return;
        }

        _deviceList.TileSize = new Size(
            Math.Max(220, _deviceList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8),
            78);
    }

    private void ResizeDetailText(int panelWidth)
    {
        var textWidth = Math.Max(260, panelWidth - 54);
        _detailsLabel.MaximumSize = new Size(textWidth, 0);
        _routeHelpLabel.MaximumSize = new Size(Math.Max(220, textWidth - _routeToggle.Width - 28), 0);
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
