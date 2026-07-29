// One route's row: what app, where it goes, how loud, and whether it's
// actually working right now.
//
// The card owns no state — it renders a RouteConfig and pushes edits straight
// back to the supervisor, which persists and re-reconciles. Bind() is called
// once a second by MainForm, so it guards against re-entrancy: moving a slider
// must not trigger a redraw that resets the slider under the mouse.

using Split.Audio;
using Split.Model;

namespace Split.UI;

internal sealed class RouteCard : CardPanel
{
    private readonly RouteSupervisor _supervisor;
    private RouteConfig _route;

    private readonly PictureBox _icon = new();
    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly StatusDot _dot = new();
    private readonly ComboBox _device = new();
    private readonly Button _mute = new();
    private readonly Button _remove = new();
    private readonly TrackBar _volume = new();
    private readonly Label _volumeLabel = new();
    private readonly LevelBar _level = new();
    private readonly Label _note = new();

    private bool _updating;

    public RouteCard(RouteSupervisor supervisor, RouteConfig route)
    {
        _supervisor = supervisor;
        _route = route;

        Height = route.Kind == RouteKind.Default ? 92 : 116;
        Margin = new Padding(0, 0, 0, 10);
        Padding = new Padding(12);

        BuildLayout();
        Bind(route);
    }

    public Guid RouteId => _route.Id;

    private void BuildLayout()
    {
        _icon.SetBounds(14, 14, 32, 32);
        _icon.SizeMode = PictureBoxSizeMode.Zoom;
        _icon.BackColor = Color.Transparent;

        _title.SetBounds(56, 13, 260, 20);
        _title.Font = Theme.CardTitle;
        _title.BackColor = Color.Transparent;

        _dot.SetBounds(56, 37, 8, 8);
        _dot.BackColor = Color.Transparent;

        _status.SetBounds(69, 34, 300, 16);
        _status.Font = Theme.Caption;
        _status.ForeColor = Theme.Secondary;
        _status.BackColor = Color.Transparent;
        _status.AutoEllipsis = true;

        _device.SetBounds(0, 16, 200, 24);
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        _device.Font = Theme.Body;
        _device.SelectedIndexChanged += OnDeviceChanged;

        _mute.SetBounds(0, 16, 30, 24);
        _mute.FlatStyle = FlatStyle.Flat;
        _mute.FlatAppearance.BorderSize = 0;
        _mute.BackColor = Theme.Card;
        _mute.Font = Theme.Body;
        _mute.Click += OnMuteClicked;

        _remove.SetBounds(0, 16, 30, 24);
        _remove.FlatStyle = FlatStyle.Flat;
        _remove.FlatAppearance.BorderSize = 0;
        _remove.BackColor = Theme.Card;
        _remove.Font = Theme.Body;
        _remove.Text = "\u00D7"; // Multiplication sign — guaranteed present in Segoe UI, unlike ✕ (U+2715).
        _remove.ForeColor = Theme.Secondary;
        _remove.Click += OnRemoveClicked;

        // AutoSize is true by default on TrackBar and *overrides* the height
        // passed to SetBounds with a DPI-derived ~45px. Left on, the trackbar
        // silently covers the level meter below it.
        _volume.AutoSize = false;
        _volume.SetBounds(56, 54, 240, 30);
        _volume.Minimum = 0;
        _volume.Maximum = 100;
        _volume.TickStyle = TickStyle.None;
        _volume.Scroll += OnVolumeChanged;

        _level.SetBounds(64, 90, 224, 3);
        _level.BackColor = Theme.Card;

        _volumeLabel.SetBounds(300, 65, 44, 18);
        _volumeLabel.Font = Theme.Monospace;
        _volumeLabel.ForeColor = Theme.Secondary;
        _volumeLabel.TextAlign = ContentAlignment.MiddleRight;
        _volumeLabel.BackColor = Color.Transparent;

        _note.SetBounds(56, 56, 480, 32);
        _note.Font = Theme.Caption;
        _note.ForeColor = Theme.Secondary;
        _note.BackColor = Color.Transparent;

        Controls.AddRange(new Control[]
        {
            _icon, _title, _dot, _status, _device, _mute, _remove,
            _volume, _level, _volumeLabel, _note,
        });

        // Later-added controls sit *behind* earlier ones in WinForms, so
        // without this the meter is painted under the opaque native trackbar.
        _level.BringToFront();

        Resize += (_, _) => LayoutRightEdge();
    }

    /// <summary>Right-aligned controls have to be placed by hand; no anchors
    /// because the card is re-laid-out on every parent resize.</summary>
    private void LayoutRightEdge()
    {
        int right = ClientSize.Width - 12;

        _remove.Left = right - _remove.Width;
        _mute.Left = _remove.Left - _mute.Width - 4;

        // A Default route has no mute button, so don't reserve its width or the
        // device picker floats with a gap beside it.
        _device.Left = (_mute.Visible ? _mute.Left : _remove.Left) - _device.Width - 8;

        // The volume row shares the line with nothing, so it can use the space
        // up to where the percentage sits.
        _volumeLabel.Left = right - _volumeLabel.Width;
        _volume.Width = Math.Max(120, _volumeLabel.Left - 8 - _volume.Left);
        _level.Left = _volume.Left + 8;
        _level.Width = Math.Max(40, _volume.Width - 16);

        _note.Width = right - _note.Left;
        _title.Width = Math.Max(80, _device.Left - _title.Left - 12);
        _status.Width = Math.Max(80, _device.Left - _status.Left - 12);
    }

    public void Bind(RouteConfig route)
    {
        _route = route;

        // Save/restore rather than a bare assignment: if Bind ever becomes
        // reachable from inside Bind, an unconditional reset would disarm the
        // outer guard and open up exactly the recursion it exists to stop.
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var isApp = route.Kind == RouteKind.App;

            _title.Text = route.AppDisplayName;

            // Never dispose the old image — Theme.IconFor hands out a shared,
            // cached bitmap.
            _icon.Image = isApp ? Theme.IconFor(route.AppPath) : null;

            PopulateDevices(route);

            _mute.Visible = isApp;
            _volume.Visible = isApp;
            _volumeLabel.Visible = isApp;
            _level.Visible = isApp;
            _note.Visible = !isApp;

            if (isApp)
            {
                _mute.Text = route.IsMuted ? "🔇" : "🔊";
                _mute.ForeColor = route.IsMuted ? Theme.Bad : Color.Black;

                // Clamp before comparing, not after. A routes.json carrying a
                // macOS-style 0...2 volume would otherwise never match the
                // clamped value written to the slider, and Bind would rewrite
                // the trackbar every single second.
                var percent = Math.Clamp((int)Math.Round(route.Volume * 100), 0, 100);
                if (_volume.Value != percent) _volume.Value = percent;
                _volumeLabel.Text = $"{percent}%";
                _level.Level = _supervisor.Levels.TryGetValue(route.Id, out var l) ? l : 0f;
            }
            else
            {
                _note.Text =
                    "Everything you haven't routed plays here, including apps that "
                    + "refuse to be moved. Nothing is captured, so DRM video works fine.";
            }

            ApplyStatus();
            LayoutRightEdge();
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    private void PopulateDevices(RouteConfig route)
    {
        var devices = _supervisor.Devices.OutputDevices.ToList();
        var current = route.PrimaryLeg;

        // Spelled out rather than a Bluetooth glyph: the obvious symbols aren't
        // in Segoe UI and render as a missing-character box.
        var entries = devices
            .Select(d => new DeviceEntry(d.Id, d.IsBluetooth ? $"{d.Name} (Bluetooth)" : d.Name))
            .ToList();

        // Keep an unplugged device in the list so the card still shows where
        // the route points, rather than silently snapping to another device.
        if (current is not null && devices.All(d => d.Id != current.DeviceId))
        {
            entries.Add(new DeviceEntry(current.DeviceId, $"{current.DeviceName} (disconnected)"));
        }

        var existing = _device.Items.Cast<DeviceEntry>().ToList();
        if (!existing.SequenceEqual(entries))
        {
            _device.Items.Clear();
            _device.Items.AddRange(entries.Cast<object>().ToArray());
        }

        var index = entries.FindIndex(e => e.Id == current?.DeviceId);
        if (_device.SelectedIndex != index) _device.SelectedIndex = index;
    }

    private void ApplyStatus()
    {
        var status = _supervisor.Statuses.TryGetValue(_route.Id, out var s)
            ? s
            : new RouteStatus(RouteState.Disabled);

        var (text, color) = status.State switch
        {
            RouteState.Disabled => ("Off", Theme.Idle),
            RouteState.WaitingForApp => (status.Detail, Theme.Idle),
            RouteState.Ready => ("Ready — waiting for audio", Theme.Idle),
            RouteState.Playing => ("Playing", Theme.Playing),
            RouteState.DeviceMissing => (status.Detail, Theme.Attention),
            RouteState.SystemDefault => ("System output", Theme.Accent),
            RouteState.Failed => (status.Detail, Theme.Bad),
            _ => ("", Theme.Idle),
        };

        _status.Text = text;
        _dot.Color = color;
    }

    // MARK: - Events

    private void OnDeviceChanged(object? sender, EventArgs e)
    {
        if (_updating || _device.SelectedItem is not DeviceEntry entry) return;
        if (entry.Id == _route.PrimaryLeg?.DeviceId) return;

        var device = _supervisor.Devices.Device(entry.Id);
        if (device is null) return;

        _route.Legs = new List<OutputLeg>
        {
            new() { DeviceId = device.Id, DeviceName = device.Name },
        };

        // Posted, not called. Update() ultimately runs Bind() -> PopulateDevices,
        // which can clear and refill this very ComboBox — and doing that while
        // it is still inside its own SelectedIndexChanged dispatch makes its
        // post-handler bookkeeping index into a list that no longer exists.
        // Reproducible by switching a route off a disconnected device, which
        // removes the synthetic "(disconnected)" entry.
        var route = _route;
        PostToForm(() => _supervisor.Update(route));
    }

    private void OnVolumeChanged(object? sender, EventArgs e)
    {
        if (_updating) return;

        _route.Volume = _volume.Value / 100.0;
        _volumeLabel.Text = $"{_volume.Value}%";

        // Scroll fires dozens of times a second while dragging, and a full
        // Update() reconciles every route — process enumeration, COM volume
        // writes, and a rebuild of every card — which makes the drag stutter.
        // This applies the new volume to just this app; the 1 Hz tick brings
        // everything else back into agreement a moment later.
        _supervisor.UpdateVolume(_route);
    }

    private void OnMuteClicked(object? sender, EventArgs e)
    {
        if (_updating) return;
        _route.IsMuted = !_route.IsMuted;
        _supervisor.Update(_route);
    }

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        // Posted, not called: removing the route disposes this card, and this
        // card is the parent of the button whose Click is still on the stack.
        // WinForms keeps using that handle after the handler returns.
        var id = _route.Id;
        PostToForm(() => _supervisor.Remove(id));
    }

    /// <summary>
    /// Runs an action on a later message, off the current control's dispatch.
    ///
    /// Posted to the *form*, deliberately, not to this card: one caller removes
    /// the route, which disposes this card — so dispatching through the card's
    /// own handle would land in the same place as calling it inline.
    /// </summary>
    private void PostToForm(Action action)
    {
        var form = FindForm();
        if (form is not null && form.IsHandleCreated && !form.IsDisposed)
        {
            form.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private sealed record DeviceEntry(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}

/// <summary>The little coloured circle in front of the status text.</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Theme.Idle;

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(8, 8);
    }

    // Built in code, never dropped on a designer surface.
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}
