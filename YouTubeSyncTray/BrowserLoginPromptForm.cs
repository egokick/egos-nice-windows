using System.ComponentModel;

namespace YouTubeSyncTray;

internal sealed class BrowserLoginPromptForm : Form
{
    private readonly ComboBox _browserComboBox;

    public BrowserLoginPromptForm(BrowserLoginPromptRequest request)
    {
        Text = "Sign In To Continue";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, request.AvailableBrowsers.Count > 1 ? 300 : 260);
        MinimumSize = Size;
        Padding = new Padding(18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var headerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 12)
        };
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var iconBox = new PictureBox
        {
            Size = new Size(32, 32),
            Margin = new Padding(0, 4, 12, 0),
            Image = SystemIcons.Information.ToBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Text = "The app needs to open a browser window"
        };

        headerPanel.Controls.Add(iconBox, 0, 0);
        headerPanel.Controls.Add(titleLabel, 1, 0);

        var bodyLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 12),
            Text =
                "To refresh your YouTube sign-in, the app will open a managed browser window. Sign into YouTube there, then close that browser window so sync can continue."
        };

        var detailLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 12),
            ForeColor = Color.FromArgb(72, 72, 72),
            Text = "This gives the app access to your Watch Later account without using a browser extension."
        };

        var browserPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 12),
            Visible = request.AvailableBrowsers.Count > 1
        };
        browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        browserPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var browserLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 10, 0),
            Text = "Browser"
        };

        _browserComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Width = 220
        };
        _browserComboBox.Items.AddRange(request.AvailableBrowsers.Cast<object>().ToArray());
        _browserComboBox.SelectedItem = request.SelectedBrowser;
        _browserComboBox.Format += (_, e) =>
        {
            if (e.ListItem is BrowserCookieSource browser)
            {
                e.Value = ChromiumBrowserLocator.GetDisplayName(browser);
            }
        };

        var profileLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = $"Managed profile: {request.Profile}"
        };

        browserPanel.Controls.Add(browserLabel, 0, 0);
        browserPanel.Controls.Add(_browserComboBox, 1, 0);
        browserPanel.Controls.Add(profileLabel, 3, 0);

        var footerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 3,
            Margin = new Padding(0)
        };
        footerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var hintLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(90, 90, 90),
            MaximumSize = new Size(320, 0),
            Margin = new Padding(0, 8, 0, 0),
            Text = "After you finish signing in, close the opened browser window and the app will continue automatically."
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            DialogResult = DialogResult.Cancel
        };

        var continueButton = new Button
        {
            Text = "Continue",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            DialogResult = DialogResult.OK
        };

        footerPanel.Controls.Add(hintLabel, 0, 0);
        footerPanel.Controls.Add(cancelButton, 1, 0);
        footerPanel.Controls.Add(continueButton, 2, 0);

        root.Controls.Add(headerPanel, 0, 0);
        root.Controls.Add(bodyLabel, 0, 1);
        if (request.AvailableBrowsers.Count > 1)
        {
            root.Controls.Add(browserPanel, 0, 2);
        }
        else
        {
            var profileOnlyLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12),
                Text = $"Managed profile: {request.Profile}"
            };
            root.Controls.Add(profileOnlyLabel, 0, 2);
        }
        root.Controls.Add(detailLabel, 0, 3);

        Controls.Add(root);
        Controls.Add(footerPanel);

        AcceptButton = continueButton;
        CancelButton = cancelButton;

        if (_browserComboBox.Items.Count == 1)
        {
            _browserComboBox.SelectedIndex = 0;
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BrowserCookieSource SelectedBrowser =>
        _browserComboBox.SelectedItem is BrowserCookieSource browser
            ? browser
            : ChromiumBrowserLocator.GetPreferredBrowserOrFallback(BrowserCookieSource.Chrome);
}
