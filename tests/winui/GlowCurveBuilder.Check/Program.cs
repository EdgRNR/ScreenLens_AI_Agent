using ScreenLens.WinUI.Views.Capture;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

var builder = new GlowCurveBuilder();
var points = new List<Point>();
const double cx = -500, cy = 240, rx = 220, ry = 160;
GlowCurveBuilder.Curve[]? prefix = null;
for (int i = 0; i <= 24000; i++)
{
    double angle = i * Math.PI * 2 / 200;
    points.Add(new Point(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
    if (i % 17 != 0 && i != 24000) continue;
    builder.Update(points, rebuild: false);
    if (i > 256 && prefix is null) prefix = builder.Curves.Take(2).ToArray();
}
Check(builder.Count > 256, "Long trails retain more than 256 curve segments");
Check(prefix!.SequenceEqual(builder.Curves.Take(2)), "Extending a trail preserves completed curves");
double error = 0, minimumJoinDot = 1;
GlowCurveBuilder.Curve? previous = null;
foreach (var curve in builder.Curves)
{
    for (int i = 0; i <= 80; i++)
    {
        var p = Evaluate(curve, i / 80d);
        double radius = Math.Sqrt(Math.Pow((p.X - cx) / rx, 2) + Math.Pow((p.Y - cy) / ry, 2));
        error = Math.Max(error, Math.Abs(radius - 1) * rx);
    }
    if (previous is { } before)
    {
        Check(before.End == curve.Start, "Connected curve endpoints");
        double ax = before.End.X - before.Control2.X, ay = before.End.Y - before.Control2.Y;
        double bx = curve.Control1.X - curve.Start.X, by = curve.Control1.Y - curve.Start.Y;
        double length = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
        if (length > 1e-10) minimumJoinDot = Math.Min(minimumJoinDot, (ax * bx + ay * by) / length);
    }
    previous = curve;
}
Check(error < 0.5, $"120 laps stay within 0.5 DIP of the ellipse (actual {error:F4})");
Check(minimumJoinDot > 0.999, $"Curve joins stay tangent (dot {minimumJoinDot:F6})");
Check(points.Count == 24001, "Original polygon remains intact");
Console.WriteLine($"Long trail: {points.Count} original points, {builder.Count} curves, max radial error {error:F4} DIP");

builder.Reset();
points.Clear();
for (int i = 0; i <= 64; i++) points.Add(new Point(i * 2, 0));
for (int i = 1; i <= 64; i++) points.Add(new Point(128, i * 2));
builder.Update(points, rebuild: false);
foreach (var curve in builder.Curves)
    for (int i = 0; i <= 80; i++)
    {
        var p = Evaluate(curve, i / 80d);
        double distance = Math.Min(SegmentDistance(p, new Point(0, 0), new Point(128, 0)),
            SegmentDistance(p, new Point(128, 0), new Point(128, 128)));
        Check(distance < 0.5, "A deliberate right angle stays within 0.5 DIP");
    }
for (int i = 0; i < points.Count; i++) points[i] = new Point(points[i].X * 2, points[i].Y * 2);
builder.Update(points, rebuild: true);
Check(builder.Curves.Last().End == points[^1], "Resizing rebuilds the display geometry");
builder.Reset();
builder.Update(new[] { new Point(0, 0), new Point(128, 0), new Point(128, 128) }, rebuild: false);
foreach (var curve in builder.Curves)
    for (int i = 0; i <= 80; i++)
    {
        var p = Evaluate(curve, i / 80d);
        Check(Math.Min(SegmentDistance(p, new Point(0, 0), new Point(128, 0)), SegmentDistance(p, new Point(128, 0), new Point(128, 128))) < 0.5,
            "Sparse fast turns retain their corner");
    }
builder.Update(new[] { new Point(0, 0), new Point(128, 0), new Point(160, 50), new Point(0, 0) }, rebuild: true, straightClosingEdge: true);
var closing = builder.Curves.Last();
for (int i = 0; i <= 80; i++)
    Check(SegmentDistance(Evaluate(closing, i / 80d), closing.Start, closing.End) < 1e-4, "Automatic closing edge stays straight within float precision");
builder.Update(Array.Empty<Point>(), rebuild: false);
Check(builder.Count == 0, "Empty input clears the display geometry");
builder.Reset();
builder.Update(new[] { new Point(3, 4) }, rebuild: false);
Check(builder.Count == 0, "A single point does not invent a stroke");
builder.Update(Enumerable.Repeat(new Point(3, 4), 150).ToArray(), rebuild: false);
foreach (var curve in builder.Curves)
    Check(curve.Control1 == new Point(3, 4) && curve.Control2 == new Point(3, 4), "Repeated points stay finite");
Console.WriteLine("PASS: joins, precision, sharp turns, resize, reset, and degenerate input");

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static Point Evaluate(GlowCurveBuilder.Curve c, double t)
{
    double s = 1 - t;
    return new Point(s * s * s * c.Start.X + 3 * t * s * s * c.Control1.X + 3 * t * t * s * c.Control2.X + t * t * t * c.End.X,
        s * s * s * c.Start.Y + 3 * t * s * s * c.Control1.Y + 3 * t * t * s * c.Control2.Y + t * t * t * c.End.Y);
}
static double SegmentDistance(Point p, Point a, Point b)
{
    double dx = b.X - a.X, dy = b.Y - a.Y;
    double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
    return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
}
