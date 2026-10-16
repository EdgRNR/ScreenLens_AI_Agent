using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Capture;

public sealed partial class SelectionWindow
{
    private bool _freeformMoving;
    private uint _freeformMovePointerId;
    private Point _freeformMovePointerStart;
    private Rect _freeformMoveStartBounds;
    private Point[] _freeformMoveStartPoints = Array.Empty<Point>();

    private bool TryBeginFreeformMove(PointerRoutedEventArgs e)
    {
        if (!_freeformSelection || _annotationMode is not null || _annotations.Count > 0
            || _sel.Width < MinSize || _sel.Height < MinSize)
            return false;
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || !_sel.Contains(point.Position))
            return false;
        if (!Root.CapturePointer(e.Pointer)) return false;

        _freeformMoving = true;
        _freeformMovePointerId = e.Pointer.PointerId;
        _freeformMovePointerStart = point.Position;
        _freeformMoveStartBounds = _sel;
        _freeformMoveStartPoints = _freeformPoints.ToArray();
        _lastPointerPosition = point.Position;
        Toolbar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        Root.SetCursorShape(InputSystemCursorShape.SizeAll);
        e.Handled = true;
        return true;
    }

    private void MoveFreeformSelection(Point point)
    {
        if (!_freeformMoving) return;
        var dx = point.X - _freeformMovePointerStart.X;
        var dy = point.Y - _freeformMovePointerStart.Y;
        var x = Math.Clamp(_freeformMoveStartBounds.Left + dx, 0,
            Math.Max(0, Root.ActualWidth - _freeformMoveStartBounds.Width));
        var y = Math.Clamp(_freeformMoveStartBounds.Top + dy, 0,
            Math.Max(0, Root.ActualHeight - _freeformMoveStartBounds.Height));
        dx = x - _freeformMoveStartBounds.Left;
        dy = y - _freeformMoveStartBounds.Top;
        _sel = new Rect(x, y, _freeformMoveStartBounds.Width,
            _freeformMoveStartBounds.Height);
        _freeformPoints.Clear();
        _freeformPoints.AddRange(_freeformMoveStartPoints.Select(p =>
            new Point(p.X + dx, p.Y + dy)));
        _lastPointerPosition = point;
        ApplySelection();
    }

    private void FinishFreeformMove(PointerRoutedEventArgs e, bool cancelled)
    {
        if (!_freeformMoving || e.Pointer.PointerId != _freeformMovePointerId) return;
        if (cancelled)
        {
            _sel = _freeformMoveStartBounds;
            _freeformPoints.Clear();
            _freeformPoints.AddRange(_freeformMoveStartPoints);
            ApplySelection();
        }
        else
        {
            MoveFreeformSelection(e.GetCurrentPoint(Root).Position);
        }

        _freeformMoving = false;
        _freeformMoveStartPoints = Array.Empty<Point>();
        try { Root.ReleasePointerCapture(e.Pointer); }
        catch { /* The system may already have cancelled capture. */ }
        _selectionMonitor = GetMonitorAtRootPoint(new Point(
            _sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2));
        _toolbarMonitor = cancelled ? _toolbarMonitor
            : GetMonitorAtRootPoint(_lastPointerPosition);
        SyncDimensionInputs();
        PlaceSelectionControls();
        UpdateRectangleCursor(_lastPointerPosition);
        e.Handled = true;
    }
}
