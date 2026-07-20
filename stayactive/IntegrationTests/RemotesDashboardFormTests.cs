using System.Runtime.ExceptionServices;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

[CollectionDefinition("WinForms", DisableParallelization = true)]
public sealed class WinFormsCollectionDefinition;

[Collection("WinForms")]
public sealed class RemotesDashboardFormTests
{
    [Fact]
    public void ConstructorAndLayout_DefaultAndMinimumSizes_KeepPrimaryControlsReadable()
    {
        RunSta(() =>
        {
            using var form = CreateForm(CreateSnapshot(activeExitNodeId: null));
            ShowForLayout(form);

            foreach (var size in new[] { new Size(1220, 780), form.MinimumSize })
            {
                form.Size = size;
                ForceLayout(form);
                Application.DoEvents();

                var split = Find<SplitContainer>(form, "RemotesSplitContainer");
                Assert.True(split.Panel1.Width >= split.Panel1MinSize);
                Assert.True(split.Panel2.Width >= split.Panel2MinSize);
                Assert.InRange(
                    split.SplitterDistance,
                    split.Panel1MinSize,
                    split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth);

                var headerActions = Find<FlowLayoutPanel>(form, "HeaderActions");
                var buttons = headerActions.Controls.OfType<Button>().Where(button => button.Visible).ToArray();
                Assert.Equal(3, buttons.Length);
                Assert.Equal(
                    new[] { "MoreButton", "RefreshButton", "AddDeviceButton" },
                    buttons.Select(button => button.Name));
                foreach (var button in buttons)
                {
                    Assert.True(headerActions.ClientRectangle.Contains(button.Bounds));
                    AssertFits(button);
                }

                var list = Find<ListView>(form, "DeviceList");
                Assert.Equal(View.Tile, list.View);
                Assert.Equal(3, list.Columns.Count);
                Assert.True(list.ClientSize.Width > 250);
                Assert.True(list.TileSize.Width <= list.ClientSize.Width);

                var detailsPanel = Find<Panel>(form, "DetailsPanel");
                Assert.True(detailsPanel.AutoScroll);
                Assert.True(detailsPanel.ClientSize.Width > 450);

                var toggle = Find<CheckBox>(form, "VpnToggle");
                Assert.Equal(Appearance.Button, toggle.Appearance);
                Assert.True(toggle.Width >= 146);
                Assert.True(toggle.Height >= 54);
                Assert.Equal(AccessibleRole.CheckButton, toggle.AccessibleRole);
                Assert.True(toggle.TabStop);
                Assert.Contains("Austin-PC", toggle.AccessibleName, StringComparison.Ordinal);
                AssertFits(toggle);
                Assert.True(toggle.Parent!.ClientRectangle.Contains(toggle.Bounds));

                foreach (var name in new[] { "ComputerNameTextBox", "OwnerLabelTextBox", "LocationTextBox", "VpnAddressTextBox" })
                {
                    var field = Find<TextBox>(form, name);
                    Assert.True(field.Width >= 250);
                    Assert.True(field.Parent!.ClientRectangle.Contains(field.Bounds));

                    var caption = Find<Label>(form, name + "Label");
                    var measuredCaption = TextRenderer.MeasureText(
                        caption.Text,
                        caption.Font,
                        Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    Assert.True(
                        measuredCaption.Width <= caption.ClientSize.Width,
                        $"{caption.Name} text '{caption.Text}' measured {measuredCaption.Width}px but its client width is {caption.ClientSize.Width}px.");
                }
            }

            Assert.True(Find<TextBox>(form, "ComputerNameTextBox").ReadOnly);
            Assert.False(Find<TextBox>(form, "OwnerLabelTextBox").ReadOnly);
            Assert.False(Find<TextBox>(form, "LocationTextBox").ReadOnly);
            Assert.True(Find<TextBox>(form, "VpnAddressTextBox").ReadOnly);
            Assert.Equal(SystemColors.Control, Find<TextBox>(form, "ComputerNameTextBox").BackColor);
            Assert.Equal(SystemColors.Window, Find<TextBox>(form, "OwnerLabelTextBox").BackColor);
            Assert.Contains("Read-only", Find<TextBox>(form, "ComputerNameTextBox").AccessibleDescription, StringComparison.Ordinal);
            Assert.Contains("Read-only", Find<TextBox>(form, "VpnAddressTextBox").AccessibleDescription, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void MinimumSize_LongComputerAndLocation_DoNotPushVpnToggleOffScreen()
    {
        RunSta(() =>
        {
            var snapshot = CreateSnapshot(activeExitNodeId: null);
            var longDevice = snapshot.Devices[0] with
            {
                DeviceName = "International-Travel-Laptop-With-A-Very-Long-Computer-Name",
                OwnerDisplayName = "A deliberately long but valid owner or user label for layout verification",
                Location = "A deliberately long location label that should remain selectable without widening the window"
            };
            using var form = CreateForm(snapshot with { Devices = new[] { longDevice } });
            ShowForLayout(form);
            form.Size = form.MinimumSize;
            ForceLayout(form);
            Application.DoEvents();

            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.True(toggle.Parent!.ClientRectangle.Contains(toggle.Bounds));
            AssertFits(toggle);
            Assert.False(Find<Panel>(form, "DetailsPanel").HorizontalScroll.Visible);
            Assert.True(Find<TextBox>(form, "ComputerNameTextBox").Width >= 250);
            Assert.Contains("Turn VPN on", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void DirectRoute_RendersUnambiguousOffToggle_AndConnectsSelectedComputer()
    {
        RunSta(() =>
        {
            var exitNodeController = new FakeExitNodeController();
            using var form = CreateForm(
                CreateSnapshot(activeExitNodeId: null),
                exitNodeController: exitNodeController);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var list = Find<ListView>(form, "DeviceList");
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(3, list.Columns.Count);
            Assert.Equal("Austin-PC", list.Items[0].Text);
            Assert.Equal(3, list.Items[0].SubItems.Count);
            Assert.Contains("User: Alice", list.Items[0].SubItems[1].Text, StringComparison.Ordinal);
            Assert.Contains("Location: Austin office", list.Items[0].SubItems[1].Text, StringComparison.Ordinal);
            Assert.Contains("Online", list.Items[0].SubItems[2].Text, StringComparison.Ordinal);
            Assert.Contains("VPN ready", list.Items[0].SubItems[2].Text, StringComparison.Ordinal);

            Assert.StartsWith("Private network connected", Find<Label>(form, "ConnectionStatusLabel").Text, StringComparison.Ordinal);
            Assert.Equal("VPN OFF", Find<Label>(form, "InternetRouteStatusLabel").Text);
            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.Equal("VPN OFF", toggle.Text);
            Assert.False(toggle.Checked);
            Assert.True(toggle.Enabled);
            Assert.Contains("direct", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Turn VPN on", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);

            Assert.Equal("Austin-PC", Find<TextBox>(form, "ComputerNameTextBox").Text);
            Assert.Equal("Alice", Find<TextBox>(form, "OwnerLabelTextBox").Text);
            Assert.Equal("Austin office", Find<TextBox>(form, "LocationTextBox").Text);
            Assert.Equal("100.64.0.10", Find<TextBox>(form, "VpnAddressTextBox").Text);
            Assert.Contains("Last seen: Now", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Identity: Verified", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Remote access: Linked", GetDetailsText(form), StringComparison.Ordinal);

            InvokeClick(toggle);
            Application.DoEvents();

            Assert.Equal(1, exitNodeController.UseCalls);
            Assert.Equal("node:austin", exitNodeController.LastUsedDevice?.Id);
            Assert.False(exitNodeController.LastAllowLocalNetworkAccess);

            Find<TextBox>(form, "DeviceSearchTextBox").Text = "Austin";
            Application.DoEvents();
            Assert.Single(list.Items.Cast<ListViewItem>());

            form.Close();
        });
    }

    [Fact]
    public void ActiveRoute_RendersUnambiguousOnToggle_AndDisconnects()
    {
        RunSta(() =>
        {
            var exitNodeController = new FakeExitNodeController();
            using var form = CreateForm(
                CreateSnapshot(activeExitNodeId: "node:austin"),
                exitNodeController: exitNodeController);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            Assert.Equal("VPN ON through Austin-PC", Find<Label>(form, "InternetRouteStatusLabel").Text);
            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.Equal("VPN ON", toggle.Text);
            Assert.True(toggle.Checked);
            Assert.True(toggle.Enabled);
            Assert.Equal(Color.White, toggle.ForeColor);
            Assert.Contains("Websites and downloads are using Austin-PC", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);
            Assert.Contains("turn it off", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.OrdinalIgnoreCase);

            form.Size = form.MinimumSize;
            ForceLayout(form);
            Application.DoEvents();
            var headline = Find<Label>(form, "InternetRouteStatusLabel");
            var measuredHeadline = TextRenderer.MeasureText(headline.Text, headline.Font);
            Assert.True(
                measuredHeadline.Width <= headline.ClientSize.Width,
                $"VPN headline measured {measuredHeadline.Width}px but only {headline.ClientSize.Width}px is available.");

            InvokeClick(toggle);
            Application.DoEvents();

            Assert.Equal(1, exitNodeController.ClearCalls);
            Assert.Equal(0, exitNodeController.UseCalls);

            form.Close();
        });
    }

    [Fact]
    public void AnotherComputerSelected_KeepsGlobalVpnOnObviousAndOffersSwitchOrTurnOff()
    {
        RunSta(() =>
        {
            var snapshot = CreateSnapshot(activeExitNodeId: "node:austin");
            var london = snapshot.Devices[1] with
            {
                IsOnline = true,
                IsVerified = true,
                OwnerDisplayName = "Bob",
                Location = "London"
            };
            var fleetClient = new FakeFleetClient(snapshot with
            {
                Devices = new[] { snapshot.Devices[0], london }
            });
            var exitController = new FakeExitNodeController();
            using var form = CreateForm(
                fleetClient.Snapshot,
                fleetClient: fleetClient,
                exitNodeController: exitController);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var list = Find<ListView>(form, "DeviceList");
            list.Items[0].Selected = false;
            list.Items[1].Selected = true;
            list.Items[1].Focused = true;
            Application.DoEvents();

            Assert.Equal("VPN ON through Austin-PC", Find<Label>(form, "InternetRouteStatusLabel").Text);
            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.Equal("SWITCH VPN", toggle.Text);
            Assert.False(toggle.Checked);
            Assert.True(toggle.Enabled);
            var turnOff = Find<Button>(form, "TurnOffVpnButton");
            Assert.True(turnOff.Visible);
            Assert.True(turnOff.Enabled);
            AssertFits(turnOff);

            turnOff.PerformClick();
            Application.DoEvents();

            Assert.Equal(1, exitController.ClearCalls);
            Assert.Equal(0, exitController.UseCalls);

            form.Close();
        });
    }

    [Fact]
    public void DirectRoute_RemainsConnectingAndDisabled_UntilAuthoritativeRefreshReportsActiveExit()
    {
        RunSta(() =>
        {
            var initialSnapshot = CreateSnapshot(activeExitNodeId: null);
            var fleetClient = new FakeFleetClient(initialSnapshot);
            var exitNodeController = new FakeExitNodeController
            {
                UseCompletion = new TaskCompletionSource<RemoteActionResult>()
            };
            using var form = CreateForm(
                initialSnapshot,
                fleetClient: fleetClient,
                exitNodeController: exitNodeController);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var refreshCallsBeforeToggle = fleetClient.RefreshCalls;
            var toggle = Find<CheckBox>(form, "VpnToggle");
            InvokeClick(toggle);
            Application.DoEvents();

            Assert.Equal("CONNECTING...", toggle.Text);
            Assert.False(toggle.Enabled);
            Assert.Equal("CHANGING VPN...", Find<Label>(form, "InternetRouteStatusLabel").Text);
            Assert.Contains("Connecting through Austin-PC", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);
            Assert.Equal(1, exitNodeController.UseCalls);
            Assert.Equal(refreshCallsBeforeToggle, fleetClient.RefreshCalls);

            fleetClient.Snapshot = initialSnapshot with
            {
                ActiveExitNodeId = "node:austin",
                RefreshedAt = DateTimeOffset.UtcNow
            };
            exitNodeController.UseCompletion!.SetResult(RemoteActionResult.Success("Enabled."));
            PumpMessagesUntil(() => toggle.Text == "VPN ON" && toggle.Checked && toggle.Enabled);

            Assert.Equal(refreshCallsBeforeToggle + 1, fleetClient.RefreshCalls);
            Assert.Equal("VPN ON through Austin-PC", Find<Label>(form, "InternetRouteStatusLabel").Text);
            Assert.True(toggle.Checked);
            Assert.True(toggle.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void ActiveRoute_RemainsDisconnectingAndDisabled_UntilAuthoritativeRefreshReportsDirect()
    {
        RunSta(() =>
        {
            var initialSnapshot = CreateSnapshot(activeExitNodeId: "node:austin");
            var fleetClient = new FakeFleetClient(initialSnapshot);
            var exitNodeController = new FakeExitNodeController
            {
                ClearCompletion = new TaskCompletionSource<RemoteActionResult>()
            };
            using var form = CreateForm(
                initialSnapshot,
                fleetClient: fleetClient,
                exitNodeController: exitNodeController);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var refreshCallsBeforeToggle = fleetClient.RefreshCalls;
            var toggle = Find<CheckBox>(form, "VpnToggle");
            InvokeClick(toggle);
            Application.DoEvents();

            Assert.Equal("DISCONNECTING...", toggle.Text);
            Assert.False(toggle.Enabled);
            Assert.Equal("CHANGING VPN...", Find<Label>(form, "InternetRouteStatusLabel").Text);
            Assert.Contains("Restoring this computer's normal internet connection", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);
            Assert.Equal(1, exitNodeController.ClearCalls);
            Assert.Equal(refreshCallsBeforeToggle, fleetClient.RefreshCalls);

            fleetClient.Snapshot = initialSnapshot with
            {
                ActiveExitNodeId = null,
                RefreshedAt = DateTimeOffset.UtcNow
            };
            exitNodeController.ClearCompletion!.SetResult(RemoteActionResult.Success("Disabled."));
            PumpMessagesUntil(() => toggle.Text == "VPN OFF" && !toggle.Checked && toggle.Enabled);

            Assert.Equal(refreshCallsBeforeToggle + 1, fleetClient.RefreshCalls);
            Assert.Equal("VPN OFF", Find<Label>(form, "InternetRouteStatusLabel").Text);
            Assert.False(toggle.Checked);
            Assert.True(toggle.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void UnknownNonNullActiveExitNode_NeverClaimsVpnIsOff()
    {
        RunSta(() =>
        {
            using var form = CreateForm(CreateSnapshot(activeExitNodeId: "node:not-in-inventory"));
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var status = Find<Label>(form, "InternetRouteStatusLabel").Text;
            Assert.StartsWith("VPN ON", status, StringComparison.Ordinal);
            Assert.DoesNotContain("VPN OFF", status, StringComparison.Ordinal);
            Assert.Contains("Another VPN exit is active", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void DegradedFleet_UsesShortSetupGuidance_InsteadOfRawStatusCopy()
    {
        RunSta(() =>
        {
            const string diagnostic = "REMOTEHUB_DIAGNOSTIC_THAT_SHOULD_ONLY_APPEAR_IN_A_TOOLTIP";
            var unavailableActions = new FakeActionService(forceUnavailable: true);
            using var form = CreateForm(
                CreateSnapshot(
                    activeExitNodeId: null,
                    connectionState: RemoteFleetConnectionState.Degraded,
                    statusMessage: diagnostic),
                remoteActionService: unavailableActions);
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            Assert.StartsWith("Private network connected", Find<Label>(form, "ConnectionStatusLabel").Text, StringComparison.Ordinal);
            var help = Find<Label>(form, "RemoteActionsHelpLabel").Text;
            Assert.Equal("Screen and file tools need RemoteHub and MeshCentral setup. Open More > Remote access sign in.", help);
            Assert.DoesNotContain(diagnostic, help, StringComparison.Ordinal);
            Assert.DoesNotContain(diagnostic, Find<Label>(form, "RemotesFooterLabel").Text, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void OfflineComputer_DisablesVpnAndRemoteActions_WithPlainLanguageReason()
    {
        RunSta(() =>
        {
            using var form = CreateForm(CreateOfflineSnapshot());
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var list = Find<ListView>(form, "DeviceList");
            Assert.Single(list.Items.Cast<ListViewItem>());
            Assert.Contains("Offline", list.Items[0].SubItems[2].Text, StringComparison.Ordinal);
            Assert.Contains("VPN unavailable", list.Items[0].SubItems[2].Text, StringComparison.Ordinal);

            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.Equal("VPN OFF", toggle.Text);
            Assert.False(toggle.Checked);
            Assert.False(toggle.Enabled);
            Assert.Contains("offline", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Find<Button>(form, "ViewScreenButton").Enabled);
            Assert.False(Find<Button>(form, "SendFileButton").Enabled);
            Assert.False(Find<Button>(form, "RequestFileButton").Enabled);
            Assert.Contains("offline", Find<Label>(form, "RemoteActionsHelpLabel").Text, StringComparison.OrdinalIgnoreCase);

            form.Close();
        });
    }

    [Fact]
    public void OfflineActiveVpn_RemainsClearlyOnAndKeepsToggleTextReadable()
    {
        RunSta(() =>
        {
            var offlineSnapshot = CreateOfflineSnapshot();
            using var form = CreateForm(offlineSnapshot with { ActiveExitNodeId = "node:offline" });
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            Assert.Equal("VPN NEEDS ATTENTION", Find<Label>(form, "InternetRouteStatusLabel").Text);
            var toggle = Find<CheckBox>(form, "VpnToggle");
            Assert.Equal("VPN ON", toggle.Text);
            Assert.True(toggle.Checked);
            Assert.True(toggle.Enabled);
            Assert.Equal(SystemColors.ControlText, toggle.ForeColor);
            Assert.Contains("offline", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Turn VPN off", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void LocalLabels_AreEditable_AndSaveAndResetCallbacksUseStableDeviceId()
    {
        RunSta(() =>
        {
            var saved = new List<(string DeviceId, RemoteDeviceDisplayOverride DisplayOverride)>();
            using var form = CreateForm(
                CreateSnapshot(activeExitNodeId: null),
                saveDeviceDisplayOverride: (deviceId, displayOverride) => saved.Add((deviceId, displayOverride)));
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var owner = Find<TextBox>(form, "OwnerLabelTextBox");
            var location = Find<TextBox>(form, "LocationTextBox");
            Assert.True(owner.Enabled);
            Assert.True(location.Enabled);
            Assert.False(owner.ReadOnly);
            Assert.False(location.ReadOnly);

            owner.Text = "Travel laptop";
            location.Text = "Home office";
            Application.DoEvents();
            var save = Find<Button>(form, "SaveLabelsButton");
            Assert.True(save.Enabled);
            save.PerformClick();
            Application.DoEvents();

            var first = Assert.Single(saved);
            Assert.Equal("node:austin", first.DeviceId);
            Assert.Equal("Travel laptop", first.DisplayOverride.OwnerOrUserLabel);
            Assert.Equal("Home office", first.DisplayOverride.Location);
            Assert.Contains("Labels saved", Find<Label>(form, "RemotesFooterLabel").Text, StringComparison.Ordinal);

            var reset = Find<Button>(form, "ResetLabelsButton");
            Assert.True(reset.Enabled);
            reset.PerformClick();
            Application.DoEvents();

            Assert.Equal(2, saved.Count);
            Assert.Equal("node:austin", saved[1].DeviceId);
            Assert.True(saved[1].DisplayOverride.IsEmpty);
            Assert.Contains("Local labels reset", Find<Label>(form, "RemotesFooterLabel").Text, StringComparison.Ordinal);

            form.Close();
        });
    }

    [Fact]
    public void UnsavedLocalLabels_SurviveSearchAndDeviceSelectionChanges()
    {
        RunSta(() =>
        {
            var saved = new List<(string DeviceId, RemoteDeviceDisplayOverride DisplayOverride)>();
            using var form = CreateForm(
                CreateSnapshot(activeExitNodeId: null),
                saveDeviceDisplayOverride: (deviceId, displayOverride) => saved.Add((deviceId, displayOverride)));
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var owner = Find<TextBox>(form, "OwnerLabelTextBox");
            var location = Find<TextBox>(form, "LocationTextBox");
            owner.Text = "Unsaved travel owner";
            location.Text = "Unsaved travel location";

            var search = Find<TextBox>(form, "DeviceSearchTextBox");
            search.Text = "no-computer-will-match-this";
            Application.DoEvents();
            Assert.False(owner.Enabled);
            search.Clear();
            Application.DoEvents();

            Assert.Equal("Unsaved travel owner", owner.Text);
            Assert.Equal("Unsaved travel location", location.Text);
            Assert.True(Find<Button>(form, "SaveLabelsButton").Enabled);

            var list = Find<ListView>(form, "DeviceList");
            list.Items[0].Selected = false;
            list.Items[1].Selected = true;
            Application.DoEvents();
            list.Items[1].Selected = false;
            list.Items[0].Selected = true;
            Application.DoEvents();

            Assert.Equal("Unsaved travel owner", owner.Text);
            Assert.Equal("Unsaved travel location", location.Text);
            Find<Button>(form, "SaveLabelsButton").PerformClick();
            Application.DoEvents();
            Assert.Single(saved);
            Assert.Equal("node:austin", saved[0].DeviceId);

            form.Close();
        });
    }

    [Fact]
    public void LocalLabelSaveFailure_KeepsEditsAndShowsRetryableInlineMessage()
    {
        RunSta(() =>
        {
            using var form = CreateForm(
                CreateSnapshot(activeExitNodeId: null),
                saveDeviceDisplayOverride: (_, _) => throw new IOException("Simulated settings failure."));
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var owner = Find<TextBox>(form, "OwnerLabelTextBox");
            owner.Text = "Keep this edit";
            var save = Find<Button>(form, "SaveLabelsButton");
            Assert.True(save.Enabled);

            save.PerformClick();
            Application.DoEvents();

            Assert.Equal("Keep this edit", owner.Text);
            Assert.True(save.Enabled);
            Assert.Contains("could not be saved", Find<Label>(form, "RemotesFooterLabel").Text, StringComparison.OrdinalIgnoreCase);

            form.Dispose();
        });
    }

    private static string GetDetailsText(Control form)
    {
        return Find<Label>(form, "SelectedDeviceDetailsLabel").Text;
    }

    private static RemotesDashboardForm CreateForm(
        RemoteFleetSnapshot snapshot,
        FakeFleetClient? fleetClient = null,
        FakeActionService? remoteActionService = null,
        FakeExitNodeController? exitNodeController = null,
        Action<string, RemoteDeviceDisplayOverride>? saveDeviceDisplayOverride = null)
    {
        var preferences = new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotehub.example.test",
            "https://remotehub.example.test/admin",
            "https://meshcentral.example.test",
            "Local-PC",
            "London");
        return new RemotesDashboardForm(
            fleetClient ?? new FakeFleetClient(snapshot),
            remoteActionService ?? new FakeActionService(),
            exitNodeController ?? new FakeExitNodeController(),
            new FakeAdminConsoleLauncher(),
            new FakeTokenProvider(),
            () => preferences,
            _ => { },
            openAddDevice: null,
            saveDeviceDisplayOverride: saveDeviceDisplayOverride);
    }

    private static RemoteFleetSnapshot CreateSnapshot(
        string? activeExitNodeId,
        RemoteFleetConnectionState connectionState = RemoteFleetConnectionState.Connected,
        string statusMessage = "VPN and remote metadata are current.")
    {
        return new RemoteFleetSnapshot(
            connectionState,
            "Private Headscale",
            statusMessage,
            new[]
            {
                new RemoteDevice(
                    "node:austin",
                    "Austin-PC",
                    "Alice",
                    "Austin office",
                    true,
                    true,
                    DateTimeOffset.UtcNow,
                    RemoteCapability.ExitNode | RemoteCapability.ScreenView | RemoteCapability.SendFile | RemoteCapability.RequestFile,
                    "100.64.0.10",
                    "node//austin"),
                new RemoteDevice(
                    "node:london",
                    "London-Laptop",
                    "Headscale user",
                    null,
                    false,
                    false,
                    DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                    RemoteCapability.ExitNode,
                    "100.64.0.20")
            },
            activeExitNodeId,
            DateTimeOffset.UtcNow);
    }

    private static RemoteFleetSnapshot CreateOfflineSnapshot()
    {
        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "Private Headscale",
            "The private network is connected.",
            new[]
            {
                new RemoteDevice(
                    "node:offline",
                    "Travel-Laptop",
                    "Alice",
                    "Home",
                    false,
                    true,
                    DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
                    RemoteCapability.ExitNode | RemoteCapability.ScreenView | RemoteCapability.SendFile | RemoteCapability.RequestFile,
                    "100.64.0.30",
                    "node//offline")
            },
            null,
            DateTimeOffset.UtcNow);
    }

    private static void ShowForLayout(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20_000, -20_000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        ForceLayout(form);
        Application.DoEvents();
    }

    private static void ForceLayout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls)
        {
            ForceLayout(child);
        }
    }

    private static void AssertFits(ButtonBase button)
    {
        var measured = TextRenderer.MeasureText(button.Text, button.Font);
        Assert.True(
            measured.Width <= button.ClientSize.Width - 8,
            $"{button.Name} text '{button.Text}' measured {measured.Width}px but its client width is {button.ClientSize.Width}px.");
        Assert.True(
            measured.Height <= button.ClientSize.Height - 6,
            $"{button.Name} text '{button.Text}' measured {measured.Height}px but its client height is {button.ClientSize.Height}px.");
    }

    private static void InvokeClick(Control control)
    {
        var onClick = control.GetType().GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(onClick);
        onClick.Invoke(control, new object[] { EventArgs.Empty });
    }

    private static void PumpMessagesUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "The expected asynchronous dashboard state was not reached within five seconds.");
        Application.DoEvents();
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        return Assert.IsAssignableFrom<T>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = ExceptionDispatchInfo.Capture(caught);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The WinForms test thread did not finish.");
        exception?.Throw();
    }

    private sealed class FakeFleetClient(RemoteFleetSnapshot snapshot) : IRemoteFleetClient
    {
        public RemoteFleetSnapshot Snapshot { get; set; } = snapshot;

        public int RefreshCalls { get; private set; }

        public RemoteFleetSnapshot GetCachedSnapshot() => Snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeActionService(bool forceUnavailable = false) : IRemoteActionService
    {
        public RemoteActionAvailability GetAvailability(RemoteDevice device, RemoteWebAction action)
        {
            if (forceUnavailable)
            {
                return new RemoteActionAvailability(false, "Remote access metadata is unavailable.");
            }

            var capability = action switch
            {
                RemoteWebAction.ViewScreen => RemoteCapability.ScreenView,
                RemoteWebAction.SendFile => RemoteCapability.SendFile,
                _ => RemoteCapability.RequestFile
            };
            return device.IsOnline && device.IsVerified && device.Capabilities.HasFlag(capability)
                ? RemoteActionAvailability.Available
                : new RemoteActionAvailability(false, "This self-hosted action is not available for the selected computer.");
        }

        public RemoteActionResult Open(RemoteDevice device, RemoteWebAction action) => RemoteActionResult.Success("Opened.");
    }

    private sealed class FakeExitNodeController : IRemoteExitNodeController
    {
        public TaskCompletionSource<RemoteActionResult>? UseCompletion { get; init; }

        public TaskCompletionSource<RemoteActionResult>? ClearCompletion { get; init; }

        public int UseCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public RemoteDevice? LastUsedDevice { get; private set; }

        public bool LastAllowLocalNetworkAccess { get; private set; }

        public RemoteActionAvailability GetAvailability(RemoteDevice device)
        {
            return device.IsOnline && device.IsVerified && device.Capabilities.HasFlag(RemoteCapability.ExitNode)
                ? RemoteActionAvailability.Available
                : new RemoteActionAvailability(false, "This computer is not an available internet exit.");
        }

        public RemoteActionAvailability GetClearAvailability() => RemoteActionAvailability.Available;

        public Task<RemoteActionResult> UseExitNodeAsync(RemoteDevice device, bool allowLocalNetworkAccess, CancellationToken cancellationToken)
        {
            UseCalls++;
            LastUsedDevice = device;
            LastAllowLocalNetworkAccess = allowLocalNetworkAccess;
            return UseCompletion?.Task ?? Task.FromResult(RemoteActionResult.Success("Enabled."));
        }

        public Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            return ClearCompletion?.Task ?? Task.FromResult(RemoteActionResult.Success("Disabled."));
        }
    }

    private sealed class FakeAdminConsoleLauncher : IRemoteAdminConsoleLauncher
    {
        public RemoteActionResult Open() => RemoteActionResult.Success("Opened.");
    }

    private sealed class FakeTokenProvider : IRemoteHubAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-token");

        public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
