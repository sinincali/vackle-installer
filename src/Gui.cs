/*
 * Vackle, a modification for Flackle's desktop app
 * Copyright (c) 2026 Xrm0s and contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Vackle;

sealed class InstallerWindow : Form
{
    static readonly Color Page = Color.FromArgb(28, 28, 30);
    static readonly Color Card = Color.FromArgb(38, 38, 40);
    static readonly Color Ink = Color.FromArgb(235, 235, 240);
    static readonly Color Muted = Color.FromArgb(152, 152, 160);
    static readonly Color Danger = Color.FromArgb(255, 105, 97);

    readonly string root;
    readonly Label flackle = Caption("", 10f, Ink);
    readonly Label installed = Caption("", 10f, Muted);
    readonly Label status = Caption("", 9.5f, Muted);
    readonly Marquee progress = new() { Visible = false };
    readonly RoundButton install = new("Install", Ink, Page);
    readonly RoundButton repair = new("Repair", Card, Ink);
    readonly RoundButton uninstall = new("Uninstall", Page, Danger);
    readonly RoundButton browse = new("Choose folder…", Card, Ink);
    readonly RoundButton open = new("Open Flackle", Ink, Page) { Visible = false };
    Installation? host;
    bool present;

    public InstallerWindow(string root)
    {
        this.root = root;
        Text = "Vackle Installer";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Page;
        ForeColor = Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(640, 420);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Program.ExecutablePath); } catch (ArgumentException) { }

        var logo = new Logo { Bounds = new Rectangle(36, 30, 48, 48) };
        var title = Caption("Vackle", 20f, Ink, FontStyle.Bold); title.Location = new Point(92, 28);
        var subtitle = Caption("Installer for Flackle", 10f, Muted); subtitle.Location = new Point(95, 62);
        var notice = new RoundPanel(Card) { Bounds = new Rectangle(36, 100, 568, 54) };
        var noticeText = Caption("Only download Vackle from github.com/sinincali (vackle or vackle-installer). Copies from anywhere else could be harmful.", 9.5f, Muted);
        noticeText.MaximumSize = new Size(536, 0); noticeText.Location = new Point(16, 9);
        notice.Controls.Add(noticeText);
        flackle.Location = new Point(36, 178); flackle.MaximumSize = new Size(568, 0);
        installed.Location = new Point(36, 204);
        browse.Bounds = new Rectangle(36, 236, 150, 38); browse.Visible = false;
        status.Location = new Point(36, 300); status.MaximumSize = new Size(568, 0);
        progress.Bounds = new Rectangle(36, 330, 568, 3);
        uninstall.Bounds = new Rectangle(36, 350, 116, 42);
        repair.Bounds = new Rectangle(282, 350, 130, 42);
        install.Bounds = new Rectangle(424, 350, 180, 42);
        open.Bounds = new Rectangle(424, 350, 180, 42);
        Controls.AddRange([logo, title, subtitle, notice, flackle, installed, browse, status, progress, uninstall, repair, install, open]);

        install.Click += (_, _) => Run(false);
        repair.Click += (_, _) => Run(true);
        uninstall.Click += (_, _) => Remove();
        browse.Click += (_, _) => Choose();
        open.Click += (_, _) => { Program.StartInstalled(root); Close(); };
        Shown += (_, _) => Detect();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
    }

    static Label Caption(string text, float size, Color color, FontStyle style = FontStyle.Regular) =>
        new() { Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, style) };

    void Detect()
    {
        try { Display(Detection.Select()); }
        catch (VackleException error)
        {
            flackle.Text = error.Message.Replace(" Pass --path <directory>.", " Choose the folder Flackle is installed in.");
            installed.Text = "";
            browse.Visible = true;
            install.Enabled = repair.Enabled = uninstall.Enabled = false;
        }
    }

    void Display(Installation found)
    {
        host = found;
        flackle.Text = $"Flackle {found.Version} found at {found.Directory}";
        Manifest? current = null;
        try { current = Installer.ReadManifest(root); } catch (Exception error) when (error is VackleException || error is IOException || error is FormatException) { }
        installed.Text = current is null ? "Vackle is not installed yet." : $"Vackle {current.Version} is installed.";
        install.Text = current is null ? "Install" : "Install / Update";
        browse.Visible = false;
        present = current is not null;
        install.Enabled = true;
        repair.Enabled = uninstall.Enabled = present;
    }

    void Choose()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder Flackle is installed in" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { Display(Detection.Validate(dialog.SelectedPath)); }
        catch (VackleException error) { Fail(error.Message); }
    }

    async void Run(bool repairing)
    {
        if (host is not { } target || !await PrepareAsync(target)) return;
        await WorkAsync(repairing ? "Repairing Vackle…" : "Downloading and installing Vackle…", () => Program.InstallAsync(root, target, repairing, null));
        if (status.ForeColor == Danger) return;
        Display(target);
        repair.Bounds = new Rectangle(164, 350, 116, 42); install.Bounds = new Rectangle(292, 350, 120, 42); install.Text = "Update"; install.Restyle(Card, Ink);
        open.Visible = true;
        status.Text = "Done. Open Flackle with the Flackle shortcut from now on.";
    }

    async void Remove()
    {
        if (host is not { } target) return;
        if (MessageBox.Show(this, "Remove Vackle? Flackle goes back exactly how it was, and your Vackle settings are deleted.", "Vackle", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!await PrepareAsync(target)) return;
        await WorkAsync("Removing Vackle…", () => Task.Run(() => Installer.Uninstall(root)));
        if (status.ForeColor == Danger) return;
        Display(target);
        status.Text = "Vackle was removed. Flackle is back exactly how it was.";
    }

    async Task<bool> PrepareAsync(Installation target)
    {
        if (Program.InsideData(root)) { Fail("This is the installed copy of Vackle. Run the Vackle.exe you downloaded instead."); return false; }
        if (!await Task.Run(() => Program.Running(root, target))) return true;
        if (MessageBox.Show(this, "Flackle is open. Close it and continue?", "Vackle", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        await WorkAsync("Closing Flackle…", () => Program.CloseFlackleAsync(root, target));
        return status.ForeColor != Danger;
    }

    async Task WorkAsync(string message, Func<Task> work)
    {
        install.Enabled = repair.Enabled = uninstall.Enabled = browse.Enabled = false;
        status.ForeColor = Muted;
        status.Text = message;
        progress.Visible = true;
        try { await Task.Run(work); }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error.Message); }
        finally
        {
            progress.Visible = false;
            install.Enabled = browse.Enabled = true;
            repair.Enabled = uninstall.Enabled = present;
        }
    }

    void Fail(string message)
    {
        status.ForeColor = Danger;
        status.Text = message;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

sealed class Logo : Control
{
    static readonly Lazy<Image?> Picture = new(() =>
    {
        using var stream = typeof(Logo).Assembly.GetManifestResourceStream("logo.png");
        if (stream is null) return null;
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Image.FromStream(copy);
    });

    public Logo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Picture.Value is not { } image) return;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawImage(image, ClientRectangle);
    }
}

sealed class RoundPanel : Panel
{
    readonly Color fill;

    public RoundPanel(Color fill)
    {
        this.fill = fill;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12f);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
    }
}

sealed class Marquee : Control
{
    readonly Timer timer = new() { Interval = 16 };
    float offset;

    public Marquee()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(44, 44, 47);
        timer.Tick += (_, _) => { offset = (offset + 6f) % (Width + Width / 3f); Invalidate(); };
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); timer.Enabled = Visible; offset = 0; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using var brush = new SolidBrush(Color.FromArgb(235, 235, 240));
        e.Graphics.FillRectangle(brush, offset - Width / 3f, 0, Width / 3f, Height);
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

static class Shapes
{
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class RoundButton : Control
{
    Color fill;
    Color ink;
    bool hover;

    public RoundButton(string text, Color fill, Color ink)
    {
        this.fill = fill;
        this.ink = ink;
        Text = text;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        MouseEnter += (_, _) => { hover = true; Invalidate(); };
        MouseLeave += (_, _) => { hover = false; Invalidate(); };
    }

    public void Restyle(Color background, Color text) { fill = background; ink = text; Invalidate(); }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10f);
        var background = !Enabled ? Color.FromArgb(44, 44, 47) : hover ? Shade(fill) : fill;
        using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
        if (fill.GetBrightness() < 0.2f) using (var pen = new Pen(Color.FromArgb(58, 58, 62))) e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ink : Color.FromArgb(110, 110, 116), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static Color Shade(Color color) => color.GetBrightness() > 0.5f
        ? Color.FromArgb(Math.Max(color.R - 22, 0), Math.Max(color.G - 22, 0), Math.Max(color.B - 22, 0))
        : Color.FromArgb(Math.Min(color.R + 14, 255), Math.Min(color.G + 14, 255), Math.Min(color.B + 14, 255));
}
