using System.Windows.Media;
using Satsuma.Core.Model;

namespace Satsuma.App.Glass;

/// <summary>
/// The two marks that say where results are going.
///
/// Deliberately wordless. The hub already carries the action and the filename, and a
/// third line of prose to explain a key the user is currently holding down would be
/// telling them something they just did. A folder means somewhere else; two arrows
/// passing each other mean over the top. Drawn in the same thin stroke as everything
/// else on the wheel, so they read as part of it rather than as an alert.
/// </summary>
public static class ModeGlyphs
{
    /// <summary>Somewhere other than here: a folder, tab and all.</summary>
    public static Geometry Folder { get; } =
        Frozen("M1,3.4 L6.2,3.4 L7.9,5.6 L17,5.6 L17,12.6 L1,12.6 Z");

    /// <summary>
    /// Over the top: one arrow down, one arrow up, passing. The universal mark for an
    /// exchange, which is what replacing a file is.
    /// </summary>
    public static Geometry Replace { get; } = Frozen(
        "M5,1.6 L5,10.6 M2.6,8.2 L5,10.6 L7.4,8.2 " +
        "M13,12.6 L13,3.6 M10.6,6 L13,3.6 L15.4,6");

    /// <summary>A sheet of paper, for "which files".</summary>
    public static Geometry Page { get; } =
        Frozen("M3.5,1 L9.5,1 L13.5,5 L13.5,15 L3.5,15 Z M9.5,1 L9.5,5 L13.5,5");

    /// <summary>Done, on the tick there is to click.</summary>
    public static Geometry Tick { get; } = Frozen("M2,7.5 L6,11.5 L13,3.5");

    /// <summary>The mark for a destination, or null where there is nothing to say.</summary>
    public static Geometry? For(Destination destination) => destination switch
    {
        Destination.NewFolder or Destination.ChosenFolder => Folder,
        Destination.InPlace => Replace,
        _ => null
    };

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
