using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ScreenLens.WinUI.Services;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Capture;

internal enum SelectionAnnotationKind
{
    Rectangle,
    Ellipse,
}

public sealed partial class SelectionWindow
{
    private readonly List<SelectionAnnotation> _annotations = new();
    private SelectionAnnotationKind? _annotationMode;
    private bool _annotationDragging;
    private bool _annotationMoving;
    private uint _annotationPointerId;
    private Point _annotationStart;
    private int _annotationMoveIndex = -1;
    private Rect _annotationMoveStartBounds;
    private Point _annotationMoveStartPointer;
    private FrameworkElement? _annotationPreview;

    private bool TryBeginAnnotation(PointerRoutedEventArgs e)
    {
        if (_annotationMode is null || _sel.Width < MinSize || _sel.Height < MinSize)
            return false;
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return false;

        var start = ClampToSelection(point.Position);
        if (!_sel.Contains(point.Position)) return false;
        if (!Root.CapturePointer(e.Pointer)) return false;

        _annotationDragging = true;
        _annotationPointerId = e.Pointer.PointerId;
        _annotationStart = start;
        _lastPointerPosition = start;
        Toolbar.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = Visibility.Collapsed;
        Root.SetCursorShape(InputSystemCursorShape.Cross);
        UpdateAnnotationPreview(start);
        e.Handled = true;
        return true;
    }

    private void MoveAnnotation(Point point)
    {
        if (!_annotationDragging) return;
        point = ClampToSelection(point);
        _lastPointerPosition = point;
        UpdateAnnotationPreview(point);
    }

    private bool TryBeginAnnotationMove(PointerRoutedEventArgs e)
    {
        if (_annotationMode is not null || _annotations.Count == 0
            || _sel.Width < MinSize || _sel.Height < MinSize)
            return false;
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return false;
        var index = HitTestAnnotation(point.Position);
        if (index < 0 || !Root.CapturePointer(e.Pointer)) return false;

        _annotationMoving = true;
        _annotationPointerId = e.Pointer.PointerId;
        _annotationMoveIndex = index;
        _annotationMoveStartBounds = _annotations[index].Bounds;
        _annotationMoveStartPointer = point.Position;
        Toolbar.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = Visibility.Collapsed;
        Root.SetCursorShape(InputSystemCursorShape.SizeAll);
        e.Handled = true;
        return true;
    }

    private void MoveAnnotationShape(Point point)
    {
        if (!_annotationMoving || _annotationMoveIndex < 0) return;
        var bounds = _annotationMoveStartBounds;
        var dx = point.X - _annotationMoveStartPointer.X;
        var dy = point.Y - _annotationMoveStartPointer.Y;
        var x = Math.Clamp(bounds.Left + dx, _sel.Left,
            Math.Max(_sel.Left, _sel.Right - bounds.Width));
        var y = Math.Clamp(bounds.Top + dy, _sel.Top,
            Math.Max(_sel.Top, _sel.Bottom - bounds.Height));
        _annotations[_annotationMoveIndex] = new SelectionAnnotation(
            _annotations[_annotationMoveIndex].Kind,
            new Rect(x, y, bounds.Width, bounds.Height));
        RenderAnnotations();
    }

    private void FinishAnnotationMove(PointerRoutedEventArgs e, bool cancelled)
    {
        if (!_annotationMoving || e.Pointer.PointerId != _annotationPointerId) return;
        if (cancelled && _annotationMoveIndex >= 0)
            _annotations[_annotationMoveIndex] = new SelectionAnnotation(
                _annotations[_annotationMoveIndex].Kind, _annotationMoveStartBounds);
        else if (!cancelled)
            MoveAnnotationShape(e.GetCurrentPoint(Root).Position);

        _annotationMoving = false;
        _annotationMoveIndex = -1;
        try { Root.ReleasePointerCapture(e.Pointer); }
        catch { /* The system may already have cancelled capture. */ }
        RenderAnnotations();
        PlaceSelectionControls();
        UpdateRectangleHandles();
        Root.SetCursorShape(InputSystemCursorShape.Arrow);
        e.Handled = true;
    }

    private int HitTestAnnotation(Point point)
    {
        const double tolerance = 8;
        for (var i = _annotations.Count - 1; i >= 0; i--)
        {
            var bounds = _annotations[i].Bounds;
            if (_annotations[i].Kind == SelectionAnnotationKind.Ellipse)
            {
                var cx = bounds.Left + bounds.Width / 2;
                var cy = bounds.Top + bounds.Height / 2;
                var rx = Math.Max(1, bounds.Width / 2 + tolerance);
                var ry = Math.Max(1, bounds.Height / 2 + tolerance);
                if (Math.Pow((point.X - cx) / rx, 2)
                    + Math.Pow((point.Y - cy) / ry, 2) <= 1)
                    return i;
            }
            else if (point.X >= bounds.Left - tolerance
                && point.X <= bounds.Right + tolerance
                && point.Y >= bounds.Top - tolerance
                && point.Y <= bounds.Bottom + tolerance)
            {
                return i;
            }
        }
        return -1;
    }

    private void FinishAnnotation(PointerRoutedEventArgs e, bool cancelled)
    {
        if (!_annotationDragging || e.Pointer.PointerId != _annotationPointerId) return;
        var end = cancelled ? _annotationStart : ClampToSelection(e.GetCurrentPoint(Root).Position);
        _annotationDragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); }
        catch { /* The system may already have cancelled capture. */ }

        RemoveAnnotationPreview();
        if (!cancelled)
        {
            var rect = NormalizeAnnotationRect(_annotationStart, end);
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                _annotations.Add(new SelectionAnnotation(_annotationMode!.Value, rect));
                RenderAnnotations();
            }
        }

        _annotationMode = null;
        UpdateAnnotationButtonStates();
        PlaceSelectionControls();
        UpdateRectangleHandles();
        Root.SetCursorShape(InputSystemCursorShape.Arrow);
        e.Handled = true;
    }

    private Point ClampToSelection(Point point)
        => new(Math.Clamp(point.X, _sel.Left, _sel.Right),
            Math.Clamp(point.Y, _sel.Top, _sel.Bottom));

    private static Rect NormalizeAnnotationRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void SetAnnotationMode(SelectionAnnotationKind? mode)
    {
        if (_annotationDragging || _annotationMoving || _freeformMoving) return;
        _annotationMode = _annotationMode == mode ? null : mode;
        if (_annotationMode is null)
        {
            Root.SetCursorShape(InputSystemCursorShape.Arrow);
            if (_sel.Width >= MinSize && _sel.Height >= MinSize)
            {
                PlaceSelectionControls();
                UpdateRectangleHandles();
            }
        }
        else
        {
            Root.SetCursorShape(InputSystemCursorShape.Cross);
        }
        UpdateAnnotationButtonStates();
    }

    private void UpdateAnnotationButtonStates()
    {
        RectangleAnnotationBtn.Opacity = _annotationMode == SelectionAnnotationKind.Rectangle ? 1.0 : 0.72;
        EllipseAnnotationBtn.Opacity = _annotationMode == SelectionAnnotationKind.Ellipse ? 1.0 : 0.72;
        var enabled = _sel.Width >= MinSize && _sel.Height >= MinSize && !_confirmed;
        RectangleAnnotationBtn.IsEnabled = enabled;
        EllipseAnnotationBtn.IsEnabled = enabled;
    }

    private void UpdateAnnotationPreview(Point end)
    {
        RemoveAnnotationPreview();
        var rect = NormalizeAnnotationRect(_annotationStart, end);
        if (rect.Width < 1 || rect.Height < 1 || _annotationMode is null) return;
        _annotationPreview = CreateAnnotationElement(_annotationMode.Value, rect, 2.5);
        AnnotationCanvas.Children.Add(_annotationPreview);
    }

    private void RemoveAnnotationPreview()
    {
        if (_annotationPreview is null) return;
        AnnotationCanvas.Children.Remove(_annotationPreview);
        _annotationPreview = null;
    }

    private void RenderAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        _annotationPreview = null;
        foreach (var annotation in _annotations)
            AnnotationCanvas.Children.Add(CreateAnnotationElement(
                annotation.Kind, annotation.Bounds, 2.5));
    }

    private static FrameworkElement CreateAnnotationElement(
        SelectionAnnotationKind kind, Rect bounds, double strokeThickness)
    {
        FrameworkElement element = kind == SelectionAnnotationKind.Rectangle
            ? new Border
            {
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 82, 82)),
                BorderThickness = new Thickness(strokeThickness),
            }
            : new Ellipse
            {
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 82, 82)),
                StrokeThickness = strokeThickness,
            };
        element.Width = bounds.Width;
        element.Height = bounds.Height;
        Canvas.SetLeft(element, bounds.Left);
        Canvas.SetTop(element, bounds.Top);
        return element;
    }

    private void ClearAnnotations()
    {
        _annotations.Clear();
        RemoveAnnotationPreview();
        AnnotationCanvas.Children.Clear();
        _annotationMode = null;
        UpdateAnnotationButtonStates();
    }

    private bool TryUndoLastAnnotation()
    {
        if (_annotations.Count == 0 || _annotationDragging || _annotationMoving)
            return false;

        _annotations.RemoveAt(_annotations.Count - 1);
        RenderAnnotations();
        UpdateAnnotationButtonStates();
        PlaceSelectionControls();
        UpdateRectangleHandles();
        UpdateRectangleCursor(_lastPointerPosition);
        return true;
    }

    private IReadOnlyList<CaptureAnnotation> GetCaptureAnnotations(
        double scaleX, double scaleY, int cropX, int cropY)
    {
        var result = new List<CaptureAnnotation>(_annotations.Count);
        foreach (var annotation in _annotations)
        {
            var bounds = annotation.Bounds;
            result.Add(new CaptureAnnotation(
                annotation.Kind == SelectionAnnotationKind.Rectangle
                    ? CaptureAnnotationKind.Rectangle : CaptureAnnotationKind.Ellipse,
                (int)Math.Round(bounds.X * scaleX - cropX),
                (int)Math.Round(bounds.Y * scaleY - cropY),
                Math.Max(1, (int)Math.Round(bounds.Width * scaleX)),
                Math.Max(1, (int)Math.Round(bounds.Height * scaleY)),
                Math.Max(1, (int)Math.Round(2.5 * Math.Min(scaleX, scaleY)))));
        }
        return result;
    }

    private readonly record struct SelectionAnnotation(
        SelectionAnnotationKind Kind, Rect Bounds);

    private void OnRectangleAnnotationClick(object sender, RoutedEventArgs e)
        => SetAnnotationMode(SelectionAnnotationKind.Rectangle);

    private void OnEllipseAnnotationClick(object sender, RoutedEventArgs e)
        => SetAnnotationMode(SelectionAnnotationKind.Ellipse);
}
