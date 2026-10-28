using System.Windows;
using System.Windows.Media;

namespace Satsuma.App.Wheel;

/// <summary>Where one slice sits on the ring.</summary>
public readonly record struct SegmentPlacement(
    double StartAngle,
    double SweepAngle,
    double InnerRadius,
    double OuterRadius)
{
    public double MidAngle => StartAngle + SweepAngle / 2;
    public double MidRadius => (InnerRadius + OuterRadius) / 2;
}

/// <summary>
/// Builds the ring. Angles are degrees clockwise from straight up, which is how the slices
/// read to someone looking at them, and are converted to the maths convention only here.
/// </summary>
public static class WheelGeometry
{
    /// <summary>
    /// Gap between neighbouring slices, in degrees. Wide enough that each slice reads as
    /// its own pane of glass with the desktop showing between them, rather than as a
    /// scored disc.
    /// </summary>
    private const double GapDegrees = 3.2;

    /// <summary>Corner rounding, in pixels, applied to all four corners of a slice.</summary>
    public const double CornerRadius = 16;

    public static Point PointOnCircle(Point centre, double radius, double angleDegrees)
    {
        // -90 so that 0 degrees points up rather than right.
        var radians = (angleDegrees - 90) * Math.PI / 180.0;
        return new Point(
            centre.X + radius * Math.Cos(radians),
            centre.Y + radius * Math.Sin(radians));
    }

    public static IReadOnlyList<SegmentPlacement> Place(
        int count, double innerRadius, double outerRadius)
    {
        if (count <= 0) return Array.Empty<SegmentPlacement>();

        var sweep = 360.0 / count;
        var placements = new List<SegmentPlacement>(count);

        for (var index = 0; index < count; index++)
        {
            // A single slice would look odd as a full ring with a gap cut out of it,
            // so it gets the whole ring and no gap.
            var gap = count == 1 ? 0 : GapDegrees;
            placements.Add(new SegmentPlacement(
                index * sweep + gap / 2,
                sweep - gap,
                innerRadius,
                outerRadius));
        }

        return placements;
    }

    /// <summary>The filled annular slice for one placement.</summary>
    public static Geometry BuildSegment(Point centre, SegmentPlacement placement)
    {
        // A full ring has no edges to draw, so it is two circles with a hole.
        if (placement.SweepAngle >= 359.99)
        {
            var ring = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(centre, placement.OuterRadius, placement.OuterRadius),
                new EllipseGeometry(centre, placement.InnerRadius, placement.InnerRadius));
            ring.Freeze();
            return ring;
        }

        var start = placement.StartAngle;
        var end = placement.StartAngle + placement.SweepAngle;
        var outer = placement.OuterRadius;
        var inner = placement.InnerRadius;

        // Rounding is limited by both the thickness of the ring and how narrow the slice
        // is, so a crowded wheel softens its corners instead of folding them inside out.
        var corner = Math.Min(CornerRadius, (outer - inner) / 2 - 1);
        var outerInset = Degrees(corner / outer);
        var innerInset = Degrees(corner / inner);

        var widest = Math.Max(outerInset, innerInset);
        if (placement.SweepAngle - 2 * widest < 2)
        {
            // Shrink the rounding until the straight part of the arc survives.
            var allowed = (placement.SweepAngle - 2) / 2;
            corner = Math.Max(0, Math.Min(corner, Radians(allowed) * inner));
            outerInset = Degrees(corner / outer);
            innerInset = Degrees(corner / inner);
        }

        if (corner <= 0.5) return BuildSharpSegment(centre, placement);

        var cornerSize = new Size(corner, corner);
        var isLarge = placement.SweepAngle - 2 * outerInset > 180;

        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(centre, outer, start + outerInset),
            IsClosed = true,
            IsFilled = true
        };

        // Outer edge, then round down onto the trailing radial edge.
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, outer, end - outerInset),
            new Size(outer, outer), 0, isLarge, SweepDirection.Clockwise, true));

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, outer - corner, end),
            cornerSize, 0, false, SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(
            PointOnCircle(centre, inner + corner, end), true));

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, inner, end - innerInset),
            cornerSize, 0, false, SweepDirection.Clockwise, true));

        // Inner edge runs back the other way.
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, inner, start + innerInset),
            new Size(inner, inner), 0,
            placement.SweepAngle - 2 * innerInset > 180,
            SweepDirection.Counterclockwise, true));

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, inner + corner, start),
            cornerSize, 0, false, SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(
            PointOnCircle(centre, outer - corner, start), true));

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, outer, start + outerInset),
            cornerSize, 0, false, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The unrounded form, for slices too narrow to round.</summary>
    private static Geometry BuildSharpSegment(Point centre, SegmentPlacement placement)
    {
        var start = placement.StartAngle;
        var end = placement.StartAngle + placement.SweepAngle;
        var isLarge = placement.SweepAngle > 180;

        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(centre, placement.OuterRadius, start),
            IsClosed = true,
            IsFilled = true
        };

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, placement.OuterRadius, end),
            new Size(placement.OuterRadius, placement.OuterRadius), 0, isLarge,
            SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(
            PointOnCircle(centre, placement.InnerRadius, end), true));

        figure.Segments.Add(new ArcSegment(
            PointOnCircle(centre, placement.InnerRadius, start),
            new Size(placement.InnerRadius, placement.InnerRadius), 0, isLarge,
            SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>
    /// A donut used as an invisible catch area just outside the slices.
    ///
    /// The window is transparent, so the OS only routes a drag to it over pixels that are
    /// actually painted. A catch ring at alpha 1 is invisible but solid to hit testing, so
    /// the real dragged paths arrive a moment before the pointer reaches a slice, which is
    /// what lets the wheel correct itself before the user aims.
    /// </summary>
    public static Geometry BuildRing(Point centre, double innerRadius, double outerRadius)
    {
        var ring = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(centre, outerRadius, outerRadius),
            new EllipseGeometry(centre, innerRadius, innerRadius));
        ring.Freeze();
        return ring;
    }
}
