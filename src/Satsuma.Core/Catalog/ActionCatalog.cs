using Satsuma.Core.Model;

namespace Satsuma.Core.Catalog;

/// <summary>
/// The actions wheel: moves files around without transforming them. Works on folders and
/// mixed selections alike.
///
/// Tangerine's AirDrop slice has no clean Windows twin. The nearest thing is the
/// Windows share sheet, which hosts Nearby Sharing, but it is a WinRT surface that wants
/// a focused window to anchor to, and the wheel is deliberately never focused. It is
/// deferred rather than faked; Reveal takes the slice for now, being the one every
/// Windows user reaches for anyway.
/// </summary>
public static class ActionCatalog
{
    public static IReadOnlyList<WheelItem> All { get; } = new[]
    {
        new WheelItem("act.reveal", "Reveal", WheelKind.Actions, Hint: "Show in Explorer"),
        new WheelItem("act.float", "Float", WheelKind.Actions, Hint: "Park on the shelf"),
        new WheelItem("act.copy", "Copy", WheelKind.Actions, Hint: "To the clipboard"),
        new WheelItem("act.move", "Move", WheelKind.Actions, Hint: "Pick a folder"),

        // The twin of Move, and kept flat beside it rather than tucked under a shared
        // parent. Folding the two together would save a slice and cost a step, and the
        // step is the whole thing Satsuma is trying not to charge for.
        new WheelItem("act.copyto", "Copy to", WheelKind.Actions, Hint: "Pick a folder"),

        // Copy to without the folder question, which is the case it is nearly always
        // asked for: a second copy right here, to edit while the first one stays as it
        // was. Named like every other result Satsuma writes -- report.pdf duplicates to
        // report-dupe.pdf -- so the copy says what it is in the only place you will be
        // looking at it.
        new WheelItem("act.dupe", "Duplicate", WheelKind.Actions, Hint: "Beside the original"),
        new WheelItem("act.path", "Copy path", WheelKind.Actions),
        new WheelItem("act.terminal", "Terminal", WheelKind.Actions, Hint: "Open here"),
    };

    public static IReadOnlyList<WheelItem> For(FileCategory _) => All;
}
