using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinMuseSetup;

/// <summary>The install window: where to install, two options, then a progress bar and "Open now".</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Accent = Color.FromArgb(124, 77, 226);
    private static readonly Color Dim = Color.FromArgb(96, 96, 108);

    private readonly Setup _setup;
    private readonly string _exe;
    private readonly (long Offset, long Length) _payload;
    private readonly TextBox _dir;
    private readonly Button _browse, _install, _cancel;
    private readonly CheckBox _desktop, _openWith, _launch;
    private readonly ProgressBar _bar;
    private readonly Label _status, _note;
    private bool _installed;
    private readonly float _k;

    private int S(int v) => (int)Math.Round(v * _k);
    private Point P(int x, int y) => new(S(x), S(y));
    private Size Z(int w, int h) => new(S(w), S(h));

    public SetupForm(Setup setup, string exe, (long, long) payload, byte[] iconPng, long size, string dir)
    {
        _setup = setup; _exe = exe; _payload = payload;
        _k = DeviceDpi / 96f;
        Text = $"{setup.Name} Setup";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;   // laid out in 96-dpi units, scaled here by the display's DPI (fonts scale themselves)
        ClientSize = Z(560, 400);
        try { Icon = Icon.ExtractAssociatedIcon(exe); } catch { }

        // Header: the app's icon and name.
        var pic = new PictureBox { Location = P(24, 24), Size = Z(56, 56), SizeMode = PictureBoxSizeMode.Zoom };
        if (iconPng != null) pic.Image = Image.FromStream(new MemoryStream(iconPng));
        var title = new Label { Text = $"Install {setup.Name}", Location = P(96, 24), AutoSize = true, Font = new Font("Segoe UI Semibold", 16f) };
        var sub = new Label
        {
            Text = $"A music player for your own songs · {size / (1024 * 1024):N0} MB",
            Location = P(98, 60), Size = Z(440, 40), ForeColor = Dim
        };
        var line = new Label { Location = P(0, 108), Size = Z(560, 1), BackColor = Color.FromArgb(230, 230, 236) };

        var where = new Label { Text = "Install to", Location = P(24, 124), AutoSize = true };
        _dir = new TextBox { Location = P(24, 146), Size = Z(420, 26), Text = dir ?? Setup.DefaultDir(setup.Name) };
        _browse = new Button { Text = "Browse…", Location = P(452, 145), Size = Z(84, 28) };
        _browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _dir.Text, Description = $"Choose where to install {setup.Name}" };
            if (d.ShowDialog(this) == DialogResult.OK)
                _dir.Text = Path.GetFileName(d.SelectedPath).Equals(setup.Name, StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, setup.Name);
        };
        _desktop = new CheckBox { Text = "Desktop shortcut", Location = P(24, 190), AutoSize = true, Checked = true };
        _openWith = new CheckBox { Text = $"Add \"Open with {setup.Name}\" for audio files", Location = P(24, 216), AutoSize = true, Checked = true };
        _note = new Label
        {
            Text = "Installs for you only, so no administrator password is needed. Your music files are never changed.",
            Location = P(24, 254), Size = Z(512, 48), ForeColor = Dim
        };

        _launch = new CheckBox { Text = $"Open {setup.Name} now", Location = P(24, 190), AutoSize = true, Checked = true, Visible = false };
        _status = new Label { Location = P(24, 254), Size = Z(512, 22), ForeColor = Dim, Visible = false };
        _bar = new ProgressBar { Location = P(24, 282), Size = Z(512, 8), Maximum = 1000, Visible = false };

        _install = new Button
        {
            Text = "Install", Location = P(334, 348), Size = Z(100, 34), BackColor = Accent, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f)
        };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += OnInstall;
        _cancel = new Button { Text = "Cancel", Location = P(444, 348), Size = Z(90, 34) };
        _cancel.Click += (_, _) => Close();
        AcceptButton = _install; CancelButton = _cancel;

        Shown += (_, _) => { _dir.SelectionStart = 0; _dir.SelectionLength = 0; _install.Focus(); };   // show the start of the path
        Controls.AddRange(new Control[] { pic, title, sub, line, where, _dir, _browse, _desktop, _openWith, _note, _launch, _status, _bar, _install, _cancel });
    }

    private async void OnInstall(object sender, EventArgs e)
    {
        if (_installed) { Finish(); return; }
        var dir = _dir.Text.Trim();
        if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { MessageBox.Show(this, "Choose a folder to install to.", Text); return; }

        var running = Setup.RunningFrom(dir);
        if (running.Count > 0)
        {
            if (MessageBox.Show(this, $"{_setup.Name} is open. Close it to continue?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (var p in running) { try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); } catch { } }
        }

        _desktop.Visible = _openWith.Visible = _note.Visible = false;
        _dir.Enabled = _browse.Enabled = false;
        _install.Enabled = false; _cancel.Enabled = false;
        _status.Visible = _bar.Visible = true;
        _status.Text = "Copying files…";

        bool desktop = _desktop.Checked, openWith = _openWith.Checked;
        try
        {
            await Task.Run(() => _setup.Install(_exe, _payload, dir, desktop, openWith, (f, s) => BeginInvoke((Action)(() =>
            {
                _bar.Value = (int)(Math.Min(1, f) * 1000);
                _status.Text = s;
            }))));
            _installed = true;
            _status.Text = $"{_setup.Name} is installed. You'll find it in the Start menu.";
            _bar.Value = 1000;
            _launch.Visible = true;
            _install.Text = "Finish"; _install.Enabled = true;
            _cancel.Visible = false;
            _install.Left = _cancel.Right - _install.Width;   // Finish takes the corner Cancel leaves
            _install.Focus();
        }
        catch (Exception ex)
        {
            _status.Text = "Install failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(196, 43, 28);
            _status.Size = Z(512, 48);
            _cancel.Enabled = true; _cancel.Text = "Close";
        }
    }

    private void Finish()
    {
        if (_launch.Checked)
        {
            var app = Path.Combine(_dir.Text.Trim(), "WinMuse.exe");
            try { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app) }); } catch { }
        }
        Close();
    }
}
