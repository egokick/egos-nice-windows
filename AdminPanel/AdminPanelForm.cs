using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AdminPanel;

internal sealed class AdminPanelForm : Form
{
    private const int PreferredWidth = 1320;
    private const int MinimumWidth = 900;
    private const int MinimumHeight = 520;
    private const int GridColumnCount = 3;

    private readonly FlowLayoutPanel _appGrid;
    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly Button _closeButton;
    private readonly ToolTip _toolTip;

    private AdminPalette _palette;
    private Icon? _windowIcon;
    private AdminAppCard? _draggedCard;
    private List<string>? _orderBeforeDrag;
    private bool _dropCommitted;
    private bool _statusIsError;

    public AdminPanelForm()
    {
        _palette = AdminPalette.ForCurrentSystemTheme();
        _toolTip = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 400,
            ReshowDelay = 150,
            ShowAlways = true
        };

        Text = "Admin Panel";
        AccessibleName = "Nice Windows Admin Panel";
        AccessibleDescription = "Launch Nice Windows apps and choose which apps start when you sign in.";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(MinimumWidth, MinimumHeight);
        Size = new Size(PreferredWidth, MinimumHeight);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(24, 0, 12, 0)
        };
        _titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Admin Panel",
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Admin Panel"
        };
        _closeButton = new Button
        {
            Dock = DockStyle.Right,
            Width = 44,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Point),
            Text = "×",
            AccessibleName = "Close Admin Panel",
            AccessibleDescription = "Close the Admin Panel"
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.Click += (_, _) => Close();
        _titleBar.Controls.Add(_titleLabel);
        _titleBar.Controls.Add(_closeButton);

        _appGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AllowDrop = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(22, 18, 22, 26),
            TabStop = false
        };
        _appGrid.DragEnter += AppGridOnDragEnter;
        _appGrid.DragOver += AppGridOnDragOver;
        _appGrid.DragDrop += AppGridOnDragDrop;
        _appGrid.ClientSizeChanged += (_, _) => ResizeCardsForGrid();

        foreach (var app in AdminPanelOrderStore.LoadOrderedApps())
        {
            var card = new AdminAppCard(app, _toolTip);
            card.LaunchRequested += (_, _) => LaunchAppAsync(card);
            card.AutoStartToggleRequested += (_, _) => ToggleAutoStart(card);
            card.DragRequested += (_, _) => StartCardDrag(card);
            card.MoveRequested += (_, args) => MoveCard(card, args.Offset);
            RegisterDropTarget(card);
            _appGrid.Controls.Add(card);
        }

        Controls.Add(_appGrid);
        Controls.Add(_titleBar);

        Shown += (_, _) =>
        {
            ResizeCardsForGrid();
            RefreshAllAppStates();
        };
        Activated += (_, _) =>
        {
            ApplyCurrentTheme();
            RefreshAllAppStates();
        };
        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape && _draggedCard is null)
            {
                Close();
            }
        };

        UpdateCardTabOrder();
        ApplyCurrentTheme();
        CenterAndFitToCurrentScreen();
    }

    public void CenterAndFitToCurrentScreen()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var availableWidth = Math.Max(320, workingArea.Width - 24);
        var availableHeight = Math.Max(320, workingArea.Height);
        var width = Math.Min(PreferredWidth, availableWidth);
        var height = Math.Min(GetHeightForAllRows(), availableHeight);

        MinimumSize = new Size(
            Math.Min(MinimumWidth, availableWidth),
            Math.Min(MinimumHeight, availableHeight));
        Bounds = new Rectangle(
            workingArea.Left + ((workingArea.Width - width) / 2),
            workingArea.Top + ((workingArea.Height - height) / 2),
            width,
            height);
    }

    private int GetHeightForAllRows()
    {
        var cards = GetCardsInVisualOrder();
        if (cards.Count == 0)
        {
            return MinimumHeight;
        }

        var rowCount = (cards.Count + GridColumnCount - 1) / GridColumnCount;
        var rowHeight = cards[0].Height + cards[0].Margin.Vertical;
        var gridHeight = _appGrid.Padding.Vertical + (rowCount * rowHeight);
        return Math.Max(MinimumHeight, _titleBar.Height + gridHeight);
    }

    private void ResizeCardsForGrid()
    {
        if (_appGrid.ClientSize.Width <= 0)
        {
            return;
        }

        var cards = GetCardsInVisualOrder();
        if (cards.Count == 0)
        {
            return;
        }

        var marginWidth = cards[0].Margin.Horizontal;
        var usableWidth = _appGrid.ClientSize.Width
            - _appGrid.Padding.Horizontal
            - (GridColumnCount * marginWidth);
        var cardWidth = Math.Clamp(usableWidth / GridColumnCount, 200, 400);

        foreach (var card in cards)
        {
            card.SetGridWidth(cardWidth);
        }
    }

    public void ApplyCurrentTheme()
    {
        _palette = AdminPalette.ForCurrentSystemTheme();

        SuspendLayout();
        BackColor = _palette.Window;
        ForeColor = _palette.Text;
        _titleBar.BackColor = _palette.Window;
        _titleLabel.BackColor = _palette.Window;
        _titleLabel.ForeColor = _palette.Text;
        _closeButton.BackColor = _palette.Window;
        _closeButton.ForeColor = _palette.SecondaryText;
        _closeButton.FlatAppearance.MouseOverBackColor = _palette.CardHover;
        _closeButton.FlatAppearance.MouseDownBackColor = _palette.AccentSoft;
        _appGrid.BackColor = _palette.Window;

        foreach (var card in GetCardsInVisualOrder())
        {
            card.ApplyPalette(_palette);
        }

        UpdateWindowIcon();
        ResumeLayout(performLayout: true);
        Invalidate(invalidateChildren: true);

        if (IsHandleCreated)
        {
            DwmWindowTheme.TryApply(Handle, _palette.IsDark);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmWindowTheme.TryApply(Handle, _palette.IsDark);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _windowIcon?.Dispose();
            _windowIcon = null;
        }

        base.Dispose(disposing);
    }

    private void UpdateWindowIcon()
    {
        var replacement = _palette.IsDark
            ? AdminPanelTrayIconFactory.CreateDarkIcon()
            : AdminPanelTrayIconFactory.CreateLightIcon();
        var previous = _windowIcon;
        _windowIcon = replacement;
        Icon = replacement;
        previous?.Dispose();
    }

    private void RefreshAllAppStates()
    {
        if (!NiceWindowsRepositoryLocator.TryGetRepositoryRoot(out var repositoryRoot, out var locatorError))
        {
            foreach (var card in GetCardsInVisualOrder())
            {
                card.SetLaunchAvailability(false, locatorError);
                card.SetAutoStartState(null, locatorError);
            }

            SetStatus("App folders unavailable", isError: true);
            return;
        }

        var missingCount = 0;
        foreach (var card in GetCardsInVisualOrder())
        {
            var launcher = Path.Combine(repositoryRoot, card.Definition.FolderName, "start.bat");
            var launcherAvailable = File.Exists(launcher);
            if (!launcherAvailable)
            {
                missingCount++;
            }

            card.SetLaunchAvailability(
                launcherAvailable,
                launcherAvailable ? string.Empty : $"Launcher not found: {launcher}");

            if (!launcherAvailable)
            {
                card.SetAutoStartState(null, $"Launcher not found: {launcher}");
                continue;
            }

            if (AdminAppAutoStartService.TryGetEnabled(
                    card.Definition,
                    out var enabled,
                    out var errorMessage))
            {
                card.SetAutoStartState(enabled, string.Empty);
            }
            else
            {
                card.SetAutoStartState(null, errorMessage);
            }
        }

        if (missingCount > 0)
        {
            SetStatus($"{missingCount} launcher{(missingCount == 1 ? " is" : "s are")} missing", isError: true);
        }
    }

    private async void LaunchAppAsync(AdminAppCard card)
    {
        if (card.IsLaunchBusy)
        {
            return;
        }

        card.SetLaunchBusy(true, "Preparing…");
        SetStatus($"Preparing {card.Definition.DisplayName} dependencies…");

        var launchResult = await AdminAppLauncher.PrepareAndLaunchAsync(card.Definition);
        if (IsDisposed || card.IsDisposed)
        {
            return;
        }

        if (launchResult.Success)
        {
            card.SetLaunchResult(success: true);
            SetStatus($"{card.Definition.DisplayName} launch requested");
            await Task.Delay(1400);
            if (!card.IsDisposed)
            {
                card.SetLaunchBusy(false);
            }

            return;
        }

        card.SetLaunchBusy(false);
        card.SetLaunchResult(success: false, launchResult.ErrorMessage);
        SetStatus(launchResult.ErrorMessage, isError: true);
        MessageBox.Show(
            this,
            launchResult.ErrorMessage,
            $"Could not prepare {card.Definition.DisplayName}",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ToggleAutoStart(AdminAppCard card)
    {
        if (card.AutoStartEnabled is not { } currentlyEnabled)
        {
            return;
        }

        var enable = !currentlyEnabled;
        card.SetAutoStartBusy(true, enable);
        if (AdminAppAutoStartService.TrySetEnabled(card.Definition, enable, out var errorMessage))
        {
            card.SetAutoStartBusy(false, enable);
            card.SetAutoStartState(enable, string.Empty);
            card.ShowAutoStartNotification(enable);
            SetStatus(enable
                ? $"{card.Definition.DisplayName} will start when you sign in"
                : $"{card.Definition.DisplayName} removed from Windows startup");
            return;
        }

        card.SetAutoStartBusy(false, currentlyEnabled);
        card.SetAutoStartState(currentlyEnabled, errorMessage);
        SetStatus(errorMessage, isError: true);
    }

    private void StartCardDrag(AdminAppCard card)
    {
        if (_draggedCard is not null)
        {
            return;
        }

        _draggedCard = card;
        _orderBeforeDrag = GetCardsInVisualOrder().Select(item => item.Definition.Id).ToList();
        _dropCommitted = false;
        card.SetDragging(true);

        try
        {
            _ = card.DoDragDrop(card.Definition.Id, DragDropEffects.Move);
        }
        finally
        {
            if (!_dropCommitted && _orderBeforeDrag is not null)
            {
                ApplyOrder(_orderBeforeDrag);
            }

            card.SetDragging(false);
            _draggedCard = null;
            _orderBeforeDrag = null;
            _dropCommitted = false;
        }
    }

    private void AppGridOnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDraggedCard(e.Data, out _) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void AppGridOnDragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetDraggedCard(e.Data, out var draggedCard))
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;
        var clientPoint = _appGrid.PointToClient(new Point(e.X, e.Y));
        AutoScrollDuringDrag(clientPoint);
        ReorderForDragPosition(draggedCard, clientPoint);
    }

    private void AppGridOnDragDrop(object? sender, DragEventArgs e)
    {
        if (!TryGetDraggedCard(e.Data, out _))
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;
        _dropCommitted = true;
        SaveCurrentOrder();
    }

    private void RegisterDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += AppGridOnDragEnter;
        control.DragOver += AppGridOnDragOver;
        control.DragDrop += AppGridOnDragDrop;

        foreach (Control child in control.Controls)
        {
            RegisterDropTarget(child);
        }
    }

    private bool TryGetDraggedCard(IDataObject? data, out AdminAppCard card)
    {
        card = null!;
        if (data?.GetDataPresent(typeof(string)) != true
            || data.GetData(typeof(string)) is not string appId)
        {
            return false;
        }

        card = GetCardsInVisualOrder().FirstOrDefault(item =>
            string.Equals(item.Definition.Id, appId, StringComparison.OrdinalIgnoreCase))!;
        return card is not null && ReferenceEquals(card, _draggedCard);
    }

    private void ReorderForDragPosition(AdminAppCard draggedCard, Point clientPoint)
    {
        var cardsWithoutDragged = GetCardsInVisualOrder()
            .Where(card => !ReferenceEquals(card, draggedCard))
            .ToList();
        var insertionIndex = cardsWithoutDragged.Count;

        for (var index = 0; index < cardsWithoutDragged.Count; index++)
        {
            var bounds = cardsWithoutDragged[index].Bounds;
            if (clientPoint.Y < bounds.Top
                || clientPoint.Y <= bounds.Bottom && clientPoint.X < bounds.Left + (bounds.Width / 2))
            {
                insertionIndex = index;
                break;
            }
        }

        cardsWithoutDragged.Insert(insertionIndex, draggedCard);
        ApplyCardOrder(cardsWithoutDragged);
    }

    private void AutoScrollDuringDrag(Point clientPoint)
    {
        const int edgeSize = 52;
        const int scrollStep = 28;

        var delta = clientPoint.Y switch
        {
            < edgeSize => -scrollStep,
            _ when clientPoint.Y > _appGrid.ClientSize.Height - edgeSize => scrollStep,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        var currentX = -_appGrid.AutoScrollPosition.X;
        var currentY = -_appGrid.AutoScrollPosition.Y;
        var maximumY = Math.Max(0, _appGrid.DisplayRectangle.Height - _appGrid.ClientSize.Height);
        var nextY = Math.Clamp(currentY + delta, 0, maximumY);
        if (nextY != currentY)
        {
            _appGrid.AutoScrollPosition = new Point(currentX, nextY);
        }
    }

    private void MoveCard(AdminAppCard card, int offset)
    {
        var orderedCards = GetCardsInVisualOrder().ToList();
        var currentIndex = orderedCards.IndexOf(card);
        var targetIndex = Math.Clamp(currentIndex + offset, 0, orderedCards.Count - 1);
        if (currentIndex < 0 || targetIndex == currentIndex)
        {
            return;
        }

        orderedCards.RemoveAt(currentIndex);
        orderedCards.Insert(targetIndex, card);
        ApplyCardOrder(orderedCards);
        SaveCurrentOrder();
        card.FocusDragHandle();
    }

    private void ApplyOrder(IEnumerable<string> appIds)
    {
        var byId = GetCardsInVisualOrder().ToDictionary(
            card => card.Definition.Id,
            StringComparer.OrdinalIgnoreCase);
        var orderedCards = new List<AdminAppCard>();
        foreach (var appId in AdminPanelOrderStore.NormalizeOrder(appIds))
        {
            if (byId.Remove(appId, out var card))
            {
                orderedCards.Add(card);
            }
        }

        orderedCards.AddRange(byId.Values);
        ApplyCardOrder(orderedCards);
    }

    private void ApplyCardOrder(IReadOnlyList<AdminAppCard> orderedCards)
    {
        _appGrid.SuspendLayout();
        for (var index = 0; index < orderedCards.Count; index++)
        {
            _appGrid.Controls.SetChildIndex(orderedCards[index], index);
        }

        _appGrid.ResumeLayout(performLayout: true);
        UpdateCardTabOrder();
    }

    private void SaveCurrentOrder()
    {
        var names = GetCardsInVisualOrder().Select(card => card.Definition.Id);
        if (AdminPanelOrderStore.TrySaveOrder(names, out var errorMessage))
        {
            SetStatus("App order saved");
            return;
        }

        SetStatus(errorMessage, isError: true);
    }

    private IReadOnlyList<AdminAppCard> GetCardsInVisualOrder()
    {
        return _appGrid.Controls
            .OfType<AdminAppCard>()
            .OrderBy(card => _appGrid.Controls.GetChildIndex(card))
            .ToArray();
    }

    private void UpdateCardTabOrder()
    {
        var orderedCards = GetCardsInVisualOrder();
        for (var index = 0; index < orderedCards.Count; index++)
        {
            orderedCards[index].TabIndex = index;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        _statusIsError = isError;
        Text = string.IsNullOrWhiteSpace(message) || string.Equals(message, "Ready", StringComparison.Ordinal)
            ? "Admin Panel"
            : $"Admin Panel — {message}";
    }
}

internal sealed class AdminAppCard : UserControl
{
    private readonly PictureBox _logo;
    private readonly Button _dragGrip;
    private readonly Label _title;
    private readonly CheckBox _startupToggle;
    private readonly ToolTip _toolTip;

    private AdminPalette _palette = AdminPalette.ForCurrentSystemTheme();
    private Point? _dragOriginScreen;
    private bool _dragGestureRaised;
    private bool _launchAvailable = true;
    private bool _launchBusy;
    private bool _isDragging;
    private bool _isHovered;
    private string? _launchResultText;

    public AdminAppCard(AdminAppDefinition definition, ToolTip toolTip)
    {
        Definition = definition;
        _toolTip = toolTip;

        Size = new Size(352, 350);
        MinimumSize = new Size(200, 350);
        Margin = new Padding(12);
        Padding = Padding.Empty;
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = definition.DisplayName;
        AccessibleDescription = definition.Description;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        _logo = new PictureBox
        {
            Size = new Size(200, 200),
            Image = AdminAppLogoFactory.Create(definition, 256),
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false,
            AccessibleName = $"{definition.DisplayName} logo"
        };
        _logo.Paint += (_, args) =>
        {
            if (_launchBusy || _launchResultText is not null)
            {
                DrawLaunchStatus(args.Graphics, _logo.ClientRectangle, _launchResultText ?? "Launching…");
            }
            else if (_isHovered && _launchAvailable)
            {
                DrawPlayAffordance(args.Graphics, _logo.ClientRectangle);
            }
        };

        _dragGrip = new Button
        {
            Size = new Size(34, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Symbol", 11f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "⋮⋮",
            TabStop = true,
            Cursor = Cursors.SizeAll,
            AccessibleName = $"Move {definition.DisplayName}",
            AccessibleDescription = "Drag to reorder. You can also hold Control and press an arrow key."
        };
        _dragGrip.FlatAppearance.BorderSize = 0;
        _dragGrip.PreviewKeyDown += (_, args) =>
        {
            if (args.Modifiers == Keys.Control
                && args.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            {
                args.IsInputKey = true;
            }
        };
        _dragGrip.KeyDown += DragGripOnKeyDown;

        _title = new Label
        {
            AutoSize = false,
            Height = 38,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point),
            Text = definition.DisplayName,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false
        };

        _startupToggle = new CheckBox
        {
            Appearance = Appearance.Normal,
            AutoCheck = false,
            AutoSize = false,
            Size = new Size(22, 22),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Text = string.Empty,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.CheckButton,
            AccessibleName = $"Start {definition.DisplayName} with Windows"
        };
        _startupToggle.FlatAppearance.BorderSize = 1;
        _startupToggle.Click += (_, _) => AutoStartToggleRequested?.Invoke(this, EventArgs.Empty);

        Controls.Add(_logo);
        Controls.Add(_dragGrip);
        Controls.Add(_title);
        Controls.Add(_startupToggle);

        _toolTip.SetToolTip(_dragGrip, "Drag to reorder, or press Ctrl + an arrow key");
        _toolTip.SetToolTip(_startupToggle, $"Toggle Windows startup for {definition.DisplayName}");

        AttachDragSurface(this, launchesApp: true);
        AttachDragSurface(_logo, launchesApp: true);
        AttachDragSurface(_title, launchesApp: true);
        AttachDragSurface(_dragGrip);
        AttachHoverSurface(this);
        AttachHoverSurface(_logo);
        AttachHoverSurface(_title);
        AttachHoverSurface(_dragGrip);
        AttachHoverSurface(_startupToggle);

        LayoutCardContent();
        ApplyRoundedRegion();
        ApplyPalette(_palette);
    }

    public AdminAppDefinition Definition { get; }

    public bool? AutoStartEnabled { get; private set; }

    public bool IsLaunchBusy => _launchBusy;

    public event EventHandler? LaunchRequested;

    public event EventHandler? AutoStartToggleRequested;

    public event EventHandler? DragRequested;

    public event EventHandler<MoveCardRequestedEventArgs>? MoveRequested;

    public void ApplyPalette(AdminPalette palette)
    {
        _palette = palette;
        BackColor = palette.Card;
        ForeColor = palette.Text;
        _logo.BackColor = palette.Card;
        _title.BackColor = palette.Card;
        _title.ForeColor = palette.Text;
        _dragGrip.BackColor = palette.Card;
        _dragGrip.ForeColor = palette.SecondaryText;
        _dragGrip.FlatAppearance.MouseOverBackColor = palette.CardHover;
        _dragGrip.FlatAppearance.MouseDownBackColor = palette.AccentSoft;
        ApplyStartupTogglePalette();
        _logo.Invalidate();
        Invalidate();
    }

    public void SetLaunchAvailability(bool available, string reason)
    {
        _launchAvailable = available;
        _toolTip.SetToolTip(
            this,
            available ? $"Launch {Definition.DisplayName}" : reason);
        UpdateCursor();
        _logo.Invalidate();
        Invalidate();
    }

    public void SetLaunchBusy(bool busy, string? busyText = null)
    {
        _launchBusy = busy;
        if (busy)
        {
            _launchResultText = busyText ?? "Launching…";
        }
        else
        {
            _launchResultText = null;
        }

        UpdateCursor();
        _logo.Invalidate();
        Invalidate();
    }

    public void SetLaunchResult(bool success, string? detail = null)
    {
        _launchResultText = success ? "Requested" : "Could not start";
        _toolTip.SetToolTip(
            this,
            string.IsNullOrWhiteSpace(detail)
                ? success
                    ? $"{Definition.DisplayName} launch requested"
                    : $"Could not start {Definition.DisplayName}"
                : detail);
        _logo.Invalidate();
        Invalidate();
    }

    public void SetAutoStartState(bool? enabled, string errorMessage)
    {
        AutoStartEnabled = enabled;
        _startupToggle.Enabled = enabled.HasValue;
        _startupToggle.Checked = enabled == true;
        _startupToggle.AccessibleDescription = enabled switch
        {
            true => "Enabled. Activate to disable Windows startup.",
            false => "Disabled. Activate to enable Windows startup.",
            null => errorMessage
        };
        _toolTip.SetToolTip(
            _startupToggle,
            string.IsNullOrWhiteSpace(errorMessage)
                ? enabled == true
                    ? $"{Definition.DisplayName} starts with Windows. Click to disable."
                    : $"Start {Definition.DisplayName} with Windows"
                : errorMessage);
        ApplyStartupTogglePalette();
    }

    public void SetAutoStartBusy(bool busy, bool targetState)
    {
        _startupToggle.Enabled = !busy;
        _startupToggle.Checked = targetState;
    }

    public void ShowAutoStartNotification(bool enabled)
    {
        var message = enabled
            ? $"{Definition.DisplayName} will start with Windows."
            : $"{Definition.DisplayName} has been disabled from auto startup.";
        _toolTip.Show(message, _startupToggle, _startupToggle.Width / 2, _startupToggle.Height + 8, 2400);
    }

    public void SetDragging(bool dragging)
    {
        _isDragging = dragging;
        UpdateCursor();
        Invalidate();
    }

    public void FocusDragHandle()
    {
        _dragGrip.Focus();
    }

    public void SetGridWidth(int width)
    {
        Width = Math.Max(MinimumSize.Width, width);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCardContent();
        ApplyRoundedRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var highlight = _isDragging || (_isHovered && _launchAvailable && !_launchBusy);
        using var border = new Pen(highlight ? _palette.Accent : _palette.CardBorder, highlight ? 2.4f : 1.2f);
        using var path = AdminDrawing.CreateRoundedRectangle(
            new RectangleF(1, 1, Width - 2.5f, Height - 2.5f),
            16f);
        e.Graphics.DrawPath(border, path);

    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_logo is not null)
            {
                _logo.Image?.Dispose();
                _logo.Image = null;
            }
        }

        base.Dispose(disposing);
    }

    private void AttachDragSurface(Control control, bool launchesApp = false)
    {
        control.MouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _dragOriginScreen = control.PointToScreen(args.Location);
                _dragGestureRaised = false;
            }
        };
        control.MouseMove += (_, args) =>
        {
            if (args.Button != MouseButtons.Left
                || _dragOriginScreen is not { } origin
                || _dragGestureRaised)
            {
                return;
            }

            var current = control.PointToScreen(args.Location);
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(current.X - origin.X) < dragSize.Width / 2
                && Math.Abs(current.Y - origin.Y) < dragSize.Height / 2)
            {
                return;
            }

            _dragGestureRaised = true;
            DragRequested?.Invoke(this, EventArgs.Empty);
        };
        control.MouseUp += (_, args) =>
        {
            var shouldLaunch = launchesApp
                && args.Button == MouseButtons.Left
                && _dragOriginScreen is not null
                && !_dragGestureRaised;
            _dragOriginScreen = null;
            _dragGestureRaised = false;
            if (shouldLaunch && _launchAvailable && !_launchBusy)
            {
                LaunchRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private void AttachHoverSurface(Control control)
    {
        control.MouseEnter += (_, _) => SetHovered(true);
        control.MouseLeave += (_, _) =>
        {
            var pointerInCard = ClientRectangle.Contains(PointToClient(Cursor.Position));
            SetHovered(pointerInCard);
        };
    }

    private void SetHovered(bool hovered)
    {
        if (_isHovered == hovered)
        {
            return;
        }

        _isHovered = hovered;
        UpdateCursor();
        _logo.Invalidate();
        Invalidate();
    }

    private void UpdateCursor()
    {
        Cursor = _isDragging
            ? Cursors.SizeAll
            : _isHovered && _launchAvailable && !_launchBusy
                ? Cursors.Hand
                : Cursors.Default;
    }

    private void DrawPlayAffordance(Graphics graphics, Rectangle clientBounds)
    {
        var diameter = Math.Min(76f, Math.Min(clientBounds.Width, clientBounds.Height) - 24f);
        var bounds = new RectangleF(
            clientBounds.Left + ((clientBounds.Width - diameter) / 2f),
            clientBounds.Top + ((clientBounds.Height - diameter) / 2f),
            diameter,
            diameter);
        using var background = new SolidBrush(Color.FromArgb(150, _palette.Card));
        using var border = new Pen(Color.FromArgb(220, _palette.Accent), 2.4f);
        using var play = new SolidBrush(_palette.Accent);
        graphics.FillEllipse(background, bounds);
        graphics.DrawEllipse(border, bounds);
        var triangle = new[]
        {
            new PointF(bounds.Left + 30, bounds.Top + 22),
            new PointF(bounds.Left + 30, bounds.Bottom - 22),
            new PointF(bounds.Right - 21, bounds.Top + (bounds.Height / 2f))
        };
        graphics.FillPolygon(play, triangle);
    }

    private void DrawLaunchStatus(Graphics graphics, Rectangle clientBounds, string message)
    {
        var bounds = new RectangleF(18, (clientBounds.Height / 2f) - 27, clientBounds.Width - 36, 54);
        using var background = new SolidBrush(Color.FromArgb(225, _palette.Card));
        using var border = new Pen(_palette.Accent, 1.5f);
        using var textBrush = new SolidBrush(_palette.Text);
        using var path = AdminDrawing.CreateRoundedRectangle(bounds, 12f);
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(message, font, textBrush, bounds, format);
    }

    private void DragGripOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control)
        {
            return;
        }

        var offset = e.KeyCode switch
        {
            Keys.Left or Keys.Up => -1,
            Keys.Right or Keys.Down => 1,
            _ => 0
        };
        if (offset == 0)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        MoveRequested?.Invoke(this, new MoveCardRequestedEventArgs(offset));
    }

    private void ApplyRoundedRegion()
    {
        AdminDrawing.SetRoundedRegion(this, 17);
    }

    private void LayoutCardContent()
    {
        // Setting the card Size in the constructor can raise OnResize before
        // the child controls below have been assigned.
        if (_logo is null
            || _dragGrip is null
            || _title is null
            || _startupToggle is null)
        {
            return;
        }

        _startupToggle.Location = new Point(12, 17);
        _dragGrip.Location = new Point(ClientSize.Width - _dragGrip.Width - 11, 10);
        _title.Bounds = new Rectangle(42, 10, Math.Max(100, ClientSize.Width - 86), _title.Height);
        var logoSize = Math.Max(
            140,
            Math.Min(250, Math.Min(ClientSize.Width - 36, ClientSize.Height - _title.Bottom - 32)));
        _logo.Size = new Size(logoSize, logoSize);
        _logo.Location = new Point(
            (ClientSize.Width - _logo.Width) / 2,
            _title.Bottom + ((ClientSize.Height - 16 - _title.Bottom - _logo.Height) / 2));
    }

    private void ApplyStartupTogglePalette()
    {
        var enabled = AutoStartEnabled == true;
        var backColor = enabled ? _palette.SuccessSoft : _palette.MutedButton;
        var hoverColor = enabled ? _palette.SuccessHover : _palette.MutedButtonHover;
        var foreColor = enabled ? _palette.Success : _palette.SecondaryText;

        _startupToggle.BackColor = backColor;
        _startupToggle.ForeColor = foreColor;
        _startupToggle.FlatAppearance.BorderColor = enabled ? _palette.Success : _palette.CardBorder;
        _startupToggle.FlatAppearance.CheckedBackColor = backColor;
        _startupToggle.FlatAppearance.MouseOverBackColor = hoverColor;
        _startupToggle.FlatAppearance.MouseDownBackColor = hoverColor;
    }
}

internal sealed class MoveCardRequestedEventArgs(int offset) : EventArgs
{
    public int Offset { get; } = offset;
}

internal sealed class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }
}

internal sealed class PillLabel : Label
{
    public void ApplyPalette(Color background, Color foreground)
    {
        BackColor = background;
        ForeColor = foreground;
        AdminDrawing.SetRoundedRegion(this, Height / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        AdminDrawing.SetRoundedRegion(this, Height / 2);
    }
}

internal sealed class SuiteMarkControl : Control
{
    private AdminPalette _palette = AdminPalette.ForCurrentSystemTheme();

    public SuiteMarkControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        TabStop = false;
    }

    public void ApplyPalette(AdminPalette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var colors = new[]
        {
            _palette.Accent,
            _palette.Success,
            Color.FromArgb(167, 139, 250),
            Color.FromArgb(251, 146, 60)
        };
        var rectangles = new[]
        {
            new RectangleF(5, 5, 25, 25),
            new RectangleF(34, 5, 25, 25),
            new RectangleF(5, 34, 25, 25),
            new RectangleF(34, 34, 25, 25)
        };

        for (var index = 0; index < rectangles.Length; index++)
        {
            using var brush = new SolidBrush(colors[index]);
            using var path = AdminDrawing.CreateRoundedRectangle(rectangles[index], 7f);
            e.Graphics.FillPath(brush, path);
        }
    }
}

internal static class AdminAppLogoFactory
{
    public static Image Create(AdminAppDefinition app, int size)
    {
        ArgumentNullException.ThrowIfNull(app);
        size = Math.Max(32, size);

        if (app.LogoKind == AdminAppLogoKind.Embedded)
        {
            var embedded = TryLoadEmbedded(app.LogoKey, size);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return CreateGenerated(app.LogoKey, size);
    }

    private static Bitmap? TryLoadEmbedded(string logoKey, int size)
    {
        var assembly = typeof(AdminAppLogoFactory).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Assets.AdminApps.{logoKey}.png";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var source = Image.FromStream(stream);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.Clear(Color.Transparent);

        using var clip = AdminDrawing.CreateRoundedRectangle(
            new RectangleF(0, 0, size, size),
            size * 0.19f);
        graphics.SetClip(clip);
        var sourceInset = Math.Max(2f, Math.Min(source.Width, source.Height) * 0.018f);
        graphics.DrawImage(
            source,
            new RectangleF(0, 0, size, size),
            new RectangleF(
                sourceInset,
                sourceInset,
                source.Width - (sourceInset * 2),
                source.Height - (sourceInset * 2)),
            GraphicsUnit.Pixel);
        graphics.ResetClip();
        return bitmap;
    }

    private static Bitmap CreateGenerated(string logoKey, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 160f, size / 160f);

        using (var background = new SolidBrush(Color.FromArgb(9, 25, 54)))
        using (var path = AdminDrawing.CreateRoundedRectangle(new RectangleF(2, 2, 156, 156), 30f))
        {
            graphics.FillPath(background, path);
        }

        switch (logoKey)
        {
            case "power-mode-toggle":
                DrawPowerMode(graphics);
                break;
            case "stayactive":
                DrawStayActive(graphics);
                break;
            case "voicecodex":
                DrawMicrophone(graphics, Color.FromArgb(52, 211, 153));
                break;
            case "wifidevices":
                DrawWifi(graphics);
                break;
            case "finance":
                DrawFinance(graphics);
                break;
            case "youtube-sync-tray":
                DrawYouTube(graphics);
                break;
            case "light-dark-toggle":
                DrawLightDark(graphics);
                break;
            default:
                DrawFallback(graphics);
                break;
        }

        return bitmap;
    }

    private static void DrawPowerMode(Graphics graphics)
    {
        using var orangePen = new Pen(Color.FromArgb(255, 138, 38), 8f);
        using var greenPen = new Pen(Color.FromArgb(52, 211, 153), 8f);
        graphics.DrawArc(orangePen, 34, 34, 92, 92, 125, 180);
        graphics.DrawArc(greenPen, 34, 34, 92, 92, -55, 180);

        using var bolt = new SolidBrush(Color.FromArgb(255, 212, 92));
        graphics.FillPolygon(bolt,
        [
            new PointF(86, 38),
            new PointF(55, 83),
            new PointF(76, 83),
            new PointF(66, 122),
            new PointF(106, 70),
            new PointF(84, 70)
        ]);
    }

    private static void DrawStayActive(Graphics graphics)
    {
        using var eyePath = new GraphicsPath();
        eyePath.AddBezier(24, 80, 50, 42, 110, 42, 136, 80);
        eyePath.AddBezier(136, 80, 110, 118, 50, 118, 24, 80);
        eyePath.CloseFigure();
        using var eyeBrush = new SolidBrush(Color.FromArgb(235, 245, 255));
        using var pupilBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
        using var pupilRing = new Pen(Color.FromArgb(7, 28, 52), 6f);
        graphics.FillPath(eyeBrush, eyePath);
        graphics.FillEllipse(pupilBrush, 59, 59, 42, 42);
        graphics.DrawEllipse(pupilRing, 65, 65, 30, 30);
    }

    private static void DrawMicrophone(Graphics graphics, Color accent)
    {
        using var lightBrush = new SolidBrush(Color.FromArgb(245, 248, 255));
        using var accentPen = new Pen(accent, 9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var accentBrush = new SolidBrush(accent);
        using var microphoneBody = AdminDrawing.CreateRoundedRectangle(new RectangleF(57, 29, 46, 76), 22f);
        using var microphoneStand = AdminDrawing.CreateRoundedRectangle(new RectangleF(55, 135, 50, 9), 4f);
        graphics.FillPath(lightBrush, microphoneBody);
        graphics.DrawArc(accentPen, 43, 66, 74, 67, 0, 180);
        graphics.DrawLine(accentPen, 80, 126, 80, 139);
        graphics.FillPath(accentBrush, microphoneStand);
    }

    private static void DrawWifi(Graphics graphics)
    {
        using var signalPen = new Pen(Color.FromArgb(52, 211, 153), 10f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var innerPen = new Pen(Color.FromArgb(220, 232, 240), 9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var dot = new SolidBrush(Color.FromArgb(52, 211, 153));
        graphics.DrawArc(signalPen, 29, 38, 102, 88, 205, 130);
        graphics.DrawArc(innerPen, 48, 58, 64, 55, 205, 130);
        graphics.FillEllipse(dot, 69, 111, 22, 22);
    }

    private static void DrawFinance(Graphics graphics)
    {
        using var card = new SolidBrush(Color.FromArgb(239, 248, 255));
        using var accent = new SolidBrush(Color.FromArgb(52, 211, 153));
        using var navy = new SolidBrush(Color.FromArgb(19, 61, 90));
        using var border = new Pen(Color.FromArgb(111, 183, 223), 6f);
        using var ledger = AdminDrawing.CreateRoundedRectangle(new RectangleF(31, 33, 98, 94), 16f);
        graphics.FillPath(card, ledger);
        graphics.DrawPath(border, ledger);
        graphics.FillRectangle(accent, 43, 47, 74, 10);
        graphics.FillRectangle(accent, 43, 103, 42, 8);
        graphics.FillRectangle(accent, 43, 116, 58, 8);
        using var dollarFont = new Font("Segoe UI", 46f, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.DrawString("$", dollarFont, navy, new PointF(66, 53));
    }

    private static void DrawYouTube(Graphics graphics)
    {
        using var red = new SolidBrush(Color.FromArgb(226, 43, 49));
        using var white = new SolidBrush(Color.White);
        using var body = AdminDrawing.CreateRoundedRectangle(new RectangleF(25, 43, 110, 74), 20f);
        graphics.FillPath(red, body);
        graphics.FillPolygon(white,
        [
            new PointF(70, 61),
            new PointF(70, 99),
            new PointF(104, 80)
        ]);
    }

    private static void DrawLightDark(Graphics graphics)
    {
        using var border = new Pen(Color.FromArgb(99, 152, 255), 6f);
        using var sun = new SolidBrush(Color.FromArgb(255, 210, 74));
        using var moon = new SolidBrush(Color.FromArgb(238, 243, 255));
        graphics.FillEllipse(sun, 37, 37, 86, 86);
        graphics.SetClip(new Rectangle(80, 30, 55, 100));
        graphics.FillEllipse(moon, 37, 37, 86, 86);
        graphics.ResetClip();
        using var cutout = new SolidBrush(Color.FromArgb(9, 25, 54));
        graphics.FillEllipse(cutout, 83, 43, 42, 66);
        graphics.DrawEllipse(border, 37, 37, 86, 86);
    }

    private static void DrawFallback(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(96, 165, 250));
        using var path = AdminDrawing.CreateRoundedRectangle(new RectangleF(46, 46, 68, 68), 18f);
        graphics.FillPath(brush, path);
    }
}

internal sealed record AdminPalette(
    Color Window,
    Color Surface,
    Color Card,
    Color CardHover,
    Color CardBorder,
    Color Text,
    Color SecondaryText,
    Color Accent,
    Color AccentHover,
    Color AccentSoft,
    Color AccentText,
    Color Success,
    Color SuccessHover,
    Color SuccessSoft,
    Color MutedButton,
    Color MutedButtonHover,
    Color Error,
    bool IsDark)
{
    public static AdminPalette ForCurrentSystemTheme()
    {
        if (SystemInformation.HighContrast)
        {
            return new AdminPalette(
                SystemColors.Window,
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.ControlLight,
                SystemColors.WindowFrame,
                SystemColors.ControlText,
                SystemColors.GrayText,
                SystemColors.Highlight,
                SystemColors.HotTrack,
                SystemColors.ControlLight,
                SystemColors.HighlightText,
                SystemColors.Highlight,
                SystemColors.HotTrack,
                SystemColors.ControlLight,
                SystemColors.ControlLight,
                SystemColors.ControlLightLight,
                Color.Red,
                IsDark: false);
        }

        var lightMode = AdminPanelThemeService.IsLightModeEnabled();
        return lightMode
            ? new AdminPalette(
                Color.FromArgb(244, 247, 251),
                Color.White,
                Color.White,
                Color.FromArgb(241, 245, 249),
                Color.FromArgb(219, 227, 238),
                Color.FromArgb(20, 32, 51),
                Color.FromArgb(96, 112, 136),
                Color.FromArgb(37, 99, 235),
                Color.FromArgb(29, 78, 216),
                Color.FromArgb(226, 236, 255),
                Color.White,
                Color.FromArgb(5, 150, 105),
                Color.FromArgb(209, 250, 229),
                Color.FromArgb(220, 252, 231),
                Color.FromArgb(239, 243, 248),
                Color.FromArgb(226, 232, 240),
                Color.FromArgb(220, 38, 38),
                IsDark: false)
            : new AdminPalette(
                Color.FromArgb(10, 16, 30),
                Color.FromArgb(15, 23, 42),
                Color.FromArgb(23, 33, 52),
                Color.FromArgb(31, 44, 67),
                Color.FromArgb(45, 59, 82),
                Color.FromArgb(246, 248, 252),
                Color.FromArgb(155, 169, 191),
                Color.FromArgb(59, 130, 246),
                Color.FromArgb(96, 165, 250),
                Color.FromArgb(24, 54, 95),
                Color.White,
                Color.FromArgb(52, 211, 153),
                Color.FromArgb(33, 87, 72),
                Color.FromArgb(20, 66, 57),
                Color.FromArgb(31, 43, 63),
                Color.FromArgb(40, 55, 79),
                Color.FromArgb(248, 113, 113),
                IsDark: true);
    }
}

internal static class AdminDrawing
{
    public static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var arc = new RectangleF(rectangle.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void SetRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRectangle(
            new RectangleF(0, 0, control.Width, control.Height),
            radius);
        var previous = control.Region;
        control.Region = new Region(path);
        previous?.Dispose();
    }
}

internal static class DwmWindowTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static void TryApply(IntPtr windowHandle, bool useDarkMode)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var enabled = useDarkMode ? 1 : 0;
            if (DwmSetWindowAttribute(
                    windowHandle,
                    DwmwaUseImmersiveDarkMode,
                    ref enabled,
                    sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(
                    windowHandle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
