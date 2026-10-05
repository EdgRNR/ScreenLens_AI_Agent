using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using Windows.UI;

namespace ScreenLens.WinUI.Views.Capture;

internal sealed partial class FreeformGlowTrail
{
    private const int DepthLayerCount = 8;
    private readonly Path[] _haloLayers = new Path[DepthLayerCount];
    private readonly Path[] _shadowLayers = new Path[DepthLayerCount];
    private Path? _highlight;
    private readonly PathFigure[] _depthFigures = new PathFigure[DepthLayerCount * 2 + 1];
    private readonly PolyBezierSegment[] _depthCurves = new PolyBezierSegment[DepthLayerCount * 2 + 1];
    private readonly PointBuffer[,] _depthBuffers = new PointBuffer[DepthLayerCount * 2 + 1, 2];
    private int _depthCount;

    // WinUI geometries have one parent. Reuse the fitted samples in a fixed set
    // of path buffers, updating these layers only after drawing has finished.
    // There are no blur textures or ongoing rendering callbacks.
    private void CreateFinishedDepth()
    {
        var shadowBrush = new SolidColorBrush(Color.FromArgb(255, 18, 14, 34));
        double haloCoverage = 0, shadowCoverage = 0;
        for (int i = 0; i < DepthLayerCount; i++)
        {
            double width = 26 - i * 3;
            double radius = (width - 1.5) / 2;
            double haloTarget = 0.3 * Math.Exp(-radius * radius / (2 * 5 * 5));
            double shadowTarget = 0.22 * Math.Exp(-radius * radius / (2 * 3.4 * 3.4));
            _shadowLayers[i] = CreateDepthPath(shadowBrush, width,
                (shadowTarget - shadowCoverage) / (1 - shadowCoverage));
            _haloLayers[i] = CreateDepthPath(_brush, width,
                (haloTarget - haloCoverage) / (1 - haloCoverage));
            shadowCoverage = shadowTarget;
            haloCoverage = haloTarget;
        }
        // Shadows sit behind the colored halo, and both sit behind the trail.
        for (int i = 0; i < DepthLayerCount; i++)
            _host.Children.Insert(i, _shadowLayers[i]);
        for (int i = 0; i < DepthLayerCount; i++)
            _host.Children.Insert(DepthLayerCount + i, _haloLayers[i]);
        _highlight = CreateDepthPath(new SolidColorBrush(Color.FromArgb(255, 255, 248, 238)), 0.8, 0.22);
        _host.Children.Add(_highlight);
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
        for (int i = 0; i < DepthLayerCount; i++)
        {
            _haloLayers[i].StrokeThickness = (26 - i * 3) * _widthScale;
            _shadowLayers[i].StrokeThickness = (26 - i * 3) * _widthScale;
        }
        if (_highlight is not null) _highlight.StrokeThickness = 0.8 * _widthScale;
        PositionFinishedDepth(Canvas.GetLeft(_layers[0]), Canvas.GetTop(_layers[0]));
    }

    private void PositionFinishedDepth(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        for (int i = 0; i < DepthLayerCount; i++)
        {
            Place(_haloLayers[i], x, y);
            Place(_shadowLayers[i], x + 0.7 * _widthScale, y + 1.6 * _widthScale);
        }
        if (_highlight is not null) Place(_highlight, x, y - 0.5 * _widthScale);

        static void Place(Path path, double left, double top)
        {
            Canvas.SetLeft(path, left);
            Canvas.SetTop(path, top);
        }
    }

    private void SetFinishedDepthVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var layer in _haloLayers) if (layer is not null) layer.Visibility = visibility;
        foreach (var layer in _shadowLayers) if (layer is not null) layer.Visibility = visibility;
        if (_highlight is not null) _highlight.Visibility = visibility;
    }

    private void DisposeFinishedDepth()
    {
        foreach (var layer in _haloLayers) Release(layer);
        foreach (var layer in _shadowLayers) Release(layer);
        Release(_highlight);
        foreach (var buffer in _depthBuffers) buffer?.Dispose();

        void Release(Path? path)
        {
            if (path is null) return;
            if (path.Data is PathGeometry geometry) geometry.Figures.Clear();
            path.Data = null;
            path.Stroke = null;
            _host.Children.Remove(path);
        }
    }
}
