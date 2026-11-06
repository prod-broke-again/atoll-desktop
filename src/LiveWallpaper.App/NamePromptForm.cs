using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiveWallpaper.App;

/// <summary>
/// Top-level name prompt. Must not be owned by the WorkerW wallpaper form —
/// anything parented under the desktop layer cannot receive real mouse/keyboard focus.
/// </summary>
internal sealed class NamePromptForm : Form
{
    private readonly TextBox _input;
    private readonly Button _ok;
    private readonly Button _cancel;

    public string? ResultValue { get; private set; }

    public NamePromptForm(string title, string? label, string? initial, string confirmLabel, string cancelLabel)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, string.IsNullOrEmpty(label) ? 148 : 172);
        BackColor = Color.FromArgb(16, 18, 26);
        ForeColor = Color.FromArgb(238, 240, 245);
        Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);

        var y = 18;

        var titleLbl = new Label
        {
            Text = title,
            Location = new Point(20, y),
            Size = new Size(340, 24),
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(238, 240, 245),
            AutoEllipsis = true,
        };
        Controls.Add(titleLbl);
        y += 32;

        if (!string.IsNullOrEmpty(label))
        {
            var fieldLbl = new Label
            {
                Text = label,
                Location = new Point(20, y),
                Size = new Size(340, 18),
                ForeColor = Color.FromArgb(163, 169, 184),
            };
            Controls.Add(fieldLbl);
            y += 22;
        }

        _input = new TextBox
        {
            Location = new Point(20, y),
            Size = new Size(340, 28),
            Text = initial ?? string.Empty,
            BackColor = Color.FromArgb(10, 11, 16),
            ForeColor = Color.FromArgb(238, 240, 245),
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_input);
        y += 44;

        _ok = new Button
        {
            Text = confirmLabel,
            Location = new Point(180, y),
            Size = new Size(88, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(238, 240, 245),
            ForeColor = Color.FromArgb(17, 20, 28),
            DialogResult = DialogResult.None,
        };
        _ok.FlatAppearance.BorderSize = 0;
        _ok.Click += (_, _) => Accept();
        Controls.Add(_ok);

        _cancel = new Button
        {
            Text = cancelLabel,
            Location = new Point(276, y),
            Size = new Size(84, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 36, 48),
            ForeColor = Color.FromArgb(238, 240, 245),
            DialogResult = DialogResult.Cancel,
        };
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 78);
        Controls.Add(_cancel);

        AcceptButton = _ok;
        CancelButton = _cancel;

        Shown += (_, _) =>
        {
            Activate();
            _input.Focus();
            _input.SelectAll();
        };

        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept();
            }
        };
    }

    private void Accept()
    {
        var value = _input.Text.Trim();
        if (value.Length == 0) return;
        ResultValue = value;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Shows a top-most prompt with no owner (safe from WorkerW parenting).</summary>
    public static string? Ask(string title, string? label = null, string? initial = null, string confirmLabel = "ОК", string cancelLabel = "Отмена")
    {
        using var form = new NamePromptForm(title, label, initial, confirmLabel, cancelLabel);
        return form.ShowDialog() == DialogResult.OK ? form.ResultValue : null;
    }
}
