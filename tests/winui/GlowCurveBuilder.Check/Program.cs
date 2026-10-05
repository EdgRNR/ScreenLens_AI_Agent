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

var smoother = new GlowStrokeSmoother();
var filteredBuilder = new GlowCurveBuilder(errorTolerance: 0.5, lookahead: GlowStrokeSmoother.Lookahead);
var jitter = new List<Point>();
Point[]? settled = null;
for (int i = 0; i < 1000; i++)
{
    jitter.Add(new Point(i * 3, i % 2 == 0 ? 0.9 : -0.9));
    if (i % 13 != 0 && i != 999) continue;
    var display = smoother.Update(jitter, rebuild: false, straightClosingEdge: false);
    Check(display.Count == jitter.Count && display[^1] == jitter[^1], "Smoothing never delays the live endpoint");
    if (settled is not null)
        for (int j = 0; j < settled.Length - GlowStrokeSmoother.Lookahead - 1; j++)
            Check(settled[j] == display[j], "Older display samples stay immutable");
    settled = display.ToArray();
    filteredBuilder.Update(display, rebuild: false);
}
var stableDisplay = settled!;
double jitterRms = Math.Sqrt(stableDisplay.Skip(10).Take(stableDisplay.Length - 20).Average(p => p.Y * p.Y));
Check(jitterRms < 0.3, $"Small pointer ripples are suppressed (RMS {jitterRms:F4})");
for (int i = 0; i < jitter.Count; i++)
{
    double dx = stableDisplay[i].X - jitter[i].X, dy = stableDisplay[i].Y - jitter[i].Y;
    Check(Math.Sqrt(dx * dx + dy * dy) <= 1.501, "Display movement stays within 1.5 DIP plus float precision");
    Check(jitter[i] == new Point(i * 3, i % 2 == 0 ? 0.9 : -0.9), "OCR points stay unmodified");
}
var rebuilt = smoother.Update(jitter, rebuild: true, straightClosingEdge: false);
Check(stableDisplay.SequenceEqual(rebuilt), "Completion does not change the settled filtered outline");
filteredBuilder.Update(rebuilt, rebuild: true);
foreach (var curve in filteredBuilder.Curves)
    for (int i = 0; i <= 20; i++)
    {
        var p = Evaluate(curve, i / 20d);
        if (p.X > 30 && p.X < 2967)
            Check(Math.Abs(p.Y) < 0.5, "Fitted stroke retains the smoothed straight contour");
    }
smoother.Reset();
var turn = new List<Point>();
for (int i = 0; i <= 40; i++) turn.Add(new Point(i * 3, 0));
for (int i = 1; i <= 40; i++) turn.Add(new Point(120, i * 3));
var cornerDisplay = smoother.Update(turn, rebuild: false, straightClosingEdge: false);
Check(cornerDisplay[40] == turn[40], "Deliberate corner is preserved");
turn.Add(turn[0]);
cornerDisplay = smoother.Update(turn, rebuild: true, straightClosingEdge: true);
Check(cornerDisplay[^2] == turn[^2] && cornerDisplay[^1] == turn[^1], "Automatic closure endpoints stay exact");
filteredBuilder.Update(cornerDisplay, rebuild: true, straightClosingEdge: true);
foreach (var i in Enumerable.Range(0, 81))
    Check(SegmentDistance(Evaluate(filteredBuilder.Curves.Last(), i / 80d), turn[^2], turn[^1]) < 1e-4, "Filtered closing edge stays straight");
smoother.Reset();
Check(smoother.Update(Array.Empty<Point>(), false, false).Count == 0, "Filter reset clears samples");
Console.WriteLine($"PASS: spatial smoothing, RMS {jitterRms:F4} DIP, stable prefix, completion, exact endpoint, corners, closure and unchanged OCR polygon");

filteredBuilder.Reset();
var noisyLaps = new List<Point>();
GlowCurveBuilder.Curve[]? sealedPrefix = null;
for (int i = 0; i <= 12000; i++)
{
    double angle = i * Math.PI * 2 / 200;
    double noise = i % 2 == 0 ? 0.8 : -0.8;
    noisyLaps.Add(new Point(cx + (rx + noise) * Math.Cos(angle), cy + (ry + noise) * Math.Sin(angle)));
    if (i % 17 != 0 && i != 12000) continue;
    var display = smoother.Update(noisyLaps, false, false);
    filteredBuilder.Update(display, false);
    if (i > 320 && sealedPrefix is null) sealedPrefix = filteredBuilder.Curves.Take(2).ToArray();
}
Check(sealedPrefix!.SequenceEqual(filteredBuilder.Curves.Take(2)), "Long smoothed trails keep sealed curves stable");
Check(filteredBuilder.Count > 256, "Smoothed trails have no fixed curve-count cap");
double smoothRadialError = 0;
previous = null;
foreach (var curve in filteredBuilder.Curves)
{
    for (int i = 0; i <= 40; i++)
    {
        var p = Evaluate(curve, i / 40d);
        double radius = Math.Sqrt(Math.Pow((p.X - cx) / rx, 2) + Math.Pow((p.Y - cy) / ry, 2));
        smoothRadialError = Math.Max(smoothRadialError, Math.Abs(radius - 1) * rx);
    }
    if (previous is { } before)
    {
        Check(before.End == curve.Start, "Smoothed long curves stay connected");
        double ax = before.End.X - before.Control2.X, ay = before.End.Y - before.Control2.Y;
        double bx = curve.Control1.X - curve.Start.X, by = curve.Control1.Y - curve.Start.Y;
        double length = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
        if (length > 1e-10) Check((ax * bx + ay * by) / length > 0.999, "Smoothed long curves keep tangent continuity");
    }
    previous = curve;
}
Check(smoothRadialError < 2, $"Smoothed 60 laps preserve the contour within 2 DIP (actual {smoothRadialError:F4})");
Console.WriteLine($"PASS: 60 noisy laps, {filteredBuilder.Count} curves, tangent continuity and max radial error {smoothRadialError:F4} DIP");

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
