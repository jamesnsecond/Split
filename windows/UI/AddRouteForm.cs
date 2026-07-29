// Pick what to route and where it goes.
//
// The app list comes from live audio sessions, so an app only appears once
// it has played something. That mirrors the macOS build and is a real
// constraint rather than a choice: Windows has no way to ask "which installed
// apps might play audio", only "which ones currently hold a session".

using Split.Audio;
using Split.Model;

namespace Split.UI;

internal sealed class AddRouteForm : Form
{
    private const string DefaultRouteKey = "__default__";

    private readonly RouteSupervisor _supervisor;
    private readonly ListView _apps = new();
    private readonly ComboBox _device = new();
    private readonly Button _add = new();
    private readonly ImageList _icons = new();

    public AddRouteForm(RouteSupervisor supervisor)
    {
        _supervisor = supervisor;

        Text = "Route audio";
        Font = Theme.Body;
        BackColor = Theme.Page;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 460);

        Build();
        LoadApps();
        LoadDevices();
    }

    private void Build()
    {
        var heading = new Label
        {
            Text = "Pick what to route",
            Font = Theme.Heading,
        };
        heading.SetBounds(20, 18, 300, 24);

        var subtitle = new Label
        {
            Text = "Apps appear here once they've played audio.",
            ForeColor = Theme.Secondary,
        };
        subtitle.SetBounds(20, 44, 460, 20);

        _icons.ImageSize = new Size(20, 20);
        _icons.ColorDepth = ColorDepth.Depth32Bit;

        _apps.SetBounds(20, 72, 480, 268);
        _apps.View = View.Details;
        _apps.FullRowSelect = true;
        _apps.MultiSelect = false;
        _apps.HeaderStyle = ColumnHeaderStyle.None;
        _apps.SmallImageList = _icons;
        _apps.Columns.Add("App", 340);
        _apps.Columns.Add("", 120);
        _apps.SelectedIndexChanged += (_, _) => UpdateAddEnabled();
        _apps.DoubleClick += (_, _) => { if (_add.Enabled) Add(); };

        var to = new Label { Text = "To:" };
        to.SetBounds(20, 358, 30, 22);

        _device.SetBounds(54, 354, 446, 24);
        _device.DropDownStyle = ComboBoxStyle.DropDownList;

        _add.Text = "Add Route";
        _add.SetBounds(392, 400, 108, 30);
        _add.FlatStyle = FlatStyle.Flat;
        _add.FlatAppearance.BorderSize = 0;
        _add.BackColor = Theme.Accent;
        _add.ForeColor = Color.White;
        _add.Enabled = false;
        _add.Click += (_, _) => Add();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Page,
        };
        cancel.SetBounds(304, 400, 80, 30);

        Controls.AddRange(new Control[] { heading, subtitle, _apps, to, _device, _add, cancel });
        CancelButton = cancel;

        // The ImageList isn't in Controls and the ListView doesn't own it, so
        // nothing would ever dispose the icons it holds.
        FormClosed += (_, _) => _icons.Dispose();
    }

    private void LoadApps()
    {
        _supervisor.Sessions.Refresh();

        _apps.Items.Clear();
        _icons.Images.Clear();

        var everythingElse = new ListViewItem("Everything else (system output)")
        {
            Tag = DefaultRouteKey,
        };
        everythingElse.SubItems.Add("works with every app");
        _apps.Items.Add(everythingElse);

        foreach (var app in _supervisor.Sessions.Apps)
        {
            // Don't offer an app that already has a route.
            if (_supervisor.Table.Routes.Any(r =>
                    r.Kind == RouteKind.App &&
                    string.Equals(r.AppPath, app.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new ListViewItem(app.DisplayName) { Tag = app.Path };
            item.SubItems.Add(app.IsPlayingOutput ? "audible now" : "");

            var icon = Theme.IconFor(app.Path);
            if (icon is not null)
            {
                _icons.Images.Add(app.Path, icon);
                item.ImageKey = app.Path;
            }

            _apps.Items.Add(item);
        }
    }

    private void LoadDevices()
    {
        _device.Items.Clear();

        foreach (var device in _supervisor.Devices.OutputDevices)
        {
            _device.Items.Add(new DeviceEntry(
                device.Id,
                device.IsBluetooth ? $"{device.Name}  (Bluetooth)" : device.Name));
        }

        if (_device.Items.Count > 0)
        {
            var defaultId = _supervisor.Devices.DefaultOutputId;
            var index = _device.Items.Cast<DeviceEntry>().ToList().FindIndex(d => d.Id == defaultId);
            _device.SelectedIndex = index >= 0 ? index : 0;
        }

        UpdateAddEnabled();
    }

    private void UpdateAddEnabled() =>
        _add.Enabled = _apps.SelectedItems.Count > 0 && _device.SelectedItem is DeviceEntry;

    private void Add()
    {
        if (_apps.SelectedItems.Count == 0 || _device.SelectedItem is not DeviceEntry device) return;

        var tag = _apps.SelectedItems[0].Tag as string;
        if (tag == DefaultRouteKey)
        {
            _supervisor.AddDefaultRoute(device.Id);
        }
        else if (tag is not null && _supervisor.Sessions.App(tag) is { } app)
        {
            _supervisor.AddAppRoute(app, device.Id);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record DeviceEntry(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
