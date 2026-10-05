using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;

namespace ScreenLens.WinUI.Views.Capture;

/// <summary>
/// Continuous strokes share fitted curves and one color gradient. Closely
/// spaced opacity layers approximate a Gaussian cross-section without a
/// desktop-sized blur surface. All drawing resources are reused.
/// </summary>
internal sealed partial class FreeformGlowTrail : IDisposable
{
    private const int LayerCount = 12;
    private const int ColorCount = 9;
    private const double CycleSeconds = 9;
    private readonly Canvas _host;
    private readonly ContainerVisual _root;
    private readonly Path[] _layers = new Path[LayerCount];
    private readonly PathFigure[] _figures = new PathFigure[LayerCount];
    private readonly PolyBezierSegment[] _curves = new PolyBezierSegment[LayerCount];

    private readonly LinearGradientBrush _brush = new()
    {
        StartPoint = new Point(0, 1),
        EndPoint = new Point(1, 0),
    };
    private readonly SpriteVisual _head;
    private TipTransition? _tip;
    private Point _headPosition;
    private readonly ScalarKeyFrameAnimation _fade;
    private readonly DispatcherQueueTimer _colorTimer;
    private readonly Stopwatch _elapsed = new();
    private NativePoint[] _samples = Array.Empty<NativePoint>();
    private readonly GlowStrokeSmoother _strokeSmoother = new();
    private readonly GlowCurveBuilder _curveBuilder = new(errorTolerance: 0.5, lookahead: GlowStrokeSmoother.Lookahead);
    private readonly PointBuffer[,] _buffers = new PointBuffer[LayerCount, 2];
    private int _bufferIndex;
    private IReadOnlyList<Point>? _points;
    private bool _rendering;
    private bool _dirty;
    private bool _visible;
    private bool _disposed;
    private float _phase;
    private double _widthScale = 1;
    internal event Action<Exception>? Failed;

    internal FreeformGlowTrail(FrameworkElement host)
    {
        _host = (Canvas)host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _root = compositor.CreateContainerVisual();
        _root.IsVisible = false;
        _fade = compositor.CreateScalarKeyFrameAnimation();
        _fade.InsertKeyFrame(1, 0);
        _fade.Duration = TimeSpan.FromMilliseconds(180);
        _fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        _colorTimer = host.DispatcherQueue.CreateTimer();
        _colorTimer.Interval = TimeSpan.FromMilliseconds(33);
        _colorTimer.IsRepeating = true;
        _colorTimer.Tick += OnColorTick;
        try
        {
            for (int i = 0; i < ColorCount; i++)
                _brush.GradientStops.Add(new GradientStop { Offset = (double)i / (ColorCount - 1) });
            Recolor();

            // Each layer is a complete joined path, rather than overlapping
            // round-ended segments. Gradual opacity avoids a hard halo/white core.
            double coverage = 0;
            for (int i = 0; i < LayerCount; i++)
            {
                double width = (LayerCount - i) * 1.5;
                double radius = (width - 0.75) * 0.5;
                double target = 0.94 * Math.Exp(-radius * radius / (2 * 2.1 * 2.1));
                var curve = new PolyBezierSegment();
                var figure = new PathFigure { IsFilled = false, IsClosed = false };
                figure.Segments.Add(curve);
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                _figures[i] = figure;
                _curves[i] = curve;
                var path = new Path
                {
                    Data = geometry,
                    Stretch = Stretch.None,
                    Stroke = _brush,
                    StrokeThickness = width,
                    Opacity = (target - coverage) / (1 - coverage),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                };
                coverage = target;
                _layers[i] = path;
                _host.Children.Add(path);
                _buffers[i, 0] = new PointBuffer(curve.Points);
                _buffers[i, 1] = new PointBuffer(new PointCollection());
            }
            CreateFinishedDepth();
            _tip = new TipTransition(_host);
            _head = compositor.CreateSpriteVisual();
            _head.Size = new Vector2(24);
            var headBrush = compositor.CreateRadialGradientBrush();
            headBrush.InterpolationSpace = CompositionColorSpace.Rgb;
            headBrush.CenterPoint = new Vector2(0.5f);
            headBrush.EllipseRadius = new Vector2(0.5f);
            headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(255, 255, 250, 227)));
            headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.18f, Color.FromArgb(250, 255, 237, 195)));
            headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.38f, Color.FromArgb(180, 255, 201, 117)));
            headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.7f, Color.FromArgb(40, 255, 184, 102)));
            headBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 184, 102)));
            _head.Brush = headBrush;
            _root.Children.InsertAtTop(_head);
            ElementCompositionPreview.SetElementChildVisual(host, _root);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update(IReadOnlyList<Point> points, bool drawing, Point headPosition)
    {
        if (_disposed) return;
        if (points.Count == 0) { Clear(); return; }
        _points = points;
        _dirty = true;
        if (!_visible)
        {
            foreach (var layer in _layers) layer.Visibility = Visibility.Visible;
            _visible = true;
        }
        _root.IsVisible = true;
        SetFinishedDepthVisible(_finishedDepthEnabled && !drawing && points.Count >= 4);
        MoveHead(headPosition);
        if (drawing)
        {
            if (!_rendering)
            {
                _head.StopAnimation("Opacity");
                _head.Opacity = 1;
                _phase = 0;
                _elapsed.Restart();
                _colorTimer.Start();
                CompositionTarget.Rendering += OnRendering;
                _rendering = true;
                RenderGeometry();
            }
        }
        else
        {
            bool wasDrawing = _rendering;
            StopRendering();
            RenderGeometry();
            if (wasDrawing)
            {
                _head.StartAnimation("Opacity", _fade);
                _tip?.Fade(_fade);
            }
            else
            {
                _head.StopAnimation("Opacity");
                _head.Opacity = 0;
                _tip?.Hide();
            }
        }
    }

    internal void MoveHead(Point point)
    {
        if (!_disposed)
        {
            _headPosition = point;
            float radius = (float)(12 * _widthScale);
            _head.Offset = new Vector3((float)point.X - radius, (float)point.Y - radius, 0);
            if (_rendering) _dirty = true;
        }
    }

    internal void SetWidth(double scale)
    {
        if (_disposed) return;
        scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2) : 1;
        if (scale == _widthScale) return;
        _widthScale = scale;
        for (int i = 0; i < LayerCount; i++)
            _layers[i].StrokeThickness = (LayerCount - i) * 1.5 * scale;
        _head.Size = new Vector2((float)(24 * scale));
        _tip?.SetWidth((float)scale);
        UpdateFinishedDepthWidth();
        MoveHead(_headPosition);
    }

    internal void Freeze()
    {
        if (_disposed) return;
        StopRendering();
        if (_dirty) RenderGeometry();
        _head.StopAnimation("Opacity");
        _head.Opacity = 0;
        _tip?.Hide();
    }

    internal void Clear()
    {
        if (_disposed || !_visible) return;
        // The caller may already have cleared the shared point list.
        StopRendering();
        _head.StopAnimation("Opacity");
        _head.Opacity = 0;
        _tip?.Hide();
        _points = null;
        _dirty = false;
        _visible = false;
        _root.IsVisible = false;
        foreach (var layer in _layers) layer.Visibility = Visibility.Collapsed;
        SetFinishedDepthVisible(false);
        ClearFinishedDepthGeometry();
        foreach (var curve in _curves) curve.Points.Clear();
        _curveBuilder.Reset();
        _strokeSmoother.Reset();
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _phase = CurrentPhase;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
        _colorTimer.Stop();
        _elapsed.Stop();
    }

    private void OnRendering(object? sender, object args)
    {
        if (_disposed || !_rendering) return;
        try { if (_dirty) RenderGeometry(); }
        catch (Exception ex)
        {
            StopRendering();
            Failed?.Invoke(ex);
        }
    }

    private float CurrentPhase => _rendering
        ? (float)(_elapsed.Elapsed.TotalSeconds % CycleSeconds / CycleSeconds * Math.PI * 2)
        : _phase;

    private void OnColorTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !_rendering) return;
        try { Recolor(); }
        catch (Exception ex)
        {
            StopRendering();
            Failed?.Invoke(ex);
        }
    }

    private void Recolor()
    {
        float phase = CurrentPhase;
        for (int i = 0; i < ColorCount; i++)
        {
            double angle = phase + i * Math.PI * 2 / (ColorCount - 1);
            var cool = Lerp(Color.FromArgb(255, 245, 213, 250), Color.FromArgb(255, 137, 196, 255), (Math.Sin(angle) + 1) * 0.5);
            _brush.GradientStops[i].Color = Lerp(Color.FromArgb(255, 255, 226, 190), cool, (Math.Sin(angle + 1.7) + 1) * 0.5);
        }
    }

    private void RenderGeometry()
    {
        _dirty = false;
        if (_points is null || _points.Count == 0) return;
        // Fit with a fixed visual error, not a fixed total segment count.
        // Completed chunks are cached while drawing; actual OCR points stay intact.
        bool closingLine = !_rendering && _points.Count >= 3
            && _points[^1] == _points[0] && _headPosition != _points[^1];
        var displayPoints = _strokeSmoother.Update(_points, rebuild: !_rendering, straightClosingEdge: closingLine);
        if (_curveBuilder.Update(displayPoints, rebuild: !_rendering, straightClosingEdge: closingLine))
        {
            int used = _curveBuilder.Count * 3;
            if (_samples.Length < used)
                _samples = new NativePoint[Math.Max(used, Math.Max(96, _samples.Length * 2))];
            // Leave room for the widest finished halo and its small shadow offset.
            double originX = _curveBuilder.Left - 40, originY = _curveBuilder.Top - 40;
            int sample = 0;
            foreach (var curve in _curveBuilder.Curves)
            {
                Write(curve.Control1);
                Write(curve.Control2);
                Write(curve.End);
            }
            _bufferIndex = 1 - _bufferIndex;
            for (int i = 0; i < LayerCount; i++)
            {
                var buffer = _buffers[i, _bufferIndex];
                buffer.Replace(_samples, used);
                _figures[i].StartPoint = new Point(_points[0].X - originX, _points[0].Y - originY);
                Canvas.SetLeft(_layers[i], originX);
                Canvas.SetTop(_layers[i], originY);
                _curves[i].Points = buffer.Points;
            }
            PositionFinishedDepth(originX, originY);

            void Write(Point point)
                => _samples[sample++] = new NativePoint { X = (float)(point.X - originX), Y = (float)(point.Y - originY) };
        }
        if (_rendering) _tip?.Update(displayPoints, _headPosition);
        else if (_finishedDepthEnabled) UpdateFinishedDepthGeometry();
    }

    private static Color Lerp(Color a, Color b, double amount)
        => Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * amount),
            (byte)Math.Round(a.G + (b.G - a.G) * amount),
            (byte)Math.Round(a.B + (b.B - a.B) * amount));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal float X, Y; }

    /// <summary>
    /// PointCollection projects IVector&lt;Point&gt; as IList&lt;Point&gt;, hiding its
    /// native ReplaceAll method. Use that ABI operation to notify XAML once
    /// per whole path, without allocating a new collection on every frame.
    /// Windows.Foundation.Point uses two floats at the WinRT ABI boundary.
    /// </summary>
    private sealed class PointBuffer : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReplaceAll(IntPtr vector, uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] NativePoint[] points);
        internal PointCollection Points { get; }
        private IntPtr _vector;
        private readonly ReplaceAll _replace;

        internal PointBuffer(PointCollection points)
        {
            Points = points;
            _vector = WinRT.MarshalInterface<IList<Point>>.FromManaged(points);
            // IUnknown (3), IInspectable (3), IVector methods (12).
            var table = Marshal.ReadIntPtr(_vector);
            _replace = Marshal.GetDelegateForFunctionPointer<ReplaceAll>(Marshal.ReadIntPtr(table, 17 * IntPtr.Size));
        }

        internal void Replace(NativePoint[] points, int count)
            => Marshal.ThrowExceptionForHR(_replace(_vector, (uint)count, points));

        public void Dispose()
        {
            if (_vector == IntPtr.Zero) return;
            WinRT.MarshalInterface<IList<Point>>.DisposeAbi(_vector);
            _vector = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        Failed = null;
        _colorTimer.Tick -= OnColorTick;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        DisposeFinishedDepth();
        foreach (var layer in _layers)
        {
            if (layer is null) continue;
            if (layer.Data is PathGeometry geometry) geometry.Figures.Clear();
            layer.Data = null;
            layer.Stroke = null;
            _host.Children.Remove(layer);
        }
        _brush.GradientStops.Clear();
        _curveBuilder.Reset();
        _strokeSmoother.Reset();
        _samples = Array.Empty<NativePoint>();
        foreach (var buffer in _buffers) buffer?.Dispose();
        _tip?.Dispose();
        _tip = null;
        _head?.Brush?.Dispose();
        _head?.Dispose();
        _fade.Dispose();
        _root.Dispose();
    }
}
