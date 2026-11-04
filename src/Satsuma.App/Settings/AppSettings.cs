using System.Text.Json;
using System.Text.Json.Serialization;
using Satsuma.Core.Model;

namespace Satsuma.App.Settings;

/// <summary>
/// User settings, stored as JSON under %APPDATA%\Satsuma.
///
/// The wheels are bound to 1, 2 and 3 rather than to Shift, Ctrl and Alt as on the Mac.
/// Those three modifiers are not Satsuma's to take on Windows: during a drag they are how
/// Explorer is told to move, copy or make a shortcut, and that muscle memory is worth more
/// than matching the original's key choice. Number keys are free during a drag, because
/// Explorer suspends type-ahead while one is in flight.
///
/// Every binding is a name a person can type: "1", "Space", "F2", "Q", or a modifier name
/// for anyone who wants the Mac behaviour and accepts what it costs.
/// </summary>
/// <summary>How the chosen slice is marked, now that no accent colour does it.</summary>
public enum SelectionStyle
{
    /// <summary>The slice thickens into a brighter, denser pane.</summary>
    Density,

    /// <summary>The frost under the slice goes much softer.</summary>
    Depth
}

public sealed class AppSettings
{
    /// <summary>How the slice under the pointer is marked.</summary>
    public SelectionStyle SelectionStyle { get; set; } = SelectionStyle.Depth;

    /// <summary>Pressed during a drag to open the output-format wheel.</summary>
    public string FormatsKey { get; set; } = "1";

    /// <summary>Pressed during a drag to open the tools wheel.</summary>
    public string ToolsKey { get; set; } = "2";

    /// <summary>Pressed during a drag to open the file-actions wheel.</summary>
    public string ActionsKey { get; set; } = "3";

    /// <summary>
    /// Backs out of whatever Satsuma is waiting for and puts the wheel back up.
    ///
    /// The three wheel keys go forward; this one goes back, which is why it sits next to
    /// them rather than being Escape. Escape ends the whole thing; this returns you to
    /// the choice you had just made, having changed your mind about it.
    /// </summary>
    public string BackKey { get; set; } = "4";

    /// <summary>
    /// Open Explorer on the results when a job finishes. Off by default: results land
    /// next to the original, which is usually the folder the drag started in and already
    /// on screen. The progress card can be clicked to reveal them instead.
    /// </summary>
    public bool RevealResults { get; set; }

    /// <summary>Outer diameter of the wheel, in device-independent pixels.</summary>
    public double WheelDiameter { get; set; } = 348;

    /// <summary>
    /// How solid the glass is, as a multiplier on the tint Satsuma works out for itself.
    /// 1 is the default; lower is more see-through, higher is more opaque.
    ///
    /// It is a multiplier rather than a flat opacity because the tint already adapts to
    /// what the wheel is covering: one fixed value would be too heavy over a dark desktop
    /// and too light over a white document. This biases that judgement without replacing
    /// it. Clamped, so the wheel can be neither made invisible nor turned into a plate.
    /// </summary>
    public double GlassOpacity { get; set; } = 1.0;

    /// <summary>
    /// How hard the backdrop behind the wheel is blurred, in pixels. Lower values make
    /// what is underneath more recognisable; higher values make it more of a wash.
    /// </summary>
    public double GlassBlur { get; set; } = 24;

    /// <summary>The <see cref="GlassOpacity"/> multiplier, kept inside sane bounds.</summary>
    public double EffectiveGlassOpacity => Math.Clamp(GlassOpacity, 0.25, 2.5);

    /// <summary>The <see cref="GlassBlur"/> radius, kept inside sane bounds.</summary>
    public double EffectiveGlassBlur => Math.Clamp(GlassBlur, 0, 120);

    /// <summary>Start Satsuma when Windows starts.</summary>
    public bool RunAtLogin { get; set; }

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Satsuma", "settings.json");

    /// <summary>
    /// The virtual-key codes that open each wheel. Built once at startup, so a binding
    /// change takes effect when Satsuma restarts.
    /// </summary>
    public IReadOnlyDictionary<int, WheelKind> BuildKeyMap()
    {
        var map = new Dictionary<int, WheelKind>();

        Add(FormatsKey, WheelKind.Formats);
        Add(ToolsKey, WheelKind.Tools);
        Add(ActionsKey, WheelKind.Actions);

        return map;

        void Add(string name, WheelKind wheel)
        {
            foreach (var code in KeyNames.Parse(name))
            {
                // First binding wins, so a duplicate in settings cannot shadow another wheel.
                map.TryAdd(code, wheel);
            }
        }
    }

    /// <summary>
    /// The codes for the back key. Not in the wheel map, because it opens nothing: it is
    /// only ever read while Satsuma is already waiting for something.
    /// </summary>
    public IReadOnlySet<int> BuildBackKeys() => KeyNames.Parse(BackKey).ToHashSet();

    /// <summary>Bindings that would take a key Explorer needs during a drag.</summary>
    public IReadOnlyList<string> ExplorerClashes()
    {
        var clashes = new List<string>();

        foreach (var binding in new[] { FormatsKey, ToolsKey, ActionsKey })
        {
            if (KeyNames.ClashesWithExplorer(binding)) clashes.Add(KeyNames.Display(binding));
        }

        return clashes;
    }

    /// <summary>
    /// True when there was no settings file to read, which means nobody has ever run
    /// this copy before. Not persisted: it describes the moment, not the settings.
    /// </summary>
    [JsonIgnore]
    public bool FirstRun { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings { FirstRun = true };

            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

            return loaded ?? new AppSettings { FirstRun = true };
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app starting. It is not a
            // first run, though: something was there, and overwriting what the user had
            // chosen is worse than ignoring it this once.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Not worth interrupting the user over.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
