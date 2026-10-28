using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using Satsuma.App.Settings;
using Satsuma.Core.Tools;

namespace Satsuma.App.Tray;

/// <summary>
/// The tray presence: how Satsuma is reached when it is not mid-drag, and the only place
/// it has any visible chrome at all.
/// </summary>
public sealed class TrayController : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Satsuma";

    private readonly AppSettings _settings;
    private readonly NotifyIcon _icon;
    private readonly Icon _drawnIcon;

    public event EventHandler? ShelfRequested;
    public event EventHandler? QuitRequested;

    public TrayController(AppSettings settings)
    {
        _settings = settings;
        _drawnIcon = BuildIcon();

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "Satsuma",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _icon.DoubleClick += (_, _) => ShelfRequested?.Invoke(this, EventArgs.Empty);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var header = new ToolStripMenuItem("Satsuma") { Enabled = false };
        menu.Items.Add(header);

        var hint = new ToolStripMenuItem(DescribeBindings()) { Enabled = false };
        menu.Items.Add(hint);

        menu.Items.Add(new ToolStripSeparator());

        var ffmpeg = new ToolStripMenuItem(FfmpegLocator.IsAvailable
            ? "ffmpeg: ready"
            : "ffmpeg: not found") { Enabled = false };
        menu.Items.Add(ffmpeg);

        if (!FfmpegLocator.IsAvailable)
        {
            var help = new ToolStripMenuItem("How to install ffmpeg");
            help.Click += (_, _) => ShowFfmpegHelp();
            menu.Items.Add(help);
        }

        menu.Items.Add(new ToolStripSeparator());

        var shelf = new ToolStripMenuItem("Show shelf");
        shelf.Click += (_, _) => ShelfRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(shelf);

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsRegisteredForStartup()
        };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        var settingsItem = new ToolStripMenuItem("Open settings file");
        settingsItem.Click += (_, _) => OpenSettingsFile();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit Satsuma");
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(quit);

        return menu;
    }

    private string DescribeBindings() =>
        $"While dragging, press {KeyNames.Display(_settings.FormatsKey)} to convert, " +
        $"{KeyNames.Display(_settings.ToolsKey)} for tools, " +
        $"{KeyNames.Display(_settings.ActionsKey)} for actions";

    public void Notify(string title, string message, bool isError = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.ShowBalloonTip(isError ? 6000 : 2500);
    }

    private void ShowFfmpegHelp()
    {
        const string message =
            "Satsuma uses ffmpeg for video, audio and the WebP and AVIF image formats. " +
            "Images that Windows can already read and write work without it.\n\n" +
            "Install it with:\n\n    winget install BtbN.FFmpeg.GPL\n\n" +
            "Satsuma finds it on PATH, in its own tools folder, or in the winget install " +
            "location. Restart Satsuma afterwards.";

        MessageBox.Show(message, "Satsuma needs ffmpeg for this",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenSettingsFile()
    {
        _settings.Save();   // make sure the file exists before opening it

        try
        {
            Process.Start(new ProcessStartInfo(AppSettings.SettingsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Could not open settings", ex.Message, isError: true);
        }
    }

    private static bool IsRegisteredForStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }

            _settings.RunAtLogin = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Notify("Could not change startup", ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Draws the tray icon rather than shipping one, so there is no asset to keep in step
    /// with the theme and it stays crisp at any tray scale.
    /// </summary>
    private static Icon BuildIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var body = new Rectangle(3, 6, 26, 24);
            using (var brush = new LinearGradientBrush(
                       body, Color.FromArgb(0xFF, 0x9A, 0x3F), Color.FromArgb(0xEE, 0x6C, 0x1F), 60f))
            {
                graphics.FillEllipse(brush, body);
            }

            // A leaf, so it reads as fruit rather than as a dot.
            using (var leaf = new SolidBrush(Color.FromArgb(0x4C, 0xAF, 0x50)))
            {
                graphics.FillClosedCurve(leaf, new[]
                {
                    new Point(16, 7), new Point(23, 2), new Point(25, 8), new Point(18, 9)
                });
            }

            using (var stem = new Pen(Color.FromArgb(0x5D, 0x40, 0x37), 2f))
            {
                graphics.DrawLine(stem, 16, 8, 15, 4);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives destroying the handle we just made.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
    }
}
