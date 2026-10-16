using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Foundation;
using Windows.System;

namespace ScreenLens.WinUI.Views.Capture;

public sealed partial class SelectionWindow
{
    private readonly Border[] _rectangleHandles = new Border[8];
    private SelectionEdit _rectangleEdit;
    private SelectionBounds _rectangleEditStartPixels;
    private Rect _rectangleEditStart;
    private Point _rectangleEditPointerStart;

    private bool RectangleEditable => !_freeformSelection && !_dragging
        && !_confirmed && !_imageActionBusy && !_cancelCloseRequested
        && _sel.Width >= MinSize && _sel.Height >= MinSize
        && (Toolbar.Visibility == Visibility.Visible || _rectangleEdit != SelectionEdit.None);

    private SelectionBounds RectangleBounds => new(_sel.X, _sel.Y, _sel.Width, _sel.Height);

    private SelectionBounds RectanglePixels => RectangleSelectionEditor.ToPixels(
        RectangleBounds, PixelScaleX, PixelScaleY, _shot.Width, _shot.Height);

    private void InitializeRectangleHandles()
    {
        for (var i = 0; i < _rectangleHandles.Length; i++)
        {
            var handle = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1.5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Style = (Style)Root.Resources["RectangleSelectionHandleStyle"],
            };
            _rectangleHandles[i] = handle;
            SelectionHandles.Children.Add(handle);
        }
    }

    private void UpdateRectangleHandles()
    {
        SelectionHandles.Visibility = RectangleEditable
            ? Visibility.Visible : Visibility.Collapsed;
        if (SelectionHandles.Visibility != Visibility.Visible) return;
        for (var i = 0; i < _rectangleHandles.Length; i++)
        {
            var handle = _rectangleHandles[i];
            var position = RectangleSelectionEditor.HandleAt(RectangleBounds, i);
            // Keep the complete handle visible even at a desktop edge.
            Canvas.SetLeft(handle, Math.Clamp(position.X - 4,
                0, Math.Max(0, Root.ActualWidth - 8)));
            Canvas.SetTop(handle, Math.Clamp(position.Y - 4,
                0, Math.Max(0, Root.ActualHeight - 8)));
        }
    }

    private SelectionEdit HitTestRectangle(Point point) => RectangleEditable
        ? RectangleSelectionEditor.HitTest(RectangleBounds, point.X, point.Y, 8)
        : SelectionEdit.None;

    private void UpdateRectangleCursor(Point point, bool overControls = false)
    {
        var part = overControls ? SelectionEdit.None
            : _rectangleEdit != SelectionEdit.None ? _rectangleEdit : HitTestRectangle(point);
        var shape = part switch
        {
            SelectionEdit.Left or SelectionEdit.Right => InputSystemCursorShape.SizeWestEast,
            SelectionEdit.Top or SelectionEdit.Bottom => InputSystemCursorShape.SizeNorthSouth,
            SelectionEdit.TopLeft or SelectionEdit.BottomRight
                => InputSystemCursorShape.SizeNorthwestSoutheast,
            SelectionEdit.TopRight or SelectionEdit.BottomLeft
                => InputSystemCursorShape.SizeNortheastSouthwest,
            SelectionEdit.Move => InputSystemCursorShape.SizeAll,
            _ => InputSystemCursorShape.Arrow,
        };
        Root.SetCursorShape(shape);
    }

    private bool TryBeginRectangleEdit(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        // Editing always uses the left button, independently of the drawing mappings.
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return false;
        var part = HitTestRectangle(point.Position);
        if (part == SelectionEdit.None) return false;
        CommitDimensionEdits();
        if (!Root.CapturePointer(e.Pointer)) return false;
        _rectangleEdit = part;
        _selectionPointerId = e.Pointer.PointerId;
        _rectangleEditStart = _sel;
        _rectangleEditStartPixels = RectanglePixels;
        _rectangleEditPointerStart = point.Position;
        _lastPointerPosition = point.Position;
        // Keep the handles active while the toolbar is hidden during move/resize.
        Toolbar.Visibility = Visibility.Collapsed;
        HideCaptureError();
        _feedbackMonitor = null;
        SelectionFocusTarget.Focus(FocusState.Programmatic);
        UpdateRectangleCursor(point.Position);
        e.Handled = true;
        return true;
    }

    private void MoveRectangleEdit(Point point)
    {
        var pixels = RectangleSelectionEditor.Drag(_rectangleEditStartPixels, _rectangleEdit,
            (point.X - _rectangleEditPointerStart.X) * PixelScaleX,
            (point.Y - _rectangleEditPointerStart.Y) * PixelScaleY,
            _shot.Width, _shot.Height, Math.Ceiling(MinSize * PixelScaleX),
            Math.Ceiling(MinSize * PixelScaleY));
        SetRectanglePixels(pixels);
        _lastPointerPosition = point;
        // Avoid remeasuring/repositioning the toolbar or its text on every pointer event.
    }

    private void SetRectanglePixels(SelectionBounds pixels)
    {
        _sel = new Rect(pixels.X / PixelScaleX, pixels.Y / PixelScaleY,
            pixels.Width / PixelScaleX, pixels.Height / PixelScaleY);
        ApplySelection();
    }

    private void FinishRectangleEdit(PointerRoutedEventArgs e, bool cancelled)
    {
        if (cancelled)
        {
            _sel = _rectangleEditStart;
            ApplySelection();
        }
        else
        {
            MoveRectangleEdit(e.GetCurrentPoint(Root).Position);
        }
        // Clear the mode before ReleasePointerCapture raises PointerCaptureLost.
        _rectangleEdit = SelectionEdit.None;
        try { Root.ReleasePointerCapture(e.Pointer); }
        catch { /* The system may already have cancelled capture. */ }
        _selectionMonitor = GetMonitorAtRootPoint(new Point(
            _sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2));
        _toolbarMonitor = cancelled ? _toolbarMonitor : GetMonitorAtRootPoint(_lastPointerPosition);
        SyncDimensionInputs();
        PlaceSelectionControls();
        UpdateRectangleHandles();
        UpdateRectangleCursor(_lastPointerPosition);
        e.Handled = true;
    }

    private bool TryNudgeRectangle(KeyRoutedEventArgs e)
    {
        if (!RectangleEditable || e.OriginalSource is TextBox
            || (GetKeyState(0x11) & 0x8000) != 0 || (GetKeyState(0x12) & 0x8000) != 0)
            return false;
        var step = (GetKeyState(0x10) & 0x8000) != 0 ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            VirtualKey.Left => (-step, 0),
            VirtualKey.Right => (step, 0),
            VirtualKey.Up => (0, -step),
            VirtualKey.Down => (0, step),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return false;
        SetRectanglePixels(RectangleSelectionEditor.Drag(RectanglePixels, SelectionEdit.Move,
            dx, dy, _shot.Width, _shot.Height, 1, 1));
        _selectionMonitor = GetMonitorAtRootPoint(new Point(
            _sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2));
        _toolbarMonitor = _selectionMonitor;
        SyncDimensionInputs();
        PlaceSelectionControls();
        e.Handled = true;
        return true;
    }

    private void OnSelectionControlsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_rectangleEdit == SelectionEdit.None)
            Root.SetCursorShape(InputSystemCursorShape.Arrow);
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_rectangleEdit == SelectionEdit.None)
            Root.SetCursorShape(InputSystemCursorShape.Arrow);
    }
}
