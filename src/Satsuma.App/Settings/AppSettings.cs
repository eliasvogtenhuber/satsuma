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
public sealed class AppSettings
{
    /// <summary>Pressed during a drag to open the output-format wheel.</summary>
    public string FormatsKey { get; set; } = "1";

    /// <summary>Pressed during a drag to open the tools wheel.</summary>
    public string ToolsKey { get; set; } = "2";

    /// <summary>Pressed during a drag to open the file-actions wheel.</summary>
    public string ActionsKey { get; set; } = "3";

    /// <summary>
    /// Open Explorer on the results when a job finishes. Off by default: results land
    /// next to the original, which is usually the folder the drag started in and already
    /// on screen. The progress card can be clicked to reveal them instead.
    /// </summary>
    public bool RevealResults { get; set; }

    /// <summary>Outer diameter of the wheel, in device-independent pixels.</summary>
    public double WheelDiameter { get; set; } = 348;

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

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app starting.
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
