using System.ComponentModel;

namespace YouTubeSyncTray;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _downloadCountUpDown;
    private readonly ComboBox _browserComboBox;
    private readonly TextBox _profileTextBox;
    private readonly Label _summaryLabel;
    private readonly Label _authNoteLabel;
    private readonly Button _saveButton;
    private readonly Button _refreshTotalButton;

    public SettingsForm()
    {
        Text = "YouTube Sync Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Width = 520;
        Height = 300;

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Location = new Point(18, 18),
            Text = "Download scope"
        };

        var prefixLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 60),
            Text = "Download up to the most recent"
        };

        _downloadCountUpDown = new NumericUpDown
        {
            Location = new Point(185, 56),
            Width = 86,
            Minimum = 1,
            Maximum = 5000
        };

        var suffixLabel = new Label
        {
            AutoSize = true,
            Location = new Point(282, 60),
            Text = "videos."
        };

        var authLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Location = new Point(18, 102),
            Text = "Authentication"
        };

        var browserLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 138),
            Text = "Browser"
        };

        _browserComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(110, 134),
            Width = 140
        };
        var browserOptions = ChromiumBrowserLocator.GetInstalledBrowsers();
        if (browserOptions.Count == 0)
        {
            browserOptions = ChromiumBrowserLocator.GetManagedBrowsers();
        }
        _browserComboBox.Items.AddRange(browserOptions.Cast<object>().ToArray());
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
            Location = new Point(270, 138),
            Text = "Profile"
        };

        _profileTextBox = new TextBox
        {
            Location = new Point(320, 134),
            Width = 160
        };

        _summaryLabel = new Label
        {
            AutoSize = false,
            Location = new Point(20, 188),
            Size = new Size(460, 40),
            Text = "Watch Later total: loading..."
        };

        _authNoteLabel = new Label
        {
            AutoSize = false,
            Location = new Point(20, 162),
            Size = new Size(460, 24),
            Text = "If fresh cookies are needed, the app will ask before opening a managed Chrome or Edge window and will default to your Windows browser choice."
        };

        _refreshTotalButton = new Button
        {
            Text = "Refresh Total",
            Location = new Point(240, 228),
            Width = 110,
            Height = 32
        };

        _saveButton = new Button
        {
            Text = "Save",
            Location = new Point(370, 228),
            Width = 110,
            Height = 32,
            DialogResult = DialogResult.OK
        };

        Controls.AddRange([
            titleLabel,
            prefixLabel,
            _downloadCountUpDown,
            suffixLabel,
            authLabel,
            browserLabel,
            _browserComboBox,
            profileLabel,
            _profileTextBox,
            _authNoteLabel,
            _summaryLabel,
            _refreshTotalButton,
            _saveButton
        ]);

        AcceptButton = _saveButton;
    }

    public event EventHandler? RefreshTotalRequested;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int DownloadCount
    {
        get => (int)_downloadCountUpDown.Value;
        set => _downloadCountUpDown.Value = Math.Clamp(value, (int)_downloadCountUpDown.Minimum, (int)_downloadCountUpDown.Maximum);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BrowserCookieSource BrowserCookies
    {
        get => _browserComboBox.SelectedItem is BrowserCookieSource value
            ? value
            : ChromiumBrowserLocator.GetPreferredBrowserOrFallback(BrowserCookieSource.Chrome);
        set => _browserComboBox.SelectedItem = value;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BrowserProfile
    {
        get => string.IsNullOrWhiteSpace(_profileTextBox.Text) ? "Default" : _profileTextBox.Text.Trim();
        set => _profileTextBox.Text = string.IsNullOrWhiteSpace(value) ? "Default" : value;
    }

    public void SetPlaylistSummary(int downloadCount, int totalCount)
    {
        _summaryLabel.Text = $"Sync scope: up to the most recent {downloadCount} videos out of {totalCount} currently in Watch Later using {BrowserCookies}:{BrowserProfile}.";
    }

    public void SetBusy(bool isBusy, string message)
    {
        UseWaitCursor = isBusy;
        _downloadCountUpDown.Enabled = !isBusy;
        _browserComboBox.Enabled = !isBusy;
        _profileTextBox.Enabled = !isBusy;
        _refreshTotalButton.Enabled = !isBusy;
        _saveButton.Enabled = !isBusy;
        _summaryLabel.Text = message;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_browserComboBox.SelectedIndex < 0)
        {
            _browserComboBox.SelectedItem = ChromiumBrowserLocator.GetPreferredBrowserOrFallback(BrowserCookieSource.Chrome);
        }
        _refreshTotalButton.Click += (_, _) => RefreshTotalRequested?.Invoke(this, EventArgs.Empty);
    }
}
