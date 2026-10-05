using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Capture;

/// <summary>
/// Removes small pointer ripples from the display trail, without changing the
/// OCR polygon. Spatial filtering adds no time delay to the pointer endpoint.
/// Only a bounded tail can change as input arrives; older samples are cached.
/// </summary>
internal sealed class GlowStrokeSmoother
{
    internal const int Lookahead = 32;
    private const double Radius = 12;
    private const double SigmaSquared = 4 * 4;
    private const double MaxShift = 1.5;
    private readonly List<Point> _display = new();
    private IReadOnlyList<Point>? _source;
    private int _lastCount;
    private bool _closing;

    internal IReadOnlyList<Point> Update(IReadOnlyList<Point> points, bool rebuild, bool straightClosingEdge)
    {
        if (rebuild || !ReferenceEquals(points, _source) || points.Count < _lastCount || _closing != straightClosingEdge)
            Reset();
        if (points.Count == _lastCount) return _display;
        _source = points;
        _closing = straightClosingEdge;
        int from = Math.Max(0, _lastCount - 1 - Lookahead);
        while (_display.Count < points.Count) _display.Add(default);
        int end = points.Count - 1 - (straightClosingEdge && points.Count >= 3 ? 1 : 0);
        for (int i = from; i < points.Count; i++)
        {
            // The live pointer, starting point and both ends of the automatic
            // closing segment remain exact. Sparse strokes still respond at once.
            if (i == 0 || i >= end) { _display[i] = points[i]; continue; }
            _display[i] = Smooth(points, i, end);
        }
        _lastCount = points.Count;
        return _display;
    }

    internal void Reset()
    {
        _display.Clear();
        _source = null;
        _lastCount = 0;
        _closing = false;
    }

    private static Point Smooth(IReadOnlyList<Point> points, int index, int end)
    {
        var center = points[index];
        double x = center.X, y = center.Y, weight = 1;
        var before = Accumulate(-1);
        var after = Accumulate(1);
        double ax = center.X - before.X, ay = center.Y - before.Y;
        double bx = after.X - center.X, by = after.Y - center.Y;
        double lengths = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
        // Evaluate the turn over a spatial neighborhood, rather than mistaking
        // one jittery adjacent sample for a deliberate sharp corner.
        if (lengths > 1e-10 && ax * bx + ay * by < 0.5 * lengths)
            return center;
        double dx = x / weight - center.X, dy = y / weight - center.Y;
        double shift = Math.Sqrt(dx * dx + dy * dy);
        double scale = shift > MaxShift ? MaxShift / shift : 1;
        return new Point(center.X + dx * scale, center.Y + dy * scale);

        Point Accumulate(int direction)
        {
            double traveled = 0;
            var previous = center;
            var farthest = center;
            for (int n = 1; n <= Lookahead; n++)
            {
                int j = index + n * direction;
                if (j < 0 || j > end) break;
                var point = points[j];
                double sx = point.X - previous.X, sy = point.Y - previous.Y;
                traveled += Math.Sqrt(sx * sx + sy * sy);
                previous = point;
                if (traveled > Radius) break;
                farthest = point;
                double w = Math.Exp(-traveled * traveled / (2 * SigmaSquared));
                x += point.X * w; y += point.Y * w; weight += w;
            }
            return farthest;
        }
    }
}
