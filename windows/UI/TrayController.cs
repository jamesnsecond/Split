// The notification-area icon: the macOS build's menu-bar panel, translated.
//
// Its whole reason for existing is mid-movie adjustment — changing one
// listener's volume without alt-tabbing out of fullscreen video. So the menu
// is built fresh on each open (routes and devices change underneath it) and
// volume is offered as coarse steps, because a slider in a context menu is
// miserable to hit with a mouse in the dark.

using Split.Audio;
using Split.Model;

namespace Split.UI;

internal sealed class TrayController : IDisposable
{
    private readonly RouteSupervisor _supervisor;
    private readonly NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public TrayController(RouteSupervisor supervisor)
    {
        _supervisor = supervisor;

        _icon = new NotifyIcon
        {
            Text = "Split",
            Visible = true,
            Icon = LoadIcon(),
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        // Building on open rather than once keeps the menu honest without
        // having to invalidate it from the supervisor.
        var menu = new ContextMenuStrip { Font = Theme.Body };
        menu.Opening += (_, _) => Populate(menu);
        _icon.ContextMenuStrip = menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void Populate(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var routes = _supervisor.Table.Routes;
        if (routes.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No routes") { Enabled = false });
        }

        foreach (var route in routes)
        {
            var captured = route;
            var device = route.PrimaryLeg?.DeviceName ?? "";

            var item = new ToolStripMenuItem($"{route.AppDisplayName}  →  {device}");

            if (route.Kind == RouteKind.App)
            {
                var mute = new ToolStripMenuItem(route.IsMuted ? "Unmute" : "Mute")
                {
                    Checked = route.IsMuted,
                };
                mute.Click += (_, _) =>
                {
                    captured.IsMuted = !captured.IsMuted;
                    _supervisor.Update(captured);
                };
                item.DropDownItems.Add(mute);
                item.DropDownItems.Add(new ToolStripSeparator());

                foreach (var percent in new[] { 100, 80, 60, 40, 20, 0 })
                {
                    var value = percent / 100.0;
                    var volume = new ToolStripMenuItem($"{percent}%")
                    {
                        // Volume is a double; compare with tolerance rather
                        // than equality or nothing ever shows as checked.
                        Checked = Math.Abs(captured.Volume - value) < 0.005,
                    };
                    volume.Click += (_, _) =>
                    {
                        captured.Volume = value;
                        captured.IsMuted = false;
                        _supervisor.Update(captured);
                    };
                    item.DropDownItems.Add(volume);
                }
            }
            else
            {
                item.DropDownItems.Add(new ToolStripMenuItem("System output — nothing to adjust")
                {
                    Enabled = false,
                });
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Split", null, (_, _) => OpenRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke()));
    }

    public void Dispose()
    {
        // Skipping this leaves a ghost icon in the tray until the user hovers it.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
