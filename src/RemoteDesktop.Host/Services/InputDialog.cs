using System.Drawing;
using System.Windows.Forms;

namespace RemoteDesktop.Host.Services;

/// <summary>Minimal modal text prompt — WinForms ships no built-in InputBox.</summary>
internal static class InputDialog
{
    public static string? Show(string title, string prompt, bool masked, string? initial = null)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(420, 140),
            MinimizeBox = false,
            MaximizeBox = false,
        };

        var label = new Label { Left = 12, Top = 12, Width = 396, Text = prompt, AutoSize = false, Height = 40 };
        var box = new TextBox { Left = 12, Top = 56, Width = 396, UseSystemPasswordChar = masked, Text = initial ?? "" };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Width = 75, Top = 96 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Width = 75, Top = 96 };

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
