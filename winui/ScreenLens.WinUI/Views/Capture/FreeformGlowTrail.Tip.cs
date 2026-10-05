using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace ScreenLens.WinUI.Views.Capture;

internal sealed partial class FreeformGlowTrail
{
    /// <summary>
    /// A short, curved ribbon widens the trail into its warm pointer glow.
    /// Samples cover only the last 44 DIP, independent of total stroke length.
    /// Transparent entry and the same Gaussian layers keep the join soft.
    /// </summary>
    private sealed class TipTransition : IDisposable
    {
        private const float Length = 44;
        private const float Spacing = 2;
        private const int SampleCount = (int)(Length / Spacing) + 1;
        private readonly Canvas _parent;
        private readonly Canvas _host = new() { IsHitTestVisible = false };
        private readonly Visual _visual;
        private readonly Polygon[] _layers = new Polygon[LayerCount];
        private readonly PointBuffer[,] _buffers = new PointBuffer[LayerCount, 2];
        private readonly Vector2[] _centers = new Vector2[SampleCount];
        private readonly Vector2[] _normals = new Vector2[SampleCount];
        private readonly float[] _distances = new float[SampleCount];
        private readonly NativePoint[] _outline = new NativePoint[SampleCount * 2];
        private readonly LinearGradientBrush[] _brushes = new LinearGradientBrush[LayerCount];
        private int _bufferIndex;
        private float _widthScale = 1;
        private bool _disposed;

        internal TipTransition(Canvas parent)
        {
            _parent = parent;
            _visual = ElementCompositionPreview.GetElementVisual(_host);
            _visual.Opacity = 0;
            try
            {
                double coverage = 0;
                for (int i = 0; i < LayerCount; i++)
                {
                    var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
                    brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, 255, 231, 201) });
                    brush.GradientStops.Add(new GradientStop { Offset = 0.25, Color = Color.FromArgb(24, 255, 231, 201) });
                    brush.GradientStops.Add(new GradientStop { Offset = 0.65, Color = Color.FromArgb(165, 255, 228, 185) });
                    brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(250, 255, 236, 200) });
                    _brushes[i] = brush;
                    double radius = ((LayerCount - i) * 1.5 - 0.75) * 0.5;
                    double target = 0.94 * Math.Exp(-radius * radius / (2 * 2.1 * 2.1));
                    var polygon = new Polygon
                    {
                        Fill = brush,
                        Stretch = Stretch.None,
                        Opacity = (target - coverage) / (1 - coverage),
                        IsHitTestVisible = false,
                    };
                    coverage = target;
                    _layers[i] = polygon;
                    _host.Children.Add(polygon);
                    _buffers[i, 0] = new PointBuffer(polygon.Points);
                    _buffers[i, 1] = new PointBuffer(new PointCollection());
                }
                parent.Children.Add(_host);
            }
            catch { Dispose(); throw; }
        }

        internal void Update(IReadOnlyList<Point> points, Point head)
        {
            if (_disposed) return;
            _centers[0] = new Vector2((float)head.X, (float)head.Y);
            _distances[0] = 0;
            int count = 1;
            float traversed = 0;
            float spacing = Spacing * _widthScale;
            var from = _centers[0];
            for (int i = points.Count - 1; i >= 0 && count < SampleCount; i--)
            {
                var to = new Vector2((float)points[i].X, (float)points[i].Y);
                float segment = Vector2.Distance(from, to);
                if (segment < 0.001f) continue;
                while (count < SampleCount && count * spacing <= traversed + segment)
                {
                    float distance = count * spacing;
                    _centers[count] = Vector2.Lerp(from, to, Math.Clamp((distance - traversed) / segment, 0, 1));
                    _distances[count++] = distance;
                }
                traversed += segment;
                from = to;
            }
            // Short initial strokes still bridge to their first point.
            if (count < SampleCount && traversed > _distances[count - 1] + 0.01f)
            {
                _centers[count] = from;
                _distances[count++] = traversed;
            }
            if (count < 2) { Hide(); return; }

            var origin = _centers[0];
            var extent = origin;
            for (int i = 1; i < count; i++)
            {
                origin = Vector2.Min(origin, _centers[i]);
                extent = Vector2.Max(extent, _centers[i]);
            }
            float padding = 20 * _widthScale;
            origin -= new Vector2(padding);
            _host.Width = extent.X - origin.X + padding;
            _host.Height = extent.Y - origin.Y + padding;
            Canvas.SetLeft(_host, origin.X);
            Canvas.SetTop(_host, origin.Y);
            var entry = _centers[count - 1];
            // A tight loop can end almost where it entered the tip region.
            // Keep a usable color axis rather than a degenerate gradient.
            if (Vector2.DistanceSquared(entry, _centers[0]) < 1)
                for (int i = 1; i < count; i++)
                    if (Vector2.DistanceSquared(_centers[i], _centers[0]) > Vector2.DistanceSquared(entry, _centers[0]))
                        entry = _centers[i];
            for (int i = 0; i < count; i++)
            {
                var tangent = _centers[Math.Min(count - 1, i + 1)] - _centers[Math.Max(0, i - 1)];
                if (tangent.LengthSquared() < 0.0001f)
                    tangent = i > 0 ? _centers[i] - _centers[i - 1] : _centers[1] - _centers[0];
                _normals[i] = tangent.LengthSquared() < 0.0001f
                    ? (i > 0 ? _normals[i - 1] : Vector2.UnitY)
                    : Vector2.Normalize(new Vector2(-tangent.Y, tangent.X));
            }
            _bufferIndex = 1 - _bufferIndex;
            for (int layer = 0; layer < LayerCount; layer++)
            {
                float radius = (LayerCount - layer) * 0.75f * _widthScale;
                var minimum = new Vector2(float.PositiveInfinity);
                for (int i = 0; i < count; i++)
                {
                    float progress = Math.Clamp(1 - _distances[i] / (Length * _widthScale), 0, 1);
                    float smooth = progress * progress * (3 - 2 * progress);
                    var offset = _normals[i] * radius * (1 + 0.9f * smooth);
                    var center = _centers[i] - origin;
                    _outline[i] = new NativePoint { X = center.X + offset.X, Y = center.Y + offset.Y };
                    _outline[count * 2 - 1 - i] = new NativePoint { X = center.X - offset.X, Y = center.Y - offset.Y };
                    minimum = Vector2.Min(minimum, Vector2.Min(center + offset, center - offset));
                }
                // Absolute brush coordinates start at each polygon's fill
                // bounds, while its points remain in the canvas coordinate space.
                var colorOrigin = origin + minimum;
                _brushes[layer].StartPoint = new Point(entry.X - colorOrigin.X, entry.Y - colorOrigin.Y);
                _brushes[layer].EndPoint = new Point(_centers[0].X - colorOrigin.X, _centers[0].Y - colorOrigin.Y);
                var buffer = _buffers[layer, _bufferIndex];
                buffer.Replace(_outline, count * 2);
                _layers[layer].Points = buffer.Points;
            }
            _visual.StopAnimation("Opacity");
            _visual.Opacity = 1;
        }

        internal void Fade(ScalarKeyFrameAnimation fade)
        {
            if (!_disposed) _visual.StartAnimation("Opacity", fade);
        }

        internal void SetWidth(float scale) => _widthScale = scale;

        internal void Hide()
        {
            if (_disposed) return;
            _visual.StopAnimation("Opacity");
            _visual.Opacity = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Hide();
            _disposed = true;
            foreach (var layer in _layers)
            {
                if (layer is null) continue;
                layer.Points.Clear();
                layer.Fill = null;
            }
            _host.Children.Clear();
            _parent.Children.Remove(_host);
            foreach (var buffer in _buffers) buffer?.Dispose();
            foreach (var brush in _brushes) brush?.GradientStops.Clear();
        }
    }
}
