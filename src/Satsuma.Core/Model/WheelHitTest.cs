namespace Satsuma.Core.Model;

/// <summary>
/// Which pane of the wheel a pointer is choosing.
///
/// This is pure geometry with no window attached, which is the point: the behaviour that
/// matters here is what happens to a selection when a hand shakes, and that cannot be
/// tested by driving a real pointer, because the person's own mouse is in the way. Kept
/// out of the window so it can be covered properly.
///
/// Selection is by angle rather than by hitting the pane itself, which is what makes a
/// radial menu quick: the pointer only has to lean the right way.
/// </summary>
public static class WheelHitTest
{
    /// <summary>No pane: the pointer is in the hub, or beyond the rim.</summary>
    public const int None = -1;

    /// <summary>
    /// Extra reach, in pixels, granted to a selection that already exists before it is
    /// given up at either rim.
    /// </summary>
    public const double RadialSlack = 18;

    /// <summary>Tolerance, in pixels, past the outer rim that still counts as on target.</summary>
    public const double OuterTolerance = 4;

    /// <summary>
    /// The pane for a pointer at (<paramref name="dx"/>, <paramref name="dy"/>) from the
    /// centre, in pixels, y pointing down.
    ///
    /// <paramref name="current"/> is the pane already selected, or <see cref="None"/>.
    /// It is not just an optimisation: both rims and every boundary between panes are
    /// hard lines, so without widening them for a selection that already exists, a
    /// pointer resting near one flips back and forth on sub-pixel tremor and the
    /// selection strobes. Hysteresis is the whole reason this takes the current pane.
    /// </summary>
    public static int IndexAt(
        double dx, double dy, double innerRadius, double outerRadius, int count, int current)
    {
        if (count <= 0) return None;

        var held = current >= 0 && current < count;
        var slack = held ? RadialSlack : 0;

        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < innerRadius - slack) return None;
        if (distance > outerRadius + OuterTolerance + slack) return None;

        var sweep = 360.0 / count;
        var angle = AngleOf(dx, dy);

        var index = Math.Clamp((int)(angle / sweep), 0, count - 1);
        if (!held || index == current) return index;

        // Keep the pane being held until the pointer is clearly inside its neighbour.
        // The margin scales with the pane, so a crowded wheel does not become sticky.
        var margin = Math.Min(5.0, sweep * 0.18);
        return DegreesOutside(angle, current, sweep) < margin ? current : index;
    }

    /// <summary>Degrees clockwise from straight up, which is how the panes are laid out.</summary>
    public static double AngleOf(double dx, double dy)
    {
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90;

        angle %= 360;
        if (angle < 0) angle += 360;
        return angle;
    }

    /// <summary>
    /// How far past the edge of <paramref name="pane"/> an angle lies, in degrees, or
    /// zero when it is still inside. Wraps correctly across the top of the wheel.
    /// </summary>
    public static double DegreesOutside(double angle, int pane, double sweep)
    {
        var relative = ((angle - pane * sweep) % 360 + 360) % 360;
        if (relative < sweep) return 0;

        return Math.Min(relative - sweep, 360 - relative);
    }
}
