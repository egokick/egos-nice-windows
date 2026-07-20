using System.Runtime.ExceptionServices;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

[CollectionDefinition("WinForms", DisableParallelization = true)]
public sealed class WinFormsCollectionDefinition;

[Collection("WinForms")]
public sealed class RemotesDashboardFormTests
{
    [Fact]
    public void ConstructorAndLayout_DefaultAndMinimumSizes_DoNotThrowOrClipPrimaryControls()
    {
        RunSta(() =>
        {
            using var form = CreateForm(CreateSnapshot());
            ShowForLayout(form);

            foreach (var size in new[] { new Size(1180, 720), form.MinimumSize })
            {
                form.Size = size;
                ForceLayout(form);
                Application.DoEvents();

                var split = Find<SplitContainer>(form, "RemotesSplitContainer");
                Assert.True(split.Panel1.Width >= split.Panel1MinSize);
                Assert.True(split.Panel2.Width >= split.Panel2MinSize);
                Assert.True(split.SplitterDistance >= split.Panel1MinSize);
                Assert.True(split.SplitterDistance <= split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth);

                var headerActions = Find<FlowLayoutPanel>(form, "HeaderActions");
                var buttons = headerActions.Controls.OfType<Button>().Where(button => button.Visible).ToArray();
                Assert.Equal(5, buttons.Length);
                foreach (var button in buttons)
                {
                    Assert.True(headerActions.ClientRectangle.Contains(button.Bounds));
                }
                for (var first = 0; first < buttons.Length; first++)
                {
                    for (var second = first + 1; second < buttons.Length; second++)
                    {
                        Assert.False(buttons[first].Bounds.IntersectsWith(buttons[second].Bounds));
                    }
                }

                Assert.True(Find<ListView>(form, "DeviceList").ClientSize.Width > 250);
                Assert.True(Find<Panel>(form, "DetailsPanel").ClientSize.Width > 300);
            }

            form.Close();
        });
    }

    [Fact]
    public void Snapshot_RendersUsefulIdentityRoutingAndAvailabilityInformation()
    {
        RunSta(() =>
        {
            using var form = CreateForm(CreateSnapshot());
            ShowForLayout(form);
            form.RefreshFromSnapshot();
            Application.DoEvents();

            var list = Find<ListView>(form, "DeviceList");
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(5, list.Columns.Count);
            Assert.Equal("Austin-PC", list.Items[0].Text);
            Assert.Contains("VPN address: 100.64.0.10", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Owner / user label: Alice", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Location: Austin office", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Last seen: Now", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Contains("Remote management: Linked", GetDetailsText(form), StringComparison.Ordinal);
            Assert.Equal("Internet route: Austin-PC", Find<Label>(form, "InternetRouteStatusLabel").Text);
            Assert.Equal("Stop using as internet exit", Find<Button>(form, "RouteButton").Text);
            Assert.Contains("configured to leave through Austin-PC", Find<Label>(form, "RouteHelpLabel").Text, StringComparison.Ordinal);

            list.Items[0].Selected = false;
            list.Items[1].Selected = true;
            list.Items[1].Focused = true;
            Application.DoEvents();

            Assert.False(Find<Button>(form, "RouteButton").Enabled);
            Assert.False(Find<Button>(form, "ViewScreenButton").Enabled);
            Assert.False(Find<Button>(form, "SendFileButton").Enabled);
            Assert.False(Find<Button>(form, "RequestFileButton").Enabled);
            Assert.Equal("Unavailable", list.Items[1].SubItems[4].Text);
            Assert.False(string.IsNullOrWhiteSpace(Find<Label>(form, "RouteHelpLabel").Text));
            Assert.False(string.IsNullOrWhiteSpace(Find<Label>(form, "RemoteActionsHelpLabel").Text));

            Find<TextBox>(form, "DeviceSearchTextBox").Text = "Austin";
            Application.DoEvents();
            Assert.Single(list.Items.Cast<ListViewItem>());

            form.Close();
        });
    }

    private static string GetDetailsText(Control form)
    {
        return Find<Label>(form, "SelectedDeviceDetailsLabel").Text;
    }

    private static RemotesDashboardForm CreateForm(RemoteFleetSnapshot snapshot)
    {
        var preferences = new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotehub.example.test",
            "https://remotehub.example.test/admin",
            "https://meshcentral.example.test",
            "Local-PC",
            "London");
        return new RemotesDashboardForm(
            new FakeFleetClient(snapshot),
            new FakeActionService(),
            new FakeExitNodeController(),
            new FakeAdminConsoleLauncher(),
            new FakeTokenProvider(),
            () => preferences,
            _ => { });
    }

    private static RemoteFleetSnapshot CreateSnapshot()
    {
        return new RemoteFleetSnapshot(
            RemoteFleetConnectionState.Connected,
            "Private Headscale",
            "VPN and remote metadata are current.",
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
            "node:austin",
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
        public RemoteFleetSnapshot GetCachedSnapshot() => snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeActionService : IRemoteActionService
    {
        public RemoteActionAvailability GetAvailability(RemoteDevice device, RemoteWebAction action)
        {
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
        public RemoteActionAvailability GetAvailability(RemoteDevice device)
        {
            return device.IsOnline && device.IsVerified && device.Capabilities.HasFlag(RemoteCapability.ExitNode)
                ? RemoteActionAvailability.Available
                : new RemoteActionAvailability(false, "This computer is not an available internet exit.");
        }

        public RemoteActionAvailability GetClearAvailability() => RemoteActionAvailability.Available;

        public Task<RemoteActionResult> UseExitNodeAsync(RemoteDevice device, bool allowLocalNetworkAccess, CancellationToken cancellationToken)
            => Task.FromResult(RemoteActionResult.Success("Enabled."));

        public Task<RemoteActionResult> ClearExitNodeAsync(CancellationToken cancellationToken)
            => Task.FromResult(RemoteActionResult.Success("Disabled."));
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
