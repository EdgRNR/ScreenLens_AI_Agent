using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Capture;

/// <summary>
/// Fits cubic curves to the display trail with a fixed visual error.
/// Sealed chunks are immutable while drawing, so adding many laps neither
/// coarsens old curves nor refits the entire pointer history on every frame.
/// The original selection polygon is never modified.
/// </summary>
internal sealed class GlowCurveBuilder
{
    private const int ChunkSize = 128;
    private readonly double _errorSquared;
    private readonly int _lookahead;
    internal readonly record struct Curve(Point Start, Point Control1, Point Control2, Point End);
    private readonly List<Curve> _completed = new();
    private readonly List<Curve> _active = new();
    private readonly double[] _parameters;
    private IReadOnlyList<Point>? _points;
    private int _sealedEnd, _lastCount;
    private bool _straightClosingEdge;
    internal double Left { get; private set; }
    internal double Top { get; private set; }
    internal int Count => _completed.Count + _active.Count;
    internal GlowCurveBuilder(double errorTolerance = 0.35, int lookahead = 0)
    {
        if (!double.IsFinite(errorTolerance) || errorTolerance <= 0) throw new ArgumentOutOfRangeException(nameof(errorTolerance));
        if (lookahead < 0 || lookahead > ChunkSize) throw new ArgumentOutOfRangeException(nameof(lookahead));
        _errorSquared = errorTolerance * errorTolerance;
        _lookahead = lookahead;
        _parameters = new double[ChunkSize + lookahead + 1];
    }
    internal IEnumerable<Curve> Curves
    {
        get
        {
            foreach (var curve in _completed) yield return curve;
            foreach (var curve in _active) yield return curve;
        }
    }

    internal bool Update(IReadOnlyList<Point> points, bool rebuild, bool straightClosingEdge = false)
    {
        if (points.Count == 0) { Reset(); return true; }
        bool closing = straightClosingEdge && points.Count >= 3;
        if (rebuild || closing != _straightClosingEdge || !ReferenceEquals(points, _points) || points.Count < _lastCount) Reset();
        if (ReferenceEquals(points, _points) && points.Count == _lastCount) return false;
        _points = points;
        _straightClosingEdge = closing;
        if (_lastCount == 0) { Left = points[0].X; Top = points[0].Y; }
        for (int i = _lastCount; i < points.Count; i++)
        {
            Left = Math.Min(Left, points[i].X);
            Top = Math.Min(Top, points[i].Y);
        }
        // The extra tail allows spatially filtered samples to settle before
        // sealing a chunk. It does not delay rendering the live endpoint.
        int last = points.Count - (closing ? 2 : 1);
        while (last > _sealedEnd + ChunkSize + _lookahead)
        {
            int end = _sealedEnd + ChunkSize;
            Fit(_sealedEnd, end, Forward(_sealedEnd), Backward(end), _completed);
            _sealedEnd = end;
        }
        _active.Clear();
        if (last > _sealedEnd)
            Fit(_sealedEnd, last, Forward(_sealedEnd), Unit(Subtract(points[last - 1], points[last])), _active);
        if (closing)
        {
            var start = points[last]; var end = points[last + 1];
            var delta = Subtract(end, start);
            _active.Add(new Curve(start, Add(start, Scale(delta, 1d / 3)), Add(start, Scale(delta, 2d / 3)), end));
        }
        _lastCount = points.Count;
        return true;
    }

    internal void Reset()
    {
        _points = null;
        _sealedEnd = _lastCount = 0;
        _straightClosingEdge = false;
        _completed.Clear();
        _active.Clear();
    }

    private Point Forward(int index)
    {
        var points = _points!;
        if (index == 0) return Unit(Subtract(points[1], points[0]));
        if (IsCorner(index)) return Unit(Subtract(points[index + 1], points[index]));
        return Unit(Subtract(points[index + 1], points[index - 1]));
    }

    private Point Backward(int index)
        => IsCorner(index) ? Unit(Subtract(_points![index - 1], _points[index])) : Negate(Forward(index));

    private bool IsCorner(int index)
    {
        var points = _points!;
        var a = Subtract(points[index], points[index - 1]);
        var b = Subtract(points[index + 1], points[index]);
        double product = Dot(a, a) * Dot(b, b);
        return product > 1e-12 && Dot(a, b) < 0.5 * Math.Sqrt(product);
    }

    private void Fit(int first, int last, Point leftTangent, Point rightTangent, List<Curve> output)
    {
        var points = _points!;
        if (last == first + 1)
        {
            double distance = Distance(points[first], points[last]) / 3;
            output.Add(new Curve(points[first], Add(points[first], Scale(leftTangent, distance)),
                Add(points[last], Scale(rightTangent, distance)), points[last]));
            return;
        }
        // Sparse pointer input can otherwise fit a rounded corner with zero
        // error at the few recorded vertices. Preserve sharp turns up front.
        for (int i = first + 1; i < last; i++)
        {
            if (!IsCorner(i)) continue;
            Fit(first, i, leftTangent, Unit(Subtract(points[i - 1], points[i])), output);
            Fit(i, last, Unit(Subtract(points[i + 1], points[i])), rightTangent, output);
            return;
        }
        int count = last - first + 1;
        _parameters[0] = 0;
        for (int i = 1; i < count; i++)
            _parameters[i] = _parameters[i - 1] + Distance(points[first + i], points[first + i - 1]);
        double length = _parameters[count - 1];
        for (int i = 1; i < count; i++)
            _parameters[i] = length > 0 ? _parameters[i] / length : (double)i / (count - 1);
        var curve = Generate(first, last, leftTangent, rightTangent, length);
        var (error, split) = Measure(curve, first, last);
        if (error <= _errorSquared) { output.Add(curve); return; }
        if (error <= _errorSquared * 16)
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                bool ordered = true;
                for (int i = 1; i < count - 1; i++)
                {
                    double u = Refine(curve, points[first + i], _parameters[i]);
                    if (u <= _parameters[i - 1] || u >= _parameters[i + 1]) { ordered = false; break; }
                    _parameters[i] = u;
                }
                if (!ordered) break;
                curve = Generate(first, last, leftTangent, rightTangent, length);
                (error, split) = Measure(curve, first, last);
                if (error <= _errorSquared) { output.Add(curve); return; }
            }
        }
        // Both children share a tangent at a smooth join; actual sharp turns
        // retain their independent incoming/outgoing directions.
        var incoming = Unit(Subtract(points[split], points[split - 1]));
        var outgoing = Unit(Subtract(points[split + 1], points[split]));
        var center = Unit(Subtract(points[split + 1], points[split - 1]));
        bool corner = Dot(incoming, outgoing) < 0.5;
        Fit(first, split, leftTangent, Negate(corner ? incoming : center), output);
        Fit(split, last, corner ? outgoing : center, rightTangent, output);
    }

    private Curve Generate(int first, int last, Point left, Point right, double length)
    {
        var points = _points!;
        var start = points[first]; var end = points[last];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i <= last - first; i++)
        {
            double u = _parameters[i], v = 1 - u;
            double b0 = v * v * v, b1 = 3 * u * v * v, b2 = 3 * u * u * v, b3 = u * u * u;
            var a0 = Scale(left, b1); var a1 = Scale(right, b2);
            var delta = Subtract(points[first + i], Add(Scale(start, b0 + b1), Scale(end, b2 + b3)));
            c00 += Dot(a0, a0); c01 += Dot(a0, a1); c11 += Dot(a1, a1);
            x0 += Dot(a0, delta); x1 += Dot(a1, delta);
        }
        double determinant = c00 * c11 - c01 * c01;
        double alpha0 = Math.Abs(determinant) > 1e-12 ? (x0 * c11 - x1 * c01) / determinant : 0;
        double alpha1 = Math.Abs(determinant) > 1e-12 ? (c00 * x1 - c01 * x0) / determinant : 0;
        double chord = Distance(start, end);
        if (alpha0 < 1e-6 * chord || alpha1 < 1e-6 * chord || alpha0 > length * 2 || alpha1 > length * 2)
            alpha0 = alpha1 = chord / 3;
        return new Curve(start, Add(start, Scale(left, alpha0)), Add(end, Scale(right, alpha1)), end);
    }

    private (double Error, int Split) Measure(Curve curve, int first, int last)
    {
        double maximum = 0;
        int split = (first + last) / 2;
        for (int i = first + 1; i < last; i++)
        {
            var delta = Subtract(Evaluate(curve, _parameters[i - first]), _points![i]);
            double error = Dot(delta, delta);
            if (error > maximum) { maximum = error; split = i; }
        }
        return (maximum, split);
    }

    private static double Refine(Curve curve, Point point, double u)
    {
        double v = 1 - u;
        var delta = Subtract(Evaluate(curve, u), point);
        var derivative = Scale(Add(Add(Scale(Subtract(curve.Control1, curve.Start), v * v),
            Scale(Subtract(curve.Control2, curve.Control1), 2 * u * v)), Scale(Subtract(curve.End, curve.Control2), u * u)), 3);
        var second = Scale(Add(Scale(Add(Subtract(curve.Control2, Scale(curve.Control1, 2)), curve.Start), v),
            Scale(Add(Subtract(curve.End, Scale(curve.Control2, 2)), curve.Control1), u)), 6);
        double denominator = Dot(derivative, derivative) + Dot(delta, second);
        return Math.Abs(denominator) > 1e-12 ? Math.Clamp(u - Dot(delta, derivative) / denominator, 0, 1) : u;
    }

    private static Point Evaluate(Curve curve, double u)
    {
        double v = 1 - u;
        return Add(Add(Scale(curve.Start, v * v * v), Scale(curve.Control1, 3 * u * v * v)),
            Add(Scale(curve.Control2, 3 * u * u * v), Scale(curve.End, u * u * u)));
    }

    private static Point Add(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
    private static Point Subtract(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
    private static Point Scale(Point a, double factor) => new(a.X * factor, a.Y * factor);
    private static Point Negate(Point a) => new(-a.X, -a.Y);
    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
    private static double Distance(Point a, Point b) { var d = Subtract(a, b); return Math.Sqrt(Dot(d, d)); }
    private static Point Unit(Point a) { double length = Math.Sqrt(Dot(a, a)); return length > 1e-12 ? Scale(a, 1 / length) : new Point(1, 0); }
}
