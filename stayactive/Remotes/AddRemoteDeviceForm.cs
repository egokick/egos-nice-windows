using System.Drawing;

namespace StayActive.Remotes;

/// <summary>
/// Issues a single, short-lived join command through the owner-operated broker.
/// The form never invokes a shell, saves a command, or displays an auth key in
/// a status/audit message.  The user moves the command to a computer they own.
/// </summary>
internal sealed class AddRemoteDeviceForm : Form
{
    private readonly IRemoteEnrollmentClient _enrollmentClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly RadioButton _deviceChoice;
    private readonly RadioButton _exitNodeChoice;
    private readonly CheckBox _ownershipConfirmation;
    private readonly Button _issueButton;
    private readonly Button _copyButton;
    private readonly Button _statusButton;
    private readonly Button _revokeButton;
    private readonly TextBox _commandTextBox;
    private readonly Label _ticketStatusLabel;
    private readonly Label _copyWarningLabel;
    private readonly System.Windows.Forms.Timer _clipboardTimer;
    private readonly System.Windows.Forms.Timer _expiryTimer;

    private RemoteEnrollmentTicket? _ticket;
    private string? _joinCommand;
    private string? _lastCopiedCommand;
    private readonly CancellationTokenSource _closeCancellation = new();
    private bool _operationInProgress;

    public AddRemoteDeviceForm(
        IRemoteEnrollmentClient enrollmentClient,
        Func<DateTimeOffset>? utcNow = null)
    {
        _enrollmentClient = enrollmentClient ?? throw new ArgumentNullException(nameof(enrollmentClient));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        Text = "Add a device to Remotes";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(740, 560);
        MinimumSize = new Size(680, 520);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(18),
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Create a one-time command for a computer you own"
        };
        root.Controls.Add(heading, 0, 0);

        var explanation = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(0, 6, 0, 4),
            Text = "StayActive asks your self-hosted enrollment broker for one command that expires in 15 minutes and works once. " +
                   "Joining only creates an unverified peer; it does not grant screen, file, or internet-routing access."
        };
        root.Controls.Add(explanation, 0, 1);

        var choices = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            Text = "What will this computer do?"
        };
        var choicesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4
        };
        _deviceChoice = new RadioButton
        {
            AutoSize = true,
            Checked = true,
            Text = "Computer"
        };
        var deviceDescription = new Label
        {
            AutoSize = true,
            Padding = new Padding(22, 0, 0, 8),
            Text = "Join the private network. It remains unverified until you finish the owner verification and consent setup."
        };
        _exitNodeChoice = new RadioButton
        {
            AutoSize = true,
            Text = "Computer that will provide internet routing"
        };
        var exitDescription = new Label
        {
            AutoSize = true,
            Padding = new Padding(22, 0, 0, 0),
            Text = "Join with the exit-capable policy tag only. It will not advertise or activate an exit route until that computer and its owner explicitly approve it."
        };
        choicesLayout.Controls.Add(_deviceChoice, 0, 0);
        choicesLayout.Controls.Add(deviceDescription, 0, 1);
        choicesLayout.Controls.Add(_exitNodeChoice, 0, 2);
        choicesLayout.Controls.Add(exitDescription, 0, 3);
        choices.Controls.Add(choicesLayout);
        root.Controls.Add(choices, 0, 2);

        var confirmationPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 4)
        };
        _ownershipConfirmation = new CheckBox
        {
            AutoSize = true,
            Text = "I own this computer or have authority to enroll it in this private network."
        };
        _ownershipConfirmation.CheckedChanged += (_, _) => RefreshUi();
        _issueButton = new Button
        {
            AutoSize = true,
            Text = "Create one-time join command…"
        };
        _issueButton.Click += async (_, _) => await IssueAsync();
        confirmationPanel.Controls.Add(_ownershipConfirmation);
        confirmationPanel.Controls.Add(_issueButton);
        root.Controls.Add(confirmationPanel, 0, 3);

        var commandPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 4, 0, 0)
        };
        commandPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _ticketStatusLabel = new Label
        {
            AutoSize = true,
            Text = "No one-time command has been created."
        };
        var commandLabel = new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 3),
            Text = "One-time join command"
        };
        _commandTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            TabStop = false
        };
        _copyWarningLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(0, 6, 0, 0),
            Text = "Copy only when you are ready to paste it on your other computer. Clipboard managers may retain it. " +
                   "StayActive clears the clipboard after 60 seconds only if it still contains this exact command."
        };
        commandPanel.Controls.Add(_ticketStatusLabel, 0, 0);
        commandPanel.Controls.Add(commandLabel, 0, 1);
        commandPanel.Controls.Add(_commandTextBox, 0, 2);
        commandPanel.Controls.Add(_copyWarningLabel, 0, 3);
        root.Controls.Add(commandPanel, 0, 4);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 10, 0, 4)
        };
        _copyButton = new Button { AutoSize = true, Text = "Copy command" };
        _copyButton.Click += (_, _) => CopyCommand();
        _statusButton = new Button { AutoSize = true, Text = "Check ticket status" };
        _statusButton.Click += async (_, _) => await CheckStatusAsync();
        _revokeButton = new Button { AutoSize = true, Text = "Revoke unused ticket" };
        _revokeButton.Click += async (_, _) => await RevokeAsync();
        actions.Controls.AddRange(new Control[] { _copyButton, _statusButton, _revokeButton });
        root.Controls.Add(actions, 0, 5);

        var safetyNote = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(0, 4, 0, 0),
            Text = "Before using this command, install the Tailscale client and verify the self-hosted controller root certificate and hosts bootstrap on the target computer. Do not run it on a computer already joined to another tailnet. The command is not saved in StayActive; screen and file sessions still require device mapping, policy, and target-side consent."
        };
        root.Controls.Add(safetyNote, 0, 6);

        var closeButton = new Button
        {
            Anchor = AnchorStyles.Right,
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Close"
        };
        root.Controls.Add(closeButton, 0, 7);
        Controls.Add(root);
        CancelButton = closeButton;

        _clipboardTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _clipboardTimer.Tick += (_, _) => ClearCopiedCommandIfUnchanged();
        _expiryTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _expiryTimer.Tick += (_, _) => ExpireCommandIfNeeded();
        _expiryTimer.Start();
        FormClosing += (_, _) =>
        {
            _closeCancellation.Cancel();
            ClearSensitiveCommand(clearClipboard: true);
        };
        FormClosed += (_, _) =>
        {
            _clipboardTimer.Stop();
            _expiryTimer.Stop();
            _clipboardTimer.Dispose();
            _expiryTimer.Dispose();
            _closeCancellation.Dispose();
        };
        RefreshUi();
    }

    private async Task IssueAsync()
    {
        if (_operationInProgress || !_ownershipConfirmation.Checked)
        {
            return;
        }

        var kind = _exitNodeChoice.Checked
            ? RemoteEnrollmentKind.ExitNode
            : RemoteEnrollmentKind.Device;
        var roleDescription = kind == RemoteEnrollmentKind.ExitNode
            ? "an exit-capable computer"
            : "a computer";
        var confirmation = MessageBox.Show(
            this,
            $"Create a one-time 15-minute join command for {roleDescription}?\n\n" +
            "A fresh self-hosted administrator sign-in is required. The command will be shown once in this window and is not saved by StayActive.",
            "Create one-time join command",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        ClearSensitiveCommand(clearClipboard: true);
        _ticket = null;
        SetOperationInProgress(true);
        try
        {
            var issued = await _enrollmentClient.IssueAsync(kind, _closeCancellation.Token);
            if (IsDisposed || Disposing)
            {
                return;
            }

            _ticket = issued.Ticket;
            _joinCommand = issued.JoinCommand;
            _commandTextBox.Text = _joinCommand;
            _ticketStatusLabel.Text = "One-time command is ready. It expires " + FormatExpiry(_ticket.ExpiresAtUtc) + ".";
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            _ticketStatusLabel.Text = "Creating the command was cancelled.";
        }
        catch (RemoteEnrollmentException exception)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            _ticketStatusLabel.Text = "No command was created.";
            MessageBox.Show(this, exception.Message, "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            _ticketStatusLabel.Text = "No command was created.";
            MessageBox.Show(this, "The self-hosted enrollment broker could not create a command.", "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetOperationInProgress(false);
                RefreshUi();
            }
        }
    }

    private async Task CheckStatusAsync()
    {
        if (_operationInProgress || _ticket is null)
        {
            return;
        }

        SetOperationInProgress(true);
        try
        {
            _ticket = await _enrollmentClient.GetStatusAsync(_ticket.Id, _closeCancellation.Token);
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateTicketStatusAfterServerResponse();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteEnrollmentException exception)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            MessageBox.Show(this, exception.Message, "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            MessageBox.Show(this, "The self-hosted enrollment broker could not check this ticket.", "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetOperationInProgress(false);
                RefreshUi();
            }
        }
    }

    private async Task RevokeAsync()
    {
        if (_operationInProgress || _ticket is null || _ticket.State != RemoteEnrollmentTicketState.Issued)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "Revoke this unused one-time join command? It cannot be restored. The command will be removed from this window immediately.",
            "Revoke one-time command",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        // Stop exposing the key before network work. If revocation is interrupted,
        // the owner can safely issue a new command instead of reusing the old one.
        ClearSensitiveCommand(clearClipboard: true);
        SetOperationInProgress(true);
        try
        {
            _ticket = await _enrollmentClient.RevokeAsync(_ticket.Id, _closeCancellation.Token);
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateTicketStatusAfterServerResponse();
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            _ticketStatusLabel.Text = "The command is no longer shown. Ticket revocation was cancelled.";
        }
        catch (RemoteEnrollmentException exception)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            _ticketStatusLabel.Text = "The command is no longer shown. Verify the ticket status before issuing another command.";
            MessageBox.Show(this, exception.Message, "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            _ticketStatusLabel.Text = "The command is no longer shown. Verify the ticket status before issuing another command.";
            MessageBox.Show(this, "The self-hosted enrollment broker could not revoke this ticket.", "Add a device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetOperationInProgress(false);
                RefreshUi();
            }
        }
    }

    private void CopyCommand()
    {
        if (_operationInProgress || string.IsNullOrWhiteSpace(_joinCommand) || !HasUsableIssuedTicket())
        {
            return;
        }

        try
        {
            Clipboard.SetText(_joinCommand, TextDataFormat.UnicodeText);
            _lastCopiedCommand = _joinCommand;
            _clipboardTimer.Stop();
            _clipboardTimer.Start();
            _ticketStatusLabel.Text = "Command copied. StayActive clears it from the clipboard after 60 seconds only if unchanged.";
        }
        catch
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            MessageBox.Show(
                this,
                "Windows could not copy the one-time command. It remains visible only in this window.",
                "Add a device",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ExpireCommandIfNeeded()
    {
        if (_ticket is not { State: RemoteEnrollmentTicketState.Issued } || _ticket.ExpiresAtUtc > _utcNow())
        {
            return;
        }

        _ticket = new RemoteEnrollmentTicket(
            _ticket.Id,
            _ticket.Kind,
            _ticket.ExpiresAtUtc,
            RemoteEnrollmentTicketState.Expired,
            _ticket.LoginServer,
            _ticket.AdvertiseTags);
        ClearSensitiveCommand(clearClipboard: true);
        _ticketStatusLabel.Text = "The one-time command has expired and is no longer shown.";
        RefreshUi();
    }

    private void UpdateTicketStatusAfterServerResponse()
    {
        if (_ticket is null)
        {
            return;
        }

        if (_ticket.State != RemoteEnrollmentTicketState.Issued || _ticket.ExpiresAtUtc <= _utcNow())
        {
            ClearSensitiveCommand(clearClipboard: true);
        }

        _ticketStatusLabel.Text = _ticket.State switch
        {
            RemoteEnrollmentTicketState.Issued => "Ticket is still unused. It expires " + FormatExpiry(_ticket.ExpiresAtUtc) + ".",
            RemoteEnrollmentTicketState.Used => "Ticket was used. The one-time command is no longer shown.",
            RemoteEnrollmentTicketState.Expired => "Ticket expired. The one-time command is no longer shown.",
            RemoteEnrollmentTicketState.Revoked => "Ticket was revoked. The one-time command is no longer shown.",
            _ => "Ticket status is unavailable."
        };
    }

    private bool HasUsableIssuedTicket()
    {
        return _ticket is { State: RemoteEnrollmentTicketState.Issued }
            && _ticket.ExpiresAtUtc > _utcNow();
    }

    private void SetOperationInProgress(bool inProgress)
    {
        _operationInProgress = inProgress;
        UseWaitCursor = inProgress;
        RefreshUi();
    }

    private void RefreshUi()
    {
        var hasUsableTicket = HasUsableIssuedTicket();
        var canIssueAnother = _ticket is null || !hasUsableTicket;
        _issueButton.Enabled = !_operationInProgress && _ownershipConfirmation.Checked && canIssueAnother;
        _copyButton.Enabled = !_operationInProgress && hasUsableTicket && !string.IsNullOrWhiteSpace(_joinCommand);
        _statusButton.Enabled = !_operationInProgress && _ticket is not null;
        _revokeButton.Enabled = !_operationInProgress && hasUsableTicket;
        _deviceChoice.Enabled = !_operationInProgress && canIssueAnother;
        _exitNodeChoice.Enabled = !_operationInProgress && canIssueAnother;
        _ownershipConfirmation.Enabled = !_operationInProgress && canIssueAnother;
        _copyWarningLabel.Enabled = _copyButton.Enabled;
    }

    private void ClearSensitiveCommand(bool clearClipboard)
    {
        _joinCommand = null;
        _commandTextBox.Clear();
        if (clearClipboard)
        {
            ClearCopiedCommandIfUnchanged();
        }
    }

    private void ClearCopiedCommandIfUnchanged()
    {
        _clipboardTimer.Stop();
        var copied = _lastCopiedCommand;
        _lastCopiedCommand = null;
        if (string.IsNullOrEmpty(copied))
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText)
                && string.Equals(Clipboard.GetText(TextDataFormat.UnicodeText), copied, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            // Clipboard access is best effort. Never overwrite a changed
            // clipboard value and never show its contents in an error.
        }
    }

    private static string FormatExpiry(DateTimeOffset expiresAtUtc)
    {
        return expiresAtUtc.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    }
}