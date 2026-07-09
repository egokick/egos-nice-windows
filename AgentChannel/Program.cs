using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgentChannel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (CommandLine.TryHandle(args, out var exitCode))
        {
            return exitCode;
        }

        using var mutex = new Mutex(true, "AgentChannel.Singleton", out var createdNew);
        if (!createdNew)
        {
            AppLauncher.OpenExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}

internal static class CommandLine
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        if (Has(args, "--post"))
        {
            var text = GetValue(args, "--text") ?? GetTrailingText(args, "--post");
            if (string.IsNullOrWhiteSpace(text))
            {
                exitCode = 2;
                return true;
            }

            var message = AgentMessage.Create(
                from: GetValue(args, "--from") ?? Environment.UserName,
                to: GetValue(args, "--to") ?? "all",
                channel: GetValue(args, "--channel") ?? "general",
                fromSessionId: GetValue(args, "--session-id"),
                toSessionId: GetValue(args, "--to-session"),
                text: text);
            MessageStore.Append(message);
            AppLauncher.OpenExistingInstance();
            return true;
        }

        if (Has(args, "--read"))
        {
            var count = GetIntValue(args, "--count", 20);
            var channel = GetValue(args, "--channel");
            var format = GetValue(args, "--format") ?? "text";
            var messages = MessageStore.LoadRecent(Math.Clamp(count, 1, 500), channel);

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(messages, MessageStore.PrettyJsonOptions));
                return true;
            }

            foreach (var message in messages)
            {
                Console.WriteLine(FormatMessage(message));
            }

            return true;
        }

        if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
        {
            PrintHelp();
            return true;
        }

        if (Has(args, "--open"))
        {
            return false;
        }

        return false;
    }

    private static bool Has(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string GetTrailingText(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == args.Length - 1)
        {
            return string.Empty;
        }

        return string.Join(" ", args.Skip(index + 1));
    }

    private static int GetIntValue(string[] args, string name, int fallback)
    {
        var value = GetValue(args, name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string FormatMessage(AgentMessage message)
    {
        var localTime = message.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var session = string.IsNullOrWhiteSpace(message.FromSessionId) ? string.Empty : $" ({message.FromSessionId})";
        var toSession = string.IsNullOrWhiteSpace(message.ToSessionId) ? string.Empty : $" ({message.ToSessionId})";
        return $"[{localTime}] #{message.Channel} {message.From}{session} -> {message.To}{toSession}: {message.Text}";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AgentChannel");
        Console.WriteLine();
        Console.WriteLine("Post a message:");
        Console.WriteLine("  AgentChannel.exe --post --from codex --session-id <id> --to all --text \"hello\"");
        Console.WriteLine();
        Console.WriteLine("Read messages:");
        Console.WriteLine("  AgentChannel.exe --read");
        Console.WriteLine("  AgentChannel.exe --read --count 50");
        Console.WriteLine("  AgentChannel.exe --read --channel general --format json");
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _activationTimer;
    private readonly System.Windows.Forms.Timer _deliveryTimer;
    private bool _deliveryRunning;
    private ChannelForm? _form;

    public TrayApplicationContext()
    {
        MessageStore.EnsureStoreExists();
        AppLauncher.RegisterCurrentProcess();

        _icon = TrayIconFactory.CreateMessageIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "Agent Channel",
            ContextMenuStrip = new ContextMenuStrip()
        };

        var openMenuItem = new ToolStripMenuItem("Open Agent Channel");
        openMenuItem.Click += (_, _) => ShowChannel();

        var postExampleMenuItem = new ToolStripMenuItem("Copy post command");
        postExampleMenuItem.Click += (_, _) => CopyPostCommand();

        var openFolderMenuItem = new ToolStripMenuItem("Open message folder");
        openFolderMenuItem.Click += (_, _) => OpenMessageFolder();

        var responderMenuItem = new ToolStripMenuItem("Responder settings");
        responderMenuItem.Click += (_, _) => ShowResponderSettings();

        var openRoutesMenuItem = new ToolStripMenuItem("Open routes file");
        openRoutesMenuItem.Click += (_, _) => OpenRoutesFile();

        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitThread();

        _notifyIcon.ContextMenuStrip.Items.Add(openMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(postExampleMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(openFolderMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(responderMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(openRoutesMenuItem);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exitMenuItem);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowChannel();
            }
        };

        _activationTimer = new System.Windows.Forms.Timer
        {
            Interval = 500
        };
        _activationTimer.Tick += (_, _) =>
        {
            if (AppLauncher.ConsumeOpenRequest())
            {
                ShowChannel();
            }
        };
        _activationTimer.Start();

        _deliveryTimer = new System.Windows.Forms.Timer
        {
            Interval = 1500
        };
        _deliveryTimer.Tick += (_, _) => DeliverPendingMessages();
        _deliveryTimer.Start();

        ShowChannel();
    }

    protected override void ExitThreadCore()
    {
        _activationTimer.Stop();
        _activationTimer.Dispose();
        _deliveryTimer.Stop();
        _deliveryTimer.Dispose();
        _form?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        AppLauncher.UnregisterCurrentProcess();
        base.ExitThreadCore();
    }

    private void ShowChannel()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new ChannelForm();
            _form.FormClosed += (_, _) => _form = null;
        }

        if (!_form.Visible)
        {
            _form.Show();
        }

        if (_form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = FormWindowState.Normal;
        }

        _form.RefreshMessages();
        _form.Activate();
        _form.BringToFront();
    }

    private void CopyPostCommand()
    {
        Clipboard.SetText(AppLauncher.BuildPostExampleCommand());
        _notifyIcon.ShowBalloonTip(1200, "Agent Channel", "Post command copied.", ToolTipIcon.Info);
    }

    private void OpenMessageFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = MessageStore.StoreDirectory,
            UseShellExecute = true
        });
    }

    private void OpenRoutesFile()
    {
        RouteStore.EnsureRoutesFileExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = RouteStore.RoutesPath,
            UseShellExecute = true
        });
    }

    private void DeliverPendingMessages()
    {
        if (_deliveryRunning)
        {
            return;
        }

        _deliveryRunning = true;
        try
        {
            var deliveredKeys = DeliveryStore.Load();
            var routes = RouteStore.Load();
            foreach (var message in MessageStore.LoadRecent(500))
            {
                foreach (var route in CodexSessionRouter.GetDeliveryTargets(message, routes, deliveredKeys))
                {
                    var deliveryKey = CodexSessionRouter.GetDeliveryKey(message, route.Agent);
                    if (CodexSessionRouter.TryDeliver(message, route, out var error))
                    {
                        deliveredKeys.Add(deliveryKey);
                        DeliveryStore.Save(deliveredKeys);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _notifyIcon.ShowBalloonTip(2500, "Agent Channel", error, ToolTipIcon.Warning);
                    }
                }
            }

            var responderLaunches = ResponderRunner.ProcessPending(MessageStore.LoadRecent(500));
            foreach (var launch in responderLaunches)
            {
                _notifyIcon.ShowBalloonTip(1800, "Agent Channel", launch, ToolTipIcon.Info);
            }
        }
        finally
        {
            _deliveryRunning = false;
        }
    }

    private void ShowResponderSettings()
    {
        using var form = new ResponderSettingsForm(ResponderStore.Load());
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        ResponderStore.Save(form.Settings);
        _notifyIcon.ShowBalloonTip(1200, "Agent Channel", "Responder settings saved.", ToolTipIcon.Info);
    }
}

internal sealed class ChannelForm : Form
{
    private readonly FlowLayoutPanel _messagePanel;
    private TextBox _fromTextBox = null!;
    private TextBox _toTextBox = null!;
    private TextBox _messageTextBox = null!;
    private Label _countLabel = null!;
    private Label _pathLabel = null!;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private string _lastFingerprint = string.Empty;

    public ChannelForm()
    {
        Text = "Agent Channel";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 720);
        MinimumSize = new Size(760, 560);
        BackColor = ChatTheme.WindowBackground;
        Font = new Font("Segoe UI", 9.5f);

        var root = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            BackColor = BackColor
        };

        var header = BuildHeader();
        header.Height = 108;
        _messagePanel = new FlowLayoutPanel
        {
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = ChatTheme.ThreadBackground,
            Padding = new Padding(18, 16, 18, 8),
            Margin = new Padding(0),
            BorderStyle = BorderStyle.FixedSingle
        };
        _messagePanel.SizeChanged += (_, _) => ResizeMessageCards();

        var composer = BuildComposer();
        composer.Height = 168;
        root.Controls.Add(header);
        root.Controls.Add(composer);
        root.Controls.Add(_messagePanel);
        root.Resize += (_, _) => LayoutRoot(root, header, _messagePanel, composer);
        LayoutRoot(root, header, _messagePanel, composer);
        Controls.Add(root);

        _watcher = new FileSystemWatcher(MessageStore.StoreDirectory, Path.GetFileName(MessageStore.MessagesPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _watcher.Changed += (_, _) => QueueRefresh();
        _watcher.Created += (_, _) => QueueRefresh();
        _watcher.EnableRaisingEvents = true;

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshMessages();
        };

        RefreshMessages();
    }

    public void RefreshMessages(bool force = false)
    {
        if (IsDisposed)
        {
            return;
        }

        var messages = MessageStore.LoadRecent(200);
        var fingerprint = string.Join('|', messages.Select(message => message.Id));
        if (!force && fingerprint == _lastFingerprint)
        {
            return;
        }

        _lastFingerprint = fingerprint;
        _messagePanel.SuspendLayout();
        _messagePanel.Controls.Clear();

        if (messages.Count == 0)
        {
            _messagePanel.Controls.Add(new EmptyStateControl());
        }
        else
        {
            foreach (var message in messages)
            {
                _messagePanel.Controls.Add(new MessageCard(message));
            }
        }

        _messagePanel.ResumeLayout();
        ResizeMessageCards();
        _countLabel.Text = messages.Count == 1 ? "1 message" : $"{messages.Count} messages";
        ScrollToBottom();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            BackColor = BackColor
        };

        var titlePanel = new TableLayoutPanel
        {
            RowCount = 3,
            BackColor = BackColor
        };
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var title = new Label
        {
            AutoSize = true,
            Text = "Agent Channel",
            Font = new Font("Segoe UI Semibold", 18f),
            ForeColor = ChatTheme.TextStrong
        };
        var subtitle = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "Local handoff for AI agents on this computer",
            ForeColor = ChatTheme.TextMuted
        };
        _pathLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = MessageStore.MessagesPath,
            Font = new Font("Segoe UI", 8.6f),
            ForeColor = ChatTheme.TextSubtle
        };
        titlePanel.Controls.Add(title, 0, 0);
        titlePanel.Controls.Add(subtitle, 0, 1);
        titlePanel.Controls.Add(_pathLabel, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Width = 380,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = BackColor,
            Padding = new Padding(0, 13, 0, 0)
        };
        var refreshButton = CreateButton("Refresh");
        refreshButton.Click += (_, _) => RefreshMessages();
        var clearButton = CreateButton("Clear");
        clearButton.Click += (_, _) => ClearChat();
        var responderButton = CreateButton("Responder");
        responderButton.Width = 104;
        responderButton.Click += (_, _) => OpenResponderSettings();
        var folderButton = CreateButton("Folder");
        folderButton.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = MessageStore.StoreDirectory, UseShellExecute = true });
        var copyButton = CreateButton("Copy CLI");
        copyButton.Click += (_, _) => Clipboard.SetText(AppLauncher.BuildPostExampleCommand());
        _countLabel = new Label
        {
            AutoSize = false,
            Width = 88,
            Height = 34,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = ChatTheme.TextMuted
        };
        actions.Controls.Add(refreshButton);
        actions.Controls.Add(clearButton);
        actions.Controls.Add(responderButton);
        actions.Controls.Add(folderButton);
        actions.Controls.Add(copyButton);
        actions.Controls.Add(_countLabel);

        header.Controls.Add(actions);
        header.Controls.Add(titlePanel);
        header.Resize += (_, _) =>
        {
            var actionWidth = Math.Min(520, Math.Max(0, header.ClientSize.Width - 260));
            actions.Bounds = new Rectangle(header.ClientSize.Width - actionWidth, 0, actionWidth, header.ClientSize.Height);
            titlePanel.Bounds = new Rectangle(0, 0, Math.Max(260, header.ClientSize.Width - actionWidth - 14), header.ClientSize.Height);
        };
        return header;
    }

    private void ClearChat()
    {
        var result = MessageBox.Show(
            this,
            "Clear all Agent Channel messages on this computer?",
            "Clear Agent Channel",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
        {
            return;
        }

        MessageStore.Clear();
        DeliveryStore.Clear();
        RefreshMessages(force: true);
    }

    private void OpenResponderSettings()
    {
        using var form = new ResponderSettingsForm(ResponderStore.Load());
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            ResponderStore.Save(form.Settings);
        }
    }

    private static void LayoutRoot(Control root, Control header, Control messagePanel, Control composer)
    {
        var width = Math.Max(0, root.ClientSize.Width - root.Padding.Horizontal);
        var height = Math.Max(0, root.ClientSize.Height - root.Padding.Vertical);
        var left = root.Padding.Left;
        var top = root.Padding.Top;
        var headerHeight = 108;
        var composerHeight = 168;
        var threadTop = top + headerHeight;
        var threadHeight = Math.Max(120, height - headerHeight - composerHeight);

        header.Bounds = new Rectangle(left, top, width, headerHeight);
        messagePanel.Bounds = new Rectangle(left, threadTop, width, threadHeight);
        composer.Bounds = new Rectangle(left, threadTop + threadHeight, width, composerHeight);
    }

    private Control BuildComposer()
    {
        var composer = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(0, 16, 0, 0),
            BackColor = BackColor
        };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        composer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        composer.Controls.Add(CreateFieldLabel("From"), 0, 0);
        composer.Controls.Add(CreateFieldLabel("To"), 1, 0);
        composer.Controls.Add(CreateFieldLabel("Message"), 2, 0);

        _fromTextBox = CreateSingleLineTextBox(Environment.UserName);
        _toTextBox = CreateSingleLineTextBox("all");
        _messageTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.White,
            ForeColor = ChatTheme.TextStrong,
            Margin = new Padding(0, 0, 10, 0)
        };
        _messageTextBox.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                PostFromComposer();
                e.SuppressKeyPress = true;
            }
        };

        var postButton = CreateButton("Post");
        postButton.Dock = DockStyle.Fill;
        postButton.Click += (_, _) => PostFromComposer();

        composer.Controls.Add(_fromTextBox, 0, 1);
        composer.Controls.Add(_toTextBox, 1, 1);
        composer.Controls.Add(_messageTextBox, 2, 1);
        composer.SetRowSpan(_messageTextBox, 2);
        composer.Controls.Add(postButton, 3, 1);
        composer.SetRowSpan(postButton, 2);

        return composer;
    }

    private void PostFromComposer()
    {
        var text = _messageTextBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        MessageStore.Append(AgentMessage.Create(_fromTextBox.Text, _toTextBox.Text, "general", fromSessionId: null, toSessionId: null, text));
        _messageTextBox.Clear();
        RefreshMessages();
    }

    private void QueueRefresh()
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke((Action)(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }));
    }

    private void ResizeMessageCards()
    {
        var width = Math.Max(320, _messagePanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 40);
        foreach (Control control in _messagePanel.Controls)
        {
            control.Width = width;
            if (control is MessageCard messageCard)
            {
                messageCard.ApplyWidth(width);
            }
        }
    }

    private void ScrollToBottom()
    {
        if (_messagePanel.Controls.Count == 0)
        {
            return;
        }

        _messagePanel.ScrollControlIntoView(_messagePanel.Controls[^1]);
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = ChatTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static TextBox CreateSingleLineTextBox(string text)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Text = text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.White,
            ForeColor = ChatTheme.TextStrong,
            Margin = new Padding(0, 0, 10, 0)
        };
    }

    private static Button CreateButton(string text)
    {
        return new Button
        {
            Text = text,
            Width = 92,
            Height = 34,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(6, 0, 0, 0)
        };
    }
}

internal sealed class ResponderSettingsForm : Form
{
    private readonly TextBox _agentTextBox;
    private readonly TextBox _channelTextBox;
    private readonly TextBox _promptTextBox;
    private readonly CheckBox _enabledCheckBox;
    private readonly RadioButton _runOnceRadioButton;
    private readonly RadioButton _continuousRadioButton;

    public ResponderSettingsForm(ResponderSettings settings)
    {
        Text = "Responder Settings";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 560);
        MinimumSize = new Size(640, 460);
        BackColor = ChatTheme.WindowBackground;
        Font = new Font("Segoe UI", 9.5f);

        _enabledCheckBox = new CheckBox
        {
            Text = "Enable responder",
            Checked = settings.Enabled,
            AutoSize = true
        };
        _runOnceRadioButton = new RadioButton
        {
            Text = "Run once",
            Checked = !settings.Continuous,
            AutoSize = true
        };
        _continuousRadioButton = new RadioButton
        {
            Text = "Continuously run",
            Checked = settings.Continuous,
            AutoSize = true
        };
        _agentTextBox = CreateTextBox(settings.Agent);
        _channelTextBox = CreateTextBox(settings.Channel);
        _promptTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = settings.Prompt,
            AcceptsReturn = true,
            AcceptsTab = true,
            Font = new Font("Consolas", 10f)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 7,
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        root.Controls.Add(_enabledCheckBox, 0, 0);
        root.SetColumnSpan(_enabledCheckBox, 2);
        var modePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = BackColor
        };
        modePanel.Controls.Add(_runOnceRadioButton);
        modePanel.Controls.Add(_continuousRadioButton);
        root.Controls.Add(modePanel, 0, 1);
        root.SetColumnSpan(modePanel, 2);
        root.Controls.Add(CreateLabel("Agent"), 0, 2);
        root.Controls.Add(_agentTextBox, 1, 2);
        root.Controls.Add(CreateLabel("Channel"), 0, 3);
        root.Controls.Add(_channelTextBox, 1, 3);
        root.Controls.Add(CreateLabel("Prompt"), 0, 4);
        root.Controls.Add(_promptTextBox, 0, 5);
        root.SetColumnSpan(_promptTextBox, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var saveButton = new Button { Text = "Save", Width = 88, Height = 32, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 6);
        root.SetColumnSpan(buttons, 2);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    public ResponderSettings Settings => new()
    {
        Enabled = _enabledCheckBox.Checked,
        Continuous = _continuousRadioButton.Checked,
        Agent = string.IsNullOrWhiteSpace(_agentTextBox.Text) ? "responder" : _agentTextBox.Text.Trim(),
        Channel = string.IsNullOrWhiteSpace(_channelTextBox.Text) ? "general" : _channelTextBox.Text.Trim(),
        Prompt = _promptTextBox.Text.Trim()
    };

    private static Label CreateLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ChatTheme.TextMuted
        };
    }

    private static TextBox CreateTextBox(string text)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Text = text
        };
    }
}

internal sealed class MessageCard : UserControl
{
    private readonly AgentMessage _message;
    private readonly Label _avatarLabel;
    private readonly Label _metadataLabel;
    private readonly Label _textLabel;
    private readonly Label _footerLabel;
    private readonly Panel _bubblePanel;
    private readonly Color _accentColor;

    public MessageCard(AgentMessage message)
    {
        _message = message;
        _accentColor = GetAccentColor(message.From);
        Height = 112;
        Margin = new Padding(0, 0, 0, 12);
        Padding = new Padding(0);
        BackColor = ChatTheme.ThreadBackground;
        DoubleBuffered = true;

        _avatarLabel = new Label
        {
            AutoSize = false,
            Text = GetInitials(message.From),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };

        _bubblePanel = new Panel
        {
            BackColor = Color.Transparent,
            Padding = new Padding(16, 12, 16, 12)
        };

        _metadataLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI Semibold", 9.4f),
            ForeColor = ChatTheme.TextStrong,
            BackColor = Color.Transparent,
            Text = $"{message.From} -> {message.To}"
        };
        _textLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 10.2f),
            ForeColor = ChatTheme.TextStrong,
            BackColor = Color.Transparent,
            Text = message.Text,
            UseMnemonic = false
        };
        _footerLabel = new Label
        {
            AutoSize = false,
            Height = 26,
            ForeColor = ChatTheme.TextSubtle,
            Font = new Font("Segoe UI", 8.7f),
            BackColor = Color.Transparent,
            Text = BuildFooterText(message)
        };

        _bubblePanel.Controls.Add(_textLabel);
        _bubblePanel.Controls.Add(_footerLabel);
        _bubblePanel.Controls.Add(_metadataLabel);
        Controls.Add(_bubblePanel);
        Controls.Add(_avatarLabel);
        ApplyWidth(Width);
        _bubblePanel.Paint += BubblePanelOnPaint;
    }

    public void ApplyWidth(int width)
    {
        Width = width;
        var avatarSize = 34;
        var gutter = 12;
        var bubbleWidth = Math.Max(260, width - avatarSize - gutter - 8);
        var textWidth = Math.Max(160, bubbleWidth - _bubblePanel.Padding.Horizontal);
        var textHeight = TextRenderer.MeasureText(
            _message.Text,
            _textLabel.Font,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;

        var bubbleHeight = Math.Max(106, _bubblePanel.Padding.Vertical + _metadataLabel.Height + textHeight + _footerLabel.Height + 20);
        Height = bubbleHeight + 2;
        _avatarLabel.Bounds = new Rectangle(0, 8, avatarSize, avatarSize);
        _bubblePanel.Bounds = new Rectangle(avatarSize + gutter, 0, bubbleWidth, bubbleHeight);
        _metadataLabel.Bounds = new Rectangle(
            _bubblePanel.Padding.Left,
            _bubblePanel.Padding.Top,
            textWidth,
            _metadataLabel.Height);
        _textLabel.Bounds = new Rectangle(
            _bubblePanel.Padding.Left,
            _metadataLabel.Bottom + 4,
            textWidth,
            textHeight + 8);
        _footerLabel.Bounds = new Rectangle(
            _bubblePanel.Padding.Left,
            _textLabel.Bottom + 6,
            textWidth,
            _footerLabel.Height);
        Invalidate();
        _bubblePanel.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var avatarBrush = new SolidBrush(_accentColor);
        e.Graphics.FillEllipse(avatarBrush, _avatarLabel.Bounds);
    }

    private void BubblePanelOnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, _bubblePanel.Width - 1, _bubblePanel.Height - 1);
        using var path = GraphicsExtensions.CreateRoundedRectanglePath(rect, 10);
        using var shadowBrush = new SolidBrush(Color.FromArgb(18, 15, 23, 42));
        using var bubbleBrush = new SolidBrush(Color.White);
        using var borderPen = new Pen(ChatTheme.Border);
        using var accentBrush = new SolidBrush(Color.FromArgb(24, _accentColor));
        var shadowRect = new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height - 1);
        using var shadowPath = GraphicsExtensions.CreateRoundedRectanglePath(shadowRect, 10);
        e.Graphics.FillPath(shadowBrush, shadowPath);
        e.Graphics.FillPath(bubbleBrush, path);
        e.Graphics.DrawPath(borderPen, path);
        e.Graphics.FillRectangle(accentBrush, 0, 0, 5, _bubblePanel.Height);
    }

    private static Color GetAccentColor(string value)
    {
        var hash = value.Aggregate(17, (current, character) => (current * 31) + character);
        var palette = new[]
        {
            Color.FromArgb(37, 99, 235),
            Color.FromArgb(5, 150, 105),
            Color.FromArgb(190, 24, 93),
            Color.FromArgb(202, 138, 4),
            Color.FromArgb(124, 58, 237)
        };
        return palette[Math.Abs(hash) % palette.Length];
    }

    private static string GetInitials(string value)
    {
        var parts = value.Split([' ', '-', '_', '.', '@'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "?";
        }

        var initials = string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
        return initials.Length == 0 ? "?" : initials;
    }

    private static string BuildFooterText(AgentMessage message)
    {
        var session = string.IsNullOrWhiteSpace(message.FromSessionId) ? string.Empty : $"  session {message.FromSessionId}";
        return $"#{message.Channel}  {message.TimestampUtc.ToLocalTime():g}  {message.Id}{session}";
    }
}

internal sealed class EmptyStateControl : UserControl
{
    public EmptyStateControl()
    {
        Height = 190;
        Margin = new Padding(0);
        BackColor = ChatTheme.ThreadBackground;

        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 11.5f),
            ForeColor = ChatTheme.TextMuted,
            Text = "No messages yet" + Environment.NewLine + "Post from this window or use the CLI command."
        };
        Controls.Add(label);
    }
}

internal static class ChatTheme
{
    public static readonly Color WindowBackground = Color.FromArgb(241, 244, 248);
    public static readonly Color ThreadBackground = Color.FromArgb(248, 250, 252);
    public static readonly Color TextStrong = Color.FromArgb(24, 31, 42);
    public static readonly Color TextMuted = Color.FromArgb(86, 98, 116);
    public static readonly Color TextSubtle = Color.FromArgb(120, 132, 150);
    public static readonly Color Border = Color.FromArgb(218, 225, 234);
}

internal sealed record AgentMessage(
    string Id,
    DateTime TimestampUtc,
    string From,
    string To,
    string Channel,
    string Text,
    string? FromSessionId = null,
    string? ToSessionId = null)
{
    public static AgentMessage Create(string? from, string? to, string? channel, string? fromSessionId, string? toSessionId, string text)
    {
        return new AgentMessage(
            Id: Guid.NewGuid().ToString("N")[..12],
            TimestampUtc: DateTime.UtcNow,
            From: Normalize(from, Environment.UserName),
            To: Normalize(to, "all"),
            Channel: Normalize(channel, "general"),
            Text: text.Trim(),
            FromSessionId: NormalizeOptional(fromSessionId),
            ToSessionId: NormalizeOptional(toSessionId));
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= 96 ? normalized : normalized[..96];
    }
}

internal static class MessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    public static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentChannel");

    public static readonly string MessagesPath = Path.Combine(StoreDirectory, "messages.jsonl");

    public static void EnsureStoreExists()
    {
        Directory.CreateDirectory(StoreDirectory);
        if (!File.Exists(MessagesPath))
        {
            File.WriteAllText(MessagesPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public static void Append(AgentMessage message)
    {
        EnsureStoreExists();
        using var mutex = new Mutex(false, "AgentChannel.MessageStore");
        mutex.WaitOne();
        try
        {
            File.AppendAllText(MessagesPath, JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static void Clear()
    {
        EnsureStoreExists();
        using var mutex = new Mutex(false, "AgentChannel.MessageStore");
        mutex.WaitOne();
        try
        {
            File.WriteAllText(MessagesPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static IReadOnlyList<AgentMessage> LoadRecent(int count, string? channel = null)
    {
        EnsureStoreExists();
        string[] lines;
        using var mutex = new Mutex(false, "AgentChannel.MessageStore");
        mutex.WaitOne();
        try
        {
            lines = File.ReadAllLines(MessagesPath, Encoding.UTF8);
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        var messages = new List<AgentMessage>();
        foreach (var line in lines.Reverse())
        {
            try
            {
                var message = JsonSerializer.Deserialize<AgentMessage>(line);
                if (message is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(channel)
                    && !string.Equals(message.Channel, channel.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                messages.Add(message);
                if (messages.Count >= count)
                {
                    break;
                }
            }
            catch
            {
                // Ignore a malformed line so one bad writer does not break the viewer.
            }
        }

        messages.Reverse();
        return messages;
    }
}

internal sealed class AgentRoute
{
    public string Agent { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public string WindowTitle { get; set; } = string.Empty;
}

internal static class RouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static readonly string RoutesPath = Path.Combine(MessageStore.StoreDirectory, "routes.json");

    public static void EnsureRoutesFileExists()
    {
        MessageStore.EnsureStoreExists();
        if (!File.Exists(RoutesPath))
        {
            Save([]);
        }
    }

    public static IReadOnlyList<AgentRoute> Load()
    {
        EnsureRoutesFileExists();
        try
        {
            return JsonSerializer.Deserialize<List<AgentRoute>>(File.ReadAllText(RoutesPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<AgentRoute> routes)
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(RoutesPath, JsonSerializer.Serialize(routes, JsonOptions), Encoding.UTF8);
    }
}

internal static class DeliveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string DeliveredPath = Path.Combine(MessageStore.StoreDirectory, "delivered.json");

    public static HashSet<string> Load()
    {
        MessageStore.EnsureStoreExists();
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DeliveredPath), JsonOptions) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(HashSet<string> deliveredIds)
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(DeliveredPath, JsonSerializer.Serialize(deliveredIds, JsonOptions), Encoding.UTF8);
    }

    public static void Clear()
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(DeliveredPath, JsonSerializer.Serialize(new HashSet<string>(), JsonOptions), Encoding.UTF8);
    }
}

internal sealed class ResponderSettings
{
    public bool Enabled { get; set; }

    public bool Continuous { get; set; }

    public string Agent { get; set; } = "responder";

    public string Channel { get; set; } = "general";

    public string Prompt { get; set; } = "Wait for the Agent Channel message below, then complete the requested work. Post your result back to Agent Channel when done.";
}

internal sealed class ResponderState
{
    public bool RunOnceCompleted { get; set; }

    public DateTime EnabledAfterUtc { get; set; }

    public HashSet<string> HandledMessageIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class ResponderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsPath = Path.Combine(MessageStore.StoreDirectory, "responder.json");

    private static readonly string StatePath = Path.Combine(MessageStore.StoreDirectory, "responder-state.json");

    public static ResponderSettings Load()
    {
        MessageStore.EnsureStoreExists();
        try
        {
            return JsonSerializer.Deserialize<ResponderSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new ResponderSettings();
        }
        catch
        {
            return new ResponderSettings();
        }
    }

    public static void Save(ResponderSettings settings)
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions), Encoding.UTF8);
        if (settings.Enabled)
        {
            var state = LoadState();
            state.RunOnceCompleted = false;
            state.EnabledAfterUtc = DateTime.UtcNow;
            SaveState(state);
        }
    }

    public static ResponderState LoadState()
    {
        MessageStore.EnsureStoreExists();
        try
        {
            return JsonSerializer.Deserialize<ResponderState>(File.ReadAllText(StatePath), JsonOptions) ?? new ResponderState();
        }
        catch
        {
            return new ResponderState();
        }
    }

    public static void SaveState(ResponderState state)
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
    }
}

internal static class ResponderRunner
{
    public static IReadOnlyList<string> ProcessPending(IReadOnlyList<AgentMessage> messages)
    {
        var settings = ResponderStore.Load();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Prompt))
        {
            return [];
        }

        var state = ResponderStore.LoadState();
        if (!settings.Continuous && state.RunOnceCompleted)
        {
            return [];
        }

        var launched = new List<string>();
        foreach (var message in messages)
        {
            if (!ShouldTrigger(settings, state, message))
            {
                continue;
            }

            var sessionId = $"acs-{Guid.NewGuid():N}"[..16];
            CodexSessionLauncher.Start(settings, message, sessionId);
            state.HandledMessageIds.Add(message.Id);
            state.RunOnceCompleted = true;
            launched.Add($"Started Codex responder {settings.Agent} for message from {message.From}.");

            if (!settings.Continuous)
            {
                settings.Enabled = false;
                ResponderStore.Save(settings);
                break;
            }
        }

        if (state.HandledMessageIds.Count > 1000)
        {
            state.HandledMessageIds = state.HandledMessageIds.TakeLast(1000).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        ResponderStore.SaveState(state);
        return launched;
    }

    private static bool ShouldTrigger(ResponderSettings settings, ResponderState state, AgentMessage message)
    {
        return !state.HandledMessageIds.Contains(message.Id)
            && message.TimestampUtc > state.EnabledAfterUtc
            && string.Equals(message.Channel, settings.Channel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(message.From, settings.Agent, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(message.To, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.To, settings.Agent, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class CodexSessionLauncher
{
    public static void Start(ResponderSettings settings, AgentMessage trigger, string sessionId)
    {
        var scriptPath = WriteLaunchScript(settings, trigger, sessionId);
        if (TryStartWindowsTerminal(scriptPath, settings.Agent, sessionId))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}",
            UseShellExecute = true
        });
    }

    private static string WriteLaunchScript(ResponderSettings settings, AgentMessage trigger, string sessionId)
    {
        var directory = Path.Combine(MessageStore.StoreDirectory, "launchers");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{sessionId}.ps1");
        var prompt = BuildPrompt(settings, trigger, sessionId);
        var script = $$"""
            $host.UI.RawUI.WindowTitle = 'AgentChannel {{settings.Agent}} {{sessionId}}'
            $prompt = @'
            {{prompt}}
            '@
            $codex = Join-Path $env:LOCALAPPDATA 'npm\codex.cmd'
            if (Test-Path $codex) {
                & $codex $prompt
            } else {
                codex $prompt
            }
            """;
        File.WriteAllText(path, script, Encoding.UTF8);
        return path;
    }

    private static string BuildPrompt(ResponderSettings settings, AgentMessage trigger, string sessionId)
    {
        var senderSession = string.IsNullOrWhiteSpace(trigger.FromSessionId) ? "unknown" : trigger.FromSessionId;
        var cliPath = FindCliPath();
        return $$"""
            You are {{settings.Agent}}, a Codex session launched by Agent Channel.

            Your Agent Channel session id is: {{sessionId}}

            Always include this session id when posting to Agent Channel:
            & "{{cliPath}}" post --from {{settings.Agent}} --session-id {{sessionId}} --to <agent> --text "message"

            Do not post a start/ready message. Only post when complete, blocked, requesting input, or giving feedback.

            Configured prompt:
            {{settings.Prompt}}

            Triggering Agent Channel message:
            From: {{trigger.From}}
            FromSessionId: {{senderSession}}
            To: {{trigger.To}}
            ToSessionId: {{trigger.ToSessionId ?? "none"}}
            Channel: {{trigger.Channel}}
            MessageId: {{trigger.Id}}

            {{trigger.Text}}
            """;
    }

    private static string FindCliPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AgentChannel.Cli.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Cli", "bin", "Release", "net10.0", "AgentChannel.Cli.exe")),
            Path.Combine(Directory.GetCurrentDirectory(), "AgentChannel", "Cli", "bin", "Release", "net10.0", "AgentChannel.Cli.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "Cli", "bin", "Release", "net10.0", "AgentChannel.Cli.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "AgentChannel.Cli.exe";
    }

    private static bool TryStartWindowsTerminal(string scriptPath, string agent, string sessionId)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GetWindowsTerminalPath(),
                Arguments = $"new-tab --title {QuoteCommandLineArgument($"AgentChannel {agent} {sessionId}")} powershell.exe -NoExit -ExecutionPolicy Bypass -File {QuoteCommandLineArgument(scriptPath)}",
                UseShellExecute = false
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowsTerminalPath()
    {
        var appAliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");

        return File.Exists(appAliasPath) ? appAliasPath : "wt.exe";
    }

    private static string QuoteCommandLineArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class CodexSessionRouter
{
    public static IEnumerable<AgentRoute> GetDeliveryTargets(AgentMessage message, IReadOnlyList<AgentRoute> routes, HashSet<string> deliveredKeys)
    {
        if (string.IsNullOrWhiteSpace(message.To))
        {
            yield break;
        }

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Agent)
                || string.IsNullOrWhiteSpace(route.WindowTitle)
                || string.Equals(route.Agent, message.From, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(message.FromSessionId) && string.Equals(route.SessionId, message.FromSessionId, StringComparison.OrdinalIgnoreCase))
                || deliveredKeys.Contains(message.Id)
                || deliveredKeys.Contains(GetDeliveryKey(message, route.Agent)))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToSessionId)
                && string.Equals(route.SessionId, message.ToSessionId, StringComparison.OrdinalIgnoreCase))
            {
                yield return route;
            }
            else if (string.Equals(message.To, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(route.Agent, message.To, StringComparison.OrdinalIgnoreCase))
            {
                yield return route;
            }
        }
    }

    public static string GetDeliveryKey(AgentMessage message, string recipient)
    {
        return $"{message.Id}:{recipient.Trim().ToLowerInvariant()}";
    }

    public static bool TryDeliver(AgentMessage message, AgentRoute route, out string? error)
    {
        error = null;
        if (route is null || string.IsNullOrWhiteSpace(route.WindowTitle))
        {
            return false;
        }

        if (!TryFindWindow(route.WindowTitle, out var hwnd))
        {
            error = $"No Codex window found for {route.Agent}. Expected title containing: {route.WindowTitle}";
            return false;
        }

        var payload = BuildPayload(message, route);
        var previousClipboardText = TryGetClipboardText(out var hadClipboardText);
        Clipboard.SetText(payload);
        SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        SendKeys.SendWait("^v");
        SendKeys.SendWait("{ENTER}");
        Thread.Sleep(100);

        if (hadClipboardText && previousClipboardText is not null)
        {
            Clipboard.SetText(previousClipboardText);
        }

        return true;
    }

    private static string BuildPayload(AgentMessage message, AgentRoute route)
    {
        var senderSession = string.IsNullOrWhiteSpace(message.FromSessionId) ? "unknown" : message.FromSessionId;
        var recipientSession = string.IsNullOrWhiteSpace(route.SessionId) ? "<your-session-id>" : route.SessionId;
        return $"""
            Agent Channel message from {message.From} (session {senderSession}) to {route.Agent} (session {recipientSession}) in #{message.Channel} at {message.TimestampUtc.ToLocalTime():g}:

            {message.Text}

            You can reply through Agent Channel with:
            & ".\AgentChannel\Cli\bin\Release\net10.0\AgentChannel.Cli.exe" post --from {route.Agent} --session-id {recipientSession} --to {message.From} --to-session {senderSession} --text "your reply"
            """;
    }

    private static bool TryFindWindow(string titleFragment, out IntPtr hwnd)
    {
        var found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            var title = GetWindowTitle(candidate);
            if (title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                found = candidate;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        hwnd = found;
        return hwnd != IntPtr.Zero;
    }

    private static string? TryGetClipboardText(out bool hadText)
    {
        try
        {
            hadText = Clipboard.ContainsText();
            return hadText ? Clipboard.GetText() : null;
        }
        catch
        {
            hadText = false;
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

internal static class AppLauncher
{
    private static readonly string OpenRequestPath = Path.Combine(MessageStore.StoreDirectory, "open.request");
    private static readonly string ProcessPathFile = Path.Combine(MessageStore.StoreDirectory, "process.path");

    public static void RegisterCurrentProcess()
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(ProcessPathFile, Application.ExecutablePath, Encoding.UTF8);
    }

    public static void UnregisterCurrentProcess()
    {
        try
        {
            File.Delete(ProcessPathFile);
        }
        catch
        {
        }
    }

    public static void OpenExistingInstance()
    {
        MessageStore.EnsureStoreExists();
        File.WriteAllText(OpenRequestPath, DateTime.UtcNow.Ticks.ToString(), Encoding.UTF8);
    }

    public static bool ConsumeOpenRequest()
    {
        try
        {
            if (!File.Exists(OpenRequestPath))
            {
                return false;
            }

            File.Delete(OpenRequestPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildPostExampleCommand()
    {
        var executablePath = File.Exists(ProcessPathFile)
            ? File.ReadAllText(ProcessPathFile, Encoding.UTF8).Trim()
            : Application.ExecutablePath;
        return $"& \"{executablePath}\" --post --from codex --to all --text \"hello from codex\"";
    }
}

internal static class TrayIconFactory
{
    public static Icon CreateMessageIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var bubbleBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var shadowBrush = new SolidBrush(Color.FromArgb(70, 15, 23, 42));
        using var dotBrush = new SolidBrush(Color.White);

        graphics.FillRoundedRectangle(shadowBrush, new RectangleF(5, 7, 23, 18), 7);
        graphics.FillRoundedRectangle(bubbleBrush, new RectangleF(4, 5, 23, 18), 7);
        graphics.FillPolygon(bubbleBrush, [new PointF(11, 21), new PointF(9, 28), new PointF(17, 22)]);
        graphics.FillEllipse(dotBrush, 10, 12, 3, 3);
        graphics.FillEllipse(dotBrush, 16, 12, 3, 3);
        graphics.FillEllipse(dotBrush, 22, 12, 3, 3);

        return CreateIcon(bitmap);
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal static class GraphicsExtensions
{
    public static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
