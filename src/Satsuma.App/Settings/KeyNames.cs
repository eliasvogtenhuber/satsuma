using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Settings;

/// <summary>
/// Turns the key names in settings.json into the virtual-key codes the input hook sees.
///
/// Names are deliberately the ones a person would write: "1", "Space", "F2", "Q". A digit
/// maps to both the top row and the numpad, since nobody means only one of them.
/// </summary>
public static class KeyNames
{
    /// <summary>
    /// The virtual-key codes a name covers, or empty when the name means nothing.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<int>();

        var key = name.Trim();

        // Digits: top row and numpad both count.
        if (key.Length == 1 && key[0] is >= '0' and <= '9')
        {
            var digit = key[0] - '0';
            return new[] { 0x30 + digit, 0x60 + digit };
        }

        if (key.Length == 1 && char.IsAsciiLetter(key[0]))
        {
            // A letter's virtual-key code is its uppercase ASCII value.
            return new[] { (int)char.ToUpperInvariant(key[0]) };
        }

        if (key.Length is 2 or 3
            && (key[0] is 'F' or 'f')
            && int.TryParse(key[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return new[] { 0x70 + functionKey - 1 };
        }

        return key.ToLowerInvariant() switch
        {
            "space" or "spacebar" => new[] { 0x20 },
            "tab" => new[] { 0x09 },
            "capslock" => new[] { 0x14 },
            "backquote" or "grave" or "tilde" => new[] { 0xC0 },

            // Available, but they are what Explorer uses to pick copy, move and shortcut
            // during a drag. Taking them over means losing that, which is why they are
            // not the defaults.
            "shift" => new[] { VK_SHIFT, VK_LSHIFT, VK_RSHIFT },
            "ctrl" or "control" => new[] { VK_CONTROL, VK_LCONTROL, VK_RCONTROL },
            "alt" or "menu" => new[] { VK_MENU, VK_LMENU, VK_RMENU },

            _ => Array.Empty<int>()
        };
    }

    /// <summary>True when this binding would take a key Explorer needs during a drag.</summary>
    public static bool ClashesWithExplorer(string? name) =>
        name?.Trim().ToLowerInvariant() is "shift" or "ctrl" or "control" or "alt" or "menu";

    /// <summary>How to write the key in a menu or a hint.</summary>
    public static string Display(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "(unset)";

        var key = name.Trim();
        return key.ToLowerInvariant() switch
        {
            "space" or "spacebar" => "Space",
            "ctrl" or "control" => "Ctrl",
            "alt" or "menu" => "Alt",
            "shift" => "Shift",
            "tab" => "Tab",
            _ => key.ToUpperInvariant()
        };
    }
}
