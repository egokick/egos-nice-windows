using System.Drawing;

namespace StayActive.Remotes;

internal sealed class RemotesDashboardForm : Form
{
    private readonly IRemoteFleetClient _fleetClient;
    private readonly IRemoteActionService _remoteActionService;
    private readonly IRemoteExitNodeController _exitNodeController;
    private readonly IRemoteAdminConsoleLauncher _adminConsoleLauncher;
    private readonly IRemoteHubAccessTokenProvider _remoteHubTokenProvider;
    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly Action<RemoteClientPreferences> _savePreferences;
    private readonly Label _connectionLabel;
    private readonly ListView _deviceList;
    private readonly Label _detailsLabel;
    private readonly Button _routeButton;
    private readonly Button _viewScreenButton;
    private readonly Button _sendFileButton;
    private readonly Button _requestFileButton;

    public RemotesDashboardForm(
        IRemoteFleetClient fleetClient,
        IRemoteActionService remoteActionService,
        IRemoteExitNodeController exitNodeController,
        IRemoteAdminConsoleLauncher adminConsoleLauncher,
        IRemoteHubAccessTokenProvider remoteHubTokenProvider,
        Func<RemoteClientPreferences> getPreferences,
        Action<RemoteClientPreferences> savePreferences)
    {
        _fleetClient = fleetClient;
        _remoteActionService = remoteActionService;
        _exitNodeController = exitNodeController;
        _adminConsoleLauncher = adminConsoleLauncher;
        _remoteHubTokenProvider = remoteHubTokenProvider;
        _getPreferences = getPreferences;
        _savePreferences = savePreferences;

        Text = "StayActive Remotes";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 560);
        Size = new Size(980, 640);
        Font = new Font("Segoe UI", 9F);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(16, 12, 16, 12)
        };
        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Remotes",
            Location = new Point(16, 12)
        };
        _connectionLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(16, 36),
            Size = new Size(560, 22)
        };
        var signInButton = new Button
        {
            Text = "Sign in…",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(490, 18)
        };
        signInButton.Click += async (_, _) => await SignInToRemoteHubAsync();
        var adminConsoleButton = new Button
        {
            Text = "Admin console…",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(580, 18)
        };
        adminConsoleButton.Click += (_, _) => OpenAdminConsole();
        var pairButton = new Button
        {
            Text = "Pair a computer…",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(708, 18)
        };
        pairButton.Click += (_, _) => OpenSettings();
        var settingsButton = new Button
        {
            Text = "Settings…",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(832, 18)
        };
        settingsButton.Click += (_, _) => OpenSettings();
        header.Controls.AddRange(new Control[] { title, _connectionLabel, signInButton, adminConsoleButton, pairButton, settingsButton });

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 390,
            Panel1MinSize = 280,
            Panel2MinSize = 380
        };

        _deviceList = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            View = View.Details
        };
        _deviceList.Columns.Add("Computer", 145);
        _deviceList.Columns.Add("Owner", 100);
        _deviceList.Columns.Add("Location", 120);
        _deviceList.Columns.Add("Status", 70);
        _deviceList.SelectedIndexChanged += (_, _) => RenderDetails();
        split.Panel1.Padding = new Padding(12);
        split.Panel1.Controls.Add(_deviceList);

        var detailsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };
        _detailsLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 150,
            Text = "Select a computer to see its verified identity, available capabilities, and consent policy."
        };
        var actionFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _routeButton = CreateActionButton("Route traffic through this computer");
        _viewScreenButton = CreateActionButton("View screen");
        _sendFileButton = CreateActionButton("Send file");
        _requestFileButton = CreateActionButton("Request a file");
        _routeButton.Click += async (_, _) => await RouteSelectedAsync();
        _viewScreenButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.ViewScreen);
        _sendFileButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.SendFile);
        _requestFileButton.Click += (_, _) => OpenSelectedAction(RemoteWebAction.RequestFile);
        actionFlow.Controls.AddRange(new Control[]
        {
            _routeButton,
            _viewScreenButton,
            _sendFileButton,
            _requestFileButton
        });

        var safetyLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(0, 18, 0, 0),
            Text = "Sensitive actions require enrollment, policy authorization, and target-side consent. " +
                   "Phone-based authentication is supported only through local authenticator approval; " +
                   "this app does not relay Bluetooth adapters or passkey material."
        };
        detailsPanel.Controls.Add(safetyLabel);
        detailsPanel.Controls.Add(actionFlow);
        detailsPanel.Controls.Add(_detailsLabel);
        split.Panel2.Controls.Add(detailsPanel);

        Controls.Add(split);
        Controls.Add(header);
        Shown += (_, _) => RefreshFromSnapshot();
    }

    public void RefreshFromSnapshot()
    {
        var snapshot = _fleetClient.GetCachedSnapshot();
        var model = RemoteMenuModelBuilder.Build(snapshot);
        _connectionLabel.Text = model.ConnectionText;
        _deviceList.BeginUpdate();
        _deviceList.Items.Clear();
        foreach (var device in model.Devices)
        {
            var item = new ListViewItem(device.Device.DeviceName)
            {
                Tag = device
            };
            item.SubItems.Add(device.Device.OwnerDisplayName);
            item.SubItems.Add(device.Device.LocationDisplay);
            item.SubItems.Add(device.Device.IsOnline ? "Online" : "Offline");
            _deviceList.Items.Add(item);
        }
        _deviceList.EndUpdate();
        RenderDetails();
    }

    private void RenderDetails()
    {
        var selected = _deviceList.SelectedItems.Count == 1
            ? _deviceList.SelectedItems[0].Tag as RemoteMenuDevice
            : null;

        if (selected is null)
        {
            _detailsLabel.Text = "Select a computer to see its verified identity, available capabilities, and consent policy.";
            SetActionButtons(null);
            return;
        }

        var device = selected.Device;
        _detailsLabel.Text = $"{device.DeviceName}\r\n" +
                             $"Owner: {device.OwnerDisplayName}\r\n" +
                             $"Location: {device.LocationDisplay}\r\n" +
                             $"Status: {(device.IsOnline ? "Online" : "Offline")}\r\n" +
                             $"Identity: {(device.IsVerified ? "Verified" : "Unverified")}\r\n" +
                             $"Capabilities: {FormatCapabilities(device.Capabilities)}\r\n\r\n" +
                             "Screen and file actions open only the configured self-hosted MeshCentral server. " +
                             "Target consent and server-side permission policies remain in effect.";
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
            await _remoteHubTokenProvider.SignInAsync(CancellationToken.None);
            await _fleetClient.RefreshAsync(CancellationToken.None);
            RefreshFromSnapshot();
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

    private static Button CreateActionButton(string text)
    {
        return new Button
        {
            AutoSize = true,
            Enabled = false,
            Text = text,
            Margin = new Padding(0, 0, 8, 8)
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
            ? "Turn off internet routing"
            : "Route traffic through this computer";
        _routeButton.Enabled = exitAvailability.IsAvailable;
        _routeButton.AccessibleDescription = exitAvailability.Reason;
        SetActionButton(_viewScreenButton, device, RemoteWebAction.ViewScreen);
        SetActionButton(_sendFileButton, device, RemoteWebAction.SendFile);
        SetActionButton(_requestFileButton, device, RemoteWebAction.RequestFile);
    }

    private void SetActionButton(Button button, RemoteDevice? device, RemoteWebAction action)
    {
        var availability = device is null
            ? new RemoteActionAvailability(false, "Select a computer first.")
            : _remoteActionService.GetAvailability(device, action);
        button.Enabled = availability.IsAvailable;
        button.AccessibleDescription = availability.Reason;
    }

    private void OpenSelectedAction(RemoteWebAction action)
    {
        var selected = _deviceList.SelectedItems.Count == 1
            ? _deviceList.SelectedItems[0].Tag as RemoteMenuDevice
            : null;
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
        var selected = _deviceList.SelectedItems.Count == 1
            ? _deviceList.SelectedItems[0].Tag as RemoteMenuDevice
            : null;
        if (selected is null)
        {
            return;
        }

        RemoteActionResult result;
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

            result = await _exitNodeController.ClearExitNodeAsync(CancellationToken.None);
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

            result = await _exitNodeController.UseExitNodeAsync(
                selected.Device,
                allowLocalNetworkAccess: false,
                CancellationToken.None);
        }

        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "StayActive Remotes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            await _fleetClient.RefreshAsync(CancellationToken.None);
        }
        catch
        {
            // The next tray opening retries the status refresh. A completed route
            // command should not be presented as failed merely because telemetry
            // was temporarily unavailable.
        }

        RefreshFromSnapshot();
    }

    private static string FormatCapabilities(RemoteCapability capabilities)
    {
        return capabilities == RemoteCapability.None
            ? "None reported"
            : string.Join(", ", Enum.GetValues<RemoteCapability>()
                .Where(capability => capability != RemoteCapability.None && capabilities.HasFlag(capability))
                .Select(capability => capability switch
                {
                    RemoteCapability.ExitNode => "Exit node",
                    RemoteCapability.ScreenView => "Screen view",
                    RemoteCapability.SendFile => "Send file",
                    RemoteCapability.RequestFile => "Request file",
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
        ClientSize = new Size(620, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(16),
            AutoSize = false
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _controlPlaneUrlTextBox = AddField(table, 0, "Headscale URL", preferences.ControlPlaneUrl, "https://headscale.example.com");
        _remoteHubUrlTextBox = AddField(table, 1, "RemoteHub URL", preferences.RemoteHubUrl, "https://remotes.example.com");
        _remoteHubOidcIssuerUrlTextBox = AddField(table, 2, "OIDC issuer URL", preferences.RemoteHubOidcIssuerUrl, "https://id.example.com/realms/remotes");
        _remoteHubOidcClientIdTextBox = AddField(table, 3, "OIDC public client", preferences.RemoteHubOidcClientId, "stayactive-remotes");
        _adminConsoleUrlTextBox = AddField(table, 4, "Admin GUI URL", preferences.AdminConsoleUrl, "https://remotes.example.com/admin");
        _meshCentralUrlTextBox = AddField(table, 5, "MeshCentral URL", preferences.MeshCentralUrl, "https://mesh.example.com");
        _deviceDisplayNameTextBox = AddField(table, 6, "This computer name", preferences.DeviceDisplayName, Environment.MachineName);
        _locationTextBox = AddField(table, 7, "Coarse location", preferences.Location, "Optional, for example: Austin office");

        var note = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "Only non-secret display and endpoint settings are stored here. Enrollment keys, certificates, and server API keys are never stored in this file."
        };
        table.Controls.Add(note, 0, 8);
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
        table.Controls.Add(buttons, 0, 9);
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
            _remoteHubOidcClientIdTextBox.Text.Trim());

        if ((!string.IsNullOrWhiteSpace(preferences.ControlPlaneUrl)
                && !RemoteClientPreferences.IsSelfHostedControlPlane(preferences.ControlPlaneUrl))
            || !IsSafeEndpoint(preferences.RemoteHubUrl)
            || !IsSafeEndpoint(preferences.AdminConsoleUrl)
            || !IsSafeEndpoint(preferences.MeshCentralUrl)
            || !IsValidOidcConfiguration(preferences))
        {
            MessageBox.Show(
                this,
                "Use absolute HTTPS URLs for self-hosted endpoints, do not use a Tailscale-hosted control-plane URL, and provide both OIDC values together.",
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

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
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
}
