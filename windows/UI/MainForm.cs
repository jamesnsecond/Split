// The window: a toolbar, an advisory banner, and the list of routes.
//
// Cards are reused across refreshes rather than rebuilt — recreating them at
// 1 Hz would fight the user for the volume slider and flicker the whole list.

using Split.Audio;
using Split.Model;

namespace Split.UI;

internal sealed class MainForm : Form
{
    private readonly RouteSupervisor _supervisor;

    private readonly Panel _toolbar = new();
    private readonly Button _add = new();
    private readonly Button _presets = new();
    private readonly Button _diagnostics = new();
    private readonly Label _banner = new();
    private readonly FlowLayoutPanel _list = new();
    private readonly Panel _empty = new();

    private readonly Dictionary<Guid, RouteCard> _cards = new();

    public MainForm(RouteSupervisor supervisor)
    {
        _supervisor = supervisor;

        Text = "Split";
        BackColor = Theme.Page;
        Font = Theme.Body;
        MinimumSize = new Size(620, 460);
        Size = new Size(700, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadAppIcon();

        BuildToolbar();
        BuildBanner();
        BuildList();
        BuildEmptyState();

        // Order matters: docked controls fill in reverse order of addition.
        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(_banner);
        Controls.Add(_toolbar);

        _supervisor.Updated += OnSupervisorUpdated;
        Shown += (_, _) => MaybeShowOnboarding();
        VisibleChanged += (_, _) => { if (Visible) Rebuild(); };
        FormClosed += (_, _) => _supervisor.Updated -= OnSupervisorUpdated;

        Rebuild();
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = 52;
        _toolbar.BackColor = Theme.Page;

        _add.Text = "Route an app…";
        _add.SetBounds(16, 12, 130, 30);
        _add.FlatStyle = FlatStyle.Flat;
        _add.FlatAppearance.BorderSize = 0;
        _add.BackColor = Theme.Accent;
        _add.ForeColor = Color.White;
        _add.Click += (_, _) => AddRoute();

        _presets.Text = "Presets ▾";
        _presets.SetBounds(156, 12, 92, 30);
        _presets.FlatStyle = FlatStyle.Flat;
        _presets.BackColor = Theme.Page;
        _presets.Click += (_, _) => ShowPresetsMenu();

        _diagnostics.Text = "Diagnostics";
        _diagnostics.SetBounds(256, 12, 96, 30);
        _diagnostics.FlatStyle = FlatStyle.Flat;
        _diagnostics.BackColor = Theme.Page;
        // ShowDialog, unlike Show, leaves disposal to the caller.
        _diagnostics.Click += (_, _) =>
        {
            using var diagnostics = new DiagnosticsForm(_supervisor);
            diagnostics.ShowDialog(this);
        };

        _toolbar.Controls.AddRange(new Control[] { _add, _presets, _diagnostics });
    }

    private void BuildBanner()
    {
        _banner.Dock = DockStyle.Top;
        _banner.BackColor = Theme.WarningBack;
        _banner.ForeColor = Theme.WarningText;
        _banner.Font = Theme.Body;
        _banner.Padding = new Padding(12, 8, 12, 8);
        _banner.AutoSize = false;
        _banner.Height = 52;
        _banner.Visible = false;
    }

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.AutoScroll = true;
        _list.WrapContents = false;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.Padding = new Padding(16, 12, 16, 16);
        _list.BackColor = Theme.Page;

        // FlowLayoutPanel won't stretch children horizontally on its own.
        _list.Resize += (_, _) => SizeCards();
    }

    private void BuildEmptyState()
    {
        _empty.Dock = DockStyle.Fill;
        _empty.BackColor = Theme.Page;
        _empty.Visible = false;

        var heading = new Label
        {
            Text = "No routes yet",
            Font = Theme.Heading,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 30,
        };

        var body = new Label
        {
            Text = "Send an app's audio to any headphones or speakers.\n"
                 + "Everyone at this PC can listen to their own thing.",
            ForeColor = Theme.Secondary,
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Dock = DockStyle.Top,
            Height = 44,
        };

        var button = new Button
        {
            Text = "Route an app…",
            Size = new Size(140, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Anchor = AnchorStyles.None,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => AddRoute();

        var host = new Panel { Dock = DockStyle.Top, Height = 48 };
        host.Controls.Add(button);
        host.Resize += (_, _) => button.Location =
            new Point((host.Width - button.Width) / 2, 8);

        // Added top-down, so they stack in reverse.
        _empty.Controls.Add(host);
        _empty.Controls.Add(body);
        _empty.Controls.Add(heading);

        _empty.Resize += (_, _) =>
        {
            int top = Math.Max(0, (_empty.Height - 122) / 2);
            heading.Top = top;
        };
    }

    // MARK: - Refresh

    private void OnSupervisorUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;

        // Living in the tray with the window hidden is this app's normal
        // resting state, and re-binding every card once a second at nothing is
        // pure waste. VisibleChanged catches the window up when it reappears.
        if (!Visible) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(Rebuild));
            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var routes = _supervisor.Table.Routes;

        _empty.Visible = routes.Count == 0;
        _list.Visible = routes.Count > 0;

        UpdateBanner();

        // Drop cards whose route is gone.
        foreach (var id in _cards.Keys.Where(id => routes.All(r => r.Id != id)).ToList())
        {
            var card = _cards[id];
            _cards.Remove(id);
            _list.Controls.Remove(card);
            card.Dispose();
        }

        foreach (var route in routes)
        {
            if (_cards.TryGetValue(route.Id, out var card))
            {
                card.Bind(route);
            }
            else
            {
                card = new RouteCard(_supervisor, route);
                _cards[route.Id] = card;
                _list.Controls.Add(card);
            }
        }

        // Keep visual order matching config order.
        foreach (var route in routes)
        {
            if (_cards.TryGetValue(route.Id, out var card))
            {
                _list.Controls.SetChildIndex(card, routes.IndexOf(route));
            }
        }

        SizeCards();
    }

    private void SizeCards()
    {
        int width = _list.ClientSize.Width - _list.Padding.Horizontal - 4;
        if (width <= 0) return;

        foreach (var card in _cards.Values)
        {
            if (card.Width != width) card.Width = width;
        }
    }

    private void UpdateBanner()
    {
        // Windows will pair any number of Bluetooth headphones but only ever
        // streams to one of them; the second endpoint exists and stays silent.
        // This is a limit of the Microsoft Bluetooth stack, not of Split, so
        // the honest thing is to say so before the user blames their hardware.
        if (_supervisor.BluetoothLegCount > 1)
        {
            _banner.Text =
                "Two routes are pointed at Bluetooth devices. Windows can only stream to "
                + "one Bluetooth headphone at a time — the second will be silent. Use wired "
                + "or USB headphones for the other listeners.";
            _banner.Visible = true;
        }
        else
        {
            _banner.Visible = false;
        }
    }

    // MARK: - Actions

    private void AddRoute()
    {
        using var dialog = new AddRouteForm(_supervisor);
        dialog.ShowDialog(this);
    }

    private void MaybeShowOnboarding()
    {
        if (_supervisor.Settings.HasSeenOnboarding) return;

        using var onboarding = new OnboardingForm();
        onboarding.ShowDialog(this);

        _supervisor.Settings.HasSeenOnboarding = true;
        _supervisor.SaveSettings();
    }

    private void ShowPresetsMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.Body };

        if (_supervisor.Presets.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No presets yet") { Enabled = false });
        }

        foreach (var preset in _supervisor.Presets)
        {
            var captured = preset;
            menu.Items.Add(new ToolStripMenuItem(preset.Name,
                null, (_, _) => _supervisor.Apply(captured)));
        }

        if (_supervisor.Presets.Count > 0)
        {
            var delete = new ToolStripMenuItem("Delete");
            foreach (var preset in _supervisor.Presets)
            {
                var captured = preset;
                delete.DropDownItems.Add(new ToolStripMenuItem(preset.Name,
                    null, (_, _) => _supervisor.DeletePreset(captured.Id)));
            }
            menu.Items.Add(delete);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Save current as preset…", null, (_, _) => SavePreset())
        {
            Enabled = _supervisor.Table.Routes.Count > 0,
        });

        menu.Items.Add(new ToolStripSeparator());
        var launch = new ToolStripMenuItem("Launch at sign-in")
        {
            CheckOnClick = true,
            Checked = _supervisor.Settings.LaunchAtLogin,
        };
        launch.CheckedChanged += (_, _) =>
        {
            _supervisor.Settings.LaunchAtLogin = launch.Checked;
            _supervisor.SaveSettings();
            StartupRegistration.Set(launch.Checked);
        };
        menu.Items.Add(launch);

        // Built fresh per click, so it also has to be torn down per click.
        menu.Closed += (_, _) => menu.BeginInvoke(new Action(menu.Dispose));
        menu.Show(_presets, new Point(0, _presets.Height));
    }

    private void SavePreset()
    {
        var name = Prompt.Ask(this, "Save preset",
            "Saves the current routes so you can bring them all back in one click.",
            "Name (e.g. Movie night)");

        if (!string.IsNullOrWhiteSpace(name)) _supervisor.SavePreset(name.Trim());
    }
}
