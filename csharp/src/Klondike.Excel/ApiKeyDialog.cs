using System.Drawing;
using System.Windows.Forms;

namespace Klondike.Excel;

/// <summary>Asks for the API key. Masked: it is a secret, on a screen that may be shared.</summary>
internal sealed class ApiKeyDialog : Form
{
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Left = 12, Top = 56, Width = 440, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };

    public ApiKeyDialog(bool hasKey)
    {
        Text = "Klondike API key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(464, 128);

        var label = new Label
        {
            Left = 12,
            Top = 10,
            Width = 440,
            Height = 40,
            Text = (hasKey ? "A key is saved. " : "") + "Paste a key created on https://api.klondike-crypto.com/portal/. It is stored encrypted for this Windows account.",
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 296, Top = 90, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 377, Top = 90, Width = 75 };
        var remove = new Button { Text = "Remove", DialogResult = DialogResult.Abort, Left = 12, Top = 90, Width = 75, Enabled = hasKey };
        Controls.AddRange([label, _key, save, cancel, remove]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public string Key => _key.Text.Trim();
}
