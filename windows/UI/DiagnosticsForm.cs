// What Split thinks is going on, and the two buttons that fix a wedged state.
//
// This exists because the routing API fails quietly. When an app won't move,
// the useful questions are: did Windows hand us the interface at all, is the
// target device actually present, and does the app have a live session — all
// of which are answered here rather than guessed at.

using System.Text;
using Split.Audio;
using Split.Model;

namespace Split.UI;

internal sealed class DiagnosticsForm : Form
{
    private readonly RouteSupervisor _supervisor;
    private readonly TextBox _output = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public DiagnosticsForm(RouteSupervisor supervisor)
    {
        _supervisor = supervisor;

        Text = "Split — Diagnostics";
        Font = Theme.Body;
        BackColor = Theme.Page;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(620, 480);
        MinimumSize = new Size(500, 360);

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.Font = Theme.Monospace;
        _output.BackColor = Color.White;
        _output.BorderStyle = BorderStyle.FixedSingle;
        _output.Dock = DockStyle.Fill;

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 52 };

        var restore = new Button
        {
            Text = "Restore system audio",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Page,
        };
        restore.SetBounds(12, 11, 160, 30);
        restore.Click += (_, _) => ConfirmRestore();

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Page,
        };
        close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        close.SetBounds(ClientSize.Width - 96, 11, 84, 30);

        buttons.Controls.AddRange(new Control[] { restore, close });

        Controls.Add(_output);
        Controls.Add(buttons);

        _timer.Tick += (_, _) => Render();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();

        Render();
    }

    private void ConfirmRestore()
    {
        var answer = MessageBox.Show(
            this,
            "This clears every per-app output assignment Windows is holding — including "
                + "any you set yourself in Settings, or that another app set — and puts the "
                + "default output back where Split found it.\n\nYour routes stay saved, and "
                + "will re-apply.\n\nContinue?",
            "Restore system audio",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer == DialogResult.Yes)
        {
            _supervisor.RestoreSystemAudio();
            Render();
        }
    }

    private void Render()
    {
        if (IsDisposed) return;

        var text = new StringBuilder();

        text.AppendLine($"Windows build       {Environment.OSVersion.Version.Build}");
        text.AppendLine($"Routing interface   {(AudioPolicyConfig.IsAvailable ? "available" : "NOT AVAILABLE")}");
        if (AudioPolicyConfig.LastError is { } policyError)
        {
            text.AppendLine($"  last error        {policyError}");
        }
        if (SystemDefaultDevice.LastError is { } defaultError)
        {
            text.AppendLine($"  default-out error {defaultError}");
        }

        text.AppendLine();
        text.AppendLine("OUTPUT DEVICES");
        foreach (var device in _supervisor.Devices.OutputDevices)
        {
            var isDefault = device.Id == _supervisor.Devices.DefaultOutputId ? "  [system default]" : "";
            var bluetooth = device.IsBluetooth ? "  [bluetooth]" : "";
            text.AppendLine($"  {device.Name}{bluetooth}{isDefault}");
            text.AppendLine($"    {device.Id}");
        }

        text.AppendLine();
        text.AppendLine("ROUTES");
        if (_supervisor.Table.Routes.Count == 0) text.AppendLine("  (none)");

        foreach (var route in _supervisor.Table.Routes)
        {
            var status = _supervisor.Statuses.TryGetValue(route.Id, out var s)
                ? s
                : new RouteStatus(RouteState.Disabled);

            var level = _supervisor.Levels.TryGetValue(route.Id, out var l) ? l : 0f;

            text.AppendLine($"  {route.AppDisplayName}  →  {route.PrimaryLeg?.DeviceName ?? "(no device)"}");
            text.AppendLine($"    kind      {route.Kind}");
            text.AppendLine($"    state     {status.State}{(string.IsNullOrEmpty(status.Detail) ? "" : $" — {status.Detail}")}");

            if (route.Kind == RouteKind.App)
            {
                text.AppendLine($"    volume    {route.Volume:P0}{(route.IsMuted ? "  (muted)" : "")}");
                text.AppendLine($"    peak      {level:F3}");
                text.AppendLine($"    exe       {route.AppPath}");
            }
        }

        text.AppendLine();
        text.AppendLine("APPS WITH AUDIO SESSIONS");
        if (_supervisor.Sessions.Apps.Count == 0) text.AppendLine("  (none)");

        foreach (var app in _supervisor.Sessions.Apps)
        {
            var pids = string.Join(", ", app.ProcessIds);
            var audible = app.IsPlayingOutput ? "  [audible]" : "";
            text.AppendLine($"  {app.DisplayName}  (pid {pids}){audible}");
        }

        // Only rewrite when something actually changed. Assigning Text resets
        // the scroll position, and most of this output is static — without
        // this check the box snapped back to the top once a second and
        // anything past the first screenful was unreadable.
        var rendered = text.ToString();
        if (_output.Text != rendered) _output.Text = rendered;
    }
}
