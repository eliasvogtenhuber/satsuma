using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using Satsuma.App.Diagnostics;
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

        SettleStartup();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var header = new ToolStripMenuItem("Satsuma") { Enabled = false };
        menu.Items.Add(header);

        var hint = new ToolStripMenuItem(DescribeBindings()) { Enabled = false };
        menu.Items.Add(hint);

        menu.Items.Add(new ToolStripSeparator());

        var how = new ToolStripMenuItem("How it works");
        how.Click += (_, _) => ShowHowItWorks();
        menu.Items.Add(how);

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

        // The other optional half. Satsuma does not ship LibreOffice: it is several
        // hundred megabytes, far more than everything else here put together, for the
        // one corner of the app most people never reach. Saying plainly whether it is
        // there, and offering to fetch it, is the honest middle.
        var office = new ToolStripMenuItem(Core.Tools.LibreOffice.IsAvailable
            ? "LibreOffice: ready"
            : "LibreOffice: not found") { Enabled = false };
        menu.Items.Add(office);

        if (!Core.Tools.LibreOffice.IsAvailable)
        {
            var help = new ToolStripMenuItem("How to install LibreOffice");
            help.Click += (_, _) => ShowLibreOfficeHelp();
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

        // Read again each time the menu opens rather than trusting what it was built
        // with. The first run registers Satsuma after this menu exists, and the entry
        // can be taken away from outside -- Task Manager's Startup tab does exactly
        // that -- so a tick that was right once is not right for long.
        var settling = false;

        menu.Opening += (_, _) =>
        {
            settling = true;
            startup.Checked = IsRegisteredForStartup();
            settling = false;
        };

        startup.CheckedChanged += (_, _) =>
        {
            if (!settling) SetStartup(startup.Checked);
        };

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

    /// <summary>
    /// What the keys do, which is the whole of Satsuma's interface.
    ///
    /// There is no window to put this in and no installer to say it during, so an app
    /// that does not say it here does not say it at all: a stranger who unzips Satsuma
    /// gets a tray icon and no idea that any of this exists. Shown once on the first
    /// run, and from the menu whenever anyone wants it again.
    /// </summary>
    private void ShowHowItWorks()
    {
        var message =
            "Satsuma has no window. It works inside the drag you were already doing.\n\n" +
            "START DRAGGING a file in File Explorer, then press:\n\n" +
            $"    {Describe(_settings.FormatsKey)}   convert to another format\n" +
            $"    {Describe(_settings.ToolsKey)}   tools: compress, resize, rotate, split, merge\n" +
            $"    {Describe(_settings.ActionsKey)}   actions: reveal, copy, move, shelf\n\n" +
            "A wheel appears under the pointer. Lean toward a slice and let go of the " +
            "mouse. The result lands next to the original.\n\n" +
            "Or skip the drag: select a file and press the same key. The wheel stays " +
            "put and you click a slice.\n\n" +
            "WHILE THE WHEEL IS OPEN\n\n" +
            $"    {Describe(_settings.BackKey)}   back out of a question and show the wheel again\n" +
            "    Esc   or right-click, to cancel\n\n" +
            "HOLD THESE THROUGH TO THE END OF THE GESTURE\n\n" +
            "    Space   put the results somewhere else. Let go after dropping and\n" +
            "            Satsuma makes a folder; keep holding and select one instead.\n\n" +
            "    Alt     replace the original. It goes to the Recycle Bin and the\n" +
            "            result takes its name.\n\n" +
            "Holding either shows a small mark, so you can see which you are in before " +
            "you let go.\n\n" +
            "This is in the tray menu whenever you want it again.";

        MessageBox.Show(message, "How Satsuma works",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string Describe(string binding) =>
        string.IsNullOrWhiteSpace(binding) ? "?" : binding.Trim();

    private void ShowLibreOfficeHelp()
    {
        const string message =
            "Satsuma uses LibreOffice for documents, spreadsheets and slides: docx, " +
            "xlsx, pptx, odt and the rest. Everything else works without it.\n\n" +
            "Install it with:\n\n    winget install TheDocumentFoundation.LibreOffice\n\n" +
            "It is not bundled because it is several hundred megabytes, more than the " +
            "rest of Satsuma put together, and it updates on its own schedule.\n\n" +
            "If you would rather keep everything in one folder, put a copy at " +
            "tools\\LibreOffice next to Satsuma.exe and it will be found there. " +
            "Restart Satsuma afterwards either way.";

        MessageBox.Show(message, "Satsuma needs LibreOffice for this",
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

    private static bool IsRegisteredForStartup() => RegisteredPath() is not null;

    /// <summary>The exe the startup entry points at, or null if there is no entry.</summary>
    private static string? RegisteredPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue(RunValue) as string)?.Trim('"');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Satsuma is only any use while it is running, and it has no window to be found
    /// again from, so the first run registers it to come back after a restart. The tray
    /// says so at the time and the menu unticks it; a tray app that quietly installs
    /// itself into startup and never mentions it is the thing nobody wants.
    ///
    /// Also repairs the entry when the folder has moved. There is no installer here,
    /// and a folder that gets dragged to Program Files three days later would otherwise
    /// leave a startup entry pointing at nothing.
    /// </summary>
    private void SettleStartup()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        var registered = RegisteredPath();

        if (registered is null)
        {
            if (!_settings.FirstRun) return;

            SetStartup(true);

            Notify("Satsuma is running",
                "It lives in the tray and will start with Windows. " +
                "Right-click the icon to change that.");

            // The one moment it is worth interrupting someone. Satsuma has no window
            // and does nothing until a key is pressed during a drag, so a first run
            // that says nothing is indistinguishable from one that did not work.
            ShowHowItWorks();

            return;
        }

        if (!string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"startup entry moved: {registered} -> {exe}");
            SetStartup(true);
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
