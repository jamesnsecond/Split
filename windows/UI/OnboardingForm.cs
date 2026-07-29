// Shown once, before the first route exists.
//
// The macOS build uses this space to warn about a permission prompt. Windows
// asks for nothing, so this covers the things that actually surprise people
// here instead — chiefly that Bluetooth tops out at one device, and that some
// apps need a restart before they move.

namespace Split.UI;

internal sealed class OnboardingForm : Form
{
    public OnboardingForm()
    {
        Text = "Before you route anything";
        Font = Theme.Body;
        BackColor = Theme.Page;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 400);

        var heading = new Label
        {
            Text = "Before you route anything",
            Font = Theme.Heading,
        };
        heading.SetBounds(24, 22, 400, 26);

        var points = new (string Title, string Body)[]
        {
            ("One Bluetooth headphone at a time",
             "Windows can pair several but only streams to one — a second Bluetooth "
             + "route will simply be silent. Wired and USB headphones have no such limit, "
             + "so give those to the other listeners."),

            ("Some apps need a restart",
             "Most move to their new device within a second. A few — games, and some "
             + "browsers — only pick it up the next time they start playing, or after "
             + "you restart them."),

            ("Routing is per app, not per tab",
             "A browser plays every tab through one route. Two people watching two "
             + "different sites means two different browsers."),

            ("Nothing is captured",
             "Split just tells Windows where each app should play. That means no "
             + "permission prompts, and DRM video like Netflix routes like anything else."),
        };

        int y = 62;
        foreach (var (title, body) in points)
        {
            var titleLabel = new Label { Text = title, Font = Theme.CardTitle };
            titleLabel.SetBounds(24, y, 470, 20);

            var bodyLabel = new Label { Text = body, ForeColor = Theme.Secondary };
            bodyLabel.SetBounds(24, y + 20, 470, 52);

            Controls.Add(titleLabel);
            Controls.Add(bodyLabel);
            y += 80;
        }

        var ok = new Button
        {
            Text = "Got it",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.SetBounds(414, 350, 82, 30);

        Controls.Add(ok);
        Controls.Add(heading);
        AcceptButton = ok;
    }
}
