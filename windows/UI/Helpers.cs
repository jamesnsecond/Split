// Small shared bits that don't deserve files of their own.

using Microsoft.Win32;

namespace Split.UI;

/// <summary>WinForms has no InputBox, so here is a minimal one.</summary>
internal static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string message, string placeholder)
    {
        using var form = new Form
        {
            Text = title,
            Font = Theme.Body,
            BackColor = Theme.Page,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(400, 150),
        };

        var label = new Label
        {
            Text = message,
            ForeColor = Theme.Secondary,
        };
        label.SetBounds(16, 16, 368, 36);

        var input = new TextBox { PlaceholderText = placeholder };
        input.SetBounds(16, 60, 368, 24);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
        ok.SetBounds(224, 104, 76, 28);
        ok.FlatStyle = FlatStyle.Flat;
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Color.White;
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(308, 104, 76, 28);
        cancel.FlatStyle = FlatStyle.Flat;
        cancel.BackColor = Theme.Page;

        form.Controls.AddRange(new Control[] { label, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}

/// <summary>
/// Launch at sign-in, via the Run key. No installer and no scheduled task, so
/// removing Split is still just deleting the exe — this is the only trace it
/// leaves, and turning the toggle off removes it.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Split";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Locked-down machine or roaming policy; not worth an error dialog.
        }
    }
}
