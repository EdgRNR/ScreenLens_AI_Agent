using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Numerics;
using Windows.UI;

namespace ScreenLens.WinUI.Views.Capture;

internal sealed partial class FreeformGlowTrail
{
    // Finer colored halo samples soften contour bands. Keep the faint shadow
    // small so this refinement does not multiply all completed stroke resources.
    private const int HaloLayerCount = 16;
    private const int ShadowLayerCount = 8;
    private readonly Path[] _haloLayers = new Path[HaloLayerCount];
    private readonly Path[] _shadowLayers = new Path[ShadowLayerCount];
    private Path? _highlight;
    private readonly PathFigure[] _depthFigures = new PathFigure[HaloLayerCount + ShadowLayerCount + 1];
    private readonly PolyBezierSegment[] _depthCurves = new PolyBezierSegment[HaloLayerCount + ShadowLayerCount + 1];
    private readonly PointBuffer[,] _depthBuffers = new PointBuffer[HaloLayerCount + ShadowLayerCount + 1, 2];
    private int _depthCount;
    private readonly Canvas _depthHost = new() { IsHitTestVisible = false };
    private readonly Canvas _highlightHost = new() { IsHitTestVisible = false };
    private Visual? _depthVisual;
    private Visual? _highlightVisual;
    private ScalarKeyFrameAnimation? _depthReveal;
    private bool _finishedDepthVisible;

    // WinUI geometries have one parent. Reuse the fitted samples in a fixed set
    // of path buffers, updating these layers only after drawing has finished.
    // Two groups fade in once through the compositor without per-frame updates.
    private void CreateFinishedDepth()
    {
        var shadowBrush = new SolidColorBrush(Color.FromArgb(255, 18, 14, 34));
        CreateGaussianLayers(_shadowLayers, shadowBrush, 3, 3.4, 0.22);
        CreateGaussianLayers(_haloLayers, _brush, 1.5, 5, 0.3);
        // Shadows sit behind the colored halo, and both sit behind the trail.
        foreach (var layer in _shadowLayers) _depthHost.Children.Add(layer);
        foreach (var layer in _haloLayers) _depthHost.Children.Add(layer);
        _host.Children.Insert(0, _depthHost);
        _highlight = CreateDepthPath(new SolidColorBrush(Color.FromArgb(255, 255, 248, 238)), 0.8, 0.22);
        _highlightHost.Children.Add(_highlight);
        _host.Children.Add(_highlightHost);
        _depthVisual = ElementCompositionPreview.GetElementVisual(_depthHost);
        _highlightVisual = ElementCompositionPreview.GetElementVisual(_highlightHost);
        _depthVisual.Opacity = _highlightVisual.Opacity = 0;
        var compositor = _depthVisual.Compositor;
        _depthReveal = compositor.CreateScalarKeyFrameAnimation();
        _depthReveal.Duration = TimeSpan.FromMilliseconds(280);
        _depthReveal.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        _depthReveal.InsertKeyFrame(0, 0);
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));
        _depthReveal.InsertKeyFrame(1, 1, easing);
    }

    private void CreateGaussianLayers(Path[] layers, Brush brush, double spacing, double sigma, double strength)
    {
        double coverage = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            double width = 26 - i * spacing;
            double radius = (width - 1.5) / 2;
            double target = strength * Math.Exp(-radius * radius / (2 * sigma * sigma));
            layers[i] = CreateDepthPath(brush, width, (target - coverage) / (1 - coverage));
            coverage = target;
        }
    }

    private Path CreateDepthPath(Brush brush, double width, double opacity)
    {
        int index = _depthCount++;
        var curve = new PolyBezierSegment();
        var figure = new PathFigure { IsFilled = false, IsClosed = false };
        figure.Segments.Add(curve);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _depthFigures[index] = figure;
        _depthCurves[index] = curve;
        _depthBuffers[index, 0] = new PointBuffer(curve.Points);
        _depthBuffers[index, 1] = new PointBuffer(new PointCollection());
        return new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            Stroke = brush,
            StrokeThickness = width,
            Opacity = opacity,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
    }

    private void UpdateFinishedDepthGeometry()
    {
        for (int i = 0; i < _depthCount; i++)
        {
            var buffer = _depthBuffers[i, _bufferIndex];
            buffer.Replace(_samples, _curveBuilder.Count * 3);
            _depthFigures[i].StartPoint = _figures[0].StartPoint;
            _depthCurves[i].Points = buffer.Points;
        }
    }

    private void ClearFinishedDepthGeometry()
    {
        foreach (var buffer in _depthBuffers) buffer?.Points.Clear();
    }

    private void UpdateFinishedDepthWidth()
    {
        for (int i = 0; i < HaloLayerCount; i++)
            _haloLayers[i].StrokeThickness = (26 - i * 1.5) * _widthScale;
        for (int i = 0; i < ShadowLayerCount; i++)
            _shadowLayers[i].StrokeThickness = (26 - i * 3) * _widthScale;
        if (_highlight is not null) _highlight.StrokeThickness = 0.8 * _widthScale;
        PositionFinishedDepth(Canvas.GetLeft(_layers[0]), Canvas.GetTop(_layers[0]));
    }

    private void PositionFinishedDepth(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        for (int i = 0; i < HaloLayerCount; i++)
            Place(_haloLayers[i], x, y);
        for (int i = 0; i < ShadowLayerCount; i++)
            Place(_shadowLayers[i], x + 0.7 * _widthScale, y + 1.6 * _widthScale);
        if (_highlight is not null) Place(_highlight, x, y - 0.5 * _widthScale);

        static void Place(Path path, double left, double top)
        {
            Canvas.SetLeft(path, left);
            Canvas.SetTop(path, top);
        }
    }

    private void SetFinishedDepthVisible(bool visible)
    {
        // Toolbar/size updates can reapply a completed selection. Do not restart
        // its reveal or introduce an opacity jump on those repeated updates.
        if (_finishedDepthVisible == visible) return;
        _finishedDepthVisible = visible;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var layer in _haloLayers) if (layer is not null) layer.Visibility = visibility;
        foreach (var layer in _shadowLayers) if (layer is not null) layer.Visibility = visibility;
        if (_highlight is not null) _highlight.Visibility = visibility;
        Reveal(_depthVisual);
        Reveal(_highlightVisual);

        void Reveal(Visual? visual)
        {
            if (visual is null) return;
            visual.StopAnimation("Opacity");
            visual.Opacity = 0;
            if (visible && _depthReveal is not null)
                visual.StartAnimation("Opacity", _depthReveal);
        }
    }

    private void DisposeFinishedDepth()
    {
        _depthVisual?.StopAnimation("Opacity");
        _highlightVisual?.StopAnimation("Opacity");
        foreach (var layer in _haloLayers) Release(layer);
        foreach (var layer in _shadowLayers) Release(layer);
        Release(_highlight);
        foreach (var buffer in _depthBuffers) buffer?.Dispose();
        _depthHost.Children.Clear();
        _highlightHost.Children.Clear();
        _host.Children.Remove(_depthHost);
        _host.Children.Remove(_highlightHost);
        _depthReveal?.Dispose();
        _depthReveal = null;
        // Element visuals are owned by XAML; only release our references.
        _depthVisual = _highlightVisual = null;

        void Release(Path? path)
        {
            if (path is null) return;
            if (path.Data is PathGeometry geometry) geometry.Figures.Clear();
            path.Data = null;
            path.Stroke = null;
        }
    }
}
